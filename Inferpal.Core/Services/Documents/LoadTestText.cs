using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;

namespace Inferpal.Services.Documents;

/// <summary>
/// The result of a load test — a k6 summary (<c>--summary-export</c>, or the data a <c>handleSummary</c> writes as JSON), k6's
/// per-request output (<c>--out json</c>), a JMeter <c>.jtl</c> (CSV or XML) — read as what someone asking "did it hold?"
/// wants: the thresholds that failed, then per request its count, errors, percentiles and throughput, the worst first.
/// </summary>
/// <remarks>⚠ Recognised by its content, never its name alone. ⚠ Never throws for a file of an unexpected shape: it is read
/// as its lines (<c>null</c>). Percentiles of k6's summary are k6's own; those of per-request output are computed here, by
/// nearest rank, and said to be. No value of the file writes a line of its own: labels and messages are cut and flattened.</remarks>
internal static class LoadTestText
{
    private const int MaxChars = DocumentText.ReportMaxChars;

    /// <summary>The most requests (rows) listed; past it, the rest is counted.</summary>
    internal const int MaxRequests = 40;

    private const int LabelChars = 120, MessageChars = 160, MaxMessages = 3;

    /// <summary>A load test's result, <paramref name="text"/> read from <paramref name="name"/>, within
    /// <paramref name="budget"/> characters; <c>null</c> for any other file.</summary>
    internal static string? Summarize(string name, string text, int budget, CancellationToken ct = default)
    {
        if (text.Length > MaxChars) return null;
        var head = text.Length > 4096 ? text[..4096] : text;
        head = head.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        try
        {
            if (head.StartsWith("<", StringComparison.Ordinal) && head.Contains("<testResults", StringComparison.Ordinal))
                return Render(name, "JMeter XML", JmeterXml(text, ct), budget);
            if (head.StartsWith("{", StringComparison.Ordinal))
            {
                var firstLine = head.Split('\n', 2)[0];
                if (firstLine.Contains("\"metric\"", StringComparison.Ordinal)
                    && (firstLine.Contains("\"type\":\"Point\"", StringComparison.Ordinal) || firstLine.Contains("\"type\":\"Metric\"", StringComparison.Ordinal)))
                    return Render(name, "k6 output", K6Points(text, ct), budget);
                // The whole text: a summary's root_group, written first, can be long.
                if (text.Contains("\"metrics\"", StringComparison.Ordinal) && text.Contains("\"http_req", StringComparison.Ordinal))
                    return K6Summary(name, text, budget);
                return null;
            }
            var header = head.Split('\n', 2)[0];
            if (header.Contains("timeStamp", StringComparison.Ordinal) && header.Contains("elapsed", StringComparison.Ordinal)
                && header.Contains("label", StringComparison.Ordinal))
                return Render(name, "JMeter CSV", JmeterCsv(text, ct), budget);
            return null;
        }
        catch (Exception ex) when (ex is JsonException or XmlException or FormatException or InvalidOperationException
                                       or ArgumentException or OverflowException or KeyNotFoundException)
        {
            Diagnostics.Swallow("LoadTestText.Summarize", ex);
            return null;
        }
    }

    /// <summary>What one request (label) did: durations in milliseconds, errors by cause.</summary>
    private sealed class Request
    {
        public readonly List<double> Durations = [];
        public int Errors;
        public readonly Dictionary<string, int> Causes = new(StringComparer.Ordinal);
        public void Fail(string cause) { Errors++; Causes[cause] = Causes.GetValueOrDefault(cause) + 1; }
    }

    /// <summary>Every request of a run by label, with the run's first start and last end (ms since the epoch; NaN unknown).</summary>
    private sealed class Run
    {
        public readonly Dictionary<string, Request> Requests = new(StringComparer.Ordinal);
        public double Start = double.NaN, End = double.NaN;
        public int Unreadable;
        /// <summary>The file records no outcome (JMeter's <c>success</c> not saved): failures are unknown, not zero.</summary>
        public bool OutcomeUnknown;
        public Request Of(string label) => Requests.TryGetValue(label, out var r) ? r : Requests[label] = new Request();
        public void Span(double start, double end)
        {
            if (double.IsNaN(Start) || start < Start) Start = start;
            if (double.IsNaN(End) || end > End) End = end;
        }
    }

    private static string Clean(string? text, int max)
    {
        var flat = OneLine.Flat(text);
        return flat.Length > max ? flat[..(max - 1)] + "…" : flat;
    }

