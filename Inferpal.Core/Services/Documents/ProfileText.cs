using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Inferpal.Services.Documents;

/// <summary>
/// A CPU profile already recorded — V8's <c>.cpuprofile</c> (Node's <c>--cpu-prof</c>, Chrome's JavaScript profiler), the
/// trace Chrome's Performance panel exports, a speedscope file — read as where the time goes: the functions by their own
/// time, then by their total time, then the hottest paths; each figure with its unit and its share of the profile.
/// </summary>
/// <remarks>
/// <para>
/// Self time is what the samples spent in the function's own code; total time counts a sample once for every function on
/// its stack, a recursive one once. What is no function — the profiler's <c>(idle)</c>, <c>(program)</c>,
/// <c>(garbage collector)</c> — is counted apart, never ranked as the hottest function.
/// </para>
/// <para>
/// ⚠ A function is its script, line AND column: a minified bundle holds its whole program on line 1, and keyed by name and
/// line its anonymous functions would merge into one row that wins. Samples are ordered by their time before their
/// durations are taken (V8 can write them out of order). A file with several profiles (threads, workers) is read by the
/// one whose code ran longest, and the others are named. Only a profile of TIME is read: a speedscope profile of bytes or
/// counts is no time.
/// </para>
/// <para>
/// The profile is read, never recorded: nothing is run. The file stays a text file: asked for lines, its JSON. Never
/// throws for a file of an unexpected shape; any other JSON reads as its lines (<c>null</c>).
/// </para>
/// </remarks>
internal static class ProfileText
{
    private const int MaxChars = 64_000_000, MaxRows = 15, MaxPaths = 8, PathFrames = 6;

    /// <summary>A function as a profile names it: its name, where it is shown to be, and what tells it apart.</summary>
    /// <param name="Key">Its script (full address or id), line and column: two functions with the same name and line differ by it.</param>
    internal readonly record struct Frame(string Name, string Where, string Key)
    {
        /// <summary>Whether it is code of the program — not V8's <c>(program)</c>, <c>(idle)</c>, <c>(garbage collector)</c>,
        /// <c>(root)</c>; an anonymous function is code.</summary>
        public bool IsFunction => Name is not ("(program)" or "(idle)" or "(garbage collector)" or "(root)");
        public string Label => Name.Length == 0 ? "(anonymous)" : Name;
    }

    /// <summary>One stack the profile saw, root first, and how long it was seen, in microseconds. Stacks of one node share
    /// one list: what is computed per stack is computed once.</summary>
    internal readonly record struct Sample(IReadOnlyList<Frame> Stack, double Micros);

    /// <summary>A profile read: its kind, its samples, how many it took, what else the file held and was not read.</summary>
    internal sealed record Profile(string Kind, IReadOnlyList<Sample> Samples, int Count, string Unit = "samples", string? Others = null);

    /// <summary>The profile's figures, <paramref name="text"/> read from <paramref name="name"/>, within
    /// <paramref name="budget"/> characters; <c>null</c> for any other file.</summary>
    internal static string? Summarize(string name, string text, int budget, CancellationToken ct = default)
    {
        if (text.Length > MaxChars && Looks(name, text))
            return string.Create(CultureInfo.InvariantCulture,
                $"{name} is a CPU profile of {text.Length / 1_000_000} million characters, past the {MaxChars / 1_000_000} million this reads as a profile: read_file with start_line pages its raw JSON. A shorter recording reads as its functions.");
        return Read(name, text, ct) is { } profile ? Render(profile, budget, ct) : null;
    }

    private static bool Looks(string name, string text)
    {
        var lower = name.ToLowerInvariant();
        if (lower.EndsWith(".cpuprofile", StringComparison.Ordinal)) return true;
        var head = text.Length > 2_000_000 ? text[..2_000_000] : text;
        return lower.EndsWith(".json", StringComparison.Ordinal)
               && (head.Contains("\"ProfileChunk\"", StringComparison.Ordinal) || head.Contains("speedscope.app", StringComparison.Ordinal));
    }

