using Inferpal.Localization;
using Inferpal.Services.Commands;
using Inferpal.Services.Debugging;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>/debug</c> (roadmap §21, tranche 3). The rule these pin is the one the consent model rests
/// on: the command reports, or it hands the model an instruction — it never drives the debugger
/// itself, because that would be a second way to run the user's program next to the approved one.
/// </summary>
public class DebugCommandHandlerTests
{
    private sealed class FakeSession : IDebugSession
    {
        public bool IsAvailable { get; set; } = true;
        public DebugStopState? State { get; set; }
        public readonly List<DebugBreakpointInfo> Breakpoints = [];
        public readonly List<string> Calls = [];

        public Task<DebugBreakpointInfo?> AddBreakpointAsync(string file, int line, CancellationToken ct)
        { Calls.Add("add"); return Task.FromResult<DebugBreakpointInfo?>(null); }

        public Task<bool> RemoveBreakpointAsync(string file, int line, CancellationToken ct)
        { Calls.Add("remove"); return Task.FromResult(false); }

        public Task<IReadOnlyList<DebugBreakpointInfo>> ListBreakpointsAsync(CancellationToken ct)
        { Calls.Add("list"); return Task.FromResult<IReadOnlyList<DebugBreakpointInfo>>(Breakpoints); }

        public Task<DebugStartResult> StartAsync(CancellationToken ct)
        { Calls.Add("start"); return Task.FromResult(DebugStartResult.RanToCompletion); }

        public Task<DebugStopState?> ContinueAsync(CancellationToken ct)
        { Calls.Add("continue"); return Task.FromResult(State); }

        public Task<DebugStopState?> StepAsync(DebugStepKind kind, CancellationToken ct)
        { Calls.Add("step"); return Task.FromResult(State); }

        public Task<DebugStopState?> GetStateAsync(CancellationToken ct)
        { Calls.Add("state"); return Task.FromResult(State); }

        public Task<string?> EvaluateAsync(string expression, int? frameId, CancellationToken ct)
        { Calls.Add("evaluate"); return Task.FromResult<string?>(null); }

        public Task StopAsync(CancellationToken ct) { Calls.Add("stop"); return Task.CompletedTask; }
    }

    private static string[] Parts(string text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public async Task WithoutADebugger_ItSaysSo_RatherThanPretendingToInvestigate()
    {
        var result = await DebugCommandHandler.HandleAsync(null, Parts("/debug why is x null"), CancellationToken.None);

        Assert.Equal(Strings.DebugUnavailable, result.Message);
        Assert.Null(result.SendAsPrompt);
    }

    [Fact]
    public async Task AnUnreachableDebugger_CountsAsNone()
    {
        var session = new FakeSession { IsAvailable = false };

        var result = await DebugCommandHandler.HandleAsync(session, Parts("/debug"), CancellationToken.None);

        Assert.Equal(Strings.DebugUnavailable, result.Message);
        Assert.Empty(session.Calls);
    }

    [Fact]
    public async Task AHypothesis_BecomesAPromptToRun_AndTouchesNothing()
    {
        // The load-bearing assertion is the second one. A command that started the session itself
        // would bypass the approval that exists precisely because starting executes the user's code.
        var session = new FakeSession();

        var result = await DebugCommandHandler.HandleAsync(
            session, Parts("/debug why is total 106 instead of 105"), CancellationToken.None);

        Assert.Null(result.Message);
        Assert.Contains("why is total 106 instead of 105", result.SendAsPrompt);
        Assert.Empty(session.Calls);
    }

    [Fact]
    public async Task BareDebug_ReportsWhatTheDebuggerIsDoing()
    {
        var session = new FakeSession
        {
            State = new DebugStopState("breakpoint", 0,
                [new DebugFrame(1, "Program.Compute", @"C:\ws\Program.cs", 14)],
                [new DebugVariable("total", "int", "106")]),
        };
        session.Breakpoints.Add(new DebugBreakpointInfo(@"C:\ws\Program.cs", 14, true));

        var result = await DebugCommandHandler.HandleAsync(session, Parts("/debug"), CancellationToken.None);

        Assert.Null(result.SendAsPrompt);
        Assert.Contains("Program.Compute", result.Message);
        Assert.Contains(@"C:\ws\Program.cs:14", result.Message);
    }

    [Fact]
    public async Task BareDebug_WhenNothingIsPaused_SaysThat_AndStillListsTheBreakpoints()
    {
        var session = new FakeSession();
        session.Breakpoints.Add(new DebugBreakpointInfo(@"C:\ws\Program.cs", 14, false));

        var result = await DebugCommandHandler.HandleAsync(session, Parts("/debug"), CancellationToken.None);

        Assert.Contains(Strings.DebugStatusNotPaused, result.Message);
        Assert.Contains("(disabled)", result.Message);
    }

    [Fact]
    public async Task Stop_EndsTheSession()
    {
        var session = new FakeSession();

        var result = await DebugCommandHandler.HandleAsync(session, Parts("/debug stop"), CancellationToken.None);

        Assert.Equal(Strings.DebugStopped, result.Message);
        Assert.Equal(["stop"], session.Calls);
    }

    [Fact]
    public void TheLoopPrompt_TellsTheModelWhatToDoWhenItCannotConclude()
    {
        // §20's lesson, written into the wording rather than hoped for: a loop that stops must
        // report that it stopped instead of concluding anyway.
        var prompt = DebugCommandHandler.BuildLoopPrompt("is the cache ever hit?");

        Assert.Contains("is the cache ever hit?", prompt);
        Assert.Contains("budget", prompt);
        Assert.Contains("verbatim", prompt);
        Assert.Contains("debug_inspect", prompt);
    }
}
