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
///         a cross-process signal (via <see cref="Services.Signals.BuildSignalFile"/>) that is
///         consumed by the out-of-process <see cref="Services.Signals.VsBuildMonitor"/>.</item>
/// </list>
/// The package is auto-loaded (no solution required) via the <c>pkgdef</c> entry:
/// <c>AutoLoadPackages\{f1536ef8-…}</c>.
/// </summary>
[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[Guid(PackageGuid)]
internal sealed class GhostTextPackage : AsyncPackage
{
    internal const string PackageGuid = "6a7b2c3d-4e5f-4a8b-9c0d-1e2f3a4b5c6d";

    private VsSolutionTracker?   _solutionTracker;
    private VsDebuggerTracker?   _debuggerTracker;
    private VsDebugDriver?       _debugDriver;

    protected override async Task InitializeAsync(CancellationToken ct, IProgress<ServiceProgressData> progress)
    {
        // §22 tranche 2: in-process, this IS devenv — the family-A signal instance key is our own
        // PID. Also declared by GhostTextViewListener (the MEF bootstrap, which can run first).
        Services.Signals.SignalScope.DeclareVsInstance(SignalFile.CurrentPid);

        // The heartbeat: "the package was loaded in this devenv". Written BEFORE any VS service
        // call - what follows can fail, and that is precisely what we want to be able to say.
        // Without it, an autoload failure is invisible to everyone but an external probe.
        Services.Signals.InProcAliveSignal.Record(Services.Signals.InProcAliveSignal.ComponentPackage);

        // GetServiceAsync may be called from any thread in AsyncPackage — the VSTHRD010 warnings
        // below are false positives for that API. ⚠ "May be called" is not "answers": see the
        // debugger block further down.
#pragma warning disable VSTHRD010
        // Shell services, already loaded by the time a package initializes: they answer from any
        // thread.
        var buildMgr = await GetServiceAsync(typeof(SVsSolutionBuildManager)) as IVsSolutionBuildManager2;
        var solution = await GetServiceAsync(typeof(SVsSolution))              as IVsSolution;
        // IVsTaskList provides access to the VS Error List so VsBuildEventHandler can
        // embed error messages in the signal file, eliminating the second dotnet build pass.
        var taskList = await GetServiceAsync(typeof(SVsTaskList))              as IVsTaskList;
#pragma warning restore VSTHRD010

        // IVsUpdateSolutionEvents.Advise must be called on the VS UI thread.
        await JoinableTaskFactory.SwitchToMainThreadAsync(ct);

        // ⚠ These two are requested ON THE UI THREAD, and that was a measured failure:
        // `debuggerReason` said "VS debugger service unavailable" on a package that was otherwise
        // loaded, with `active_solution` written. What separates them from the three requests
        // above is not the service, it is its owner: `SVsSolution` & co. belong to the shell,
        // already there; `SVsShellDebugger` and `SDTE` are served by packages loaded on demand,
        // and a service request that must first LOAD a package does not succeed from a background
        // thread - VS refuses synchronous loading off the UI thread and returns `null`, with no
        // error and no trace. Hence three successes and one null in the same block.
        // Do not move them above the `SwitchToMainThreadAsync`.
        var dbgService = await GetServiceAsync(typeof(SVsShellDebugger));
        var dteService = await GetServiceAsync(typeof(SDTE));
        var shellDbg   = dbgService as IVsDebugger;
        var dte        = dteService as EnvDTE.DTE;

        // Chat auto-scroll: the tool window is Remote UI (XAML lives in devenv's visual
        // tree), so only this in-process side can call BringIntoView/ScrollToEnd on it.
        try { ChatAutoScroller.Initialize(); } catch { /* non-critical */ }

        if (buildMgr is null || solution is null) return;
        try
        {
            // Through the shared funnel: the MEF listener bootstraps the same handler on the
            // first editor open, and depending on which ran first each used to create its OWN
            // subscription — every failed build collected twice (§27.4).
            BuildEventsBootstrap.EnsureCreated(buildMgr, solution, taskList);
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

        try
        {
            // The reverse leg (roadmap §21): serves the host's debugger commands so `/debug` can
            // set breakpoints, run and step. Requires the DTE — without it there is nothing to
            // drive.
            //
            // ⚠ Every outcome is now recorded, and that is the whole point (2026-08-27). This
            // block used to be silent in all three failure branches: `Diagnostics.Swallow` writes
            // to the IN-PROC ring, while `/diagnostics` reads the out-of-process host's — so a
            // driver that never started was unobservable by anyone. Downstream, `/tdd` gates its
            // §25 capture on the driver and degraded without a word, and the step-2 probe scored
            // that as a product red. A capability that can be absent must say so itself.
            // Four branches because there are four distinct failures, and "the service did not
            // answer" and "it answered something else" are not repaired in the same place: the
            // first is a thread or package problem, the second an interop binding problem.
            // Conflating them costs a full restart round trip to find out.
            if (dbgService is null)
                Services.Signals.InProcAliveSignal.RecordDebuggerUnavailable("VS debugger service unavailable");
            else if (shellDbg is null)
                Services.Signals.InProcAliveSignal.RecordDebuggerUnavailable("VS debugger service is not IVsDebugger");
            else if (dte is null)
                Services.Signals.InProcAliveSignal.RecordDebuggerUnavailable(
                    dteService is null ? "DTE automation unavailable" : "DTE service is not EnvDTE.DTE");
            else
            {
                _debugDriver = new VsDebugDriver(shellDbg, dte);
                Services.Signals.InProcAliveSignal.Record(Services.Signals.InProcAliveSignal.ComponentDebugger);
            }
        }
        catch (Exception ex)
        {
            Services.Diagnostics.Swallow("GhostText.DebugDriver", ex);
            Services.Signals.InProcAliveSignal.RecordDebuggerUnavailable($"driver failed to start: {ex.GetType().Name}");
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && (BuildEventsBootstrap.IsCreated || _solutionTracker is not null
                          || _debuggerTracker is not null || _debugDriver is not null))
        {
            try
            {
                // Unadvise* calls must run on the UI thread.
                ThreadHelper.JoinableTaskFactory.Run(async () =>
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    // Shared handler (whoever of the two bootstraps created it): the package's
                    // dispose runs at devenv shutdown, the right moment to unadvise it.
                    BuildEventsBootstrap.DisposeHandler();
                    _solutionTracker?.Dispose();
                    _solutionTracker = null;
                    _debuggerTracker?.Dispose();
                    _debuggerTracker = null;
                    _debugDriver?.Dispose();
                    _debugDriver = null;
                });
            }
            catch (Exception ex) { Services.Diagnostics.Swallow("GhostText.PackageDispose", ex); }
        }
        base.Dispose(disposing);
    }
}
