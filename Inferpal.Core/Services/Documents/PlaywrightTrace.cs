using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Inferpal.Services.Documents;

/// <summary>
/// Reads a Playwright trace (<c>trace.zip</c>) as what someone looking at a failed test wants from it: the failure (the step,
/// its line in the test, the message, the end of its call log, the page as the failure saw it), then the steps in order, the
/// console, the page's errors and the network — and the last picture of the page before the failure, for a model that sees.
/// </summary>
/// <remarks>
/// ⚠ Recognised by what the zip holds (<c>test.trace</c>, <c>trace.trace</c>, <c>N-trace.trace</c>), never by its name: any
/// other zip stays an archive. ⚠ The zip is data from anywhere: what is decompressed is bounded (<see cref="MaxRead"/>), each
/// line and each list too; DOM snapshots (<c>frame-snapshot</c>) are skipped unread.
/// </remarks>
internal static class PlaywrightTrace
{
    /// <summary>The most bytes decompressed from one trace, all entries together: past it, reading stops (a zip bomb, said).</summary>
    internal const long MaxRead = 64L * 1024 * 1024;

    /// <summary>The most entries a trace may declare: a trace holds a few files and its pictures; past it, the zip is not one.</summary>
    private const long MaxEntries = 100_000;

    /// <summary>The longest line parsed: longer ones are DOM snapshots or bodies, skipped.</summary>
    private const int MaxLine = 256 * 1024;

    /// <summary>How many lines each list keeps; past it, the rest is counted and said.</summary>
    internal const int MaxSteps = 200, MaxConsole = 150, MaxNetwork = 150, MaxPageErrors = 50, MaxCallLog = 10, MaxContextLines = 60;

    /// <summary>The largest picture taken out of the trace for the model.</summary>
    private const int MaxFrameBytes = 4 * 1024 * 1024;

    /// <summary>What a trace reads as: its text, and the picture of the page at the failure (or at the end) when it has one.</summary>
    internal sealed record Reading(string Text, byte[]? Frame, string? FrameName);

    private static readonly Regex Ansi = new(@"\x1B\[[0-9;]*[A-Za-z]", RegexOptions.CultureInvariant, RegexBudget.Default);

