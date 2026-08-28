using Microsoft.VisualStudio.Shell.Interop;

namespace Inferpal.GhostText;

/// <summary>
/// Process-wide funnel for the single <see cref="VsBuildEventHandler"/> — first caller wins.
/// </summary>
/// <remarks>
/// <para>
/// Two independent bootstraps can create the build-event subscription, and which one runs
/// depends on the install path: <see cref="GhostTextPackage"/> loads when the
/// Packages/AutoLoadPackages registry keys exist (written by <c>deploy-dev.ps1</c> on dev
/// machines, merged from <c>Inferpal.pkgdef</c> by VSIXInstaller on release installs), while
/// <see cref="GhostTextViewListener"/> is the MEF belt-and-braces that always loads with the
/// first editor. On a machine where BOTH run — the common case — each used to create its own
/// handler: every failed build was collected twice on the UI thread and the signal file written
/// twice (pre-1.6.0 architecture review, §27.4). Neither bootstrap can be removed (each is the only one that
/// runs in SOME install world), so they funnel here instead: one handler per devenv, whoever
/// arrives first.
/// </para>
/// </remarks>
internal static class BuildEventsBootstrap
{
    private static readonly object _gate = new();
    private static VsBuildEventHandler? _handler;

    /// <summary>Whether the process-wide handler exists (for cheap pre-checks).</summary>
    internal static bool IsCreated { get { lock (_gate) return _handler is not null; } }

    /// <summary>
    /// Creates the process-wide handler if none exists yet. Idempotent: a second caller is a
    /// no-op. Must run on the VS UI thread (the Advise calls require it — both callers already
    /// switch before calling). Throws only out of the first, creating call.
    /// </summary>
    internal static void EnsureCreated(IVsSolutionBuildManager2 buildMgr, IVsSolution solution, IVsTaskList? taskList)
    {
        lock (_gate)
        {
            if (_handler is not null) return;
            _handler = new VsBuildEventHandler(buildMgr, solution, taskList);
        }
    }

    /// <summary>Disposes the shared handler (devenv shutdown path — UI thread).</summary>
    internal static void DisposeHandler()
    {
        VsBuildEventHandler? handler;
        lock (_gate)
        {
            handler  = _handler;
            _handler = null;
        }
        handler?.Dispose();
    }
}
