using System.IO;
using System.Text.Json;

namespace Inferpal.Services.VsIntegration;

/// <summary>
/// Durable, never-expiring record of the <em>last solution Inferpal successfully resolved</em>.
/// </summary>
/// <remarks>
/// <para>
/// This is the last-resort fallback for solution-root lookup, sitting <em>below</em>
/// <see cref="ActiveSolutionSignal"/> and the live open-editor / CWD probes. It exists because
/// every live source is ephemeral:
/// </para>
/// <list type="bullet">
///   <item><see cref="ActiveSolutionSignal"/> is <em>cleared on solution close</em> and is absent
///         whenever the in-process package has not loaded (or has not published yet).</item>
///   <item>The set of open editor files is empty when the user is working only in the chat tool
///         window with no document open.</item>
///   <item>The out-of-process host's <see cref="Directory.GetCurrentDirectory"/> never follows
///         solution open/close, so it rarely sits near the real <c>.sln</c>.</item>
/// </list>
/// <para>
/// When all three miss, <c>/solution</c>, <c>/map</c> and friends would otherwise hard-fail even
/// though the user has clearly been working in a solution this session. This cache remembers it.
/// Unlike <see cref="ActiveSolutionSignal"/> it is <em>never cleared</em>: it represents "the last
/// solution we knew about", not "the solution currently open". Readers validate the recorded path
/// still exists on disk, and explicit/live sources always win over it, so a stale entry can only
/// ever surface when nothing better is available.
/// </para>
/// </remarks>
internal static class LastKnownSolutionFile
{
    private static readonly string TempDir = Path.Combine(Path.GetTempPath(), "Inferpal");

    /// <summary>Full path of the cache file.</summary>
    internal static string FilePath { get; } = Path.Combine(TempDir, "last_solution.json");

    /// <summary>
    /// Records the full path of a successfully resolved <c>.sln</c>, overwriting any previous value.
    /// No-ops on a null/empty/non-existent path so a failed lookup never pollutes the cache.
    /// </summary>
    internal static void Record(string? solutionPath)
    {
        if (string.IsNullOrEmpty(solutionPath) || !File.Exists(solutionPath)) return;
        try
        {
            Directory.CreateDirectory(TempDir);
            var json = JsonSerializer.Serialize(new
            {
                solutionPath,
                ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });
            File.WriteAllText(FilePath, json);
        }
        catch { /* non-critical */ }
    }

    /// <summary>
    /// Returns the last recorded <c>.sln</c> path, or <c>null</c> when nothing was ever recorded or
    /// the recorded file no longer exists on disk (moved/deleted since it was cached).
    /// </summary>
    internal static string? TryReadSolutionPath()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var obj  = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(FilePath));
            var path = obj.TryGetProperty("solutionPath", out var p) ? p.GetString() : null;
            return !string.IsNullOrEmpty(path) && File.Exists(path) ? path : null;
        }
        catch { return null; }
    }
}
