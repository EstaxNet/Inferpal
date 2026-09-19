using Inferpal.Services.Signals;

namespace Inferpal.Services.Debugging;

/// <summary>
/// Visual Studio implementation of the <c>/tdd</c> capture: asks the in-process driver (over the
/// signal channel) to launch the repro runner in wait-for-debugger mode, attach through EnvDTE and
/// hand back the unhandled-exception stop — a few seconds to attach and break at the original
/// throw site.
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
            // left on disk, it opens a debug session by itself seconds later, on a run the user
            // abandoned.
            var response = await DebugCommandSignal.WaitForAnswerAsync(id, CaptureTimeout, ct);
            return response is { Ok: true } ? response.State : null;
        }
        finally
        {
            DebugCommandSignal.ChannelLock.Release();
        }
    }
}
