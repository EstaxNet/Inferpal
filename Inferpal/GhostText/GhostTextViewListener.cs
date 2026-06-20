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
/// Also bootstraps build-event detection on the first editor open.
/// This avoids relying on <see cref="GhostTextPackage"/> (an AsyncPackage) which is NOT
/// loaded by VS for VisX OOP extensions — pkgdef files in VisX extension directories are
/// not processed by VS's traditional package loader.  MEF components such as this one,
/// however, are discovered and loaded in-process (devenv.exe) via the MEFComponent registry
/// key, so they have full access to VS COM services via <c>Package.GetGlobalService</c>.
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
    private static int _buildEventsInitializing = 0;

    // Static reference so the handler is kept alive for the lifetime of VS.
    // VsBuildEventHandler.Dispose() is intentionally never called here because the
    // handler should remain active until devenv.exe exits.
#pragma warning disable CA2213   // suppress "disposable field not disposed" — intentional
    private static VsBuildEventHandler? _staticBuildEventHandler;
#pragma warning restore CA2213

    public void TextViewCreated(IWpfTextView textView)
    {
        _ = new GhostTextController(textView);

        // Retry build-event subscription on each new editor until we succeed.
        // The Interlocked flag prevents concurrent duplicate attempts but is
        // reset to 0 on failure so that the next TextViewCreated can retry.
        if (_staticBuildEventHandler is null &&
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

            _staticBuildEventHandler = new VsBuildEventHandler(buildMgr, solution, taskList);
            // Leave _buildEventsInitializing = 1 — handler is alive, no more retries needed.
        }
        catch
        {
            // Reset so the next TextViewCreated can retry.
            Interlocked.Exchange(ref _buildEventsInitializing, 0);
        }
    }
}
