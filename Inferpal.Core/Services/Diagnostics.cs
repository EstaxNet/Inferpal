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
    /// The message that NAMES the cause: a wrapper exception hides its own.
    /// </summary>
    /// <remarks>
    /// ⚠ <c>TypeInitializationException.Message</c> reads "The type initializer for 'X' threw an
    /// exception" — a fixed sentence that names nothing, and it is exactly what the user is shown
    /// when the native SQLite library fails to load, i.e. when the semantic index does not exist at
    /// all. Same for <c>TargetInvocationException</c> and <c>AggregateException</c>. Unwrapping
    /// stops at the <b>three</b> known wrapper types: the message of an <c>IOException</c> or an
    /// <c>HttpRequestException</c> is already the right one, and unwrapping past it would replace
    /// an exact cause with a deeper, less actionable one.
    /// </remarks>
    internal static string RootMessage(Exception ex)
    {
        var cur = ex;
        while (cur is TypeInitializationException or System.Reflection.TargetInvocationException
                   or AggregateException
               && cur.InnerException is { } inner)
            cur = inner;
        return cur.Message;
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

    /// <summary>
    /// <see cref="DroppedLine"/> for a parser that runs <b>again and again</b>: the same rejection
    /// is reported once per distinct <paramref name="key"/>, not once per pass.
    /// </summary>
    /// <param name="key">
    /// What identifies the rejection across passes — the offending line, or the name it claims.
    /// Once it stops being rejected, <see cref="ForgetDroppedLine"/> makes the next occurrence
    /// speak again.
    /// </param>
    /// <remarks>
    /// <para>
    /// ⚠ <b>The "a noisy channel stops being read" caution is written in the remarks of
    /// <see cref="DroppedLine"/>, and three of its four callers undo it by calling it in a
    /// loop.</b> The ring only keeps <see cref="Capacity"/> entries:
    /// </para>
    /// <list type="bullet">
    ///   <item><c>CustomTools</c> is reparsed on every read of <c>ToolRegistry.Definitions</c>,
    ///         that is at least three times per request to the model — one bad line wiped the whole
    ///         ring within a few agent turns, and with it the failure being looked for.</item>
    ///   <item><c>UserTemplates</c> is reloaded by autocomplete, so <b>on every keystroke</b>
    ///         while a slash command is being typed.</item>
    ///   <item><c>PinnedFiles</c> had already understood this and carried its own set of
    ///         already-reported paths, with the reason in a comment. The gesture lives here now, so
    ///         that the next repeated parser inherits it instead of rediscovering it.</item>
    /// </list>
    /// <para>
    /// The memory is emptied by <see cref="Clear"/>: without that, a <c>/diagnostics clear</c>
    /// would hide those lines for good, just as the user asked for a clean ring.
    /// </para>
    /// <para>
    /// ⚠ The de-duplication can NOT live in <see cref="DroppedLine"/>: its return value feeds the
    /// list that <c>/permissions</c> and the settings window display, and a <c>false</c> on the
    /// second call would empty that listing.
    /// </para>
    /// </remarks>
    /// <returns><c>true</c> when this pass actually reported it.</returns>
    internal static bool DroppedLineOnce(string context, string reason, string key, string? line)
    {
        var slot = context + "\u0001" + key;
        lock (_saidGate)
            if (!_said.Add(slot)) return false;

        if (DroppedLine(context, reason, line)) return true;

        // What the parsers normally skip (a blank line, a comment) is not a rejection: it must not
        // occupy the memory, otherwise the real line taking that same key later would stay mute.
        lock (_saidGate) _said.Remove(slot);
        return false;
    }

    /// <summary>
    /// The rejection identified by <paramref name="key"/> is over — the next one will be reported.
    /// </summary>
    /// <remarks>
    /// Without this, "reported once" would become "reported once in the life of the process": a
    /// pinned file that comes back then disappears again, a tool renamed then duplicated again,
    /// would never say so.
    /// </remarks>
    internal static void ForgetDroppedLine(string context, string key)
    {
        lock (_saidGate) _said.Remove(context + "\u0001" + key);
    }

    /// <summary>
    /// <see cref="Record"/> for a note that would otherwise repeat <b>per item</b> — once per
    /// distinct <paramref name="key"/>, forgotten by <see cref="Clear"/> like
    /// <see cref="DroppedLineOnce"/>, with which it shares its memory.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠ <b>This is the half of the class <see cref="DroppedLineOnce"/> had left open.</b> That one
    /// covers a rejected configuration <i>line</i>; the ring drowns just as well under a note
    /// emitted per file or per pattern — and the multiplier is worse there:
    /// <c>IndexExclusions</c> traced a pathological pattern <b>once per indexed file</b>, that is
    /// thousands of entries in a ring that keeps <see cref="Capacity"/>. The failure one came
    /// looking for was long gone.
    /// </para>
    /// <para>
    /// ⚠ Kept separate from <see cref="DroppedLineOnce"/> rather than reused: that one skips blank
    /// lines and <c>#</c> comments, which is right for a list written line by line and wrong for a
    /// pattern coming from a JSON array — a glob <c>#something</c> would be taken for a comment
    /// there and **never** reported.
    /// </para>
    /// </remarks>
    /// <returns><c>true</c> when this call actually recorded it.</returns>
    internal static bool RecordOnce(string context, string detail, string key)
    {
        var slot = context + "\u0001" + key;
        lock (_saidGate)
            if (!_said.Add(slot)) return false;

        Record(context, detail);
        return true;
    }

    private static readonly HashSet<string> _said = new(StringComparer.Ordinal);
    private static readonly object _saidGate = new();

    /// <summary>Snapshot of the in-memory ring, oldest first.</summary>
    internal static IReadOnlyList<DiagnosticEntry> Snapshot()
    {
        lock (_gate) return [.. _ring];
    }

    /// <summary>Clears the in-memory ring, and with it what <see cref="DroppedLineOnce"/> remembers
    /// having said — see its remarks.</summary>
    internal static void Clear()
    {
        lock (_gate) _ring.Clear();
        lock (_saidGate) _said.Clear();
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
    // Serialises the file writes: two entries recorded at once collided on the open file (the second
    // was lost) or both truncated it, erasing an entry just written.
    private static readonly object _fileGate = new();

    private static void AppendToFile(DiagnosticEntry e, Exception? full = null)
    {
        try
        {
            lock (_fileGate)
            {
                var path = LogPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (new FileInfo(path) is { Exists: true, Length: > MaxLogBytes })
                    File.WriteAllText(path, $"[log truncated at {MaxLogBytes / (1024 * 1024)} MB — older entries dropped]{Environment.NewLine}");
                File.AppendAllText(path, e.ToLine() + Environment.NewLine);
                if (full is not null)
                    File.AppendAllText(path, full.ToString() + Environment.NewLine + Environment.NewLine);
            }
        }
        catch { /* file logging is best-effort too */ }
    }
}

/// <summary>One recorded diagnostic: when, where (context label), and what.</summary>
internal readonly record struct DiagnosticEntry(DateTime Timestamp, string Context, string Detail)
{
    internal string ToLine() => $"{Timestamp:yyyy-MM-dd HH:mm:ss} [{Context}] {Detail}";
}
