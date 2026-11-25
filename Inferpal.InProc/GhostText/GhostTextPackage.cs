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
///         consumed by the out-of-process <c>VsBuildMonitor</c>.</item>
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
    private VsDebugDriver?       _debugDriver;

    protected override async Task InitializeAsync(CancellationToken ct, IProgress<ServiceProgressData> progress)
    {
        // In-process, this IS devenv — the per-instance (family-A) signal key is our own
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

        // ⚠ These two are requested ON THE UI THREAD. What separates them from the three requests
        // above is not the service, it is its owner: `SVsSolution` & co. belong to the shell and
        // are already there; `SVsShellDebugger` and `SDTE` are served by packages loaded on demand,
        // and a service request that must first LOAD a package does not succeed from a background
        // thread — VS refuses synchronous loading off the UI thread and returns `null`, with no
        // error and no trace, which reads as "debugger service unavailable" on a package that
        // loaded fine. Do not move them above the `SwitchToMainThreadAsync`.
        var dbgService = await GetServiceAsync(typeof(SVsShellDebugger));
        var dteService = await GetServiceAsync(typeof(SDTE));
        var dte        = dteService as EnvDTE.DTE;

        InitializeChatWindowSide(dte);

        // The chat, open at the last close, is reopened from here: Visual Studio no longer recreates it from its layout.
        JoinableTaskFactory.RunAsync(() => ChatWindowRestore.ReopenAsync(this, DisposalToken)).FileAndForget("Inferpal/ChatRestore");

        // The Error List's "Fix with Inferpal" asks this side for the lines chosen in the list.
        ErrorListSelectionWatcher.Start();
        // @output asks this side for the text of the Output window.
        OutputPaneWatcher.Start();

        if (buildMgr is null || solution is null) return;
        try
        {
            // Through the shared funnel: the MEF listener bootstraps the same handler on the
            // first editor open, so without it each creates its OWN subscription and every failed
            // build is collected twice.
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

        StartDebugDriver(dbgService, dteService);
    }

    /// <summary>
    /// The chat window's devenv side: the tool window is Remote UI (its XAML lives in devenv's visual tree), so only this
    /// in-process side reaches its controls. Each part is non-critical. UI thread only.
    /// </summary>
    private static void InitializeChatWindowSide(EnvDTE.DTE? dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        // Chat auto-scroll: only this side can call BringIntoView/ScrollToEnd on it.
        try { ChatAutoScroller.Initialize(); } catch { /* non-critical */ }
        // A protected PDF's password, typed on the chat's card: Remote UI binds no PasswordBox, only this side reads one.
        try { PasswordBoxRelay.Initialize(); } catch { /* non-critical: the card then says to type the password first */ }
        // A file dropped on the chat window: only this side sees the drop, for the same reason.
        try
        {
            ChatDropTarget.Initialize(() =>
            {
                try { return dte?.Solution?.FullName is { Length: > 0 } sln ? System.IO.Path.GetDirectoryName(sln) : null; }
                catch (Exception ex) { Diagnostics.Swallow("ChatDrop.Solution", ex); return null; }
            });
        }
        catch (Exception ex) { Diagnostics.Swallow("ChatDrop.Init", ex); }
    }

    /// <summary>
    /// Serves the host's debugger requests so <c>/debug</c> can set breakpoints, run and step. Requires the DTE — without
    /// it there is nothing to drive. UI thread only (the driver subscribes to debugger events there).
    /// </summary>
    /// <remarks>
    /// ⚠ Every outcome is recorded, and that is the whole point: <c>Diagnostics.Swallow</c> writes to the IN-PROC ring
    /// while <c>/diagnostics</c> reads the out-of-process host's, so a driver that never started is otherwise unobservable
    /// by anyone. <c>/tdd</c> gates its debugger capture on this driver and degrades silently without it. A capability
    /// that can be absent must say so itself. Four branches because there are four distinct failures, and "the service did
    /// not answer" and "it answered something else" are not repaired in the same place: the first is a thread or package
    /// problem, the second an interop binding problem. Conflating them costs a full restart round trip to find out.
    /// </remarks>
    /// <param name="dbgService">What <c>SVsShellDebugger</c> answered.</param>
    /// <param name="dteService">What <c>SDTE</c> answered.</param>
    private void StartDebugDriver(object? dbgService, object? dteService)
    {
        try
        {
            // Inside the try: even a call off the UI thread is an outcome recorded, never a silent fault.
            ThreadHelper.ThrowIfNotOnUIThread();
            if (dbgService is null)
                Services.Signals.InProcAliveSignal.RecordDebuggerUnavailable("VS debugger service unavailable");
            else if (dbgService is not IVsDebugger shellDbg)
                Services.Signals.InProcAliveSignal.RecordDebuggerUnavailable("VS debugger service is not IVsDebugger");
            else if (dteService is not EnvDTE.DTE dte)
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

    /// <summary>Visual Studio is closing, its windows still there: whether the chat is open is recorded now.</summary>
    protected override int QueryClose(out bool canClose)
    {
        ThreadHelper.ThrowIfNotOnUIThread();   // IVsPackage.QueryClose: always called on the UI thread
        ChatWindowRestore.RecordAtClose(this);
        return base.QueryClose(out canClose);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && (BuildEventsBootstrap.IsCreated || _solutionTracker is not null
                          || _debugDriver is not null))
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
                    _debugDriver?.Dispose();
                    _debugDriver = null;
                });
            }
            catch (Exception ex) { Services.Diagnostics.Swallow("GhostText.PackageDispose", ex); }
        }
        // The sidecar dies with devenv anyway (its stdin closes), but a package disposed while devenv
        // lives on must not leave it running.
        if (disposing)
        {
            FimSidecar.Shutdown();
            ErrorListSelectionWatcher.Stop();
            OutputPaneWatcher.Stop();
        }
        base.Dispose(disposing);
    }
}
