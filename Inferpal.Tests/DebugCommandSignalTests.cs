using System.IO;
using Inferpal.Services.Debugging;
using Inferpal.Services.Signals;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The file-IPC protocol behind <c>/debug</c> under Visual Studio: the out-of-process host writes a
/// command, the in-process driver in devenv claims it and answers.
/// </summary>
/// <remarks>
/// Serialised on the signal files — xUnit runs same-class facts sequentially, and the collection
/// keeps the other signal-file tests apart.
/// </remarks>
[Collection(SignalCollection.Name)]
public class DebugCommandSignalTests : IDisposable
{
    // ⚠ Installed before anything else runs: these facts publish a request carrying a live PID,
    // one of them `op: "start"`. Against the real folder a VsDebugDriver in the developer's own
    // Visual Studio claims it and starts debugging. See SignalScratchDir.
    private readonly SignalScratchDir _scratch = new();

    public DebugCommandSignalTests() => Cleanup();

    public void Dispose()
    {
        SignalFile._isProcessAliveOverride = null;
        SignalFile._nowOverride = null;
        Cleanup();
        _scratch.Dispose();
    }

    private static void Cleanup()
    {
        foreach (var path in new[] { DebugCommandSignal.RequestPath,
                                     DebugCommandSignal.ResponsePath,
                                     DebugCommandSignal.ReadyPath })
            try { File.Delete(path); } catch { }
    }

