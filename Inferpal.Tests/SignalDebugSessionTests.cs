using System.IO;
using Inferpal.Services.Debugging;
using Inferpal.Services.Signals;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The Visual Studio side of the <c>/debug</c> port, exercised against a stand-in driver that
/// speaks the same file protocol as the real in-process one. Everything below the EnvDTE calls is
/// therefore covered without a devenv — what is not covered is the automation itself, which is what
/// the §21 probe measured.
/// </summary>
[Collection("DebugCommandSignal")]
public class SignalDebugSessionTests : IDisposable
{
    private readonly FakeDriver _driver = new();

    public SignalDebugSessionTests()
    {
        Cleanup();
        // Short waits: these tests are about the protocol, not about how long a build takes.
        SignalDebugSession.StartTimeout  = TimeSpan.FromSeconds(5);
        SignalDebugSession.ResumeTimeout = TimeSpan.FromSeconds(5);
        SignalDebugSession.QueryTimeout  = TimeSpan.FromSeconds(5);
    }

    public void Dispose()
    {
        _driver.Dispose();
        SignalDebugSession.StartTimeout  = TimeSpan.FromMinutes(5);
        SignalDebugSession.ResumeTimeout = TimeSpan.FromMinutes(2);
        SignalDebugSession.QueryTimeout  = TimeSpan.FromSeconds(20);
        DebugCommandSignal._isProcessAliveOverride = null;
        DebugCommandSignal._nowOverride = null;
        Cleanup();
    }

    private static void Cleanup()
    {
        foreach (var path in new[] { DebugCommandSignal.RequestPath,
                                     DebugCommandSignal.ResponsePath,
                                     DebugCommandSignal.ReadyPath })
            try { File.Delete(path); } catch { }
    }

    /// <summary>Stand-in for the in-process driver: claims requests and answers what it is told to.</summary>
    private sealed class FakeDriver : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private Task? _loop;

        internal readonly List<string> SeenOps = [];
        internal readonly List<DebugCommandRequest> Seen = [];

        /// <summary>Delay before answering, used to prove that calls do not interleave.</summary>
        internal TimeSpan Latency { get; set; } = TimeSpan.Zero;

