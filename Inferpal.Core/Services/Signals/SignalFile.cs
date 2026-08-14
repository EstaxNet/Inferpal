using System.Text.Json;

namespace Inferpal.Services.Signals;

/// <summary>
/// The plumbing every file-signal channel shares: where the files live, whether a recorded process
/// is still alive, what "now" is, and best-effort JSON read/write/delete.
/// </summary>
/// <remarks>
/// <para>
/// Seven channels had each carried their own copy of the same five members —
/// <c>DefaultTempDir</c>, a directory override for tests, an <c>IsProcessAlive</c> with its
/// override, a clock with its override, and try/catch JSON I/O. Four had the liveness check, three
/// the clock, all seven the directory. The copies agreed, which is exactly why they were dangerous:
/// nothing kept them agreeing, and the §22 review had already found channels drifting apart on
/// smaller things.
/// </para>
/// <para>
/// <b>One directory, one clock, one liveness check — because that is the truth.</b> These are
/// properties of the machine, not of a channel: there is a single <c>%TEMP%\Inferpal</c>, a single
/// process table, a single wall clock. Modelling them per channel invited seven answers to
/// questions that have one.
/// </para>
/// <para>
/// ⚠ The overrides are process-wide statics and exist for tests only; production never sets them.
/// See <c>SignalScratchDir</c> in the test project for why writing to the real directory during a
/// test run is not a cosmetic problem.
/// </para>
/// </remarks>
internal static class SignalFile
{
    private static readonly string DefaultDir = Path.Combine(Path.GetTempPath(), "Inferpal");

    /// <summary>Test seam: redirects every channel at once.</summary>
    internal static string? _directoryOverride;

    /// <summary>Test seam: decides staleness without a real process table.</summary>
    internal static Func<int, bool>? _isProcessAliveOverride;

    /// <summary>Test seam: decides expiry without a real clock.</summary>
    internal static Func<DateTimeOffset>? _nowOverride;

    /// <summary>The directory holding every signal file.</summary>
    internal static string Dir => _directoryOverride ?? DefaultDir;

    internal static DateTimeOffset Now => _nowOverride?.Invoke() ?? DateTimeOffset.UtcNow;

    /// <summary>Full path of a channel's file, resolved against the current directory.</summary>
    /// <remarks>
    /// A method rather than a cached string: the override is set after static initialisation, so a
    /// <c>static readonly</c> path would freeze the real directory before a test could redirect it.
    /// </remarks>
    internal static string PathFor(string fileName) => Path.Combine(Dir, fileName);

    /// <summary>Whether the process that wrote a signal is still running.</summary>
    internal static bool IsProcessAlive(int pid)
    {
        if (_isProcessAliveOverride is not null) return _isProcessAliveOverride(pid);
        try { System.Diagnostics.Process.GetProcessById(pid); return true; }
        catch { return false; }
    }

    /// <summary>Serialises <paramref name="payload"/> to <paramref name="path"/>. Never throws.</summary>
    internal static void Write<T>(string path, T payload, string context)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(path, JsonSerializer.Serialize(payload));
        }
        catch (Exception ex) { Diagnostics.Swallow(context, ex); }
    }

    /// <summary>Reads and deserialises, or <c>null</c> when absent, unreadable or malformed.</summary>
    /// <remarks>
    /// A signal is a hint about live state; a corrupt one means "no signal", never an error to
    /// propagate. Callers apply their own staleness rules (PID, age) on top of what comes back.
    /// </remarks>
    internal static T? TryRead<T>(string path) where T : class
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) : null;
        }
        catch { return null; }
    }

    /// <summary>Removes a signal file. Safe if absent, silent on failure.</summary>
    internal static void Delete(string path)
    {
        try { File.Delete(path); }
        catch { /* non-critical: the next writer overwrites, the next reader validates */ }
    }
}
