using System.Text.Json;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Services;
using Inferpal.Services.Debugging;
using Inferpal.Services.Docs;
using Inferpal.Services.Lsp;
using Inferpal.Services.Mcp;
using Inferpal.Services.Rag;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

// Roadmap §21. The rules being locked here were written before the code, and two of them are
// safety rules rather than behaviour: starting a debugging session executes the user's program, so
// it goes through approval; and a background task must never be handed that capability.
public class DebugToolsTests
{
    private sealed class StubApproval(bool approve) : IApprovalService
    {
        public int Calls { get; private set; }
        public string? LastTool { get; private set; }
        public string? LastSubject { get; private set; }

        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct,
                                               string? subject = null, DiffInfo? diff = null, bool forcePrompt = false)
        {
            Calls++;
            LastTool = toolName;
            LastSubject = subject;
            return Task.FromResult(approve);
        }
    }

    private sealed class FakeDebugSession : IDebugSession
    {
        public bool IsAvailable { get; set; } = true;
        public int Starts { get; private set; }
        public int Continues { get; private set; }
        public int Steps { get; private set; }
        public int Stops { get; private set; }
        public string? LastExpression { get; private set; }
        public int? LastFrameId { get; private set; }
        public DebugStopState? State { get; set; } = Paused();
        public bool BreakpointAccepted { get; set; } = true;

        public readonly List<DebugBreakpointInfo> Breakpoints = [];

        public static DebugStopState Paused(string file = @"C:\ws\src\Program.cs") => new(
            "breakpoint", ThreadId: 0,
            Frames: [new DebugFrame(1, "Program.Compute", file, 14),
                     new DebugFrame(2, "Program.Main", file, 20)],
            Locals: [new DebugVariable("total", "int", "106")]);

        public Task<DebugBreakpointInfo?> AddBreakpointAsync(string file, int line, CancellationToken ct)
        {
            if (!BreakpointAccepted) return Task.FromResult<DebugBreakpointInfo?>(null);
            var bp = new DebugBreakpointInfo(file, line, true);
            Breakpoints.Add(bp);
            return Task.FromResult<DebugBreakpointInfo?>(bp);
        }

        public Task<bool> RemoveBreakpointAsync(string file, int line, CancellationToken ct) =>
            Task.FromResult(Breakpoints.RemoveAll(b => b.File == file && b.Line == line) > 0);

        public Task<IReadOnlyList<DebugBreakpointInfo>> ListBreakpointsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DebugBreakpointInfo>>(Breakpoints);

        /// <summary>Set to make the launch fail outright — no build, no run, nothing observed.</summary>
        public string? StartFailure { get; set; }

        public Task<DebugStartResult> StartAsync(CancellationToken ct)
        {
            Starts++;
            return Task.FromResult(
                StartFailure is { } why ? DebugStartResult.Failed(why) :
                State is { } state      ? DebugStartResult.Stopped(state)
                                        : DebugStartResult.RanToCompletion);
        }

        public Task<DebugStopState?> ContinueAsync(CancellationToken ct) { Continues++; return Task.FromResult(State); }

        public Task<DebugStopState?> StepAsync(DebugStepKind kind, CancellationToken ct)
        {
            Steps++;
            return Task.FromResult(State);
        }

        public Task<DebugStopState?> GetStateAsync(CancellationToken ct) => Task.FromResult(State);

        public Task<string?> EvaluateAsync(string expression, int? frameId, CancellationToken ct)
        {
            LastExpression = expression;
            LastFrameId = frameId;
            return Task.FromResult<string?>("42");
        }

        public Task StopAsync(CancellationToken ct) { Stops++; return Task.CompletedTask; }
    }

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;

    private static DebugControlTool Control(FakeDebugSession session, IApprovalService approval,
                                            DebugStepBudget? budget = null) =>
        new(session, approval, budget ?? new DebugStepBudget(), () => @"C:\ws");

    // ── Consent: the session, not the step ──────────────────────────────────────────

    [Fact]
    public async Task Start_WhenDenied_DoesNotLaunchTheProgram()
    {
        var session  = new FakeDebugSession();
        var approval = new StubApproval(approve: false);

        var result = await Control(session, approval).ExecuteAsync(Args("""{"action":"start"}"""), CancellationToken.None);

        Assert.Equal(0, session.Starts);          // the point: nothing ran
        Assert.Equal(Strings.DebugStartCancelled, result);
        Assert.Equal(1, approval.Calls);
        Assert.Equal("debug_control", approval.LastTool);
        Assert.Equal(@"C:\ws", approval.LastSubject);   // matchable by permission rules
    }

    [Fact]
    public async Task Start_WhenApproved_LaunchesAndReportsTheStop()
    {
        var session  = new FakeDebugSession();
        var approval = new StubApproval(approve: true);

        var result = await Control(session, approval).ExecuteAsync(Args("""{"action":"start"}"""), CancellationToken.None);

        Assert.Equal(1, session.Starts);
        Assert.Contains("Debugger paused", result);
        Assert.Contains("Program.Compute", result);
    }

    [Fact]
    public async Task Start_ThatCouldNotRun_IsNotReportedAsAProgramThatRanWithoutStopping()
    {
        // The two outcomes look alike from the port's old `null` and are opposite in meaning. "It
        // ran and never hit your breakpoint" tells the agent to go and question its breakpoint; "it
        // never started" tells the user to go and fix their build or their launch configuration. In
        // VS Code the second is the common case — a workspace with no launch configuration.
        var session  = new FakeDebugSession { StartFailure = "No launch configuration in this workspace." };
        var approval = new StubApproval(approve: true);

        var result = await Control(session, approval).ExecuteAsync(Args("""{"action":"start"}"""), CancellationToken.None);

        Assert.Contains("did not start", result);
        Assert.Contains("No launch configuration", result);
        Assert.DoesNotContain("ran to completion", result);
    }

    [Fact]
    public async Task Start_ThatRanWithoutStopping_StillSaysSo()
    {
        var session  = new FakeDebugSession { State = null };   // started, never paused
        var approval = new StubApproval(approve: true);

        var result = await Control(session, approval).ExecuteAsync(Args("""{"action":"start"}"""), CancellationToken.None);

        Assert.Contains("ran to completion", result);
        Assert.DoesNotContain("did not start", result);
    }

    [Fact]
    public async Task Stepping_And_Breakpoints_NeverPrompt()
    {
        var session  = new FakeDebugSession();
        var approval = new StubApproval(approve: true);
        var tool     = Control(session, approval);

        await tool.ExecuteAsync(Args("""{"action":"set_breakpoint","file":"C:\\ws\\src\\Program.cs","line":14}"""), CancellationToken.None);
        await tool.ExecuteAsync(Args("""{"action":"step_over"}"""), CancellationToken.None);
        await tool.ExecuteAsync(Args("""{"action":"continue"}"""), CancellationToken.None);
        await tool.ExecuteAsync(Args("""{"action":"stop"}"""), CancellationToken.None);

        // Granularity of consent is the session. A prompt per step would make the loop unusable,
        // and none of these actions is a new execution.
        Assert.Equal(0, approval.Calls);
        Assert.Equal(1, session.Steps);
        Assert.Equal(1, session.Continues);
        Assert.Equal(1, session.Stops);
    }

    // ── Budget: exhaustion is reported, never silently ignored (lesson of §20) ───────

    [Fact]
    public async Task Budget_WhenExhausted_StopsSteppingAndForbidsGuessing()
    {
        var session  = new FakeDebugSession();
        var approval = new StubApproval(approve: true);
        var tool     = Control(session, approval, new DebugStepBudget(max: 2));

        await tool.ExecuteAsync(Args("""{"action":"step_over"}"""), CancellationToken.None);
        await tool.ExecuteAsync(Args("""{"action":"step_over"}"""), CancellationToken.None);
        var third = await tool.ExecuteAsync(Args("""{"action":"step_over"}"""), CancellationToken.None);

        Assert.Equal(2, session.Steps);                       // the third never reached the debugger
        Assert.Contains("Step budget exhausted", third);
        Assert.Contains("did not reach a conclusion", third);
    }

    [Fact]
    public async Task Start_ResetsTheBudget()
    {
        var session  = new FakeDebugSession();
        var approval = new StubApproval(approve: true);
        var budget   = new DebugStepBudget(max: 2);
        var tool     = Control(session, approval, budget);

        await tool.ExecuteAsync(Args("""{"action":"step_over"}"""), CancellationToken.None);
        await tool.ExecuteAsync(Args("""{"action":"step_over"}"""), CancellationToken.None);
        Assert.True(budget.IsExhausted);

        await tool.ExecuteAsync(Args("""{"action":"start"}"""), CancellationToken.None);

        Assert.False(budget.IsExhausted);
        var after = await tool.ExecuteAsync(Args("""{"action":"step_over"}"""), CancellationToken.None);
        Assert.DoesNotContain("Step budget exhausted", after);
    }

    // ── Values are opaque: the adapter renders them, we never reinterpret ────────────

    [Theory]
    [InlineData("\"probe-42\"")]        // Visual Studio rendering
    [InlineData("'probe-42'")]          // VS Code / Node rendering
    [InlineData("(3) [21, 42, 43]")]
    [InlineData("Count = 3")]
    public void Formatter_PassesAdapterRenderedValuesThroughVerbatim(string value)
    {
        var state = new DebugStopState("breakpoint", 0, [], [new DebugVariable("label", "string", value)]);

        var text = DebugStateFormatter.Format(state);

        Assert.Contains(value, text);
    }

    // ── Frame filtering: measured need, not a preference (§21 probe: 9 frames for 3 calls) ──

    [Fact]
    public void Formatter_HidesRuntimeFramesOutsideTheWorkspace()
    {
        var state = new DebugStopState("breakpoint", 0,
            [new DebugFrame(1, "compute", @"C:\ws\src\app.js", 9),
             new DebugFrame(2, "wrapModuleLoad", @"C:\nodejs\internal\modules.js", 255),
             new DebugFrame(3, "executeUserEntryPoint", null, 154)],
            []);

        var text = DebugStateFormatter.Format(state, @"C:\ws");

        Assert.Contains("compute", text);
        Assert.DoesNotContain("wrapModuleLoad", text);
        Assert.Contains("2 runtime frame(s) outside the workspace hidden", text);
    }

    [Fact]
    public void Formatter_KeepsEverythingWhenNoFrameIsInTheWorkspace()
    {
        // Showing an empty stack to explain a pause would be worse than showing runtime frames.
        var state = new DebugStopState("exception", 0,
            [new DebugFrame(1, "wrapModuleLoad", @"C:\nodejs\internal\modules.js", 255)], []);

        var text = DebugStateFormatter.Format(state, @"C:\ws");

        Assert.Contains("wrapModuleLoad", text);
    }

    // ── Inspection ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Inspect_Evaluate_ScopesToTheTopFrame()
    {
        var session = new FakeDebugSession();
        var tool    = new DebugInspectTool(session, () => @"C:\ws");

        var result = await tool.ExecuteAsync(
            Args("""{"action":"evaluate","expression":"total * 2"}"""), CancellationToken.None);

        Assert.Equal("total * 2", session.LastExpression);
        Assert.Equal(1, session.LastFrameId);        // frame id from the stack, never assumed
        Assert.Contains("42", result);
    }

    [Fact]
    public async Task Inspect_WhenNotPaused_SaysSoInsteadOfPretending()
    {
        var session = new FakeDebugSession { State = null };
        var tool    = new DebugInspectTool(session, () => @"C:\ws");

        var result = await tool.ExecuteAsync(Args("""{}"""), CancellationToken.None);

        Assert.Contains("not paused", result);
    }

    [Fact]
    public async Task Tools_WhenNoDebuggerIsReachable_TellTheModelNotToRetry()
    {
        var session = new FakeDebugSession { IsAvailable = false };

        var control = await Control(session, new StubApproval(true))
            .ExecuteAsync(Args("""{"action":"start"}"""), CancellationToken.None);
        var inspect = await new DebugInspectTool(session, () => @"C:\ws")
            .ExecuteAsync(Args("""{}"""), CancellationToken.None);

        Assert.Equal(0, session.Starts);
        Assert.Contains("Do not retry", control);
        Assert.Contains("Do not retry", inspect);
    }

    [Fact]
    public async Task SetBreakpoint_OutsideTheWorkspace_IsRefused()
    {
        var session = new FakeDebugSession();

        var result = await Control(session, new StubApproval(true)).ExecuteAsync(
            Args("""{"action":"set_breakpoint","file":"C:\\elsewhere\\Other.cs","line":3}"""),
            CancellationToken.None);

        Assert.StartsWith("Error:", result);
        Assert.Empty(session.Breakpoints);
    }

    // ── Registry wiring ─────────────────────────────────────────────────────────────

    private static ToolRegistry BuildRegistry(IDebugSession? debug)
    {
        var config = new InferpalConfig();
        var client = new FakeInferenceProvider();
        var editor = new NullEditor();
        var approval = new StubApproval(true);
        var index = new ProjectIndexService(client, config, new LspSemanticProvider());
        return new ToolRegistry(editor, approval, config, index, client,
                                new ProjectMapService(editor), new McpToolService(config, approval),
                                new DocsIndexService(client, config), new OpenDocumentOverlay(), debug);
    }

    private sealed class NullEditor : Services.Editor.IEditorSurface
    {
        public bool IsAvailable => false;
        public string? ActiveDocumentPath => null;
        public IReadOnlyList<string> GetOpenDocumentPaths() => [];
        public Task<Services.Editor.ActiveDocument?> GetActiveDocumentAsync(CancellationToken ct) => Task.FromResult<Services.Editor.ActiveDocument?>(null);
        public Task<string?> InsertAtCursorAsync(string text, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task<Services.Editor.EditorEditResult?> ReplaceSelectionAsync(string text, CancellationToken ct) => Task.FromResult<Services.Editor.EditorEditResult?>(null);
        public Task<string?> GetEditorDiagnosticsAsync(CancellationToken ct) => Task.FromResult<string?>(null);
    }

    [Fact]
    public void Registry_WithoutADebugSurface_DoesNotShowTheModelDebugTools()
    {
        var names = BuildRegistry(debug: null).Definitions.Select(d => d.Function.Name).ToList();

        Assert.DoesNotContain(DebugControlTool.ToolName, names);
        Assert.DoesNotContain(DebugInspectTool.ToolName, names);
    }

    [Fact]
    public void Registry_WithADebugSurface_AddsExactlyTheTwoDebugTools()
    {
        var without = BuildRegistry(debug: null).Definitions.Select(d => d.Function.Name).ToHashSet();
        var with    = BuildRegistry(new FakeDebugSession()).Definitions.Select(d => d.Function.Name).ToHashSet();

        Assert.Equal(
            new[] { DebugControlTool.ToolName, DebugInspectTool.ToolName }.OrderBy(n => n),
            with.Except(without).OrderBy(n => n));
    }

    [Fact]
    public void BackgroundTaskRegistry_NeverGetsTheDebugSurface()
    {
        // A background task launching the user's program would be the blank cheque §9 refused:
        // an execution consented to before anyone knows what it is. The sibling registry drops the
        // surface entirely rather than routing its approval into the proposal recorder.
        var sibling = BuildRegistry(new FakeDebugSession()).WithApprovalService(new StubApproval(true));
        var names   = sibling.Definitions.Select(d => d.Function.Name).ToList();

        Assert.DoesNotContain(DebugControlTool.ToolName, names);
        Assert.DoesNotContain(DebugInspectTool.ToolName, names);
    }

    [Fact]
    public void RestrictedRegistries_DoNotWhitelistDebugTools()
    {
        // Both are whitelists, so this holds by construction today — the test exists so that
        // widening either list has to walk past a named decision.
        Assert.False(PlanModeToolRegistry.IsAllowed(DebugControlTool.ToolName));
        Assert.False(PlanModeToolRegistry.IsAllowed(DebugInspectTool.ToolName));
        Assert.False(Services.Tasks.BackgroundTaskToolRegistry.IsAllowed(DebugControlTool.ToolName));
        Assert.False(Services.Tasks.BackgroundTaskToolRegistry.IsProposable(DebugControlTool.ToolName));
    }
}
