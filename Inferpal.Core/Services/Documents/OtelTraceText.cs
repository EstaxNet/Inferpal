using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Inferpal.Services.Documents;

/// <summary>
/// A distributed trace exported as JSON — OpenTelemetry's OTLP (one request, or one per line as the Collector's file exporter
/// writes them), Jaeger, Zipkin v2 — read as what someone chasing a slow or failing request wants: per trace, the spans in
/// error with their status, attributes and exception, the slow path (from the root, the child that ends last at each level),
/// then the tree with each span's offset and duration. The file stays a text file: asked for lines, each is the text it holds.
/// </summary>
/// <remarks>
/// ⚠ Recognised by its content, never its name: a <c>.json</c> is looked at only when its head names spans. ⚠ Never throws
/// for a file of an unexpected shape: it is read as its lines (<c>null</c>). ⚠ Everything fits the page budget, and what does
/// not is counted; a span whose parent is not in the file is said to be one.
/// </remarks>
internal static class OtelTraceText
{
    private const int MaxChars = DocumentText.ReportMaxChars;

    /// <summary>The most spans held from one file: past it they are counted only.</summary>
    internal const int MaxSpans = 200_000;

    private const int MaxAttributes = 12, ValueChars = 200, StackLines = 4;

    private sealed class Span
    {
        public required string TraceId;
        public required string Id;
        public string? Parent;
        public required string Service;
        public required string Name;
        public string Kind = string.Empty;
        public long Start, End;   // nanoseconds since the epoch
        public bool Error;
        public string? Status;
        public List<string> Attributes = [];
        public List<string> Events = [];
    }

