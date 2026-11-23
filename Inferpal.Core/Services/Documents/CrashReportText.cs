using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Inferpal.Services.Documents;

/// <summary>
/// A crash report read as what crashed: the exception first (its code named), the message the program left, the faulting
/// thread's stack as written, then what else the report holds, counted. Apple's <c>.ips</c> (JSON) and <c>.crash</c> (text),
/// Windows' <c>.wer</c>, Android's tombstones and ANR traces. The file stays a text file: asked for lines, each is the text it
/// holds (as <see cref="TestReportText"/>).
/// </summary>
/// <remarks>
/// ⚠ Recognised by its NAME and its shape, never by its content alone: a log that quotes a crash stays a log, read whole.
/// ⚠ A WER report holds no stack: the dump beside it, if any, is not read (dumps stay out). Never throws for a file of an
/// unexpected shape: it is read as its lines (<c>null</c>).
/// </remarks>
internal static class CrashReportText
{
    private const int MaxChars = DocumentText.ReportMaxChars;

    /// <summary>The most frames of one stack shown; the rest is counted.</summary>
    private const int FramesShown = 60;

    /// <summary>The crash, <paramref name="text"/> read from <paramref name="name"/>, within <paramref name="budget"/>
    /// characters; <c>null</c> for any other file.</summary>
    internal static string? Summarize(string name, string text, int budget, CancellationToken ct = default)
    {
        if (text.Length > MaxChars) return null;
        // In an archive the name is the entry's whole path (bugreport.zip's FS/data/tombstones/…): judged on the file's name.
        var lower = Path.GetFileName(name.Replace('\\', '/')).ToLowerInvariant();
        try
        {
            var body = lower.EndsWith(".ips", StringComparison.Ordinal) ? Ips(name, text)
                : lower.EndsWith(".crash", StringComparison.Ordinal) ? AppleText(name, Lines(text))
                : lower.EndsWith(".wer", StringComparison.Ordinal) ? Wer(name, Lines(text))
                : lower.StartsWith("tombstone", StringComparison.Ordinal) && text.TrimStart().StartsWith("*** *** ***", StringComparison.Ordinal)
                    ? Tombstone(name, Lines(text))
                : (lower.StartsWith("anr", StringComparison.Ordinal) || lower.StartsWith("traces", StringComparison.Ordinal))
                  && text.Contains("----- pid ", StringComparison.Ordinal) ? Anr(name, Lines(text))
                : null;
            ct.ThrowIfCancellationRequested();
            return body is null ? null : Fit(body, budget);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException or JsonException
                                       or KeyNotFoundException or InvalidCastException or IndexOutOfRangeException or OverflowException)
        {
            Diagnostics.Swallow("CrashReportText.Summarize", ex);
            return null;
        }
    }

    private static string Fit(string body, int budget)
    {
        const string Raw = "\n[The raw report: read_file with start_line.]";
        if (body.Length + Raw.Length <= budget) return body.TrimEnd() + Raw;
        var cut = body.LastIndexOf('\n', Math.Max(0, budget - 200));
        return body[..Math.Max(0, cut)].TrimEnd() + "\n[Cut to fit: read_file with start_line pages the raw report.]";
    }

    private static string[] Lines(string text) => text.ReplaceLineEndings("\n").Split('\n');

    // ── Apple .ips: a one-line JSON header, then the report's JSON ──────────────────────────────────────────────────────

