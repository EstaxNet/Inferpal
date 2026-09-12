using System.IO;

namespace Inferpal.Services;

/// <summary>
/// Lightweight, never-throwing diagnostics sink for the project's many "best-effort" <c>catch</c>
/// blocks. Records a swallowed exception (or a free-form note) into a bounded in-memory ring buffer
/// and the debug trace, so a field issue can be inspected without telemetry. Optional file logging
/// (off by default) appends to <c>%AppData%/Inferpal/diagnostics.log</c> for deeper diagnosis.
/// </summary>
/// <remarks>
/// Honors the 100%-local, zero-footprint ethic: nothing leaves the machine and nothing is written to
/// disk unless <see cref="FileLoggingEnabled"/> is turned on. Every public entry point is wrapped so
/// the sink can never throw — it runs <em>inside</em> catch blocks, where an exception would defeat
/// the purpose. The <see cref="Snapshot"/> is what a future <c>/diagnostics</c> command or VS Output
/// pane would render.
/// </remarks>
internal static class Diagnostics
{
    /// <summary>Max entries kept in the in-memory ring before the oldest is dropped.</summary>
    internal const int Capacity = 200;

    private static readonly LinkedList<DiagnosticEntry> _ring = new();
    private static readonly object _gate = new();

    /// <summary>When true, entries are also appended to the on-disk log. Off by default.</summary>
    internal static bool FileLoggingEnabled { get; set; }

    /// <summary>Overrides the log file path (tests).</summary>
    internal static string? LogPathOverride { get; set; }

    private static string LogPath => LogPathOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Inferpal", "diagnostics.log");

    /// <summary>Records a swallowed exception with a short context label. Never throws.</summary>
    internal static void Swallow(string context, Exception ex) =>
        Record(context, $"{ex.GetType().Name}: {ex.Message}{Where(ex)}", ex);

    /// <summary>
    /// Where it was thrown, compacted to the three innermost frames — <c>Type.Method ← caller ←
    /// caller</c>.
    /// </summary>
    /// <remarks>
    /// ⚠ Without them an exception whose message says nothing cannot be diagnosed from a support
    /// bundle: <c>NullReferenceException: Object reference not set to an instance of an object.</c>
    /// names neither the file, nor the method, nor what was null. Method names come from metadata,
    /// not from the PDB, so they survive a Release build with no symbols (line numbers do not).
    ///
    /// ⚠ Bounded: three frames, and the type name without its namespace. A ring of 200 entries ends
    /// up pasted into a public issue.
    /// </remarks>
    private static string Where(Exception ex)
    {
        try
        {
            var stack = ex.StackTrace;
            if (string.IsNullOrEmpty(stack)) return string.Empty;

            var frames = new List<string>(3);
            foreach (var raw in stack!.Split('\n'))
            {
                var frame = CompactFrame(raw);
                if (frame.Length == 0) continue;
                frames.Add(frame);
                if (frames.Count == 3) break;
            }
            // ⚠ TargetSite both as a fallback AND at the front: the JIT inlines, and a Release
            // stack may no longer carry the frame that threw. Measured in the field on 2026-09-12 -
            // the SAME failure produced two different stacks, depending on tier-0 vs tier-1.
            var thrower = Site(ex);
            if (thrower.Length > 0 && (frames.Count == 0 || frames[0] != thrower))
                frames.Insert(0, thrower);
            if (frames.Count > 3) frames.RemoveRange(3, frames.Count - 3);

            return frames.Count == 0 ? string.Empty : " @ " + string.Join(" ← ", frames);
        }
        catch { return string.Empty; }   // the diagnostics channel never breaks its caller
    }

