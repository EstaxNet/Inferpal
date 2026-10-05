using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Inferpal.Services.Tools;

namespace Inferpal.Services.Signals;

/// <summary>
/// Monitors for build failures and fires <see cref="BuildFailed"/> when errors are found.
///
/// <para><b>Architecture — cross-process IPC:</b></para>
/// <para>
/// The legacy COM interface <c>IVsUpdateSolutionEvents</c> is only accessible from within
/// the VS process.  This class runs in the VisX out-of-process extension host and therefore
/// cannot subscribe to it directly.
/// </para>
/// <para>
/// Instead, the in-process <see cref="GhostText.GhostTextPackage"/> subscribes to build
/// events via <see cref="GhostText.VsBuildEventHandler"/> and, on failure, writes a JSON
/// signal file (see <see cref="BuildSignalFile"/>) that contains the solution path AND the
/// error messages already collected from the VS Error List.
/// <see cref="InitializeAsync"/> sets up a <see cref="FileSystemWatcher"/> on that file;
/// when the file appears the monitor reads the errors and fires <see cref="BuildFailed"/>
/// immediately — no second build required.
/// </para>
/// <para>
/// Fallback: if the signal file carries no pre-collected errors (e.g. the in-process package
/// could not access <c>IVsTaskList</c>), the monitor falls back to running
/// <c>dotnet build</c> to collect them.
/// </para>
/// </summary>
internal sealed class VsBuildMonitor : IDisposable
{
    /// <summary>
    /// Fired on a background thread when a build completes with at least one compilation error.
    /// <para><c>errorCount</c> — number of distinct error lines <b>found</b>, which is not
    /// necessarily the number listed in <c>errorLines</c>: the text is capped and says so.</para>
    /// <para><c>errorLines</c> — newline-separated error messages.</para>
    /// </summary>
    public event Action<int, string>? BuildFailed;

    /// <summary>A build succeeded: the banner of an earlier failure describes a solution that builds now.</summary>
    public event Action? BuildSucceeded;

    private FileSystemWatcher? _watcher;

    // ── Initialization ─────────────────────────────────────────────────────────

    /// <summary>
    /// Sets up a <see cref="FileSystemWatcher"/> that reacts to the signal file written by
    /// the in-process <see cref="GhostText.GhostTextPackage"/> when a VS build fails.
    /// Also checks for a signal that may have been written before the watcher was ready.
    /// Safe to call multiple times — only the first call has effect.
    /// </summary>
    public Task InitializeAsync()
    {
        if (_watcher is not null) return Task.CompletedTask;

        // Ensure the shared temp directory exists before creating the watcher.
        BuildSignalFile.EnsureDir();

        // Check for a signal that was written before we started watching
        // (e.g., build failed while the tool window was still initialising).
        var early = BuildSignalFile.TryRead();
        BuildSignalFile.Clear();
        if (!string.IsNullOrEmpty(early.SolutionPath))
            Dispatch(early);

        // Watch for future signals from the in-process package.
        try
        {
            _watcher = new FileSystemWatcher(
                Path.GetDirectoryName(BuildSignalFile.FilePath)!,
                Path.GetFileName(BuildSignalFile.FilePath))
            {
                NotifyFilter        = NotifyFilters.FileName | NotifyFilters.LastWrite,
                EnableRaisingEvents = true,
            };
            _watcher.Created += OnSignalFileEvent;
            _watcher.Changed += OnSignalFileEvent;
        }
        catch { /* non-critical */ }

        return Task.CompletedTask;
    }

    private void OnSignalFileEvent(object sender, FileSystemEventArgs e)
    {
        // Brief pause so the in-process writer has time to finish flushing.
        Thread.Sleep(150);

        var payload = BuildSignalFile.TryRead();
        BuildSignalFile.Clear();

        if (!string.IsNullOrEmpty(payload.SolutionPath))
            Dispatch(payload);
    }

    /// <summary>A success clears, a failure raises the banner.</summary>
    internal void Dispatch(BuildSignalFile.SignalPayload payload)
    {
        if (payload.Succeeded) BuildSucceeded?.Invoke();
        else                   FireOrFallback(payload);
    }

    // ── Fast path vs. fallback ─────────────────────────────────────────────────

    /// <summary>
    /// Fires <see cref="BuildFailed"/> with whatever error information is available.
    /// <para>
    /// If the signal file contains errors collected in-process by
    /// <see cref="GhostText.VsBuildEventHandler"/>, they are forwarded directly.
    /// </para>
    /// <para>
    /// If the error list is empty (VS Error List was not yet populated when the signal was
    /// written — a known timing issue), the event is still fired with an empty error string
    /// so the "Build Failed" banner appears.  The user can then click "Fix with AI" which
    /// runs a fresh build check to obtain the actual errors.
    /// </para>
    /// </summary>
    private void FireOrFallback(BuildSignalFile.SignalPayload payload)
    {
        if (payload.ErrorLines.Length > 0)
        {
            // Fast path: errors were captured in-process — use them directly. The COUNT is the build's, not the number
            // of lines: those are capped, and 200 errors announced as 30 is worse than no number.
            BuildFailed?.Invoke(Math.Max(payload.ErrorCount, payload.ErrorLines.Length), string.Join("\n", payload.ErrorLines));
        }
        else
        {
            // Error details were not available at signal time (IVsTaskList timing issue).
            // Fire with empty detail — the banner still appears and the user can click
            // "Fix with AI" to trigger /fix-build which runs a fresh dotnet build check.
            BuildFailed?.Invoke(0, string.Empty);
        }
    }

    /// <summary>
    /// Testing helper — fires <see cref="BuildFailed"/> with a synthetic payload.
    /// Used by the <c>/test-build-banner</c> slash command to verify the OOP banner pipeline.
    /// </summary>
    internal void FireTestBuildFailed() =>
        BuildFailed?.Invoke(1, "TestFile.cs(1,1): error CS9999: Synthetic test error from /test-build-banner");

    // ── IDisposable ────────────────────────────────────────────────────────────

    public void Dispose()
    {
        _watcher?.Dispose();
        _watcher = null;
    }
}
