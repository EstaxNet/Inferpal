using System.IO;
using System.Text.Json;

namespace Inferpal.Services;

/// <summary>
/// File-based IPC channel that publishes the <em>currently open</em> Visual Studio solution
/// from the in-process <see cref="GhostText.GhostTextPackage"/> (which can query
/// <c>IVsSolution</c> and subscribe to <c>IVsSolutionEvents</c>) to the out-of-process
/// extension host (Commands, ToolWindow, tools) which cannot access VS COM services.
/// </summary>
/// <remarks>
/// <para>
/// Why this exists: an out-of-process extension's <see cref="Directory.GetCurrentDirectory"/>
/// is fixed for the lifetime of the host process — it does <em>not</em> follow solution
/// open/close. Relying on it (or on the set of open editor files) makes solution detection
/// stale: opening a new solution leaves <c>/solution</c>, <c>/map</c> and RAG indexing pinned
/// to the previously-detected directory. This signal is the authoritative source instead.
/// </para>
/// <para>Protocol:</para>
/// <list type="bullet">
///   <item>In-process: calls <see cref="Write"/> on solution open and <see cref="Clear"/> on
///         solution close (see <c>VsSolutionTracker</c>).</item>
///   <item>Out-of-process: calls <see cref="TryReadSolutionPath"/> /
///         <see cref="TryReadSolutionDir"/> as the first step of any solution-root lookup.</item>
/// </list>
/// <para>
/// Unlike <see cref="BuildSignalFile"/> this signal carries no expiry: it represents persistent
/// state (the open solution), not a one-shot event. Readers validate that the recorded path
/// still exists on disk so a stale entry from a crashed session never wins.
/// </para>
/// </remarks>
internal static class ActiveSolutionSignal
{
    private static readonly string TempDir = Path.Combine(Path.GetTempPath(), "Inferpal");

    /// <summary>Full path of the signal file.</summary>
    internal static string FilePath { get; } = Path.Combine(TempDir, "active_solution.json");

    // ── In-process side (VsSolutionTracker) ────────────────────────────────────

    /// <summary>
    /// Records the full path of the currently open <c>.sln</c> file, overwriting any previous value.
    /// </summary>
    internal static void Write(string solutionPath)
    {
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

    /// <summary>Deletes the signal file (called when the solution closes). Safe if absent.</summary>
    internal static void Clear()
    {
        try { File.Delete(FilePath); }
        catch { /* non-critical */ }
    }

    // ── Out-of-process side (tools / ToolWindow) ───────────────────────────────

    /// <summary>
    /// Returns the full path of the currently open <c>.sln</c> file, or <c>null</c> when no
    /// solution is open, the package has not reported one yet, or the recorded file no longer
    /// exists on disk (stale entry from a previous session).
    /// </summary>
    internal static string? TryReadSolutionPath()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var obj  = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(FilePath));
            var path = obj.TryGetProperty("solutionPath", out var p) ? p.GetString() : null;
            // Validate the recorded path still exists — guards against a stale file left behind
            // by a crashed VS session pointing at a moved/deleted solution.
            return !string.IsNullOrEmpty(path) && File.Exists(path) ? path : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Returns the directory containing the currently open <c>.sln</c> file, or <c>null</c>.
    /// </summary>
    internal static string? TryReadSolutionDir()
    {
        var path = TryReadSolutionPath();
        return path is null ? null : Path.GetDirectoryName(path);
    }
}
