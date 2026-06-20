using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace Inferpal.GhostText;

/// <summary>
/// Minimal <see cref="AsyncPackage"/> whose purposes are:
/// <list type="bullet">
///   <item>Force VS to load <c>Inferpal.dll</c> in-process so that the MEF catalog
///         discovers the ghost-text components (<see cref="GhostTextViewListener"/>,
///         <c>GhostTextAdornmentLayer</c>, …).</item>
///   <item>Subscribe to <c>IVsUpdateSolutionEvents</c> so that build failures trigger
///         a cross-process signal (via <see cref="Services.VsIntegration.BuildSignalFile"/>) that is
///         consumed by the out-of-process <see cref="Services.VsIntegration.VsBuildMonitor"/>.</item>
/// </list>
/// The package is auto-loaded (no solution required) via the <c>pkgdef</c> entry:
/// <c>AutoLoadPackages\{f1536ef8-…}</c>.
/// </summary>
[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[Guid(PackageGuid)]
internal sealed class GhostTextPackage : AsyncPackage
{
    internal const string PackageGuid = "6a7b2c3d-4e5f-4a8b-9c0d-1e2f3a4b5c6d";

    private VsBuildEventHandler? _buildEventHandler;
    private VsSolutionTracker?   _solutionTracker;
    private VsDebuggerTracker?   _debuggerTracker;

    protected override async Task InitializeAsync(CancellationToken ct, IProgress<ServiceProgressData> progress)
    {
        // GetServiceAsync is explicitly designed to be called from any thread in AsyncPackage —
        // the VSTHRD010 warnings below are false positives for that API.
#pragma warning disable VSTHRD010
        // Retrieve VS services (may run on a background thread).
        var buildMgr = await GetServiceAsync(typeof(SVsSolutionBuildManager)) as IVsSolutionBuildManager2;
        var solution = await GetServiceAsync(typeof(SVsSolution))              as IVsSolution;
        // IVsTaskList provides access to the VS Error List so VsBuildEventHandler can
        // embed error messages in the signal file, eliminating the second dotnet build pass.
        var taskList = await GetServiceAsync(typeof(SVsTaskList))              as IVsTaskList;
        // Debugger service + DTE automation feed the @debugger / get_debugger_state signal.
        var shellDbg = await GetServiceAsync(typeof(SVsShellDebugger))         as IVsDebugger;
        var dte      = await GetServiceAsync(typeof(SDTE))                     as EnvDTE.DTE;
#pragma warning restore VSTHRD010

        // IVsUpdateSolutionEvents.Advise must be called on the VS UI thread.
        await JoinableTaskFactory.SwitchToMainThreadAsync(ct);

        // Chat auto-scroll: the tool window is Remote UI (XAML lives in devenv's visual
        // tree), so only this in-process side can call BringIntoView/ScrollToEnd on it.
        try { ChatAutoScroller.Initialize(); } catch { /* non-critical */ }

        if (buildMgr is null || solution is null) return;
        try
        {
            _buildEventHandler = new VsBuildEventHandler(buildMgr, solution, taskList);
        }
        catch { /* non-critical */ }

        try
        {
            // Publishes the open solution to ActiveSolutionSignal so the OOP host resolves the
            // real solution root instead of the (stale) host process working directory.
            _solutionTracker = new VsSolutionTracker(solution);
        }
        catch { /* non-critical */ }

        try
        {
            // Publishes break-mode snapshots to DebuggerStateSignal for the OOP host.
            if (shellDbg is not null)
                _debuggerTracker = new VsDebuggerTracker(shellDbg, dte);
        }
        catch { /* non-critical */ }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && (_buildEventHandler is not null || _solutionTracker is not null || _debuggerTracker is not null))
        {
            try
            {
                // Unadvise* calls must run on the UI thread.
                ThreadHelper.JoinableTaskFactory.Run(async () =>
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    _buildEventHandler?.Dispose();
                    _buildEventHandler = null;
                    _solutionTracker?.Dispose();
                    _solutionTracker = null;
                    _debuggerTracker?.Dispose();
                    _debuggerTracker = null;
                });
            }
            catch { }
        }
        base.Dispose(disposing);
    }
}