    private static string? Ips(string name, string text)
    {
        var newline = text.IndexOf('\n');
        if (newline < 0) return null;
        var header = JsonNode.Parse(text[..newline]) as JsonObject;
        var rest = text[(newline + 1)..];
        // An older .ips carries the text report under its JSON header.
        if (rest.TrimStart().StartsWith('{') is false)
            return rest.Contains("Exception Type:", StringComparison.Ordinal) ? AppleText(name, Lines(rest)) : null;
        if (JsonNode.Parse(rest) is not JsonObject r || (r["threads"] is not JsonArray && r["exception"] is not JsonObject)) return null;

        var images = (r["usedImages"] as JsonArray)?.Select(i => Str(i?["name"])).ToList() ?? [];
        var sb = new StringBuilder();
        var app = Str(r["procName"]) is { Length: > 0 } p ? p : Str(header?["app_name"]);
        var version = Str(header?["app_version"]);
        sb.Append(CultureInfo.InvariantCulture,
            $"Crash report {name}: {app}{(version.Length > 0 ? " " + version : "")}{(Str(r["bundleInfo"]?["CFBundleIdentifier"]) is { Length: > 0 } id ? " (" + id + ")" : "")}");
        if (Str(header?["os_version"]) is { Length: > 0 } os) sb.Append(", ").Append(os);
        if (Str(r["captureTime"]) is { Length: > 0 } at) sb.Append(", ").Append(at);
        sb.Append(".\n");

        if (r["exception"] is JsonObject e)
            sb.Append("Exception: ").Append(string.Join(" · ", new[] { Str(e["type"]), Str(e["signal"]), Str(e["subtype"]), Str(e["codes"]), Str(e["message"]) }
                .Where(s => s.Length > 0))).Append('\n');
        if (r["termination"] is JsonObject t)
        {
            // An iOS watchdog (0x8BADF00D) says itself in its reasons only, with no indicator.
            var reasons = (t["reasons"] as JsonArray)?.Select(Str).Where(s => s.Length > 0).ToList() ?? [];
            var head = Str(t["indicator"]) is { Length: > 0 } ind ? ind
                : string.Join(" ", new[] { Str(t["namespace"]), Str(t["code"]) }.Where(s => s.Length > 0));
            if (head.Length > 0 || reasons.Count > 0)
                sb.Append("Termination: ").Append(head).Append(reasons.Count > 0 ? (head.Length > 0 ? " — " : "") + string.Join("; ", reasons) : "")
                  .Append('\n');
        }
        // Application Specific Information: what the program (or the runtime) said before it stopped.
        if (r["asi"] is JsonObject asi)
            foreach (var (owner, said) in asi)
                foreach (var line in (said as JsonArray)?.Select(Str).Where(s => s.Length > 0) ?? [])
                    sb.Append(CultureInfo.InvariantCulture, $"Application said ({owner}): {line}\n");

        if (r["lastExceptionBacktrace"] is JsonArray last && last.Count > 0)
            sb.Append("\nLast exception backtrace (where the exception was thrown):\n").Append(IpsFrames(last, images));
        var threads = r["threads"] as JsonArray ?? [];
        int? faulting = Int(r["faultingThread"]) is int f && f >= 0 && f < threads.Count ? f
            : threads.Select((th, i) => (th, i)).Where(x => x.th?["triggered"] is JsonValue v && v.TryGetValue<bool>(out var b) && b)
                     .Select(x => (int?)x.i).FirstOrDefault();
        if (faulting is null) sb.Append("\nNo thread is marked as the one that crashed.\n");
        else if (threads[faulting.Value] is JsonObject crashed)
        {
            var label = string.Join(", ", new[] { Str(crashed["name"]), Str(crashed["queue"]) }.Where(s => s.Length > 0));
            sb.Append(CultureInfo.InvariantCulture, $"\nThread {faulting} crashed{(label.Length > 0 ? " (" + label + ")" : "")}:\n")
              .Append(IpsFrames(crashed["frames"] as JsonArray ?? [], images));
        }
        sb.Append(CultureInfo.InvariantCulture, $"\n{Math.Max(0, threads.Count - (faulting is null ? 0 : 1))} other threads, {images.Count} images loaded: not shown.");
        return sb.ToString();
    }

