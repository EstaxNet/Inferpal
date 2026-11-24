using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Inferpal.Services.Documents;

/// <summary>
/// A log line that is a record rather than text: a JSON object (Serilog's CLEF and JSON formatters, pino, bunyan, zap,
/// structlog, ECS, the <c>ILogger</c> JSON console, winston, log4j2) or logfmt (<c>time=… level=… msg=…</c>). It is an entry
/// of its own when it carries a time that reads as one or a level; anything else stays text (a data <c>.jsonl</c> is no log).
/// </summary>
/// <param name="Level">The level's rank in <see cref="Tools.LogFilter.LevelNames"/>; <c>null</c> when the record has none.</param>
/// <param name="Date">The date, as written (an epoch number: UTC).</param>
/// <param name="Time">The time of day, as written (an epoch number: UTC).</param>
/// <param name="When">The moment as shown: the record's own text, or an epoch number written as a UTC time.</param>
/// <param name="Epoch">Whether the time was an epoch number, read as UTC.</param>
/// <param name="Message">The message, a template rendered with the record's properties.</param>
/// <param name="Template">What makes two records "the same message": the template when the format writes one, the message
/// with its numbers and identifiers neutralised otherwise.</param>
/// <param name="Exception">The exception or stack trace the record carries.</param>
/// <param name="Fields">The record's other fields, <c>key=value</c>, bounded.</param>
/// <param name="Format">"JSON" or "logfmt".</param>
internal sealed record StructuredLogLine(
    int? Level, DateOnly? Date, TimeOnly? Time, string? When, bool Epoch, string Message, string Template, string? Exception,
    string Fields, string Format)
{
    /// <summary>The most characters of a record's other fields shown after its message.</summary>
    internal const int MaxFieldChars = 240;

    // Read in this order: the first key a record has wins.
    private static readonly string[] TimeKeys = ["@t", "@timestamp", "timestamp", "Timestamp", "time", "ts", "t", "timeMillis", "datetime"];
    private static readonly string[] LevelKeys = ["@l", "level", "lvl", "severity", "Level", "LogLevel", "levelname", "log.level", "loglevel"];
    // "log": Docker's json-file driver; "short_message": GELF; "body": OpenTelemetry's log records.
    private static readonly string[] MessageKeys = ["@m", "RenderedMessage", "msg", "message", "Message", "event", "log", "short_message", "body"];
    private static readonly string[] TemplateKeys = ["@mt", "MessageTemplate"];
    private static readonly string[] ExceptionKeys =
        ["@x", "Exception", "exception", "exc_info", "error.stack_trace", "stack", "stack_trace", "stacktrace", "err", "error", "thrown"];

    // Fields that say nothing about the entry: a format's version, the process, the renderings of a template.
    private static readonly HashSet<string> Noise = new(StringComparer.Ordinal)
        { "v", "pid", "hostname", "@r", "@i", "ecs.version", "State", "Properties", "EventId", "instant", "endOfBatch", "threadId", "threadPriority" };

    private static readonly Regex IsoStamp = new(@"^(\d{4}-\d{2}-\d{2})[T ](\d{2}:\d{2}(?::\d{2})?)", RegexOptions.CultureInvariant, RegexBudget.Default);
    private static readonly Regex LogfmtStart = new(@"^[A-Za-z_@][\w.@-]*=", RegexOptions.CultureInvariant, RegexBudget.Default);
    private static readonly Regex Hole = new(@"\{\{|\}\}|\{[@$]?(?<name>[\w.]+)(?:[,:][^}]*)?\}", RegexOptions.CultureInvariant, RegexBudget.Default);
    // What varies between two records of one message: a GUID, a hexadecimal identifier, a number standing alone ("sha256" and
    // "utf8" are words, not numbers). ⚠ Linear: each alternative starts at a word boundary and checks the run it is on once —
    // a backtracking "[hex]*\d[hex]*[a-f]" was quadratic on a long run of digits and ran out of its budget.
    private static readonly Regex Variable = new(
        @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}|\b0x[0-9a-fA-F]+\b"
        + @"|\b(?=[0-9a-fA-F]*\d)(?=[0-9a-fA-F]*[a-fA-F])[0-9a-fA-F]{6,}\b|\b\d+(?:\.\d+)?\b",
        RegexOptions.CultureInvariant, RegexBudget.Default);

    /// <summary>The most characters of a message whose variable parts are set aside to find its template.</summary>
    private const int MaxNeutralChars = 2_000;

    /// <summary>A line longer than this is never taken for a record when a file is sampled: a minified JSON file is one line.</summary>
    private const int MaxSampledLine = 16 * 1024;

    /// <summary>The record <paramref name="line"/> is, or <c>null</c> when it is text.</summary>
    internal static StructuredLogLine? Read(string line)
    {
        var text = line.Trim();
        if (text.Length < 2) return null;
        if (text[0] == '{') return text[^1] == '}' ? FromJson(text, "JSON") : null;
        return LogfmtStart.IsMatch(text) && LogfmtAsJson(text) is { } json ? FromJson(json, "logfmt") : null;
    }

    /// <summary>The structured format most of the first lines of <paramref name="text"/> are written in, at least
    /// <paramref name="minRecords"/> of them; <c>null</c> when they are text.</summary>
    /// <param name="text">The file's text.</param>
    /// <param name="minRecords">The fewest records that make it a log of records.</param>
    /// <param name="maxLine">The longest line parsed: a plain read samples a file it may never filter (a minified JSON file is
    /// one line), a filtered read parses every record anyway.</param>
    internal static string? FormatOf(string text, int minRecords = 2, int maxLine = MaxSampledLine)
    {
        int lines = 0, records = 0, at = 0;
        string? format = null;
        // The first lines only, never the whole file split: a log runs to hundreds of megabytes.
        while (at < text.Length && lines < 50)
        {
            var end = text.IndexOf('\n', at);
            if (end < 0) end = text.Length;
            var line = text.AsSpan(at, end - at).TrimEnd('\r');
            at = end + 1;
            if (line.IsWhiteSpace()) continue;
            lines++;
            if (line.Length <= maxLine && Read(line.ToString()) is { } record) { records++; format ??= record.Format; }
        }
        return records >= minRecords && records * 2 > lines ? format : null;
    }

    /// <summary>The record as a log line: when, level, message, fields; the exception under it.</summary>
    internal string Render()
    {
        var sb = new StringBuilder();
        if (When is not null) sb.Append(When).Append(' ');
        if (Level is { } level) sb.Append(Tools.LogFilter.LevelNames[level].ToUpperInvariant()).Append(' ');
        sb.Append(Message);
        if (Fields.Length > 0) sb.Append("  ").Append(Fields);
        if (Exception is { Length: > 0 } exception)
            foreach (var line in exception.Replace("\r\n", "\n").TrimEnd().Split('\n'))
                sb.Append("\n    ").Append(line);
        return sb.ToString();
    }

    /// <summary>Two records are the same message when this is equal: the level (Serilog's request log writes a 500 at Error
    /// under the template of the 200s at Information), the template, and the first line of the exception — two different
    /// failures under one template are not one. <c>null</c> for a record with no message: nothing says it repeats another.</summary>
    internal string? GroupKey =>
        Message.Length == 0 ? null
        : $"{Level}\u0001{Template}\u0001{(Exception is { } e ? Neutral(e.Replace("\r\n", "\n").Split('\n')[0], "#") : "")}";

    // The text with what varies set aside; a text too long, or a match out of budget, is its own template.
    private static string Neutral(string text, string hole)
    {
        if (text.Length > MaxNeutralChars) return text;
        try { return Variable.Replace(text, hole); }
        catch (RegexMatchTimeoutException) { return text; }
    }

    private static StructuredLogLine? FromJson(string json, string format)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 }); }
        catch (JsonException) { return null; }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var used = new HashSet<string>(StringComparer.Ordinal);

            (DateOnly? date, TimeOnly? time, string? when, bool epoch) = (null, null, null, false);
            foreach (var key in TimeKeys)
                if (Property(root, key, used, mark: false) is { } v && TimeOf(v) is { } read)
                {
                    (date, time, when, epoch) = read;
                    used.Add(key);
                    break;
                }
            if (time is null && root.TryGetProperty("instant", out var instant) && instant.ValueKind == JsonValueKind.Object
                && instant.TryGetProperty("epochSecond", out var seconds) && TimeOf(seconds) is { } fromInstant)
                (date, time, when, epoch) = fromInstant;   // log4j2's JSON layout

            int? level = null;
            foreach (var key in LevelKeys)
                if (Property(root, key, used, mark: false) is { } v && LevelOf(v, key) is { } rank)
                {
                    level = rank;
                    Property(root, key, used, mark: true);
                    break;
                }
            // CLEF: an event with no level is Information (the format's specification).
            if (level is null && root.TryGetProperty("@t", out _) && (root.TryGetProperty("@mt", out _) || root.TryGetProperty("@m", out _)))
                level = Tools.LogFilter.RankOf("information");
            // GELF: a syslog severity, 0 (emergency) to 7 (debug).
            if (level is null && root.TryGetProperty("short_message", out _) && root.TryGetProperty("level", out var gelf)
                && gelf.ValueKind == JsonValueKind.Number && gelf.TryGetInt32(out var severity) && severity is >= 0 and <= 7)
            {
                level = severity switch { <= 2 => 5, 3 => 4, 4 => 3, <= 6 => 2, _ => 1 };
                used.Add("level");
            }

            var properties = root.TryGetProperty("Properties", out var p) && p.ValueKind == JsonValueKind.Object ? p
                           : root.TryGetProperty("State", out var s) && s.ValueKind == JsonValueKind.Object ? s : root;
            var template = TemplateKeys.Select(k => Text(root, k, used)).FirstOrDefault(t => t is not null)
                           ?? (properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty("{OriginalFormat}", out var of)
                               && of.ValueKind == JsonValueKind.String ? of.GetString() : null);
            var message = MessageKeys.Select(k => Text(root, k, used)).FirstOrDefault(m => m is not null);
            // Rendered even when the record carries its message: the properties it fills are then not repeated as fields.
            if (template is not null && Render(template, properties, used) is var rendered) message ??= rendered;
            // A record has a time, or a level and a message: {"course":"French","level":20} is data.
            if (time is null && (level is null || message is null)) return null;
            message = OneLine(message ?? "").Trim();

            var exception = ExceptionOf(root, used);
            return new(level, date, time, when, epoch, message, template ?? Neutral(message, "{n}"), exception, FieldsOf(root, used), format);
        }
    }

    // A key with a dot is read flat ("log.level") or nested ({"log":{"level":…}}).
    private static JsonElement? Property(JsonElement root, string key, HashSet<string> used, bool mark)
    {
        if (root.TryGetProperty(key, out var v))
        {
            if (mark) used.Add(key);
            return v;
        }
        var dot = key.IndexOf('.');
        if (dot > 0 && root.TryGetProperty(key[..dot], out var parent) && parent.ValueKind == JsonValueKind.Object
            && parent.TryGetProperty(key[(dot + 1)..], out var nested))
        {
            if (mark) used.Add(key);   // its parent's other members are still shown, as parent.member
            return nested;
        }
        return null;
    }

    private static string? Text(JsonElement root, string key, HashSet<string> used)
    {
        if (!root.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.String) return null;
        used.Add(key);
        return v.GetString();
    }

    private static int? LevelOf(JsonElement v, string key) => v.ValueKind switch
    {
        JsonValueKind.String => Tools.LogFilter.RankOf(v.GetString() ?? ""),
        // pino and bunyan: 10 trace, 20 debug, 30 info, 40 warn, 50 error, 60 fatal.
        JsonValueKind.Number when key == "level" && v.TryGetInt32(out var n) && n is >= 10 and <= 60 && n % 10 == 0 => n / 10 - 1,
        _ => null,
    };

    // A time as written (ISO), or an epoch number in seconds, milliseconds or nanoseconds of a plausible year, read as UTC.
    private static (DateOnly?, TimeOnly?, string?, bool)? TimeOf(JsonElement v)
    {
        if (v.ValueKind == JsonValueKind.String)
        {
            var text = v.GetString() ?? "";
            var m = IsoStamp.Match(text);
            if (!m.Success
                || !DateOnly.TryParseExact(m.Groups[1].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                || !TimeOnly.TryParseExact(m.Groups[2].Value, ["HH:mm:ss", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
                return null;
            return (d, t, text, false);
        }
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out var n) || n <= 0) return null;
        // Seconds, milliseconds, microseconds or nanoseconds, by magnitude; then a plausible moment only, from 2000 to a year
        // from now: a duration in a "time" field is no date.
        var ms = n < 1e11 ? n * 1000 : n < 1e14 ? n : n < 1e17 ? n / 1e3 : n / 1e6;
        if (ms < 946_684_800_000 || ms > DateTimeOffset.UtcNow.AddYears(1).ToUnixTimeMilliseconds()) return null;
        var at = DateTimeOffset.FromUnixTimeMilliseconds((long)ms).UtcDateTime;
        return (DateOnly.FromDateTime(at), TimeOnly.FromDateTime(at), at.ToString("yyyy-MM-dd HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture), true);
    }

    // A message template's holes filled with the record's properties: "{host}", "{@user}", "{elapsed:0.00}"; "{{" is a brace.
    private static string Render(string template, JsonElement properties, HashSet<string> used) =>
        Hole.Replace(template, m =>
        {
            if (m.Value == "{{") return "{";
            if (m.Value == "}}") return "}";
            var name = m.Groups["name"].Value;
            if (properties.ValueKind != JsonValueKind.Object || !properties.TryGetProperty(name, out var value)) return m.Value;
            used.Add(name);
            return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText();
        });

    /// <summary>The record's stack (<c>@x</c>, <c>exception</c>, <c>stack</c>…) and its error (<c>err</c>, <c>error</c>) — zap
    /// writes the error's text and the logging call's stack apart, and both say what went wrong.</summary>
    private static string? ExceptionOf(JsonElement root, HashSet<string> used)
    {
        string? First(IEnumerable<string> keys)
        {
            foreach (var key in keys)
            {
                if (Property(root, key, used, mark: false) is not { } v) continue;
                var text = v.ValueKind switch
                {
                    JsonValueKind.String => v.GetString(),
                    JsonValueKind.Object => FromErrorObject(v),
                    JsonValueKind.Array => string.Join('\n', v.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : e.GetRawText())),
                    _ => null,
                };
                if (string.IsNullOrWhiteSpace(text)) continue;
                Property(root, key, used, mark: true);
                if (key.IndexOf('.') is > 0 and var dot) used.Add(key[..dot]);
                return text;
            }
            return null;
        }
        var stack = First(ExceptionKeys.Where(k => k is not ("err" or "error")));
        var error = First(["err", "error"]);
        if (error is null || stack is null) return error ?? stack;
        if (error.Contains(stack, StringComparison.Ordinal)) return error;
        return stack.Contains(error.Split('\n')[0], StringComparison.Ordinal) ? stack : error + "\n" + stack;
    }

    // pino's err, ECS's nested error, log4j2's thrown: a type, a message and a stack, each optional.
    private static string? FromErrorObject(JsonElement error)
    {
        string? Field(params string[] names) => names.Select(n => error.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null)
                                                     .FirstOrDefault(s => !string.IsNullOrEmpty(s));
        var type = Field("type", "name", "kind");
        var message = Field("message", "localizedMessage");
        var stack = Field("stack", "stack_trace", "stacktrace", "extendedStackTrace");
        if (stack is null && error.TryGetProperty("extendedStackTrace", out var frames) && frames.ValueKind == JsonValueKind.Array)
            stack = string.Join('\n', frames.EnumerateArray().Select(f =>
                f.ValueKind != JsonValueKind.Object ? f.GetRawText()
                : $"at {Get(f, "class")}.{Get(f, "method")}({Get(f, "file")}:{Get(f, "line")})"));
        var head = type is null ? message : message is null ? type : $"{type}: {message}";
        if (stack is null) return head;
        return head is null || stack.Contains(message ?? head, StringComparison.Ordinal) ? stack : head + "\n" + stack;
    }

    private static string Get(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.GetRawText()) : "?";

    private static string FieldsOf(JsonElement root, HashSet<string> used)
    {
        var sb = new StringBuilder();
        // Serilog's JSON formatter keeps the event's properties in an object of their own: shown as the record's fields.
        var all = root.EnumerateObject().Concat(root.TryGetProperty("Properties", out var p) && p.ValueKind == JsonValueKind.Object
            ? p.EnumerateObject() : Enumerable.Empty<JsonProperty>());
        var pairs = new List<(string Name, JsonElement Value)>();
        foreach (var property in all)
        {
            if (used.Contains(property.Name) || Noise.Contains(property.Name)) continue;
            // An object one of whose members was read (ECS's {"log":{"level":…}}): its other members, never it again whole.
            if (property.Value.ValueKind == JsonValueKind.Object && used.Any(u => u.StartsWith(property.Name + ".", StringComparison.Ordinal)))
                pairs.AddRange(property.Value.EnumerateObject().Where(m => !used.Contains(property.Name + "." + m.Name))
                                                               .Select(m => (property.Name + "." + m.Name, m.Value)));
            else pairs.Add((property.Name, property.Value));
        }
        foreach (var (name, element) in pairs)
        {
            if (sb.Length > 0) sb.Append(' ');
            var value = element.ValueKind == JsonValueKind.String ? element.GetString() ?? "" : element.GetRawText();
            // Quoted as written, never escaped: "échec" and C:\Program Files read as they are, and can be searched for.
            sb.Append(name).Append('=').Append(element.ValueKind == JsonValueKind.String && value.Contains(' ') ? $"\"{value}\"" : value);
            if (sb.Length > MaxFieldChars) return OneLine(sb.ToString(0, MaxFieldChars)) + " …";
        }
        return OneLine(sb.ToString());
    }

    private static string OneLine(string text) => text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');

    // logfmt read into a JSON object of strings: two key=value pairs at least, or it is a sentence that holds an '='.
    private static string? LogfmtAsJson(string line)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            var pairs = 0;
            var i = 0;
            while (i < line.Length)
            {
                while (i < line.Length && line[i] == ' ') i++;
                var keyStart = i;
                while (i < line.Length && line[i] is not (' ' or '=')) i++;
                if (i == keyStart) break;
                var key = line[keyStart..i];
                if (i >= line.Length || line[i] != '=') continue;   // a bare flag
                i++;
                string value;
                if (i < line.Length && line[i] == '"')
                {
                    var sb = new StringBuilder();
                    i++;
                    while (i < line.Length && line[i] != '"')
                    {
                        if (line[i] == '\\' && i + 1 < line.Length) { i++; sb.Append(line[i] switch { 'n' => '\n', 't' => '\t', var c => c }); }
                        else sb.Append(line[i]);
                        i++;
                    }
                    if (i >= line.Length) return null;   // an unclosed quote: not logfmt
                    i++;
                    value = sb.ToString();
                }
                else
                {
                    var valueStart = i;
                    while (i < line.Length && line[i] != ' ') i++;
                    value = line[valueStart..i];
                }
                writer.WriteString(key, value);
                pairs++;
            }
            if (pairs < 2) return null;
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
