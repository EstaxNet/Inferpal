using Inferpal.Services.Signals;

namespace Inferpal.Services.Debugging;

/// <summary>
/// Visual Studio implementation of the §25 capture: asks the in-process driver (over the §21
/// signal channel) to launch the repro runner in wait-for-debugger mode, attach through EnvDTE
/// and hand back the unhandled-exception stop. The recipe was probed before being wired
/// (2026-08-20, <c>docs/probes/tdd-debug-launch/</c>): attach ~2 s, break at the original throw
/// site ~3 s.
/// </summary>
internal sealed class SignalTestDebugCapture : TestDebugCaptureBase
{
    /// <summary>Attach + run + break, with margin; the capture is a bonus, not worth more waiting.</summary>
    internal static TimeSpan CaptureTimeout { get; set; } = TimeSpan.FromMinutes(2);

    public override bool IsAvailable => DebugCommandSignal.IsDriverReady();

    protected override async Task<DebugStopState?> LaunchAndCaptureAsync(
        string runnerDll, string testDll, string failingTestFqn,
        string cwd, string projectRoot, CancellationToken ct)
    {
        await DebugCommandSignal.ChannelLock.WaitAsync(ct);
        try
        {
            var request = new DebugCommandRequest(
                Id:  Guid.NewGuid().ToString("N"),
                Pid: System.Diagnostics.Process.GetCurrentProcess().Id,
                Ts:  DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Op:  SignalDebugSession.OpCaptureTest,
                Program: runnerDll,
                Args:    [testDll, failingTestFqn],
                Cwd:     cwd,
                Root:    projectRoot);

            var id = DebugCommandSignal.WriteRequest(request);
            if (id is null) return null;

            // The channel withdraws the request on every answerless path, cancellation included:
            // pressing Stop mid-capture used to leave it on disk, and a debug session opened by
            // itself seconds later on a run the user had abandoned.
            var response = await DebugCommandSignal.WaitForAnswerAsync(id, CaptureTimeout, ct);
            return response is { Ok: true } ? response.State : null;
        }
        finally
        {
            DebugCommandSignal.ChannelLock.Release();
        }
    }
}