    /// <summary>Whether the file at <paramref name="path"/> is a zip holding a Playwright trace.</summary>
    internal static bool IsTrace(string path)
    {
        if (!path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return false;
        // Its directory counted before it is opened: a few megabytes can declare millions of entries, all of them loaded by
        // the zip reader, for every .zip read or attached.
        if (Archives.DeclaredZipEntries(path) > MaxEntries) return false;
        try
        {
            using var zip = ZipFile.OpenRead(path);
            return zip.Entries.Take(10_000).Any(e => IsTraceEntry(e.FullName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsTraceEntry(string name) =>
        name == "test.trace" || name == "trace.trace" || Regex.IsMatch(name, @"^\d+-trace\.trace$", RegexOptions.CultureInvariant, RegexBudget.Default);

    private sealed class Step
    {
        public required string Id;
        public string? Parent;
        public required string Title;
        public required string Method;
        public double Start;
        public double? End;
        public string? Error;
        public string? Where;
        public List<string> Attachments = [];
    }

    /// <summary>A stream that counts what it gives and stops past <see cref="MaxRead"/> for the whole trace.</summary>
    private sealed class Budget(Stream inner, long[] spent) : Stream
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            spent[0] += read;
            if (spent[0] > MaxRead) throw new DocumentLimitException($"more than {MaxRead / (1024 * 1024)} MB decompressed");
            return read;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }

    /// <summary>Each JSON line of <paramref name="entry"/>, parsed; a line too long or not JSON is skipped.</summary>
    private static IEnumerable<JsonElement> Lines(ZipArchiveEntry entry, long[] spent, CancellationToken ct)
    {
        using var reader = new StreamReader(new Budget(entry.Open(), spent), Encoding.UTF8);
        var buffer = new StringBuilder();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            // Read by hand: ReadLine would build a DOM snapshot of megabytes into one string before it could be skipped.
            buffer.Clear();
            var tooLong = false;
            int c;
            while ((c = reader.Read()) >= 0 && c != '\n')
            {
                if (buffer.Length < MaxLine) buffer.Append((char)c);
                else tooLong = true;
            }
            if (c < 0 && buffer.Length == 0) yield break;
            if (tooLong || buffer.Length == 0 || IsSnapshot(buffer)) { if (c < 0) yield break; continue; }
            JsonElement element;
            try
            {
                using var doc = JsonDocument.Parse(buffer.ToString());
                element = doc.RootElement.Clone();
            }
            catch (JsonException) { if (c < 0) yield break; continue; }
            if (element.ValueKind == JsonValueKind.Object) yield return element;
            if (c < 0) yield break;
        }
    }

    /// <summary>A DOM snapshot's line, by its start: never parsed.</summary>
    private static bool IsSnapshot(StringBuilder line)
    {
        var start = line.ToString(0, Math.Min(line.Length, 48));
        return start.Contains("\"frame-snapshot\"", StringComparison.Ordinal);
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static JsonElement Prop(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;

    /// <summary>Text from the trace, as plain one-line-at-a-time text: ANSI colours out, control characters made visible.</summary>
    private static string Clean(string? text, int max = 2000)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var plain = Ansi.Replace(text, string.Empty);
        var chars = plain.Select(ch => ch is '\n' or '\t' ? ch : ch < ' ' ? '?' : ch).ToArray();
        var cleaned = new string(chars).Replace("\r", string.Empty, StringComparison.Ordinal);
        return cleaned.Length > max ? cleaned[..max] + " …" : cleaned;
    }

    private static string OneLine(string? text, int max = 300) => Clean(text, max).Replace('\n', ' ').Trim();

    /// <summary>The test's own place in a step's stack: the first frame outside node_modules, as file:line.</summary>
    private static string? Where(JsonElement step)
    {
        var stack = Prop(step, "stack");
        if (stack.ValueKind != JsonValueKind.Array) return null;
        foreach (var frame in stack.EnumerateArray().Take(20))
        {
            var file = Str(frame, "file");
            if (file is null || file.Contains("node_modules", StringComparison.Ordinal) || file.EndsWith('#')) continue;
            var line = Num(frame, "line");
            return OneLine(Path.GetFileName(file.Replace('\\', '/')), 120) + (line is { } l ? string.Create(CultureInfo.InvariantCulture, $":{l:0}") : string.Empty);
        }
        return null;
    }

    /// <summary>The trace in the zip at <paramref name="path"/>, read.</summary>
    internal static Reading Read(string path, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(path);
        // A zip may hold two entries of one name: the first is read.
        var tooMany = zip.Entries.Count > 10_000;
        var entries = zip.Entries.Take(10_000).GroupBy(e => e.FullName, StringComparer.Ordinal)
                                 .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        string? title = null, version = null, browser = null, platform = null;
        double? origin = null;
        double wallTime = 0, wallMonotonic = 0;
        var steps = new Dictionary<string, Step>(StringComparer.Ordinal);
        var order = new List<Step>();
        var testErrors = new List<string>();
        var callLogs = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var console = new List<(double At, string Line)>();
        var pageErrors = new List<(double At, string Line)>();
        var frames = new List<(double At, string File)>();
        var network = new List<(DateTimeOffset? At, string Line)>();
        var fromRunner = entries.ContainsKey("test.trace");
        // Each part of the trace has its own budget: DOM snapshots in the library traces never starve the network or the test.
        long[] runnerBudget = [0], networkBudget = [0], libraryBudget = [0];
        var stopped = new List<string>();
        int stepsBeyond = 0, consoleBeyond = 0, errorsBeyond = 0, networkBeyond = 0;
        var damaged = new List<string>();

        // One entry, its lines handed on; a stop or a damage is said in the header, what was read before it stays.
        void Each(ZipArchiveEntry entry, long[] budget, Action<JsonElement> handle)
        {
            try
            {
                foreach (var e in Lines(entry, budget, ct)) handle(e);
            }
            catch (DocumentLimitException) { stopped.Add(entry.FullName); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException)
            {
                Diagnostics.Swallow("PlaywrightTrace.Entry", ex);
                damaged.Add(entry.FullName);
            }
        }

        void Context(JsonElement e)
        {
            title ??= Str(e, "title");
            version ??= Str(e, "playwrightVersion");
            if (Str(e, "browserName") is { Length: > 0 } b) browser ??= b;
            platform ??= Str(e, "platform");
            if (Num(e, "wallTime") is { } wall && Num(e, "monotonicTime") is { } mono && wallTime == 0) { wallTime = wall; wallMonotonic = mono; }
        }

        void Before(JsonElement e, bool runner)
        {
            if (Str(e, "callId") is not { } id || steps.ContainsKey(id)) return;
            if (steps.Count >= 100_000) { stepsBeyond++; return; }
            var start = Num(e, "startTime") ?? 0;
            origin = origin is { } o ? Math.Min(o, start) : start;
            var label = Str(e, "title") ?? $"{Str(e, "class")}.{Str(e, "method")}";
            var subtitle = Str(e, "subtitle");
            if (subtitle is null && !runner)
            {
                var parameters = Prop(e, "params");
                subtitle = Str(parameters, "selector") ?? Str(parameters, "url");
            }
            var step = new Step
            {
                Id = id, Parent = Str(e, "parentId"), Title = OneLine(label + (subtitle is { Length: > 0 } ? " " + subtitle : string.Empty)),
                Method = Str(e, "method") ?? string.Empty, Start = start, Where = Where(e),
            };
            steps[id] = step;
            order.Add(step);
        }

        void After(JsonElement e)
        {
            if (Str(e, "callId") is not { } id || !steps.TryGetValue(id, out var step)) return;
            step.End = Num(e, "endTime");
            var error = Prop(e, "error");
            if (error.ValueKind == JsonValueKind.Object) step.Error = Clean(Str(error, "message"));
            var attachments = Prop(e, "attachments");
            if (attachments.ValueKind == JsonValueKind.Array)
                foreach (var a in attachments.EnumerateArray().Take(20))
                    if (Str(a, "name") == "error-context" && Str(a, "file") is { } file) step.Attachments.Add(file);
        }

    // The test runner's steps first: their callIds are the library's, so the library's call logs join them.
        if (entries.TryGetValue("test.trace", out var runner))
            Each(runner, runnerBudget, e =>
            {
                switch (Str(e, "type"))
                {
                    case "context-options": Context(e); break;
                    case "before": Before(e, runner: true); break;
                    case "after": After(e); break;
                    case "error": if (testErrors.Count < 20) testErrors.Add(Clean(Str(e, "message"))); break;
                }
            });
        // The network before the library traces: their DOM snapshots must never spend what the requests need.
        foreach (var entry in entries.Values.Where(e => e.FullName.EndsWith(".network", StringComparison.Ordinal)).OrderBy(e => e.FullName, StringComparer.Ordinal))
            Each(entry, networkBudget, e =>
            {
                if (Str(e, "type") != "resource-snapshot") return;
                if (network.Count >= 10_000) { networkBeyond++; return; }
                var snapshot = Prop(e, "snapshot");
                var request = Prop(snapshot, "request");
                var response = Prop(snapshot, "response");
                var status = (int)(Num(response, "status") ?? 0);
                var failureText = Str(response, "_failureText");
                var answer = status > 0 ? $"{status}{(Str(response, "statusText") is { Length: > 0 } text ? " " + OneLine(text, 60) : string.Empty)}"
                                        : "no response" + (failureText is { Length: > 0 } ? $" ({OneLine(failureText, 120)})" : string.Empty);
                var flag = status is 0 or >= 400 ? "  ← failed" : string.Empty;
                var duration = Num(snapshot, "time") is { } ms and >= 0 ? string.Create(CultureInfo.InvariantCulture, $" ({ms:0} ms)") : string.Empty;
                // A start that does not read is no time: never the year 1 said as "+-63000000000 s".
                DateTimeOffset? started = DateTimeOffset.TryParse(Str(snapshot, "startedDateTime"), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var when) ? when : null;
                network.Add((started, $"{OneLine(Str(request, "method"), 10)} {OneLine(Str(request, "url"), 300)} → {answer}{duration}{flag}"));
            });
        foreach (var entry in entries.Values.Where(e => e.FullName != "test.trace" && IsTraceEntry(e.FullName)).OrderBy(e => e.FullName, StringComparer.Ordinal))
            Each(entry, libraryBudget, e =>
            {
                switch (Str(e, "type"))
                {
                    case "context-options": Context(e); break;
                    case "before" when !fromRunner: Before(e, runner: false); break;
                    case "after" when !fromRunner: After(e); break;
                    case "log" when Str(e, "callId") is { } call && Str(e, "message") is { } message:
                        var log = callLogs.TryGetValue(call, out var l) ? l : callLogs[call] = [];
                        if (log.Count < 500) log.Add(OneLine(message));
                        break;
                    case "console":
                    {
                        var at = Num(e, "time") ?? 0;
                        var location = Prop(e, "location");
                        var source = Str(location, "url") is { Length: > 0 } url
                            ? $"  ({OneLine(url, 150)}{(Num(location, "lineNumber") is { } line ? string.Create(CultureInfo.InvariantCulture, $":{line + 1:0}") : string.Empty)})"
                            : string.Empty;
                        if (console.Count >= 10_000) consoleBeyond++;
                        else console.Add((at, $"{Str(e, "messageType") ?? "log"}  {OneLine(Str(e, "text"), 400)}{source}"));
                        break;
                    }
                    case "event" when Str(e, "method") == "pageError":
                    {
                        var error = Prop(Prop(e, "params"), "error");
                        var message = Str(Prop(error, "error"), "message") ?? Str(error, "message") ?? Str(Prop(error, "value"), "message");
                        if (pageErrors.Count >= 10_000) errorsBeyond++;
                        else pageErrors.Add((Num(e, "time") ?? 0, OneLine(message ?? "an uncaught error", 400)));
                        break;
                    }
                    case "screencast-frame" when Str(e, "file") is { } file:   // older traces name the frame by "sha1"
                        if (frames.Count < 100_000) frames.Add((Num(e, "timestamp") ?? 0, file));
                        break;
                    case "screencast-frame" when Str(e, "sha1") is { } sha1:
                        if (frames.Count < 100_000) frames.Add((Num(e, "timestamp") ?? 0, "resources/" + sha1));
                        break;
                }
            });

        var zero = origin ?? (console.Count > 0 ? console.Min(c => c.At) : 0);
        string At(double ms) => "+" + ((ms - zero) / 1000).ToString("0.000", CultureInfo.InvariantCulture) + " s";
        string AtWall(DateTimeOffset? when) => when is not { } at ? "(no time)"
            : wallTime > 0 ? At(wallMonotonic + (at.ToUnixTimeMilliseconds() - wallTime))
            : at.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + " UTC";   // no start to count from: the clock

        // The failure: the deepest step that failed (the expect, not the test around it), else the test's own error.
        var failed = order.Where(s => s.Error is not null).ToList();
        var failedParents = failed.Select(s => s.Parent).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var failure = failed.LastOrDefault(s => !failedParents.Contains(s.Id)) ?? failed.LastOrDefault();
        var outcome = failure is not null || testErrors.Count > 0 ? "failed" : order.Count > 0 ? "passed" : "unknown";

        var sb = new StringBuilder();
        if (failure is not null || testErrors.Count > 0)
        {
            sb.Append("## Failure\n");
            if (failure is not null)
                sb.Append(CultureInfo.InvariantCulture, $"{failure.Title}{(failure.Where is { } w ? $"  ({w})" : string.Empty)}, at {At(failure.Start)}\n");
            var message = testErrors.FirstOrDefault() ?? failure?.Error ?? string.Empty;
            sb.Append(message.TrimEnd()).Append('\n');
            // The test runner's message carries the call log already; a library trace's error does not.
            if (failure is not null && !message.Contains("Call log:", StringComparison.Ordinal)
                && callLogs.TryGetValue(failure.Id, out var log) && log.Count > 0)
            {
                sb.Append("Call log, its end:\n");
                foreach (var line in Collapse(log).TakeLast(MaxCallLog)) sb.Append("  ").Append(line).Append('\n');
            }
            var context = failed.SelectMany(s => s.Attachments).Concat(order.SelectMany(s => s.Attachments)).FirstOrDefault();
            var contextEntry = context is not null && entries.TryGetValue(context, out var found) ? found : null;
            if (contextEntry is { Length: > 256 * 1024 }) sb.Append("(Playwright's error context is larger than 256 KB: not read.)\n");
            else if (contextEntry is not null)
            {
                try
                {
                    using var reader = new StreamReader(new Budget(contextEntry.Open(), [0]), Encoding.UTF8);
                    var (snapshot, source) = ErrorContext(Clean(reader.ReadToEnd(), 64 * 1024));
                    Block(sb, "The page at the failure (its accessibility snapshot, from Playwright's error context):", snapshot, MaxContextLines);
                    Block(sb, "The test where it failed:", source, MaxContextLines / 2);
                }
                catch (Exception ex) when (ex is DocumentLimitException or InvalidDataException or IOException or NotSupportedException) { Diagnostics.Swallow("PlaywrightTrace.ErrorContext", ex); }
            }
            sb.Append('\n');
        }

        // The steps: a fixture's inner steps fold into it (setup, not the test), unless one failed.
        var shown = new List<string>();
        int Depth(Step s)
        {
            var depth = 0;
            for (var p = s.Parent; p is not null && steps.TryGetValue(p, out var parent) && depth < 20; p = parent.Parent) depth++;
            return depth;
        }
        bool InFixture(Step s)
        {
            var hops = 0;
            for (var p = s.Parent; p is not null && steps.TryGetValue(p, out var parent) && hops++ < 20; p = parent.Parent)
                if (parent.Method == "fixture") return true;
            return false;
        }
        foreach (var step in order)
        {
            if ((step.Method == "fixture" || InFixture(step)) && step.Error is null) continue;
            var took = step.End is { } end ? Duration(end - step.Start) : "did not end";
            var mark = step == failure ? "✗ " : step.Error is not null ? "✗ " : string.Empty;
            shown.Add($"{At(step.Start)}  {new string(' ', Math.Min(Depth(step), 6) * 2)}{mark}{step.Title}  {took}"
                      + (step.Where is { } w ? $"  ({w})" : string.Empty) + (step.Error is not null ? "  — failed" : string.Empty));
        }
        // A list whose source the zip does not hold, or that reading did not finish, is not "none".
        var libraryEntries = entries.Keys.Where(n => n != "test.trace" && IsTraceEntry(n)).ToList();
        var networkEntries = entries.Keys.Where(n => n.EndsWith(".network", StringComparison.Ordinal)).ToList();
        string NoneOf(List<string> sources) => sources.Count == 0 ? "not recorded in this trace"
            : sources.Any(n => stopped.Contains(n) || damaged.Contains(n)) ? "none read (reading stopped: see the first line)" : "none";
        Section(sb, fromRunner ? "Steps" : "Calls", shown, MaxSteps, stepsBeyond, NoneOf(fromRunner ? ["test.trace"] : libraryEntries));
        Section(sb, "Console", console.OrderBy(c => c.At).Select(c => $"{At(c.At)}  {c.Line}").ToList(), MaxConsole, consoleBeyond, NoneOf(libraryEntries));
        if (pageErrors.Count > 0)
            Section(sb, "Errors in the page", pageErrors.OrderBy(c => c.At).Select(c => $"{At(c.At)}  {c.Line}").ToList(), MaxPageErrors, errorsBeyond);
        Section(sb, "Network", network.OrderBy(n => n.At ?? DateTimeOffset.MaxValue).Select(n => $"{AtWall(n.At)}  {n.Line}").ToList(),
                MaxNetwork, networkBeyond, NoneOf(networkEntries));

        // The picture: the last frame before the failure ended, else the last one.
        var until = failure?.End ?? double.MaxValue;
        var frame = frames.Where(f => f.At <= until).OrderBy(f => f.At).LastOrDefault();
        if (frame.File is null && frames.Count > 0) frame = frames.OrderBy(f => f.At).Last();
        byte[]? picture = null;
        string? pictureName = null;
        if (frame.File is not null && entries.TryGetValue(frame.File, out var frameEntry) && frameEntry.Length <= MaxFrameBytes)
        {
            try
            {
                using var stream = new Budget(frameEntry.Open(), [0]);
                using var copy = new MemoryStream();
                stream.CopyTo(copy);
                picture = copy.ToArray();
                pictureName = $"page at {At(frame.At)}{Path.GetExtension(frame.File)}";
            }
            catch (Exception ex) when (ex is DocumentLimitException or InvalidDataException or IOException or NotSupportedException) { Diagnostics.Swallow("PlaywrightTrace.Frame", ex); }
        }

        var header = new StringBuilder("[Playwright trace");
        if (title is not null) header.Append(": ").Append(OneLine(title, 300));
        header.Append(CultureInfo.InvariantCulture, $" — {outcome}");
        var about = new[] { version is null ? null : "Playwright " + OneLine(version, 30), browser is null ? null : OneLine(browser, 30), platform is null ? null : OneLine(platform, 30) }
            .Where(p => p is not null).ToList();
        if (about.Count > 0) header.Append(" (").Append(string.Join(", ", about)).Append(')');
        header.Append(". Times are seconds since the trace started. The page's DOM snapshots are not read.");
        if (picture is not null)
            header.Append(CultureInfo.InvariantCulture, $" A picture of the page at {At(frame.At)}{(failure is not null ? " (the last before the failure)" : string.Empty)} is in the trace.");
        if (stopped.Count > 0)
            header.Append(CultureInfo.InvariantCulture,
                $" Reading stopped after {MaxRead / (1024 * 1024)} MB decompressed in {string.Join(", ", stopped.Distinct().Take(5))}: the rest of it is not here.");
        if (tooMany) header.Append(" The zip holds more than 10 000 entries: those past them are not read.");
        if (damaged.Count > 0) header.Append(" Damaged, not read whole: ").Append(string.Join(", ", damaged.Distinct().Take(5))).Append('.');
        header.Append(']');
        return new Reading(header + "\n\n" + sb.ToString().TrimEnd() + "\n", picture, pictureName);
    }

    /// <summary>
    /// What Playwright's <c>error-context.md</c> holds that the trace does not say otherwise: the page's accessibility
    /// snapshot (its <c>yaml</c> block) and the test's lines around the failure (its marked <c>&gt;</c> line). Its
    /// instructions to a model and its copy of the error are left out: the error is already said.
    /// </summary>
    internal static (List<string> Snapshot, List<string> Source) ErrorContext(string markdown)
    {
        var snapshot = new List<string>();
        var source = new List<string>();
        List<string>? into = null;
        var inSource = false;
        foreach (var line in markdown.Split('\n'))
        {
            if (line.StartsWith('#')) { inSource = line.Contains("source", StringComparison.OrdinalIgnoreCase); continue; }
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                into = into is not null ? null : line.StartsWith("```yaml", StringComparison.Ordinal) && snapshot.Count == 0 ? snapshot
                     : inSource && source.Count == 0 ? source : new List<string>();
                continue;
            }
            into?.Add(line);
        }
        // The test's lines: those around the one marked as failing.
        var marked = source.FindIndex(l => l.TrimStart().StartsWith('>'));
        if (marked >= 0) source = source.Skip(Math.Max(0, marked - 6)).Take(10).ToList();
        return (snapshot, source);
    }

    private static void Block(StringBuilder sb, string title, List<string> lines, int cap)
    {
        if (lines.Count == 0) return;
        sb.Append(title).Append('\n');
        foreach (var line in lines.Take(cap)) sb.Append("  ").Append(line).Append('\n');
        if (lines.Count > cap) sb.Append(CultureInfo.InvariantCulture, $"  … and {lines.Count - cap} more line(s).\n");
    }

    /// <summary>A call log with its repeats folded: a line repeated, or a pair of lines repeated (a wait that resolved the
    /// element, then found the wrong value, thirty times), is said once with its count.</summary>
    internal static IEnumerable<string> Collapse(List<string> log)
    {
        for (var i = 0; i < log.Count;)
        {
            var same = 1;
            while (i + same < log.Count && log[i + same] == log[i]) same++;
            if (same > 1) { yield return $"{log[i]}  ({same} times)"; i += same; continue; }
            var pairs = 1;
            while (i + 2 * pairs + 1 < log.Count && log[i + 2 * pairs] == log[i] && log[i + 2 * pairs + 1] == log[i + 1]) pairs++;
            if (pairs > 1)
            {
                yield return log[i];
                yield return $"{log[i + 1]}  (these two lines {pairs} times)";
                i += 2 * pairs;
                continue;
            }
            yield return log[i];
            i++;
        }
    }

    private static string Duration(double ms) => ms >= 1000
        ? (ms / 1000).ToString("0.0#", CultureInfo.InvariantCulture) + " s"
        : ms.ToString("0", CultureInfo.InvariantCulture) + " ms";

    /// <param name="beyond">Entries not even held (past what is kept in memory): counted with the rest.</param>
    /// <param name="none">What an empty list says: "none" only when its source was read.</param>
    private static void Section(StringBuilder sb, string title, List<string> lines, int cap, int beyond = 0, string none = "none")
    {
        sb.Append("## ").Append(title).Append('\n');
        if (lines.Count == 0 && beyond == 0) sb.Append(none).Append('\n');
        foreach (var line in lines.Take(cap)) sb.Append(line).Append('\n');
        if (lines.Count + beyond > cap) sb.Append(CultureInfo.InvariantCulture, $"… and {lines.Count + beyond - Math.Min(cap, lines.Count)} more, not listed.\n");
        sb.Append('\n');
    }
}