    private static string IpsFrames(JsonArray frames, List<string> images)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < Math.Min(frames.Count, FramesShown); i++)
        {
            var fr = frames[i];
            var image = Int(fr?["imageIndex"]) is int ix && ix >= 0 && ix < images.Count ? images[ix] : "?";
            var symbol = Str(fr?["symbol"]) is { Length: > 0 } s
                ? s + (Int(fr?["symbolLocation"]) is int loc ? " + " + loc.ToString(CultureInfo.InvariantCulture) : "")
                : "0x" + (fr?["imageOffset"] is JsonValue o && o.TryGetValue<long>(out var off) ? off.ToString("x", CultureInfo.InvariantCulture) : "?");
            var source = Str(fr?["sourceFile"]) is { Length: > 0 } file
                ? " (" + file + (Int(fr?["sourceLine"]) is int ln ? ":" + ln.ToString(CultureInfo.InvariantCulture) : "") + ")" : "";
            sb.Append(CultureInfo.InvariantCulture, $"  {i,-3} {image,-28} {symbol}{source}\n");
        }
        if (frames.Count > FramesShown) sb.Append(CultureInfo.InvariantCulture, $"  [{frames.Count - FramesShown} more frames]\n");
        return sb.ToString();
    }

    // ── Apple .crash: the text report ────────────────────────────────────────────────────────────────────────────────

    private static readonly string[] AppleKeys =
        ["Process", "Identifier", "Version", "OS Version", "Date/Time", "Exception Type", "Exception Codes", "Exception Subtype",
         "Exception Message", "Exception Note", "Termination Reason", "Termination Signal", "Triggered by Thread", "Crashed Thread"];

    private static readonly Regex CrashedThread = new(@"^Thread \d+( name:.*)? ?Crashed:", RegexOptions.CultureInvariant, RegexBudget.Default);
    private static readonly Regex AnyThread = new(@"^Thread \d+( Crashed)?:", RegexOptions.CultureInvariant, RegexBudget.Default);

    private static string? AppleText(string name, string[] lines)
    {
        var header = new List<string>();
        foreach (var line in lines)
            foreach (var key in AppleKeys)
                if (line.StartsWith(key + ":", StringComparison.Ordinal)) { header.Add(line.Trim()); break; }
        var crashedAt = Array.FindIndex(lines, l => CrashedThread.IsMatch(l));
        if (!header.Any(h => h.StartsWith("Exception Type:", StringComparison.Ordinal)) && crashedAt < 0) return null;

        var sb = new StringBuilder($"Crash report {name}:\n");
        foreach (var h in header) sb.Append(h).Append('\n');
        Block(sb, lines, "Application Specific Information:", null);
        Block(sb, lines, "Last Exception Backtrace:", "Last exception backtrace (where the exception was thrown):");
        if (crashedAt >= 0) Block(sb, lines, lines[crashedAt], null);
        var threads = lines.Count(l => AnyThread.IsMatch(l));
        var imagesAt = Array.FindIndex(lines, l => l.StartsWith("Binary Images:", StringComparison.Ordinal));
        var images = imagesAt < 0 ? 0 : lines.Skip(imagesAt + 1).TakeWhile(l => l.Trim().Length > 0).Count();
        sb.Append(CultureInfo.InvariantCulture, $"\n{Math.Max(0, threads - 1)} other threads, {images} binary images: not shown.");
        return sb.ToString();
    }

    /// <summary>The lines under <paramref name="heading"/> up to the next blank line, frames capped.</summary>
    private static void Block(StringBuilder sb, string[] lines, string heading, string? title)
    {
        var at = Array.FindIndex(lines, l => l.TrimEnd() == heading.TrimEnd() || l.StartsWith(heading, StringComparison.Ordinal));
        if (at < 0) return;
        var body = lines.Skip(at + 1).TakeWhile(l => l.Trim().Length > 0).ToList();
        if (body.Count == 0 && title is null && lines[at].Length <= heading.Length) return;
        sb.Append('\n').Append(title ?? lines[at].Trim()).Append('\n');
        foreach (var l in body.Take(FramesShown)) sb.Append("  ").Append(l.Trim()).Append('\n');
        if (body.Count > FramesShown) sb.Append(CultureInfo.InvariantCulture, $"  [{body.Count - FramesShown} more lines]\n");
    }

    // ── Windows Error Reporting: Report.wer ─────────────────────────────────────────────────────────────────────────

    private static readonly Dictionary<string, string> ExceptionCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["c0000005"] = "access violation", ["c0000409"] = "stack buffer overrun or fail-fast", ["c00000fd"] = "stack overflow",
        ["c0000374"] = "heap corruption", ["e0434352"] = ".NET exception (unhandled)", ["e06d7363"] = "C++ exception",
        ["80000003"] = "breakpoint", ["c000001d"] = "illegal instruction", ["c0000094"] = "integer divide by zero",
        ["c0000602"] = "fail-fast", ["c0000420"] = "assertion failure", ["40000015"] = "fatal application exit",
        ["c06d007e"] = "module not found (delay load)", ["c0000135"] = "DLL not found", ["c0000142"] = "DLL initialization failed",
    };

    private static readonly Regex WerSig = new(@"^Sig\[(\d+)\]\.(Name|Value)=(.*)$", RegexOptions.CultureInvariant, RegexBudget.Default);

    private static string? Wer(string name, string[] lines)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var sigs = new SortedDictionary<int, (string Name, string Value)>();
        var modules = new List<string>();
        foreach (var line in lines)
        {
            if (WerSig.Match(line) is { Success: true } m)
            {
                var i = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                var (n, v) = sigs.GetValueOrDefault(i, ("", ""));
                sigs[i] = m.Groups[2].Value == "Name" ? (m.Groups[3].Value, v) : (n, m.Groups[3].Value);
                continue;
            }
            if (line.StartsWith("LoadedModule[", StringComparison.Ordinal) && line.IndexOf('=') is var eq and > 0) { modules.Add(line[(eq + 1)..]); continue; }
            if (line.IndexOf('=') is var at and > 0) values.TryAdd(line[..at], line[(at + 1)..]);
        }
        if (!values.ContainsKey("EventType") || sigs.Count == 0) return null;

        var sb = new StringBuilder();
        var app = values.GetValueOrDefault("NsAppName") ?? values.GetValueOrDefault("AppName") ?? sigs.First().Value.Value;
        sb.Append(CultureInfo.InvariantCulture, $"Windows Error Report {name}: {values["EventType"]}");
        if (values.GetValueOrDefault("FriendlyEventName") is { Length: > 0 } friendly) sb.Append(" (").Append(friendly).Append(')');
        sb.Append(" of ").Append(app);
        if (long.TryParse(values.GetValueOrDefault("EventTime"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ft)
            && ft > 0 && ft <= DateTime.MaxValue.ToFileTimeUtc())
            sb.Append(", ").Append(DateTime.FromFileTimeUtc(ft).ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture));
        if (values.GetValueOrDefault("IsFatal") == "1") sb.Append(", fatal");
        sb.Append(".\n");
        if (values.GetValueOrDefault("AppPath") is { Length: > 0 } path) sb.Append("Path: ").Append(path).Append('\n');

        // The signature as written: its names are in the system's language.
        sb.Append("\nSignature:\n");
        foreach (var (_, (n, v)) in sigs)
            sb.Append(CultureInfo.InvariantCulture, $"  {n}: {v}{(ExceptionCodes.TryGetValue(v, out var meaning) ? " (" + meaning + ")" : "")}\n");

        // A crash's faulting module (signature 3 of APPCRASH and BEX): found among the loaded ones, with its path.
        if (values["EventType"] is "APPCRASH" or "BEX" or "BEX64" && sigs.TryGetValue(3, out var mod) && mod.Value.Length > 0)
            if (modules.FirstOrDefault(p => p.EndsWith("\\" + mod.Value, StringComparison.OrdinalIgnoreCase)) is { } where)
                sb.Append("Faulting module: ").Append(where).Append('\n');
        sb.Append(CultureInfo.InvariantCulture,
            $"\n{modules.Count} modules loaded: not shown. A WER report holds no stack: the dump next to it, if any, is not read here.");
        return sb.ToString();
    }

    // ── Android: tombstones and ANR traces ─────────────────────────────────────────────────────────────────────────

    private static readonly string[] TombstoneKeys = ["Build fingerprint:", "ABI:", "Timestamp:", "Process uptime:", "Cmdline:", "pid:", "signal ", "Abort message:", "Cause:"];

    private static string? Tombstone(string name, string[] lines)
    {
        var sb = new StringBuilder($"Android tombstone {name}:\n");
        // The header: up to the first register dump or backtrace, the crashing thread's.
        foreach (var line in lines.TakeWhile(l => !l.TrimStart().StartsWith("backtrace:", StringComparison.Ordinal)))
            if (TombstoneKeys.Any(k => line.TrimStart().StartsWith(k, StringComparison.Ordinal))) sb.Append(line.Trim()).Append('\n');
        var at = Array.FindIndex(lines, l => l.TrimStart().StartsWith("backtrace:", StringComparison.Ordinal));
        if (at < 0) return null;
        var frames = lines.Skip(at + 1).TakeWhile(l => l.Trim().Length > 0).ToList();
        sb.Append("\nBacktrace of the crashing thread:\n");
        foreach (var f in frames.Take(FramesShown)) sb.Append("  ").Append(f.Trim()).Append('\n');
        if (frames.Count > FramesShown) sb.Append(CultureInfo.InvariantCulture, $"  [{frames.Count - FramesShown} more frames]\n");
        var others = lines.Count(l => l.StartsWith("--- --- ---", StringComparison.Ordinal));
        sb.Append(CultureInfo.InvariantCulture, $"\n{others} other threads, registers, memory and maps: not shown.");
        return sb.ToString();
    }

    private static readonly Regex HeldBy = new(@"held by (?:thread|tid=)\s*(\d+)", RegexOptions.CultureInvariant, RegexBudget.Default);

    private static readonly Regex AnrThread = new(@"^""([^""]*)"".*\btid=(\d+)\s+(\S+)", RegexOptions.CultureInvariant, RegexBudget.Default);

    private static string? Anr(string name, string[] lines)
    {
        var start = Array.FindIndex(lines, l => l.StartsWith("----- pid ", StringComparison.Ordinal));
        if (start < 0) return null;
        var end = Array.FindIndex(lines, start + 1, l => l.StartsWith("----- end ", StringComparison.Ordinal) || l.StartsWith("----- pid ", StringComparison.Ordinal));
        var process = lines[start..(end < 0 ? lines.Length : end)];
        var more = lines.Skip(start + 1).Count(l => l.StartsWith("----- pid ", StringComparison.Ordinal));

        // Each thread: its heading, then its lines up to a blank one.
        var threads = new List<(string Name, string Tid, string State, List<string> Body)>();
        for (var i = 0; i < process.Length; i++)
        {
            if (AnrThread.Match(process[i]) is not { Success: true } m) continue;
            var body = process.Skip(i + 1).TakeWhile(l => l.Trim().Length > 0).ToList();
            threads.Add((m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, [process[i].Trim(), .. body]));
        }
        if (threads.Count == 0) return null;

        var sb = new StringBuilder($"Android ANR traces {name}: {process[0].Trim('-', ' ')}");
        if (process.FirstOrDefault(l => l.StartsWith("Cmd line:", StringComparison.Ordinal)) is { } cmd) sb.Append(" — ").Append(cmd.Trim());
        sb.Append(".\n");
        // Newer traces name the ANR's reason above the first process.
        if (lines.Take(start).FirstOrDefault(l => l.StartsWith("Subject:", StringComparison.Ordinal)) is { } subject)
            sb.Append(subject.Trim()).Append('\n');
        sb.Append("An ANR is the main thread blocked: it first, then every thread that waits on a lock and who holds it.\n");
        var shown = new HashSet<int>();
        void Thread(int i, int frames)
        {
            shown.Add(i);
            var body = threads[i].Body;
            var kept = body.Where(l => !l.TrimStart().StartsWith('|')).ToList();   // the "| sysTid=…" detail lines
            sb.Append('\n');
            foreach (var l in kept.Take(frames + 1)) sb.Append(l.StartsWith('"') ? l : "  " + l.Trim()).Append('\n');
            if (kept.Count > frames + 1) sb.Append(CultureInfo.InvariantCulture, $"  [{kept.Count - frames - 1} more lines]\n");
        }
        if (threads.FindIndex(t => t.Name == "main") is var main and >= 0)
        {
            Thread(main, FramesShown);
            foreach (var held in threads[main].Body.Select(l => HeldBy.Match(l)).Where(h => h.Success))
                if (threads.FindIndex(t => t.Tid == held.Groups[1].Value) is var holder and >= 0 && !shown.Contains(holder))
                    Thread(holder, 8);
        }
        for (var i = 0; i < threads.Count; i++)
            if (!shown.Contains(i) && (threads[i].State is "Blocked" || threads[i].Body.Any(l => l.Contains("waiting to lock", StringComparison.Ordinal))))
            {
                Thread(i, 8);
                // Who holds what it waits on: the stack that matters for a deadlock.
                foreach (var held in threads[i].Body.Select(l => HeldBy.Match(l)).Where(h => h.Success))
                    if (threads.FindIndex(t => t.Tid == held.Groups[1].Value) is var holder and >= 0 && !shown.Contains(holder))
                        Thread(holder, 8);
            }
        sb.Append(CultureInfo.InvariantCulture, $"\n{threads.Count - shown.Count} other threads of this process");
        if (more > 0) sb.Append(CultureInfo.InvariantCulture, $", {more} other processes in the file");
        sb.Append(": not shown.");
        return sb.ToString();
    }

    private static string Str(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : node is JsonValue n ? n.ToJsonString() : "";

    private static int? Int(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;
}