    /// <summary>The profile <paramref name="text"/> holds; <c>null</c> for any other file.</summary>
    internal static Profile? Read(string name, string text, CancellationToken ct = default)
    {
        if (text.Length > MaxChars) return null;
        var lower = name.ToLowerInvariant();
        var cpuprofile = lower.EndsWith(".cpuprofile", StringComparison.Ordinal);
        if (!cpuprofile && !lower.EndsWith(".json", StringComparison.Ordinal)) return null;
        if (!cpuprofile && !text.Contains("\"ProfileChunk\"", StringComparison.Ordinal) && !text.Contains("speedscope", StringComparison.Ordinal)
            && !(text.Contains("\"timeDeltas\"", StringComparison.Ordinal) && text.Contains("\"callFrame\"", StringComparison.Ordinal)))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            ct.ThrowIfCancellationRequested();
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("nodes", out _) && root.TryGetProperty("samples", out _))
                return FromCpuProfile(root, ct);
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("shared", out _) && root.TryGetProperty("profiles", out _))
                return FromSpeedscope(root, ct);
            var events = root.ValueKind == JsonValueKind.Array ? root
                       : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("traceEvents", out var te) ? te : default;
            return events.ValueKind == JsonValueKind.Array ? FromTrace(events, ct) : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            Diagnostics.Swallow("ProfileText.Read", ex);
            return null;
        }
    }

    // ── Formats ──

    private sealed class Node
    {
        public required Frame Frame;
        public int? Parent;
    }

    /// <summary>V8's format: nodes with their call frame and children, the node of each sample, the time between them.</summary>
    private static Profile? FromCpuProfile(JsonElement p, CancellationToken ct)
    {
        var nodes = new Dictionary<int, Node>();
        foreach (var n in p.GetProperty("nodes").EnumerateArray()) AddNode(nodes, n);
        foreach (var n in p.GetProperty("nodes").EnumerateArray())
            if (n.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
                foreach (var c in children.EnumerateArray())
                    if (c.TryGetInt32(out var child) && nodes.TryGetValue(child, out var cn)) cn.Parent = Int(n, "id");
        double? end = p.TryGetProperty("endTime", out var e) && e.TryGetDouble(out var et) ? et : null;
        double? start = p.TryGetProperty("startTime", out var s) && s.TryGetDouble(out var st) ? st : null;
        return Build("V8 CPU profile", nodes, Ints(p, "samples"), Numbers(p, "timeDeltas"), start, end, ct);
    }

    private static void AddNode(Dictionary<int, Node> nodes, JsonElement n)
    {
        if (!n.TryGetProperty("id", out var idEl) || !idEl.TryGetInt32(out var id)) return;
        var frame = n.TryGetProperty("callFrame", out var cf) ? FrameOf(cf) : new Frame("(unknown)", "", "");
        nodes[id] = new Node { Frame = frame, Parent = n.TryGetProperty("parent", out var pe) && pe.TryGetInt32(out var parent) ? parent : null };
    }

    private static Frame FrameOf(JsonElement cf)
    {
        var name = Str(cf, "functionName");
        var url = Str(cf, "url");
        var script = cf.TryGetProperty("scriptId", out var sid) ? sid.ValueKind == JsonValueKind.String ? sid.GetString() ?? "" : sid.GetRawText() : "";
        // V8 counts lines and columns from 0.
        var line = cf.TryGetProperty("lineNumber", out var l) && l.TryGetInt32(out var ln) && ln >= 0 ? ln + 1 : 0;
        var column = cf.TryGetProperty("columnNumber", out var c) && c.TryGetInt32(out var cn) && cn >= 0 ? cn + 1 : 0;
        return new Frame(name, Where(url, line), string.Create(CultureInfo.InvariantCulture, $"{url}#{script}:{line}:{column}"));
    }

    /// <summary>A file's name (without its query) and its line, as shown.</summary>
    private static string Where(string url, int line)
    {
        var path = url.Split('?', '#')[0];
        var file = path.Length == 0 ? "" : path[(path.LastIndexOfAny(['/', '\\']) + 1)..];
        return file.Length == 0 ? "" : line > 0 ? $"{file}:{line.ToString(CultureInfo.InvariantCulture)}" : file;
    }

    /// <summary>Each sample's stack and duration: ordered by time first, a sample lasts until the next one; the last one,
    /// until the profile's end when it says it, else as long as the one before.</summary>
    private static Profile? Build(string kind, Dictionary<int, Node> nodes, List<int> samples, List<double> deltas, double? start,
                                  double? end, CancellationToken ct)
    {
        if (samples.Count == 0 || nodes.Count == 0) return null;
        var stacks = new Dictionary<int, IReadOnlyList<Frame>>();
        IReadOnlyList<Frame> StackOf(int id)
        {
            if (stacks.TryGetValue(id, out var known)) return known;
            var frames = new List<Frame>();
            var seen = new HashSet<int>();
            for (int? at = id; at is { } cur && nodes.TryGetValue(cur, out var node) && seen.Add(cur); at = node.Parent)
                if (node.Frame.Name != "(root)") frames.Add(node.Frame);
            frames.Reverse();
            return stacks[id] = frames;
        }
        var timed = new List<(double At, int Node)>(samples.Count);
        double t = start ?? 0;
        for (var i = 0; i < samples.Count; i++) { t += i < deltas.Count ? deltas[i] : 0; timed.Add((t, samples[i])); }
        // V8 can write a sample out of order (a negative delta): the time between samples is taken in time order.
        var ordered = timed.Select((s, i) => (s.At, s.Node, i)).OrderBy(s => s.At).ThenBy(s => s.i).ToList();
        var result = new List<Sample>(samples.Count);
        for (var i = 0; i < ordered.Count; i++)
        {
            if ((i & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
            var micros = i + 1 < ordered.Count ? ordered[i + 1].At - ordered[i].At
                       : end is { } e && e > ordered[i].At ? e - ordered[i].At : i > 0 ? ordered[i].At - ordered[i - 1].At : 0;
            if (micros <= 0 || !nodes.ContainsKey(ordered[i].Node)) continue;
            result.Add(new Sample(StackOf(ordered[i].Node), micros));
        }
        return result.Count == 0 ? null : new Profile(kind, result, samples.Count);
    }

    /// <summary>Chrome's trace: a <c>Profile</c> event per thread profiled, then <c>ProfileChunk</c> events that carry its
    /// nodes (with their parent), samples and time deltas, in pieces. The profile whose code ran longest is read; the
    /// others are named.</summary>
    private static Profile? FromTrace(JsonElement events, CancellationToken ct)
    {
        var byId = new Dictionary<string, (Dictionary<int, Node> Nodes, List<int> Samples, List<double> Deltas)>(StringComparer.Ordinal);
        foreach (var e in events.EnumerateArray())
        {
            if (Str(e, "name") != "ProfileChunk" || !e.TryGetProperty("args", out var args) || !args.TryGetProperty("data", out var data)) continue;
            var key = Str(e, "id") + "@" + (e.TryGetProperty("pid", out var pid) ? pid.GetRawText() : "");
            if (!byId.TryGetValue(key, out var profile)) byId[key] = profile = (new(), [], []);
            if (data.TryGetProperty("cpuProfile", out var cpu))
            {
                if (cpu.TryGetProperty("nodes", out var ns) && ns.ValueKind == JsonValueKind.Array)
                    foreach (var n in ns.EnumerateArray()) AddNode(profile.Nodes, n);
                profile.Samples.AddRange(Ints(cpu, "samples"));
                profile.Deltas.AddRange(Numbers(cpu, "timeDeltas"));
            }
            profile.Deltas.AddRange(Numbers(data, "timeDeltas"));
        }
        var built = byId.Select(p => (Id: p.Key, Profile: Build("Chrome performance trace", p.Value.Nodes, p.Value.Samples, p.Value.Deltas, null, null, ct)))
                        .Where(p => p.Profile is not null).Select(p => (p.Id, Profile: p.Profile!, Code: CodeTime(p.Profile!))).ToList();
        return Pick(built);
    }

    /// <summary>speedscope: shared frames, then profiles either sampled (stacks and weights) or evented (open and close at
    /// a time). Profiles of time only; the one whose code ran longest is read, the others named.</summary>
    private static Profile? FromSpeedscope(JsonElement root, CancellationToken ct)
    {
        var frames = root.GetProperty("shared").GetProperty("frames").EnumerateArray()
            .Select((f, i) =>
            {
                var line = f.TryGetProperty("line", out var l) && l.TryGetInt32(out var ln) ? ln : 0;
                var col = f.TryGetProperty("col", out var c) && c.TryGetInt32(out var cn) ? cn : 0;
                return new Frame(Str(f, "name"), Where(Str(f, "file"), line), string.Create(CultureInfo.InvariantCulture, $"{Str(f, "file")}:{line}:{col}#{i}"));
            }).ToList();
        Frame At(int k) => k >= 0 && k < frames.Count ? frames[k] : new Frame("(unknown)", "", "?");
        var built = new List<(string Id, Profile Profile, double Code)>();
        var skipped = 0;
        var index = 0;
        foreach (var p in root.GetProperty("profiles").EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();
            var id = Str(p, "name") is { Length: > 0 } pn ? pn : "profile " + (++index).ToString(CultureInfo.InvariantCulture);
            double? scale = Str(p, "unit") switch
            {
                "nanoseconds" => 0.001, "microseconds" => 1.0, "milliseconds" => 1000.0, "seconds" => 1_000_000.0, _ => null,
            };
            if (scale is not { } unit) { skipped++; continue; }   // bytes, counts: no time
            try
            {
                var samples = new List<Sample>();
                if (Str(p, "type") == "sampled")
                {
                    var weights = p.TryGetProperty("weights", out var w) ? w.EnumerateArray().Select(x => x.TryGetDouble(out var d) ? d : 0).ToList() : [];
                    var i = 0;
                    var shared = new Dictionary<string, IReadOnlyList<Frame>>(StringComparer.Ordinal);
                    foreach (var stack in p.GetProperty("samples").EnumerateArray())
                    {
                        var ids = stack.EnumerateArray().Select(x => x.TryGetInt32(out var k) ? k : -1).ToList();
                        var key = string.Join(",", ids);
                        if (!shared.TryGetValue(key, out var frameList)) shared[key] = frameList = [.. ids.Select(At)];
                        var weight = (i < weights.Count ? weights[i] : 1) * unit;
                        if (weight > 0) samples.Add(new Sample(frameList, weight));
                        i++;
                    }
                    if (samples.Count > 0) built.Add((id, new Profile("speedscope profile", samples, samples.Count), 0));
                }
                else if (Str(p, "type") == "evented")
                {
                    var open = new List<int>();
                    double? last = null;
                    foreach (var ev in p.GetProperty("events").EnumerateArray())
                    {
                        var at = ev.TryGetProperty("at", out var a) && a.TryGetDouble(out var ad) ? ad : 0;
                        if (last is { } before && at > before && open.Count > 0)
                            samples.Add(new Sample([.. open.Select(At)], (at - before) * unit));
                        last = at;
                        var frame = ev.TryGetProperty("frame", out var fr) && fr.TryGetInt32(out var fi) ? fi : -1;
                        if (Str(ev, "type") == "O") open.Add(frame);
                        else if (Str(ev, "type") == "C" && open.LastIndexOf(frame) is var top and >= 0) open.RemoveRange(top, open.Count - top);
                    }
                    if (samples.Count > 0) built.Add((id, new Profile("speedscope profile", samples, samples.Count, "intervals"), 0));
                }
            }
            catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
            {
                Diagnostics.Swallow("ProfileText.Speedscope", ex);
                skipped++;
            }
        }
        var picked = Pick([.. built.Select(b => (b.Id, b.Profile, CodeTime(b.Profile)))]);
        return picked is null || skipped == 0 ? picked
             : picked with { Others = (picked.Others is null ? "" : picked.Others + " ")
                                     + $"{skipped} profile{(skipped == 1 ? "" : "s")} of the file not of time (bytes, counts) or not readable, left out." };
    }

    /// <summary>The profile whose code ran longest, the others named with theirs.</summary>
    private static Profile? Pick(List<(string Id, Profile Profile, double Code)> built)
    {
        if (built.Count == 0) return null;
        var ordered = built.OrderByDescending(b => b.Code).ToList();
        if (ordered.Count == 1) return ordered[0].Profile;
        return ordered[0].Profile with
        {
            Others = $"This file holds {ordered.Count} profiles (threads or workers); the one whose code ran longest, {ordered[0].Id}, is read. "
                   + "The others: " + string.Join(", ", ordered.Skip(1).Take(8).Select(b => $"{b.Id} ({Duration(b.Code)} of code)"))
                   + (ordered.Count > 9 ? ", …" : "") + ".",
        };
    }

    private static double CodeTime(Profile p) => p.Samples.Where(s => s.Stack.Count > 0 && s.Stack[^1].IsFunction).Sum(s => s.Micros);

    // ── Figures ──

    /// <summary>Each function's self and total time, in microseconds, and the profile's whole time; computed once per
    /// distinct stack.</summary>
    internal static (IReadOnlyList<(Frame Frame, double Self, double Total)> Functions, double Whole) Times(Profile profile, CancellationToken ct = default)
    {
        var self = new Dictionary<Frame, double>();
        var total = new Dictionary<Frame, double>();
        foreach (var (stack, micros) in PerStack(profile, ct))
        {
            if (stack.Count == 0) continue;
            self[stack[^1]] = self.GetValueOrDefault(stack[^1]) + micros;
            foreach (var f in stack.Distinct()) total[f] = total.GetValueOrDefault(f) + micros;   // a recursive one once
        }
        var whole = profile.Samples.Sum(s => s.Micros);
        return ([.. total.Keys.Select(f => (f, self.GetValueOrDefault(f), total[f]))], whole);
    }

    /// <summary>The time each distinct stack was seen.</summary>
    private static List<(IReadOnlyList<Frame> Stack, double Micros)> PerStack(Profile profile, CancellationToken ct)
    {
        var sums = new Dictionary<IReadOnlyList<Frame>, double>(ReferenceEqualityComparer.Instance);
        var i = 0;
        foreach (var s in profile.Samples)
        {
            if ((++i & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
            sums[s.Stack] = (sums.TryGetValue(s.Stack, out var sum) ? sum : 0) + s.Micros;
        }
        return [.. sums.Select(p => ((IReadOnlyList<Frame>)p.Key, p.Value))];
    }

    /// <summary>The function with the most self time, pseudo-frames aside, and its share of the profile.</summary>
    internal static (Frame Frame, double Share)? Hottest(Profile profile)
    {
        var (functions, whole) = Times(profile);
        var top = functions.Where(f => f.Frame.IsFunction).OrderByDescending(f => f.Self).FirstOrDefault();
        return whole <= 0 || top.Self <= 0 ? null : (top.Frame, top.Self / whole);
    }

    private static string Render(Profile profile, int budget, CancellationToken ct)
    {
        var (functions, whole) = Times(profile, ct);
        // Two functions shown alike (same name, file and line: a minified bundle's line 1) are told apart by their column.
        var alike = functions.GroupBy(f => (f.Frame.Label, f.Frame.Where)).Where(g => g.Count() > 1).SelectMany(g => g).Select(f => f.Frame).ToHashSet();
        string Shown(Frame f) => f.Label + (f.Where.Length > 0 ? " — " + f.Where : "")
                               + (alike.Contains(f) && f.Key.LastIndexOf(':') is var c and >= 0 ? " col " + f.Key[(c + 1)..] : "");
        string Share(double micros) => whole <= 0 ? "" : (micros / whole * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%";

        var lines = new List<string>
        {
            string.Create(CultureInfo.InvariantCulture,
                $"{profile.Kind}: {Duration(whole)} sampled, {profile.Count:N0} {profile.Unit}. Read, not recorded: nothing was run."),
        };
        if (profile.Others is { } others) lines.Add(others);

        var real = functions.Where(f => f.Frame.IsFunction).ToList();
        lines.Add("");
        lines.Add("Self time (the function's own code), largest first:");
        var withSelf = real.Where(f => f.Self > 0).OrderByDescending(f => f.Self).ToList();
        var i = 0;
        foreach (var f in withSelf.Take(MaxRows)) lines.Add($"  {++i}. {Shown(f.Frame)} — {Duration(f.Self)} ({Share(f.Self)})");
        if (withSelf.Count > MaxRows) lines.Add($"  [{withSelf.Count - MaxRows} more functions with some self time]");

        lines.Add("");
        lines.Add("Total time (with what it calls), largest first:");
        i = 0;
        foreach (var f in real.OrderByDescending(f => f.Total).Take(MaxRows)) lines.Add($"  {++i}. {Shown(f.Frame)} — {Duration(f.Total)} ({Share(f.Total)})");
        if (real.Count > MaxRows) lines.Add($"  [{real.Count - MaxRows} more functions]");

        var paths = PerStack(profile, ct).Where(p => p.Stack.Count > 0)
            .GroupBy(p => string.Join("\u0001", p.Stack.Select(f => f.Key + "|" + f.Name)))
            .Select(g => (Stack: g.First().Stack, Micros: g.Sum(p => p.Micros)))
            .OrderByDescending(p => p.Micros).ToList();
        if (paths.Count > 0)
        {
            lines.Add("");
            lines.Add("Hottest paths (root › … › where the time is spent):");
            foreach (var (stack, micros) in paths.Take(MaxPaths))
            {
                var names = stack.Select(f => f.Label).ToList();
                var shown = names.Count > PathFrames ? names[0] + " › … › " + string.Join(" › ", names[^(PathFrames - 1)..]) : string.Join(" › ", names);
                lines.Add($"  {Share(micros)} ({Duration(micros)})  {shown}");
            }
            if (paths.Count > MaxPaths) lines.Add($"  [{paths.Count - MaxPaths} more paths]");
        }
        // ⚠ What V8 inlines it counts under the caller: a short function called in a loop ("() => tick(1)") shows its time
        // under the function that calls it, often an anonymous one — read with the caller, not as absent.
        if (profile.Kind != "speedscope profile")
        {
            lines.Add("");
            lines.Add("V8 counts a function it inlined under the function that calls it: a small function called in a loop can "
                    + "show its time under its caller (often \"(anonymous)\").");
        }
        var apart = functions.Where(f => !f.Frame.IsFunction && f.Self > 0).OrderByDescending(f => f.Self).ToList();
        if (apart.Count > 0)
            lines.Add("Not a function of the program: " + string.Join(", ", apart.Select(f => $"{f.Frame.Name} {Duration(f.Self)} ({Share(f.Self)})")) + ".");

        // Cut between lines, never inside one; what is cut is said.
        var sb = new StringBuilder();
        const string Cut = "\n[cut: read_file with start_line pages the raw JSON]";
        foreach (var line in lines)
        {
            if (sb.Length + line.Length + 1 + Cut.Length > budget) { sb.Append(Cut); break; }
            sb.Append(line).Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    private static string Duration(double micros) =>
        micros >= 1_000_000 ? (micros / 1_000_000).ToString("0.00", CultureInfo.InvariantCulture) + " s"
        : micros >= 1000 ? (micros / 1000).ToString("0.0", CultureInfo.InvariantCulture) + " ms"
        : Math.Round(micros).ToString(CultureInfo.InvariantCulture) + " µs";

    private static List<int> Ints(JsonElement e, string name) =>
        e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array
            ? [.. a.EnumerateArray().Select(x => x.TryGetInt32(out var v) ? v : 0)] : [];

    // A producer may write fractional deltas: read as numbers, never as integers only.
    private static List<double> Numbers(JsonElement e, string name) =>
        e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array
            ? [.. a.EnumerateArray().Select(x => x.TryGetDouble(out var v) ? v : 0)] : [];

    private static int? Int(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : null;

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