    /// <summary>
    /// <c>   at Inferpal.ToolWindow.SettingsData.&lt;SaveCoreAsync&gt;d__12.MoveNext() in …:line 42</c>
    /// → <c>SettingsData.SaveCoreAsync</c>. The state machine of an <c>async</c> method is what the
    /// stack carries most of the time here: rendering it as-is would be unreadable.
    /// </summary>
    internal static string CompactFrame(string rawFrame)
    {
        var line = rawFrame.Trim();
        if (line.Length == 0) return string.Empty;
        // ⚠ A rethrown stack carries "--- End of stack trace from previous location ---": that is
        // not a frame, and rendering it ate one of the three slots.
        if (line.StartsWith("---", StringComparison.Ordinal)) return string.Empty;
        if (line.StartsWith("at ", StringComparison.Ordinal)) line = line.Substring(3);

        var inKeyword = line.IndexOf(" in ", StringComparison.Ordinal);
        if (inKeyword > 0) line = line.Substring(0, inKeyword);

        var paren = line.IndexOf('(');
        if (paren > 0) line = line.Substring(0, paren);

        // Async state machine or lambda: the real name sits between angle brackets.
        // ⚠ The LAST group, not the first: a closure class is written
        // "<>c__DisplayClass89_0.<SaveCoreAsync>b__0", and taking the first "<>" produced an EMPTY
        // name - measured in the field: "InferpalSettingsData. ← InferpalSettingsData.".
        var open = line.LastIndexOf('<');
        var close = open >= 0 ? line.IndexOf('>', open + 1) : -1;
        if (open >= 0 && close > open + 1)
        {
            var method = line.Substring(open + 1, close - open - 1);
            var owner = line.Substring(0, open).TrimEnd('.', '+');
            // The owner still carries "<>c__DisplayClass…" or "d__12": keep only the real type.
            owner = StripClosure(owner);
            line = owner.Length == 0 ? method : owner + "." + method;
        }

        // Namespace dropped: Type.Method is what is kept.
        var lastDot = line.LastIndexOf('.');
        if (lastDot > 0)
        {
            var owner = line.Substring(0, lastDot);
            var ownerDot = owner.LastIndexOf('.');
            if (ownerDot >= 0) line = line.Substring(ownerDot + 1);
        }

        return line.Length > 80 ? line.Substring(0, 80) : line;
    }

    /// <summary>Records a free-form best-effort note. Never throws.</summary>
    internal static void Record(string context, string detail) => Record(context, detail, null);

    /// <param name="full">
    /// The original exception, when there is one. ⚠ It serves the FILE log only: the ring stays
    /// short because it ends up pasted into an issue, but a file the user turned on deliberately can
    /// carry the whole stack - that is where a failure three frames cannot locate reads in full.
    /// </param>
    private static void Record(string context, string detail, Exception? full)
    {
        try
        {
            var entry = new DiagnosticEntry(DateTime.Now, context, detail);
            lock (_gate)
            {
                _ring.AddLast(entry);
                while (_ring.Count > Capacity) _ring.RemoveFirst();
            }
            System.Diagnostics.Debug.WriteLine($"[Inferpal] {context}: {detail}");
            if (FileLoggingEnabled) AppendToFile(entry, full);
        }
        catch { /* diagnostics must never throw — it runs inside catch blocks */ }
    }

