using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using Inferpal.Services;

namespace Inferpal.GhostText;

/// <summary>
/// MEF entry point: VS calls <see cref="TextViewCreated"/> for every new code editor.
/// Creates one <see cref="GhostTextController"/> per view; the controller self-manages
/// its lifetime by subscribing to <c>view.Closed</c>.
///
/// <para>
/// Also bootstraps build-event detection on the first editor open — through
/// <see cref="BuildEventsBootstrap"/>, the process-wide funnel it shares with
/// <see cref="GhostTextPackage"/>. Whether the package loads depends on the install path
/// (deploy-dev writes its Packages/AutoLoadPackages keys; VSIXInstaller merges them from the
/// pkgdef on release installs), so this MEF side — which always loads with the first editor —
/// stays as the belt-and-braces. Before the funnel, machines where BOTH ran subscribed the
/// build events twice (§27.4). MEF components are discovered in-process
/// (devenv.exe) via the MEFComponent registry key and reach VS COM services via
/// <c>Package.GetGlobalService</c>.
/// </para>
/// </summary>
[Export(typeof(IWpfTextViewCreationListener))]
[ContentType("code")]
[TextViewRole(PredefinedTextViewRoles.Editable)]
internal sealed class GhostTextViewListener : IWpfTextViewCreationListener
{
    // 0 = idle, 1 = init in progress.
    // Prevents concurrent simultaneous attempts; reset to 0 on failure so the
    // next TextViewCreated can retry (important when VS services aren't ready yet).
    // The handler itself lives in BuildEventsBootstrap (shared with GhostTextPackage).
    private static int _buildEventsInitializing = 0;

    // §22 tranche 2: in-process, this IS devenv — the family-A signal instance key is our own
    // PID. Declared here (and in GhostTextPackage) because this MEF listener is the in-process
    // bootstrap that always loads, and InlineDiffController reads a scoped channel.
    static GhostTextViewListener() =>
        Services.Signals.SignalScope.DeclareVsInstance(SignalFile.CurrentPid);

    public void TextViewCreated(IWpfTextView textView)
    {
        // The heartbeat of the MEF door: composed, and instantiated on a real editor - that is
        // what makes ghost text live. Here and not in the static constructor: MEF can build the
        // part without ever using it, and "discovered" is not "alive" (exactly the confusion the
        // ComponentModelCache kept alive for three days).
        Services.Signals.InProcAliveSignal.Record(Services.Signals.InProcAliveSignal.ComponentMef);

        _ = new GhostTextController(textView);
        _ = new InlineDiffController(textView);   // inline diff preview (self-manages via view.Closed)

        // Retry build-event subscription on each new editor until the shared handler exists
        // (created by whichever bootstrap gets there first). The Interlocked flag prevents
        // concurrent duplicate attempts but is reset to 0 on failure so the next
        // TextViewCreated can retry.
        if (!BuildEventsBootstrap.IsCreated &&
            Interlocked.CompareExchange(ref _buildEventsInitializing, 1, 0) == 0)
            _ = InitBuildEventsAsync();
    }

    /// <summary>
    /// Switches to the VS UI thread, acquires the build-manager and solution services via
    /// <c>Package.GetGlobalService</c>, then creates a <see cref="VsBuildEventHandler"/>
    /// that writes <see cref="BuildSignalFile"/> on every failed build.
    /// The OOP <see cref="VsBuildMonitor"/> watches that file and fires its
    /// <c>BuildFailed</c> event which the tool-window ViewModel subscribes to.
    /// On failure (services not ready), resets the flag so the next editor open retries.
    /// </summary>
    private static async Task InitBuildEventsAsync()
    {
        try
        {
            // AdviseUpdateSolutionEvents requires the VS main thread.
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            // Package.GetGlobalService works from any in-process component (MEF or package)
            // because VS populates the global service provider early in startup.
            var buildMgr =
                Package.GetGlobalService(typeof(SVsSolutionBuildManager))
                as IVsSolutionBuildManager2;

            var solution =
                Package.GetGlobalService(typeof(SVsSolution))
                as IVsSolution;

            // Optional — used to embed error messages in the signal file (avoids a second dotnet build).
            var taskList =
                Package.GetGlobalService(typeof(SVsTaskList))
                as IVsTaskList;

            if (buildMgr is null || solution is null)
            {
                // Services not ready — allow the next TextViewCreated to retry.
                Interlocked.Exchange(ref _buildEventsInitializing, 0);
                return;
            }

            BuildEventsBootstrap.EnsureCreated(buildMgr, solution, taskList);
            // Leave _buildEventsInitializing = 1 — the shared handler is alive, no more retries.
        }
        catch
        {
            // Reset so the next TextViewCreated can retry.
            Interlocked.Exchange(ref _buildEventsInitializing, 0);
        }
    }
}
