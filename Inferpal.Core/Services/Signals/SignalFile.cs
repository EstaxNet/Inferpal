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

    // ── Keyed file-name grammar (§22 tranche 2) ─────────────────────────────────
    // One owner for the "<baseName>.<key>.json" shape: the path builders, the name parser and the
    // enumeration all live here, so a future change to the grammar cannot update one copy and
    // silently strand another (the drift this class exists to prevent).

    /// <summary>Full path of a channel file keyed explicitly: <c>&lt;baseName&gt;.&lt;key&gt;.json</c>.</summary>
    internal static string KeyedPathFor(string baseName, int key) => PathFor($"{baseName}.{key}.json");

    /// <summary>
    /// Full path of a family-A channel file, scoped to the declared VS instance:
    /// <c>&lt;baseName&gt;.&lt;devenvPid&gt;.json</c>, or the legacy unscoped
    /// <c>&lt;baseName&gt;.json</c> when no instance was declared.
    /// </summary>
    /// <remarks>
    /// Migration rule (family A): once a key is declared, the legacy unscoped file is a different
    /// name and is therefore never read — a leftover from an earlier version must not be mistaken
    /// for this instance's state. It is not deleted either: an old-version pair on the same
    /// machine may still be using it.
    /// </remarks>
    internal static string ScopedPathFor(string baseName) =>
        SignalScope.VsInstanceKey is int key ? KeyedPathFor(baseName, key) : PathFor($"{baseName}.json");

    /// <summary>
    /// Whether <paramref name="fileName"/> matches the keyed grammar for
    /// <paramref name="baseName"/>. The legacy unscoped <c>&lt;baseName&gt;.json</c> never
    /// matches: the length guard is what keeps the overlapping prefix/suffix of that shorter name
    /// out of the slice below (it once threw on exactly that input and a blanket catch turned the
    /// crash into an empty enumeration).
    /// </summary>
    internal static bool IsKeyedName(string fileName, string baseName)
    {
        var prefix = baseName + ".";
        const string suffix = ".json";
        if (fileName.Length <= prefix.Length + suffix.Length) return false;
        if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return false;
        var middle = fileName[prefix.Length..^suffix.Length];
        return middle.All(char.IsAsciiDigit);
    }

    /// <summary>Every keyed file of <paramref name="baseName"/> currently on disk. Never throws.</summary>
    internal static string[] EnumerateKeyedPaths(string baseName)
    {
        try
        {
            if (!Directory.Exists(Dir)) return Array.Empty<string>();
            return Directory.EnumerateFiles(Dir, baseName + ".*")
                            .Where(p => IsKeyedName(Path.GetFileName(p), baseName))
                            .ToArray();
        }
        catch (Exception ex)
        {
            // A read-path fallback, not cleanup: trace it — a persistent failure here silently
            // disables every reader of the channel, which must be visible in /diagnostics.
            Diagnostics.Swallow("SignalFile.EnumerateKeyedPaths", ex);
            return Array.Empty<string>();
        }
    }

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