    /// <summary>
    /// Records a line the user wrote - a permission rule, a custom tool, a command template - that
    /// the product could not use. Blank lines and <c>#</c> comments are <b>never</b> reported.
    /// </summary>
    /// <remarks>
    /// The gesture repeated itself in three parsers, and it was missing from all three. Skipping a
    /// faulty line rather than throwing the whole set away is the right arbitration - saying so is
    /// what was missing: a user who writes a rule, a tool or a command believes it is in place.
    ///
    /// Two precautions, because a noisy channel stops being read: what the parsers skip NORMALLY
    /// (blank line, comment) is not reported, and the line is bounded - it comes from the settings
    /// and can be long.
    ///
    /// The text is in ENGLISH, like this whole channel: it is read by users of all ten languages
    /// and it also carries exception messages. Locked by rule 13 of ConventionCoverageTests.
    /// </remarks>
    /// <returns><c>true</c> when the line was actually reported - i.e. it was a real rejection and
    /// not something the parsers skip normally. Callers that also want to <b>count</b> rejections
    /// read this rather than re-implementing the same test.</returns>
    internal static bool DroppedLine(string context, string reason, string? line)
    {
        // Two net472 traps, and this file is shared BY SOURCE with Inferpal.InProc
        // (<Compile Link>) - so it compiles under both:
        //  - StartsWith(char) does not exist in net472: the string overload is the only common one;
        //  - net472 reference assemblies carry no [NotNullWhen] on IsNullOrWhiteSpace, so
        //    nullability flow does not pass through it and the dereference comes out as CS8602 -
        //    an error in Release only, where warnings are errors. Hence the explicit null test.
        if (line is null) return false;
        var trimmed = line.TrimStart();
        if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal)) return false;

        var excerpt = line.Trim();
        if (excerpt.Length > 120) excerpt = excerpt[..120] + "…";
        Record(context, $"{reason}: {excerpt}");
        return true;
    }

    /// <summary>Snapshot of the in-memory ring, oldest first.</summary>
    internal static IReadOnlyList<DiagnosticEntry> Snapshot()
    {
        lock (_gate) return [.. _ring];
    }

    /// <summary>Clears the in-memory ring.</summary>
    internal static void Clear()
    {
        lock (_gate) _ring.Clear();
    }

    /// <summary>Cap on the opt-in log file. A forgotten `/diagnostics on` must not grow without
    /// bound (pre-1.6.0 architecture review); past the cap the file restarts with a marker rather than
    /// silently dropping new lines — the RECENT entries are the ones a bug report needs.</summary>
    private const long MaxLogBytes = 5 * 1024 * 1024;

    /// <summary>The method the runtime says threw, independent of how the stack is formatted.</summary>
    private static string Site(Exception ex)
    {
        try
        {
            var site = ex.TargetSite;
            if (site is null) return string.Empty;
            var owner = site.DeclaringType?.Name;
            var name  = Clean(site.Name);
            if (name.Length == 0) return string.Empty;
            return string.IsNullOrEmpty(owner) ? name : owner + "." + name;
        }
        catch { return string.Empty; }
    }

    /// <summary>"&lt;SaveCoreAsync&gt;b__0" → "SaveCoreAsync"; a bare "MoveNext" says nothing.</summary>
    private static string Clean(string method)
    {
        var open = method.LastIndexOf('<');
        var close = open >= 0 ? method.IndexOf('>', open + 1) : -1;
        if (open >= 0 && close > open + 1) return method.Substring(open + 1, close - open - 1);
        return method is "MoveNext" or ".ctor" ? string.Empty : method;
    }

    /// <summary>Drops the generated class from a frame owner: "X.&lt;&gt;c__DisplayClass1" → "X".</summary>
    private static string StripClosure(string owner)
    {
        var generated = owner.IndexOf("<>", StringComparison.Ordinal);
        if (generated > 0) owner = owner.Substring(0, generated).TrimEnd('.', '+');
        return owner;
    }

    private static void AppendToFile(DiagnosticEntry e, Exception? full = null)
    {
        try
        {
            var path = LogPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (new FileInfo(path) is { Exists: true, Length: > MaxLogBytes })
                File.WriteAllText(path, $"[log truncated at {MaxLogBytes / (1024 * 1024)} MB — older entries dropped]{Environment.NewLine}");
            File.AppendAllText(path, e.ToLine() + Environment.NewLine);
            if (full is not null)
                File.AppendAllText(path, full.ToString() + Environment.NewLine + Environment.NewLine);
        }
        catch { /* file logging is best-effort too */ }
    }
}

/// <summary>One recorded diagnostic: when, where (context label), and what.</summary>
internal readonly record struct DiagnosticEntry(DateTime Timestamp, string Context, string Detail)
{
    internal string ToLine() => $"{Timestamp:yyyy-MM-dd HH:mm:ss} [{Context}] {Detail}";
}
