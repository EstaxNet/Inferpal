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
        Record(context, $"{ex.GetType().Name}: {ex.Message}");

    /// <summary>Records a free-form best-effort note. Never throws.</summary>
    internal static void Record(string context, string detail)
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
            if (FileLoggingEnabled) AppendToFile(entry);
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

    private static void AppendToFile(DiagnosticEntry e)
    {
        try
        {
            var path = LogPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (new FileInfo(path) is { Exists: true, Length: > MaxLogBytes })
                File.WriteAllText(path, $"[log truncated at {MaxLogBytes / (1024 * 1024)} MB — older entries dropped]{Environment.NewLine}");
            File.AppendAllText(path, e.ToLine() + Environment.NewLine);
        }
        catch { /* file logging is best-effort too */ }
    }
}

/// <summary>One recorded diagnostic: when, where (context label), and what.</summary>
internal readonly record struct DiagnosticEntry(DateTime Timestamp, string Context, string Detail)
{
    internal string ToLine() => $"{Timestamp:yyyy-MM-dd HH:mm:ss} [{Context}] {Detail}";
}
