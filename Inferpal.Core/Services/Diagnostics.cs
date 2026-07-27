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

    private static void AppendToFile(DiagnosticEntry e)
    {
        try
        {
            var path = LogPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
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