    // ── JMeter ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A CSV line split on commas, a quoted field kept whole (JMeter quotes a message holding a comma).</summary>
    internal static List<string> CsvFields(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else sb.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { fields.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        fields.Add(sb.ToString());
        return fields;
    }

    /// <summary>The records of a CSV text: a quoted field may hold a line break (JMeter quotes a multi-line message).</summary>
    internal static IEnumerable<string> CsvRecords(string text)
    {
        var start = 0;
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '"') quoted = !quoted;
            else if (text[i] == '\n' && !quoted)
            {
                yield return text[start..i].TrimEnd('\r');
                start = i + 1;
            }
        }
        if (start < text.Length) yield return text[start..].TrimEnd('\r');
    }

    private static Run JmeterCsv(string text, CancellationToken ct)
    {
        var run = new Run();
        using var records = CsvRecords(text.TrimStart('\uFEFF')).GetEnumerator();
        if (!records.MoveNext()) return run;
        var columns = CsvFields(records.Current);
        int Col(string n) => columns.FindIndex(c => c.Equals(n, StringComparison.OrdinalIgnoreCase));
        int ts = Col("timeStamp"), el = Col("elapsed"), lb = Col("label"), rc = Col("responseCode"),
            ok = Col("success"), fm = Col("failureMessage"), rm = Col("responseMessage");
        run.OutcomeUnknown = ok < 0;
        var i = 0;
        while (records.MoveNext())
        {
            if ((++i & 1023) == 0) ct.ThrowIfCancellationRequested();
            var line = records.Current;
            if (line.Length == 0) continue;
            var f = CsvFields(line);
            string? F(int c) => c >= 0 && c < f.Count ? f[c] : null;
            if (!double.TryParse(F(el), NumberStyles.Float, CultureInfo.InvariantCulture, out var elapsed)) { run.Unreadable++; continue; }
            var request = run.Of(Clean(F(lb), LabelChars));
            request.Durations.Add(elapsed);
            if (double.TryParse(F(ts), NumberStyles.Float, CultureInfo.InvariantCulture, out var start)) run.Span(start, start + elapsed);
            if (ok >= 0 && !string.Equals(F(ok), "true", StringComparison.OrdinalIgnoreCase))
                request.Fail(JmeterCause(F(rc), F(fm), F(rm)));
        }
        return run;
    }

    private static string JmeterCause(string? code, string? failure, string? message) =>
        Clean((string.IsNullOrWhiteSpace(code) ? "" : code + " ") + (string.IsNullOrWhiteSpace(failure) ? message : failure), MessageChars);

    private static Run JmeterXml(string xml, CancellationToken ct)
    {
        var run = new Run();
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
        reader.MoveToContent();
        var rootDepth = reader.Depth;
        var count = 0;
        while (reader.Read())
        {
            // Only the samples directly under <testResults>: a sample's children are its sub-requests (redirects,
            // embedded resources), already in its own time.
            if (reader.NodeType != XmlNodeType.Element || reader.Depth != rootDepth + 1
                || reader.LocalName is not ("httpSample" or "sample")) continue;
            if ((++count & 1023) == 0) ct.ThrowIfCancellationRequested();
            if (!double.TryParse(reader.GetAttribute("t"), NumberStyles.Float, CultureInfo.InvariantCulture, out var elapsed)) { run.Unreadable++; continue; }
            var request = run.Of(Clean(reader.GetAttribute("lb"), LabelChars));
            request.Durations.Add(elapsed);
            if (double.TryParse(reader.GetAttribute("ts"), NumberStyles.Float, CultureInfo.InvariantCulture, out var start)) run.Span(start, start + elapsed);
            if (reader.GetAttribute("s") is not { } success) run.OutcomeUnknown = true;
            else if (!string.Equals(success, "true", StringComparison.OrdinalIgnoreCase))
            {
                string? failure = null, code = reader.GetAttribute("rc"), message = reader.GetAttribute("rm");
                if (!reader.IsEmptyElement)
                    using (var sub = reader.ReadSubtree())
                        while (sub.Read())
                            if (sub.NodeType == XmlNodeType.Element && sub.LocalName == "failureMessage") { failure = sub.ReadElementContentAsString(); break; }
                request.Fail(JmeterCause(code, failure, message));
            }
        }
        return run;
    }

    // ── k6 ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>k6 <c>--out json</c>: one JSON object per line; a request is an <c>http_req_duration</c> point, its failure
    /// the <c>http_req_failed</c> point of the same request (the line after it, same tags).</summary>
    private static Run K6Points(string text, CancellationToken ct)
    {
        var run = new Run();
        var n = 0;
        foreach (var raw in text.Split('\n'))
        {
            if ((++n & 1023) == 0) ct.ThrowIfCancellationRequested();
            var line = raw.Trim();
            if (line.Length == 0) continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { run.Unreadable++; continue; }
            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object || Str(root, "type") != "Point") continue;
                var metric = Str(root, "metric");
                if (metric is not ("http_req_duration" or "http_req_failed")) continue;
                if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) continue;
                var tags = data.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Object ? t : default;
                var label = Clean(((tags.ValueKind == JsonValueKind.Object ? Str(tags, "method") : null) is { } m ? m + " " : "")
                                  + (tags.ValueKind == JsonValueKind.Object ? Str(tags, "name") ?? Str(tags, "url") : null), LabelChars);
                if (!data.TryGetProperty("value", out var v) || v.ValueKind != JsonValueKind.Number) { run.Unreadable++; continue; }
                var request = run.Of(label);
                if (metric == "http_req_duration")
                {
                    request.Durations.Add(v.GetDouble());
                    if (Str(data, "time") is { } time && DateTimeOffset.TryParse(time, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
                    {
                        var end = at.ToUnixTimeMilliseconds();
                        run.Span(end - v.GetDouble(), end);
                    }
                }
                else if (v.GetDouble() > 0)
                {
                    var status = tags.ValueKind == JsonValueKind.Object ? Str(tags, "status") : null;
                    var error = tags.ValueKind == JsonValueKind.Object ? Str(tags, "error") ?? Str(tags, "error_code") : null;
                    request.Fail(Clean(status is null or "0" ? error ?? "no response" : status + (error is null ? "" : " " + error), MessageChars));
                }
            }
        }
        return run;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        } : null;

    private static string Ms(double ms) => ms >= 10_000
        ? (ms / 1000).ToString("0.0", CultureInfo.InvariantCulture) + " s"
        : ms.ToString(ms >= 100 ? "0" : "0.#", CultureInfo.InvariantCulture) + " ms";

    /// <summary>A k6 summary: <c>--summary-export</c> (a metric's values at its top level, thresholds as booleans that say
    /// a failure) or <c>handleSummary</c>'s data (values under <c>values</c>, thresholds as <c>{ ok }</c>).</summary>
    private static string? K6Summary(string name, string text, int budget)
    {
        using var doc = JsonDocument.Parse(text);
        if (!doc.RootElement.TryGetProperty("metrics", out var metrics) || metrics.ValueKind != JsonValueKind.Object) return null;
        static JsonElement Values(JsonElement m) => m.TryGetProperty("values", out var v) && v.ValueKind == JsonValueKind.Object ? v : m;
        static double? Num(JsonElement m, string key) =>
            Values(m).TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
        if (!metrics.TryGetProperty("http_req_duration", out _) && !metrics.TryGetProperty("http_reqs", out _)) return null;

        var sb = new StringBuilder();
        double? reqs = metrics.TryGetProperty("http_reqs", out var hr) ? Num(hr, "count") : null;
        double? rate = metrics.TryGetProperty("http_reqs", out hr) ? Num(hr, "rate") : null;
        double? failed = metrics.TryGetProperty("http_req_failed", out var hf) ? (Num(hf, "rate") ?? Num(hf, "value")) : null;
        sb.Append(CultureInfo.InvariantCulture, $"[{name}: a k6 summary")
          .Append(reqs is { } c ? string.Create(CultureInfo.InvariantCulture, $", {c:0} requests") : "")
          .Append(rate is { } r ? string.Create(CultureInfo.InvariantCulture, $", {r:0.#} requests/s") : "")
          .Append(failed is { } f ? string.Create(CultureInfo.InvariantCulture, $", {f * 100:0.##} % failed") : "")
          .Append(". Percentiles are k6's own. Failed thresholds first, then each request k6 kept a sub-metric for. Read it by lines (start_line) for the raw JSON.]\n");

        // Thresholds: export → "expr": true when it failed; handleSummary → "expr": { "ok": false }.
        var crossed = new List<string>();
        var held = 0;
        foreach (var metric in metrics.EnumerateObject())
        {
            if (!metric.Value.TryGetProperty("thresholds", out var th) || th.ValueKind != JsonValueKind.Object) continue;
            foreach (var t in th.EnumerateObject())
            {
                var failedThreshold = t.Value.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.Object when t.Value.TryGetProperty("ok", out var ok) => ok.ValueKind == JsonValueKind.False,
                    _ => false,
                };
                if (failedThreshold) crossed.Add($"{Clean(metric.Name, LabelChars)}: {Clean(t.Name, LabelChars)}");
                else held++;
            }
        }
        if (crossed.Count > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $"\n## Thresholds crossed ({crossed.Count}, {held} held)\n");
            var told = 0;
            foreach (var t in crossed)
            {
                if (told > 0 && sb.Length + t.Length > budget / 2) break;   // half the page at most: the requests follow
                sb.Append("✗ ").Append(t).Append('\n');
                told++;
            }
            if (told < crossed.Count) sb.Append(CultureInfo.InvariantCulture, $"… {crossed.Count - told} more crossed threshold(s), not listed.\n");
        }
        else if (held > 0) sb.Append(CultureInfo.InvariantCulture, $"\nEvery threshold held ({held}).\n");

        string Durations(JsonElement m)
        {
            var parts = new List<string>();
            foreach (var key in new[] { "med", "p(90)", "p(95)", "p(99)", "max" })
                if (Num(m, key) is { } v) parts.Add($"{(key == "med" ? "p50" : key.Replace("(", "").Replace(")", ""))} {Ms(v)}");
            return string.Join(" · ", parts);
        }
        if (metrics.TryGetProperty("http_req_duration", out var all))
            sb.Append("\nAll requests: ").Append(Durations(all)).Append('\n');

        // Per request: sub-metrics "http_req_duration{name:…}" and "http_req_failed{name:…}".
        var rows = new List<(string Tag, string Line, double Fail, double P95)>();
        foreach (var metric in metrics.EnumerateObject())
        {
            if (!metric.Name.StartsWith("http_req_duration{", StringComparison.Ordinal)) continue;
            var tag = metric.Name["http_req_duration".Length..];
            if (tag is "{expected_response:true}") continue;
            var fail = metrics.TryGetProperty("http_req_failed" + tag, out var sf) ? (Num(sf, "rate") ?? Num(sf, "value")) : null;
            rows.Add((tag, $"- {Clean(tag.Trim('{', '}'), LabelChars)} — {Durations(metric.Value)}"
                         + (fail is { } fr ? string.Create(CultureInfo.InvariantCulture, $" · {fr * 100:0.##} % failed") : ""),
                      fail ?? 0, Num(metric.Value, "p(95)") ?? 0));
        }
        if (rows.Count > 0)
        {
            sb.Append("\n## Requests, the most failed then the slowest first\n");
            var said = 0;
            foreach (var row in rows.OrderByDescending(r => r.Fail).ThenByDescending(r => r.P95).ThenBy(r => r.Tag, StringComparer.Ordinal))
            {
                if (said == MaxRequests || sb.Length > budget) break;
                sb.Append(row.Line).Append('\n');
                said++;
            }
            if (said < rows.Count) sb.Append(CultureInfo.InvariantCulture, $"… {rows.Count - said} more request(s), not listed.\n");
        }
        else sb.Append("\nNo per-request figures: k6 keeps them only for a tag a threshold names (http_req_duration{name:…}); its --out json output has them all.\n");

        // Checks that failed somewhere in the groups.
        var checks = new List<string>();
        void Walk(JsonElement group, string path)
        {
            if (group.TryGetProperty("checks", out var cs))
            {
                var items = cs.ValueKind == JsonValueKind.Array ? cs.EnumerateArray().ToList()
                          : cs.ValueKind == JsonValueKind.Object ? cs.EnumerateObject().Select(p => p.Value).ToList() : [];
                foreach (var check in items)
                    if (check.ValueKind == JsonValueKind.Object && check.TryGetProperty("fails", out var fl) && fl.ValueKind == JsonValueKind.Number && fl.GetDouble() > 0)
                        checks.Add(string.Create(CultureInfo.InvariantCulture,
                            $"✗ {path}{Clean(Str(check, "name"), LabelChars)}: {fl.GetDouble():0} failed, {(check.TryGetProperty("passes", out var ps) && ps.ValueKind == JsonValueKind.Number ? ps.GetDouble() : 0):0} passed"));
            }
            if (group.TryGetProperty("groups", out var gs))
            {
                var items = gs.ValueKind == JsonValueKind.Array ? gs.EnumerateArray().ToList()
                          : gs.ValueKind == JsonValueKind.Object ? gs.EnumerateObject().Select(p => p.Value).ToList() : [];
                foreach (var g in items.Where(g => g.ValueKind == JsonValueKind.Object))
                    Walk(g, path + Clean(Str(g, "name"), LabelChars) + " › ");
            }
        }
        if (doc.RootElement.TryGetProperty("root_group", out var rootGroup) && rootGroup.ValueKind == JsonValueKind.Object) Walk(rootGroup, "");
        if (checks.Count > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $"\n## Checks that failed ({checks.Count})\n");
            var said = 0;
            foreach (var check in checks)
            {
                if (sb.Length > budget) break;
                sb.Append(check).Append('\n');
                said++;
            }
            if (said < checks.Count) sb.Append(CultureInfo.InvariantCulture, $"… {checks.Count - said} more failed check(s), not listed: the page is full.\n");
        }
        return sb.ToString();
    }

    // ── Per-request output, rendered ────────────────────────────────────────────────────────────────────────────────

    /// <summary>The value at <paramref name="p"/> (0-100) of sorted <paramref name="sorted"/>, by nearest rank.</summary>
    internal static double Percentile(List<double> sorted, double p) =>
        sorted[Math.Clamp((int)Math.Ceiling(p / 100 * sorted.Count) - 1, 0, sorted.Count - 1)];

    private static string? Render(string name, string format, Run run, int budget)
    {
        var total = run.Requests.Values.Sum(r => r.Durations.Count);
        if (total == 0) return null;
        // Only requests that were timed: a failure with no duration has no row to be told in.
        var errors = run.Requests.Values.Where(r => r.Durations.Count > 0).Sum(r => r.Errors);
        var seconds = double.IsNaN(run.Start) ? double.NaN : (run.End - run.Start) / 1000;
        var all = run.Requests.Values.SelectMany(r => r.Durations).Order().ToList();
        var sb = new StringBuilder(string.Create(CultureInfo.InvariantCulture,
            $"[{name}: a load test result ({format}), {total} requests, {errors} failed ({100.0 * errors / total:0.##} %)"));
        if (seconds > 0) sb.Append(CultureInfo.InvariantCulture, $", over {seconds:0.#} s: {total / seconds:0.#} requests/s");
        sb.Append(". Percentiles computed here by nearest rank. Requests the most failed, then the slowest, first.");
        if (run.Unreadable > 0) sb.Append(CultureInfo.InvariantCulture, $" {run.Unreadable} line(s) not read: left out.");
        if (run.OutcomeUnknown) sb.Append(" ⚠ Some samples record no success or failure (JMeter's success field not saved): their failures are unknown, not zero.");
        sb.Append(" Read it by lines (start_line) for the raw file.]\n");
        sb.Append(CultureInfo.InvariantCulture,
            $"\nAll requests: p50 {Ms(Percentile(all, 50))} · p90 {Ms(Percentile(all, 90))} · p95 {Ms(Percentile(all, 95))} · p99 {Ms(Percentile(all, 99))} · max {Ms(all[^1])}\n");

        var rows = run.Requests.Select(r => (Label: r.Key, Req: r.Value, Sorted: r.Value.Durations.Order().ToList()))
                              .Where(r => r.Sorted.Count > 0)
                              .OrderByDescending(r => (double)r.Req.Errors / r.Sorted.Count)
                              .ThenByDescending(r => Percentile(r.Sorted, 95))
                              .ThenBy(r => r.Label, StringComparer.Ordinal).ToList();
        sb.Append("\n## Requests\n");
        var said = 0;
        foreach (var (label, req, sorted) in rows)
        {
            if (said == MaxRequests || sb.Length > budget) break;
            said++;
            sb.Append(CultureInfo.InvariantCulture, $"- {(label.Length == 0 ? "(no label)" : label)} — {sorted.Count} req");
            if (seconds > 0) sb.Append(CultureInfo.InvariantCulture, $" ({sorted.Count / seconds:0.#}/s)");
            sb.Append(CultureInfo.InvariantCulture, $", {req.Errors} failed ({100.0 * req.Errors / sorted.Count:0.##} %)")
              .Append(CultureInfo.InvariantCulture, $" · p50 {Ms(Percentile(sorted, 50))} · p95 {Ms(Percentile(sorted, 95))} · p99 {Ms(Percentile(sorted, 99))} · max {Ms(sorted[^1])}\n");
            foreach (var (cause, count) in req.Causes.OrderByDescending(c => c.Value).ThenBy(c => c.Key, StringComparer.Ordinal).Take(MaxMessages))
                sb.Append(CultureInfo.InvariantCulture, $"  ✗ {count}× {(cause.Length == 0 ? "(no message)" : cause)}\n");
            if (req.Causes.Count > MaxMessages) sb.Append(CultureInfo.InvariantCulture, $"  … {req.Causes.Count - MaxMessages} more cause(s)\n");
        }
        if (said < rows.Count) sb.Append(CultureInfo.InvariantCulture, $"… {rows.Count - said} more request(s), not listed.\n");
        return sb.ToString();
    }
}