        internal void Start(Func<DebugCommandRequest, DebugCommandResponse> handler)
        {
            DebugCommandSignal.MarkReady(Environment.ProcessId);
            _loop = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    var request = DebugCommandSignal.ClaimRequest();
                    if (request is null) { await Task.Delay(15, _cts.Token); continue; }

                    lock (Seen) { Seen.Add(request); SeenOps.Add(request.Op); }
                    if (Latency > TimeSpan.Zero) await Task.Delay(Latency, _cts.Token);
                    DebugCommandSignal.WriteResponse(handler(request));
                }
            }, _cts.Token);
        }

        /// <summary>Advertises a driver that never answers — the timeout path.</summary>
        internal static void StartMute() => DebugCommandSignal.MarkReady(Environment.ProcessId);

        public void Dispose()
        {
            _cts.Cancel();
            try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { }
            _cts.Dispose();
            DebugCommandSignal.ClearReady();
        }
    }

    private static DebugStopState State(string reason = "breakpoint") =>
        new(reason, ThreadId: 0,
            Frames: [new DebugFrame(1, "Program.Compute", @"C:\p\Program.cs", 12)],
            Locals: [new DebugVariable("seed", "int", "21")]);

    // ── No driver at all ────────────────────────────────────────────────────────────

    [Fact]
    public async Task WithoutADriver_EveryCallGivesUpAtOnce_InsteadOfWaitingOutItsTimeout()
    {
        // ⚠ Asserting only "returns null" would pass without the readiness check too — the call
        // would merely sit on the request file for its whole timeout first. What the check buys is
        // that an agent whose devenv has no driver is told so now, not in five minutes; so the
        // timeout is set far above the assertion and the elapsed time is the measurement.
        SignalDebugSession.StartTimeout = TimeSpan.FromSeconds(10);
        SignalDebugSession.QueryTimeout = TimeSpan.FromSeconds(10);
        var session = new SignalDebugSession();

        var started = DateTimeOffset.UtcNow;
        Assert.False(session.IsAvailable);
        Assert.Null((await session.StartAsync(CancellationToken.None)).State);
        Assert.Null(await session.AddBreakpointAsync(@"C:\p\a.cs", 3, CancellationToken.None));
        Assert.Empty(await session.ListBreakpointsAsync(CancellationToken.None));
        var elapsed = DateTimeOffset.UtcNow - started;

        Assert.True(elapsed < TimeSpan.FromSeconds(2), $"three calls took {elapsed.TotalSeconds:0.0}s");
        // And nothing was published: an unreachable devenv must not be left holding a pending
        // "start" that a later session would find and act on.
        Assert.Null(DebugCommandSignal.ClaimRequest());
    }

    // ── Ordinary answers ────────────────────────────────────────────────────────────

    [Fact]
    public async Task AddBreakpoint_ReturnsItAsTheDebuggerBoundIt()
    {
        // VS moves a breakpoint to the next executable line; the caller is told where it landed,
        // not where it was asked for.
        _driver.Start(r => new DebugCommandResponse(r.Id, Ok: true,
            Breakpoints: [new DebugBreakpointInfo(r.File!, r.Line + 1, Enabled: true)]));
        var session = new SignalDebugSession();

        var bp = await session.AddBreakpointAsync(@"C:\p\a.cs", 12, CancellationToken.None);

        Assert.NotNull(bp);
        Assert.Equal(13, bp.Line);
        Assert.Equal(SignalDebugSession.OpAddBreakpoint, _driver.SeenOps.Single());
    }

    [Fact]
    public async Task RemoveBreakpoint_IsFalse_WhenThereWasNone()
    {
        _driver.Start(r => new DebugCommandResponse(r.Id, Ok: true, Flag: false));
        var session = new SignalDebugSession();

        Assert.False(await session.RemoveBreakpointAsync(@"C:\p\a.cs", 12, CancellationToken.None));
    }

    [Fact]
    public async Task Start_ReturnsTheStopState()
    {
        _driver.Start(r => new DebugCommandResponse(r.Id, Ok: true, State: State()));
        var session = new SignalDebugSession();

        var result = await session.StartAsync(CancellationToken.None);

        Assert.Null(result.Failure);
        Assert.NotNull(result.State);
        Assert.Equal("breakpoint", result.State.Reason);
        Assert.Equal("21", result.State.Locals[0].Value);
    }

    [Fact]
    public async Task Start_WithoutAStop_IsACompletedRun_NotAFailure()
    {
        // The program ran to completion without hitting anything. An ordinary answer, which the
        // tool renders as a sentence rather than a failure.
        _driver.Start(r => new DebugCommandResponse(r.Id, Ok: true, State: null));
        var session = new SignalDebugSession();

        var result = await session.StartAsync(CancellationToken.None);

        Assert.Null(result.State);
        Assert.Null(result.Failure);
    }

    [Fact]
    public async Task Start_WithoutADriver_IsAFailure_NotACompletedRun()
    {
        var session = new SignalDebugSession();   // no driver advertised at all

        var result = await session.StartAsync(CancellationToken.None);

        Assert.Null(result.State);
        Assert.Contains("No debugger is reachable", result.Failure);
    }

    // The kind travels as a string here rather than as DebugStepKind: xUnit needs a public test
    // method, and the enum is internal to the Core.
    [Theory]
    [InlineData("over", SignalDebugSession.OpStepOver)]
    [InlineData("into", SignalDebugSession.OpStepInto)]
    [InlineData("out",  SignalDebugSession.OpStepOut)]
    public async Task EachStepKind_TravelsAsItsOwnOperation(string kind, string expected)
    {
        _driver.Start(r => new DebugCommandResponse(r.Id, Ok: true, State: State("step")));
        var session = new SignalDebugSession();

        await session.StepAsync(kind switch
        {
            "into" => DebugStepKind.Into,
            "out"  => DebugStepKind.Out,
            _      => DebugStepKind.Over,
        }, CancellationToken.None);

        Assert.Equal(expected, _driver.SeenOps.Single());
    }

    [Fact]
    public async Task Evaluate_CarriesTheFrame_AndReturnsTheAdaptersRendering()
    {
        _driver.Start(r => new DebugCommandResponse(r.Id, Ok: true, Text: $"{r.Expression}@{r.FrameId}"));
        var session = new SignalDebugSession();

        var text = await session.EvaluateAsync("items.Count * 100", frameId: 4, CancellationToken.None);

        Assert.Equal("items.Count * 100@4", text);
    }

    [Fact]
    public async Task Evaluate_OfAnInvalidExpression_IsNull()
    {
        _driver.Start(r => new DebugCommandResponse(r.Id, Ok: false, Error: "identifier not found"));
        var session = new SignalDebugSession();

        Assert.Null(await session.EvaluateAsync("nope", null, CancellationToken.None));
    }

    // ── Failure paths ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task AMuteDriver_TimesOut_AndTheRequestIsWithdrawn()
    {
        FakeDriver.StartMute();
        SignalDebugSession.StartTimeout = TimeSpan.FromMilliseconds(300);
        var session = new SignalDebugSession();

        var result = await session.StartAsync(CancellationToken.None);

        // The tranche-2 review flagged this exact confusion: under Visual Studio a launch whose
        // build fails never leaves design mode, so nothing ever answers and the whole budget burns.
        // Rendering that as "ran to completion without stopping" would send the agent looking for a
        // bug in code that was never executed.
        Assert.Null(result.State);
        Assert.Contains("did not start", result.Failure);
        // The point of the withdrawal: a devenv that wakes up two minutes later must not launch the
        // user's program because an agent asked for it and gave up.
        Assert.Null(DebugCommandSignal.ClaimRequest());
    }

    [Fact]
    public async Task AnAnswerToAnEarlierCall_IsNeverReadAsThisOnes()
    {
        // The driver answers with a stale id: the wait must ignore it and time out instead of
        // reporting a state that belongs to the previous step.
        DebugCommandSignal.MarkReady(Environment.ProcessId);
        var pump = Task.Run(async () =>
        {
            for (var i = 0; i < 40; i++)
            {
                if (DebugCommandSignal.ClaimRequest() is not null)
                {
                    DebugCommandSignal.WriteResponse(
                        new DebugCommandResponse("stale-id", Ok: true, State: State("wrong")));
                    return;
                }
                await Task.Delay(15);
            }
        });
        SignalDebugSession.ResumeTimeout = TimeSpan.FromMilliseconds(400);

        var state = await new SignalDebugSession().ContinueAsync(CancellationToken.None);

        await pump;
        Assert.Null(state);
    }

    [Fact]
    public async Task TwoConcurrentCalls_AreServedOneAfterTheOther()
    {
        // Without serialisation the second request would overwrite the first in the single request
        // file, and the first caller would wait for an answer to a command nobody ever ran.
        _driver.Latency = TimeSpan.FromMilliseconds(150);
        _driver.Start(r => new DebugCommandResponse(r.Id, Ok: true, Text: r.Op));
        var session = new SignalDebugSession();

        var first  = session.EvaluateAsync("a", null, CancellationToken.None);
        var second = session.GetStateAsync(CancellationToken.None);
        await Task.WhenAll(first, second);

        Assert.Equal(SignalDebugSession.OpEvaluate, await first);
        Assert.Equal(2, _driver.SeenOps.Count);
    }
}
