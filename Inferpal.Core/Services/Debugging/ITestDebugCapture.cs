namespace Inferpal.Services.Debugging;

/// <summary>
/// Port for the `/tdd` × `/debug` crossing (ROADMAP §25): re-runs <b>one failing test</b> under
/// the host editor's debugger and returns the state at the first exception stop. Distinct from
/// <see cref="IDebugSession"/> on purpose — that port drives an interactive session for the
/// model, this one is a single fire-and-collect capture the `/tdd` loop injects into its fix
/// prompt.
/// </summary>
/// <remarks>
/// Both editor recipes were probed before this port existed (2026-08-20,
/// <c>docs/probes/tdd-debug-launch/</c>): VS Code launches an inline <c>coreclr</c> config on a
/// repro runner and sets the exception filter mid-session (~1.4 s to the stop); Visual Studio
/// attaches to a runner waiting on <c>Debugger.IsAttached</c> whose reflection invoke uses
/// <c>DoNotWrapExceptions</c>, so the unhandled break lands on the original throw site (~5 s).
/// <para>
/// Every implementation is <b>best-effort and never throws</b> for an ordinary failure (no
/// debugger available, test assembly not found, no exception stop): it returns <c>null</c>,
/// which the caller reports as a failed capture and continues without — the block is a bonus,
/// never a prerequisite. Only <see cref="OperationCanceledException"/> propagates.
/// </para>
/// </remarks>
internal interface ITestDebugCapture
{
    /// <summary>False when this front-end cannot capture at all (no debugger surface).</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Re-runs <paramref name="failingTestFqn"/> under the editor's debugger and returns the
    /// state at the first exception stop whose stack reaches the workspace, or <c>null</c> when
    /// the capture could not be made.
    /// </summary>
    Task<DebugStopState?> CaptureAsync(string failingTestFqn, string projectRoot, CancellationToken ct);
}
