using System.IO;
using System.Text.Json;

namespace Inferpal.Services.Signals;

/// <summary>
/// File-based IPC channel that publishes the <em>currently open</em> Visual Studio solution — or the folder it has open
/// without one — from the in-process <c>GhostTextPackage</c> (which can query
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
///   <item>Out-of-process: calls <see cref="TryReadSolutionPath"/> as the first step of any
///         solution-root lookup — through <c>SolutionExtent</c> when it is a root that is wanted.</item>
/// </list>
/// <para>
/// Unlike <see cref="BuildSignalFile"/> this signal carries no expiry: it represents persistent
/// state (the open solution), not a one-shot event. Readers validate that the recorded path
/// still exists on disk, and that its writer is the devenv running now (<see cref="SignalFile.WriterAlive"/>):
/// ⚠ a crashed devenv's solution still exists, and the next devenv that gets its PID — the file is keyed by it — read it
/// as its own until it opened one: the root pinned there, <c>/fix-build</c> building it.
/// </para>
/// </remarks>
internal static class ActiveSolutionSignal
{
    /// <summary>Full path of the signal file, scoped to the declared VS instance.</summary>
    internal static string FilePath => SignalFile.ScopedPathFor("active_solution");

    // ── In-process side (VsSolutionTracker) ────────────────────────────────────

    /// <summary>
    /// Records the full path of the currently open <c>.sln</c> file, overwriting any previous value.
    /// </summary>
    internal static void Write(string solutionPath)
    {
        SignalFile.Write(FilePath,
            new { solutionPath, pid = SignalFile.CurrentPid, ts = SignalFile.Now.ToUnixTimeMilliseconds() },
            "ActiveSolutionSignal.Write");
    }

    /// <summary>
    /// Records the folder Visual Studio has open with no solution ("Open Folder"), overwriting any previous value.
    /// </summary>
    /// <remarks>
    /// The same file as the solution: one thing is open at a time, and the scripts that prove the in-process package
    /// loaded test that file's existence. A folder is never read as a solution (<see cref="TryReadSolutionPath"/>).
    /// </remarks>
    internal static void WriteFolder(string folderPath)
    {
        SignalFile.Write(FilePath,
            new { folderPath, pid = SignalFile.CurrentPid, ts = SignalFile.Now.ToUnixTimeMilliseconds() },
            "ActiveSolutionSignal.WriteFolder");
    }

    /// <summary>Deletes the signal file (called when the solution or the folder closes). Safe if absent.</summary>
    internal static void Clear()
    {
        SignalFile.Delete(FilePath);
    }

    // ── Out-of-process side (tools / ToolWindow) ───────────────────────────────

    /// <summary>
    /// Returns the full path of the currently open <c>.sln</c> file, or <c>null</c> when no
    /// solution is open, the package has not reported one yet, or the recorded file no longer
    /// exists on disk (stale entry from a previous session).
    /// </summary>
    internal static string? TryReadSolutionPath() =>
        // Validate the recorded path still exists — guards against a stale file left behind
        // by a crashed VS session pointing at a moved/deleted solution.
        TryReadField("solutionPath") is { } path && File.Exists(path) ? path : null;

    /// <summary>
    /// Returns the folder Visual Studio has open with no solution, or <c>null</c> when a solution (or nothing) is open,
    /// the package has not reported it yet, or the folder no longer exists.
    /// </summary>
    /// <remarks>One spelling, whichever event wrote it (VS ends one with a separator): a root that differs by a trailing
    /// separator is re-pinned, and the index restarts.</remarks>
    internal static string? TryReadOpenFolder()
    {
        if (TryReadField("folderPath") is not { } path || !Directory.Exists(path)) return null;
        try
        {
            // By hand: also compiled into the net472 in-process assembly, which has no TrimEndingDirectorySeparator.
            var full    = Path.GetFullPath(path);
            var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return trimmed.Length < (Path.GetPathRoot(full)?.Length ?? 0) ? full : trimmed;   // a drive root keeps its "\"
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    private static string? TryReadField(string field)
    {
        // This file is written by a Visual Studio in-process package and the folder is
        // machine-wide. A host with no such peer (Inferpal.Host, i.e. VS Code) would otherwise
        // answer with the solution open in Visual Studio — and win, since callers consult this
        // first. Someone else's state is not a better answer than no answer.
        if (!SignalScope.HasVsInProcessPeer) return null;

        try
        {
            if (!File.Exists(FilePath)) return null;
            var obj  = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(FilePath));
            var path = obj.TryGetProperty(field, out var p) ? p.GetString() : null;
            // Its writer, when it said who it is (an older version did not): an earlier devenv's under the same PID is no
            // answer about this one.
            if (obj.TryGetProperty("pid", out var pid) && pid.TryGetInt32(out var writer)
                && obj.TryGetProperty("ts", out var ts) && ts.TryGetInt64(out var stamp)
                && !SignalFile.WriterAlive(writer, stamp)) return null;
            return string.IsNullOrEmpty(path) ? null : path;
        }
        catch { return null; }
    }

    // ⚠ No "solution directory" accessor: the .sln's folder is not the workspace root when the solution lists projects
    // beside it (UI\UI.sln naming ..\DAL\DAL.csproj) — the root is SolutionExtent.OfActiveSolution().Root.
}