    private static DebugCommandRequest Request(string op = "state", string id = "r1") =>
        new(id, Environment.ProcessId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), op);

    // ── Readiness ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Driver_IsNotReady_UntilItSaysSo()
    {
        Assert.False(DebugCommandSignal.IsDriverReady());

        DebugCommandSignal.MarkReady(Environment.ProcessId);
        Assert.True(DebugCommandSignal.IsDriverReady());

        DebugCommandSignal.ClearReady();
        Assert.False(DebugCommandSignal.IsDriverReady());
    }

    [Fact]
    public void Driver_OfADeadProcess_IsNotReady()
    {
        // A devenv that crashed leaves its marker behind; the next session must not trust it.
        DebugCommandSignal.MarkReady(Environment.ProcessId);
        SignalFile._isProcessAliveOverride = _ => false;

        Assert.False(DebugCommandSignal.IsDriverReady());
    }

    // ── Request leg ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Request_RoundTrips_AndIsClaimedOnlyOnce()
    {
        DebugCommandSignal.WriteRequest(Request("step_over"));

        var claimed = DebugCommandSignal.ClaimRequest();

        Assert.NotNull(claimed);
        Assert.Equal("step_over", claimed.Op);
        // Consumed: a driver that loops must not run the same step twice.
        Assert.Null(DebugCommandSignal.ClaimRequest());
    }

    [Fact]
    public void Request_FromDeadProcess_IsNotClaimed()
    {
        DebugCommandSignal.WriteRequest(Request());
        SignalFile._isProcessAliveOverride = _ => false;

        Assert.Null(DebugCommandSignal.ClaimRequest());
    }

    [Fact]
    public void Request_OlderThanMaxAge_IsNotClaimed()
    {
        DebugCommandSignal.WriteRequest(Request());
        SignalFile._nowOverride = () => DateTimeOffset.UtcNow + DebugCommandSignal.MaxAge + TimeSpan.FromSeconds(1);

        Assert.Null(DebugCommandSignal.ClaimRequest());
    }

    [Fact]
    public void DiscardRequest_WithdrawsAnUnclaimedRequest()
    {
        DebugCommandSignal.WriteRequest(Request("start"));

        DebugCommandSignal.DiscardRequest();

        Assert.Null(DebugCommandSignal.ClaimRequest());
    }

    // ── Response leg ────────────────────────────────────────────────────────────────

    [Fact]
    public void Response_IsInvisible_ToAnotherRequestsId()
    {
        // The whole reason this transport carries correlation ids: several operations follow one
        // another, and the answer to the previous step must never be read as the answer to this one.
        DebugCommandSignal.WriteResponse(new DebugCommandResponse("r1", Ok: true));

        Assert.Null(DebugCommandSignal.TryReadResponse("r2"));
        Assert.NotNull(DebugCommandSignal.TryReadResponse("r1"));
    }

    [Fact]
    public void NewRequest_DropsThePreviousAnswer()
    {
        DebugCommandSignal.WriteResponse(new DebugCommandResponse("r1", Ok: true));

        DebugCommandSignal.WriteRequest(Request(id: "r1"));

        // Same id reused on purpose: without the deletion, the leftover file would satisfy the new
        // wait instantly with a state captured before the command ran.
        Assert.Null(DebugCommandSignal.TryReadResponse("r1"));
    }

    [Fact]
    public async Task WaitForAnswer_TimesOut_AndWithdrawsTheRequest()
    {
        DebugCommandSignal.MarkReady(Environment.ProcessId);
        DebugCommandSignal.WriteRequest(Request());

        var response = await DebugCommandSignal.WaitForAnswerAsync(
            "r1", TimeSpan.FromMilliseconds(250), CancellationToken.None);

        Assert.Null(response);
        Assert.False(File.Exists(DebugCommandSignal.RequestPath),
            "an unanswered request must not survive its own wait: a driver claiming it later " +
            "would act on a command the caller has already given up on.");
    }

    [Fact]
    public async Task WaitForAnswer_ReturnsTheAnswer_OnceWritten()
    {
        DebugCommandSignal.MarkReady(Environment.ProcessId);
        var wait = DebugCommandSignal.WaitForAnswerAsync("r1", TimeSpan.FromSeconds(5), CancellationToken.None);

        DebugCommandSignal.WriteResponse(new DebugCommandResponse("r1", Ok: true, Text: "42"));

        var response = await wait;
        Assert.NotNull(response);
        Assert.Equal("42", response.Text);
    }

    /// <summary>
    /// Cancelling withdraws the request too — the branch the two callers used to miss.
    /// </summary>
    /// <remarks>
    /// Both call sites discarded on the timeout branch only, so a cancelled turn left a live
    /// request on disk. For <c>/debug</c> that means a driver waking up afterwards <b>starts the
    /// user's program</b> after the agent gave up; for the §25 capture, a debug session opening by
    /// itself seconds after the user pressed Stop. The withdrawal now lives in the channel, so
    /// neither caller can forget it again.
    /// </remarks>
    [Fact]
    public async Task WaitForAnswer_WithdrawsTheRequest_WhenTheCallerCancels()
    {
        DebugCommandSignal.MarkReady(Environment.ProcessId);
        DebugCommandSignal.WriteRequest(Request(op: "start"));
        using var cts = new CancellationTokenSource();

        var wait = DebugCommandSignal.WaitForAnswerAsync("r1", TimeSpan.FromMinutes(2), cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        Assert.False(File.Exists(DebugCommandSignal.RequestPath),
            "a cancelled wait must withdraw its request: this one carries op \"start\".");
    }

    /// <summary>
    /// A peer that dies mid-wait ends the wait, instead of burning the whole budget on a devenv
    /// that can no longer answer (two minutes, for a §25 capture).
    /// </summary>
    [Fact]
    public async Task WaitForAnswer_GivesUp_WhenTheDriverDiesMidWait()
    {
        DebugCommandSignal.MarkReady(Environment.ProcessId);
        DebugCommandSignal.WriteRequest(Request());

        var alive = true;
        SignalFile._isProcessAliveOverride = _ => alive;

        var started = DateTimeOffset.UtcNow;
        var wait    = DebugCommandSignal.WaitForAnswerAsync("r1", TimeSpan.FromMinutes(2), CancellationToken.None);
        await Task.Delay(120);
        alive = false;                                   // the devenv closes

        Assert.Null(await wait);
        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(20),
            "the wait outlived the driver: it only checked liveness once, before writing.");
        Assert.False(File.Exists(DebugCommandSignal.RequestPath));
    }

    [Fact]
    public void StopState_SurvivesTheRoundTrip_UnParsed()
    {
        // Values are rendered by the debug adapter and travel as opaque strings (§21): whatever VS
        // wrote must come back byte for byte, including the IDE's localised pseudo-locals.
        var state = new DebugStopState(
            "breakpoint", ThreadId: 7,
            Frames: [new DebugFrame(3, "Program.Compute", @"C:\p\Program.cs", 12)],
            Locals: [new DebugVariable("int.ToString retourné", "string", "\"probe-42\"")],
            Exception: "`InvalidOperationException` — boom");

        DebugCommandSignal.WriteResponse(new DebugCommandResponse("r1", Ok: true, State: state));
        var read = DebugCommandSignal.TryReadResponse("r1");

        Assert.NotNull(read?.State);
        Assert.Equal(7, read.State.ThreadId);
        Assert.Equal("Program.Compute", read.State.Frames[0].Function);
        Assert.Equal(3, read.State.Frames[0].Id);
        Assert.Equal("int.ToString retourné", read.State.Locals[0].Name);
        Assert.Equal("\"probe-42\"", read.State.Locals[0].Value);
        Assert.Equal("`InvalidOperationException` — boom", read.State.Exception);
    }
}