    /// <summary>A trace export's spans, <paramref name="text"/> read from <paramref name="name"/>, within
    /// <paramref name="budget"/> characters; <c>null</c> for any other file.</summary>
    internal static string? Summarize(string name, string text, int budget, CancellationToken ct = default)
    {
        if (text.Length > MaxChars) return null;
        var lower = name.ToLowerInvariant();
        if (!(lower.EndsWith(".json", StringComparison.Ordinal) || lower.EndsWith(".jsonl", StringComparison.Ordinal)
              || lower.EndsWith(".ndjson", StringComparison.Ordinal))) return null;
        var head = text.Length > 4096 ? text[..4096] : text;
        var otlp = head.Contains("\"resourceSpans\"", StringComparison.Ordinal) || head.Contains("\"instrumentationLibrarySpans\"", StringComparison.Ordinal)
                   || head.Contains("\"resource_spans\"", StringComparison.Ordinal);
        var jaeger = !otlp && head.Contains("\"traceID\"", StringComparison.Ordinal) && head.Contains("\"spanID\"", StringComparison.Ordinal);
        var zipkin = !otlp && !jaeger && head.TrimStart().StartsWith('[') && head.Contains("\"traceId\"", StringComparison.Ordinal)
                     && (head.Contains("\"localEndpoint\"", StringComparison.Ordinal) || head.Contains("\"duration\"", StringComparison.Ordinal));
        if (!otlp && !jaeger && !zipkin) return null;
        try
        {
            var spans = new List<Span>();
            var beyond = 0;
            void Add(Span s)
            {
                if (s.TraceId.Length == 0 || s.Id.Length == 0) return;
                if (spans.Count < MaxSpans) spans.Add(s); else beyond++;
            }
            var format = otlp ? "OTLP JSON" : jaeger ? "Jaeger JSON" : "Zipkin v2 JSON";
            var options = new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 256 };
            if (otlp)
            {
                // One request, or one per line (the Collector's file exporter).
                JsonDocument? whole = null;
                try { whole = JsonDocument.Parse(text, options); } catch (JsonException) { }
                if (whole is not null)
                    using (whole) ReadOtlp(whole.RootElement, Add, ct);
                else
                {
                    int lines = 0, unread = 0;
                    foreach (var line in text.Split('\n'))
                    {
                        ct.ThrowIfCancellationRequested();
                        if (line.Trim().Length == 0) continue;
                        JsonDocument doc;
                        try { doc = JsonDocument.Parse(line, options); }
                        catch (JsonException) { unread++; continue; }
                        using (doc) ReadOtlp(doc.RootElement, Add, ct);
                        lines++;
                    }
                    format = string.Create(CultureInfo.InvariantCulture, $"OTLP JSON, {lines} request(s), one per line")
                           + (unread > 0 ? string.Create(CultureInfo.InvariantCulture, $"; {unread} line(s) that are not JSON, not read") : string.Empty);
                }
            }
            else
            {
                using var doc = JsonDocument.Parse(text, options);
                if (jaeger) ReadJaeger(doc.RootElement, Add, ct);
                else ReadZipkin(doc.RootElement, Add, ct);
            }
            if (spans.Count == 0) return null;
            return Render(name, format, spans, beyond, budget, ct);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or ArgumentException
                                       or KeyNotFoundException or IndexOutOfRangeException or OverflowException)
        {
            Diagnostics.Swallow("OtelTraceText.Summarize", ex);
            return null;
        }
    }

    // ── Formats ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A property by its camelCase name, or its snake_case one (protobuf's JSON with the proto's field names).</summary>
    private static JsonElement Prop(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object) return default;
        if (e.TryGetProperty(name, out var v)) return v;
        var snake = string.Concat(name.Select((c, i) => char.IsUpper(c) && i > 0 && char.IsLower(name[i - 1]) ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));
        return snake != name && e.TryGetProperty(snake, out v) ? v : default;
    }

    private static string? Str(JsonElement e, string name) => Prop(e, name) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;

    private static IEnumerable<JsonElement> Items(JsonElement e) => e.ValueKind == JsonValueKind.Array ? e.EnumerateArray() : [];

    /// <summary>A number written as a number or as a string (OTLP's 64-bit integers are strings in JSON).</summary>
    private static long Long(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Number when e.TryGetInt64(out var n) => n,
        JsonValueKind.Number => (long)e.GetDouble(),
        JsonValueKind.String when long.TryParse(e.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
        _ => 0,
    };

    /// <summary>A value that is no string, as its JSON; nothing for one that is missing (a tag without a value).</summary>
    private static string Raw(JsonElement e) => e.ValueKind == JsonValueKind.Undefined ? string.Empty : e.GetRawText();

    /// <summary>An identifier as lowercase hex: OTLP/JSON writes hex, proto3 JSON base64.</summary>
    private static string Id(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (value.All(Uri.IsHexDigit)) return value.ToLowerInvariant();   // Jaeger writes its ids unpadded: any length
        try { return Convert.ToHexString(Convert.FromBase64String(value)).ToLowerInvariant(); }
        catch (FormatException) { return Value(value); }   // a value from the file: never a line of this text
    }

    private static string Value(string? text) =>
        text is null ? string.Empty : Clean(text.Length > ValueChars ? text[..ValueChars] + "…" : text);

    /// <summary>Text from the file on one line: a line break becomes ⏎ (a value must never write a line of this text), any other
    /// control character a space.</summary>
    private static string Clean(string text) =>
        new(text.Select(c => c == (char)10 ? '⏎' : char.IsControl(c) || c is '\u2028' or '\u2029' ? ' ' : c).ToArray());

    /// <summary>An OTLP AnyValue as text.</summary>
    private static string AnyValue(JsonElement v)
    {
        foreach (var kind in new[] { "stringValue", "intValue", "doubleValue", "boolValue" })
            if (Prop(v, kind) is { ValueKind: not JsonValueKind.Undefined } x)
                return Value(x.ValueKind == JsonValueKind.String ? x.GetString() : x.GetRawText());
        if (Prop(v, "arrayValue") is { ValueKind: JsonValueKind.Object } array)
        {
            var values = Items(Prop(array, "values")).ToList();
            return Value("[" + string.Join(", ", values.Take(10).Select(AnyValue))
                         + (values.Count > 10 ? string.Create(CultureInfo.InvariantCulture, $", … and {values.Count - 10} more") : string.Empty) + "]");
        }
        return Value(v.ValueKind == JsonValueKind.Undefined ? string.Empty : v.GetRawText());
    }

    private static List<(string Key, string Value)> OtlpAttributes(JsonElement list) =>
        [.. Items(list).Select(a => (Value(Str(a, "key") ?? "?"), AnyValue(Prop(a, "value"))))];

    private static void ReadOtlp(JsonElement root, Action<Span> add, CancellationToken ct)
    {
        foreach (var resourceSpans in Items(Prop(root, "resourceSpans")))
        {
            var resource = OtlpAttributes(Prop(Prop(resourceSpans, "resource"), "attributes"));
            var service = resource.FirstOrDefault(a => a.Key == "service.name").Value is { Length: > 0 } s ? s : "(no service.name)";
            var scopes = Prop(resourceSpans, "scopeSpans") is { ValueKind: JsonValueKind.Array } scoped ? scoped : Prop(resourceSpans, "instrumentationLibrarySpans");
            foreach (var scope in Items(scopes))
                foreach (var span in Items(Prop(scope, "spans")))
                {
                    ct.ThrowIfCancellationRequested();
                    var status = Prop(span, "status");
                    var code = Prop(status, "code");
                    var error = code.ValueKind == JsonValueKind.Number ? Long(code) == 2 : Str(status, "code") is "STATUS_CODE_ERROR" or "ERROR";
                    var kind = Prop(span, "kind");
                    var s2 = new Span
                    {
                        TraceId = Id(Str(span, "traceId")), Id = Id(Str(span, "spanId")), Parent = Id(Str(span, "parentSpanId")) is { Length: > 0 } p ? p : null,
                        Service = service, Name = Value(Str(span, "name") ?? "?"),
                        Kind = kind.ValueKind == JsonValueKind.Number
                            ? Long(kind) switch { 1 => "INTERNAL", 2 => "SERVER", 3 => "CLIENT", 4 => "PRODUCER", 5 => "CONSUMER", _ => string.Empty }
                            : Value(Str(span, "kind") ?? string.Empty).Replace("SPAN_KIND_", string.Empty, StringComparison.Ordinal).Replace("UNSPECIFIED", string.Empty, StringComparison.Ordinal),
                        Start = Long(Prop(span, "startTimeUnixNano")), End = Long(Prop(span, "endTimeUnixNano")),
                        Error = error, Status = Str(status, "message") is { Length: > 0 } m ? Value(m) : null,
                        Attributes = [.. OtlpAttributes(Prop(span, "attributes")).Select(a => $"{a.Key}={a.Value}")],
                    };
                    foreach (var ev in Items(Prop(span, "events")))
                        s2.Events.Add(Event(Str(ev, "name") ?? "event", OtlpAttributes(Prop(ev, "attributes"))));
                    add(s2);
                }
        }
    }

    /// <summary>An event in one line; an exception as its type, message and the top of its stack.</summary>
    private static string Event(string name, List<(string Key, string Value)> attributes)
    {
        string? Get(string key) => attributes.FirstOrDefault(a => a.Key == key).Value;
        if (name == "exception" || Get("exception.type") is not null)
        {
            var stack = Get("exception.stacktrace");
            return $"exception {Get("exception.type")}: {Get("exception.message")}"
                 + (stack is { Length: > 0 } ? " — " + string.Join(" | ", stack.Split(new[] { '⏎', '|' }, StringSplitOptions.RemoveEmptyEntries)
                                                                        .Select(l => l.Trim()).Where(l => l.Length > 0).Take(StackLines)) : string.Empty);
        }
        return Value(name) + (attributes.Count > 0 ? " " + string.Join(", ", attributes.Take(6).Select(a => $"{a.Key}={a.Value}")) : string.Empty)
             + (attributes.Count > 6 ? string.Create(CultureInfo.InvariantCulture, $", … and {attributes.Count - 6} more") : string.Empty);
    }

    private static void ReadJaeger(JsonElement root, Action<Span> add, CancellationToken ct)
    {
        // The UI's download ({"data": [traces]}), an array of traces, or one trace.
        var traces = Prop(root, "data") is { ValueKind: JsonValueKind.Array } data ? Items(data)
                   : root.ValueKind == JsonValueKind.Array ? Items(root) : [root];
        foreach (var trace in traces)
        {
            var processes = Prop(trace, "processes");
            foreach (var span in Items(Prop(trace, "spans")))
            {
                ct.ThrowIfCancellationRequested();
                var tags = Items(Prop(span, "tags")).Select(t => (Key: Value(Str(t, "key") ?? "?"), Value: Value(Prop(t, "value").ValueKind == JsonValueKind.String
                               ? Prop(t, "value").GetString() : Raw(Prop(t, "value"))))).ToList();
                string? Tag(string key) => tags.FirstOrDefault(t => t.Key == key).Value;
                var processId = Str(span, "processID");
                var process = processId is not null ? Prop(processes, processId) : Prop(span, "process");
                var reference = Items(Prop(span, "references")).FirstOrDefault(r => Str(r, "refType") == "CHILD_OF");
                if (reference.ValueKind == JsonValueKind.Undefined) reference = Items(Prop(span, "references")).FirstOrDefault();
                var start = Long(Prop(span, "startTime")) * 1000;
                var s2 = new Span
                {
                    TraceId = Id(Str(span, "traceID")), Id = Id(Str(span, "spanID")), Parent = Str(reference, "spanID") is { Length: > 0 } p ? Id(p) : null,
                    Service = Str(process, "serviceName") is { Length: > 0 } svc ? Value(svc) : "(no service)",
                    Name = Value(Str(span, "operationName") ?? "?"), Kind = (Tag("span.kind") ?? string.Empty).ToUpperInvariant(),
                    Start = start, End = start + Long(Prop(span, "duration")) * 1000,
                    Error = Tag("error") == "true" || Tag("otel.status_code") == "ERROR",
                    Status = Tag("otel.status_description") is { Length: > 0 } d ? d : null,
                    Attributes = [.. tags.Where(t => t.Key is not ("span.kind" or "error" or "otel.status_code" or "otel.status_description")).Select(t => $"{t.Key}={t.Value}")],
                };
                foreach (var log in Items(Prop(span, "logs")))
                {
                    var fields = Items(Prop(log, "fields")).Select(f => (Value(Str(f, "key") ?? "?"), Value(Prop(f, "value").ValueKind == JsonValueKind.String
                                     ? Prop(f, "value").GetString() : Raw(Prop(f, "value"))))).ToList();
                    var eventName = fields.FirstOrDefault(f => f.Item1 == "event").Item2 ?? "log";
                    s2.Events.Add(Event(eventName, [.. fields.Where(f => f.Item1 != "event")]));
                }
                add(s2);
            }
        }
    }

    private static void ReadZipkin(JsonElement root, Action<Span> add, CancellationToken ct)
    {
        // An array of spans, or an array of traces (the API's /api/v2/traces).
        var spans = Items(root).SelectMany(e => e.ValueKind == JsonValueKind.Array ? Items(e) : [e]);
        foreach (var span in spans)
        {
            ct.ThrowIfCancellationRequested();
            var tags = Prop(span, "tags") is { ValueKind: JsonValueKind.Object } t
                ? t.EnumerateObject().Select(p => (Key: Value(p.Name), Value: Value(p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.GetRawText()))).ToList()
                : [];
            string? Tag(string key) => tags.FirstOrDefault(x => x.Key == key).Value;
            var start = Long(Prop(span, "timestamp")) * 1000;
            // The service: the span's own service.name tag first (one exporter for many services names its endpoint once).
            var service = Tag("service.name") ?? Str(Prop(span, "localEndpoint"), "serviceName") ?? "(no service)";
            var errorTag = Tag("error");
            var s2 = new Span
            {
                TraceId = Id(Str(span, "traceId")), Id = Id(Str(span, "id")), Parent = Str(span, "parentId") is { Length: > 0 } p ? Id(p) : null,
                Service = Value(service), Name = Value(Str(span, "name") ?? "?"), Kind = Value(Str(span, "kind") ?? string.Empty),
                Start = start, End = Prop(span, "duration").ValueKind == JsonValueKind.Undefined ? 0 : start + Long(Prop(span, "duration")) * 1000,
                Error = errorTag is not null || Tag("otel.status_code") == "ERROR",
                Status = Tag("otel.status_description") ?? (errorTag is { Length: > 0 } and not "true" ? errorTag : null),
                Attributes = [.. tags.Where(x => x.Key is not ("error" or "otel.status_code" or "otel.status_description") && !x.Key.StartsWith("telemetry.", StringComparison.Ordinal)
                                                 && !x.Key.StartsWith("otel.", StringComparison.Ordinal) && x.Key is not ("service.name" or "service.instance.id"))
                                     .Select(x => $"{x.Key}={x.Value}")],
            };
            foreach (var annotation in Items(Prop(span, "annotations")))
                s2.Events.Add(Annotation(Str(annotation, "value") ?? "annotation"));
            add(s2);
        }
    }

    /// <summary>A Zipkin annotation: OpenTelemetry writes an event as JSON — <c>{"exception": {"exception.type": …}}</c> —
    /// read as the event it is; any other value as it is.</summary>
    private static string Annotation(string value)
    {
        if (value.TrimStart().StartsWith('{'))
            try
            {
                using var doc = JsonDocument.Parse(value);
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.EnumerateObject().ToList() is [var only]
                    && only.Value.ValueKind == JsonValueKind.Object)
                    return Event(Value(only.Name), [.. only.Value.EnumerateObject().Select(f => (Value(f.Name), Value(f.Value.ValueKind == JsonValueKind.String
                                                      ? f.Value.GetString() : f.Value.GetRawText())))]);
            }
            catch (JsonException) { }
        return Value(value);
    }

    // ── The text ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A duration or an offset: microseconds under a tenth of a millisecond (a span of 5 µs is not "0.0 ms").</summary>
    private static string Ms(long nanos) => nanos is > 0 and < 100_000
        ? (nanos / 1000.0).ToString("0", CultureInfo.InvariantCulture) + " µs"
        : (nanos / 1_000_000.0).ToString(nanos < 10_000_000 ? "0.0" : "0", CultureInfo.InvariantCulture) + " ms";

    private static string Render(string name, string format, List<Span> spans, int beyond, int budget, CancellationToken ct)
    {
        var traces = spans.GroupBy(s => s.TraceId, StringComparer.Ordinal)
            .Select(g => (Id: g.Key, Spans: g.ToList()))
            .OrderByDescending(t => t.Spans.Count(s => s.Error))
            .ThenByDescending(t => t.Spans.Max(s => s.End) - t.Spans.Min(s => s.Start))
            .ToList();
        var services = spans.Select(s => s.Service).Distinct(StringComparer.Ordinal).ToList();
        var sb = new StringBuilder(string.Create(CultureInfo.InvariantCulture,
            $"[{name}: a distributed trace export ({format}), {traces.Count} trace(s), {spans.Count + beyond} span(s), services: {string.Join(", ", services.Take(12))}{(services.Count > 12 ? $" and {services.Count - 12} more" : string.Empty)}. "
          + $"Per trace, errors first, then the slow path and the tree; times in ms from the trace's start. Read it by lines (start_line) for the raw JSON.]\n"));
        if (beyond > 0) sb.Append(CultureInfo.InvariantCulture, $"[Past {MaxSpans} spans, {beyond} are counted only.]\n");
        var shownTraces = 0;
        foreach (var (traceId, all) in traces)
        {
            ct.ThrowIfCancellationRequested();
            if (sb.Length > budget) break;
            shownTraces++;
            var timed = all.Where(s => s.Start > 0).ToList();
            var origin = timed.Count > 0 ? timed.Min(s => s.Start) : 0;
            string At(Span s) => s.Start > 0 ? "+" + Ms(s.Start - origin) : "time unknown";
            string Took(Span s) => s.Start > 0 && s.End >= s.Start ? Ms(s.End - s.Start) : "duration unknown";
            var byId = all.GroupBy(s => s.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            var children = all.Where(s => s.Parent is not null && byId.ContainsKey(s.Parent) && s.Parent != s.Id)
                              .GroupBy(s => s.Parent!, StringComparer.Ordinal)
                              .ToDictionary(g => g.Key, g => g.OrderBy(s => s.Start).ToList(), StringComparer.Ordinal);
            var roots = all.Where(s => s.Parent is null || !byId.ContainsKey(s.Parent) || s.Parent == s.Id).OrderBy(s => s.Start).ToList();
            var orphans = roots.Count(s => s.Parent is not null && s.Parent != s.Id);
            var errors = all.Where(s => s.Error).ToList();
            sb.Append(CultureInfo.InvariantCulture,
                $"\n## Trace {traceId} — {(timed.Any(s => s.End >= s.Start) ? Ms(timed.Where(s => s.End >= s.Start).Max(s => s.End) - origin) : "duration unknown")}, {all.Count} span(s), {errors.Count} in error, services: {Services(all)}\n");
            if (orphans > 0)
                sb.Append(CultureInfo.InvariantCulture, $"({orphans} span(s) name a parent that is not in the file: shown as roots.)\n");

            if (errors.Count > 0)
            {
                sb.Append("### Spans in error\n");
                var said = 0;
                foreach (var s in errors.OrderBy(s => s.Start))
                {
                    if (sb.Length > budget)
                    {
                        sb.Append(CultureInfo.InvariantCulture, $"… {errors.Count - said} more span(s) in error, not listed: the page is full.\n");
                        break;
                    }
                    said++;
                    sb.Append(CultureInfo.InvariantCulture, $"✗ {s.Service}  {s.Name}{Kind(s)} — {Took(s)}, at {At(s)}")
                      .Append(s.Status is { } status ? $" — status: {status}" : string.Empty).Append('\n');
                    if (s.Attributes.Count > 0)
                        sb.Append("  attributes: ").Append(string.Join(", ", s.Attributes.Take(MaxAttributes)))
                          .Append(s.Attributes.Count > MaxAttributes ? string.Create(CultureInfo.InvariantCulture, $" … and {s.Attributes.Count - MaxAttributes} more") : string.Empty).Append('\n');
                    foreach (var ev in s.Events.Take(4)) sb.Append("  event: ").Append(ev).Append('\n');
                    if (s.Events.Count > 4) sb.Append(CultureInfo.InvariantCulture, $"  … and {s.Events.Count - 4} more event(s)\n");
                }
            }

            // The slow path: from the longest root, at each level the child that ends last — the one its parent waited for.
            var path = new List<Span>();
            var current = roots.OrderByDescending(s => s.End - s.Start).FirstOrDefault();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (current is not null && seen.Add(current.Id) && path.Count < 50)
            {
                path.Add(current);
                current = children.TryGetValue(current.Id, out var kids) ? kids.OrderByDescending(k => k.End).First() : null;
            }
            if (path.Count > 1 && sb.Length <= budget)
            {
                var line = string.Join(" → ", path.Select(s => $"{s.Service} {s.Name} {Took(s)}"));
                sb.Append("### Slow path (at each level, the child that ends last)\n")
                  .Append(line.Length > 2000 ? line[..2000] + " …" : line)
                  .Append(path.Count >= 50 && current is not null ? " → … (cut at 50 levels)" : string.Empty).Append('\n');
            }

            sb.Append("### Spans\n");
            var lines = 0;
            var full = false;
            var listed = new HashSet<Span>(ReferenceEqualityComparer.Instance);
            void Walk(Span s, int depth)
            {
                ct.ThrowIfCancellationRequested();
                if (full || depth >= 64 || !listed.Add(s)) return;
                if (sb.Length > budget) { full = true; return; }
                lines++;
                sb.Append(CultureInfo.InvariantCulture, $"{At(s)}  {Took(s)}  {new string(' ', Math.Min(depth, 12) * 2)}{(s.Error ? "✗ " : string.Empty)}{s.Service}  {s.Name}{Kind(s)}\n");
                if (!children.TryGetValue(s.Id, out var kids)) return;
                foreach (var kid in kids) Walk(kid, depth + 1);
            }
            foreach (var root in roots) Walk(root, 0);
            // Not listed because the page is full, or because no root leads to it: a second span under an id already used, a
            // loop of parents, a tree deeper than 64.
            if (lines < all.Count)
                sb.Append(full
                    ? string.Create(CultureInfo.InvariantCulture, $"… {all.Count - lines} more span(s) of this trace, not listed: the page is full.\n")
                    : string.Create(CultureInfo.InvariantCulture, $"… {all.Count - lines} span(s) of this trace reached from no root (an id used twice, a loop of parents, or a tree deeper than 64), not listed.\n"));
        }
        if (shownTraces < traces.Count)
            sb.Append(CultureInfo.InvariantCulture, $"\n… {traces.Count - shownTraces} more trace(s), not listed: read the file by lines for them.\n");
        return sb.ToString();
    }

    /// <summary>A trace's services, eight at most and the rest counted.</summary>
    private static string Services(List<Span> spans)
    {
        var services = spans.Select(s => s.Service).Distinct(StringComparer.Ordinal).ToList();
        return string.Join(", ", services.Take(8)) + (services.Count > 8 ? string.Create(CultureInfo.InvariantCulture, $" and {services.Count - 8} more") : string.Empty);
    }

    private static string Kind(Span s) => s.Kind is { Length: > 0 } k ? $" ({k})" : string.Empty;
}
