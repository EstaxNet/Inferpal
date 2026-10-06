using System.IO;
using System.Text.Json;
using Inferpal.Host;
using Inferpal.Services.Debugging;
using Inferpal.Services.Execution;
using Inferpal.Services.Signals;
using Inferpal.Services.Tools;
using Nerdbank.Streams;
using StreamJsonRpc;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A debugger that did not answer is not a debugger that answered "no".
/// </summary>
/// <remarks>
/// Both front-ends turned a dead channel into an ordinary answer: under Visual Studio the in-process driver held by a
/// dialog on the UI thread answers nothing within its budget, under VS Code the extension's request fails — and the
/// port returned <c>null</c>, which the tools read as "the debugger refused a breakpoint (no executable code on that
/// line?)", "no paused debug session", "the program ended". The model then moved the breakpoint, or concluded that the
/// code never ran, about a debugger that had not been reached at all.
/// </remarks>
[Collection(SignalCollection.Name)]
public sealed class DebuggerNoAnswerTests : IDisposable
{
    private readonly SignalScratchDir _scratch = new();
    private readonly string _root = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "inferpal-tests", $"debug-noanswer-{Guid.NewGuid():N}")).FullName;

    public DebuggerNoAnswerTests()
    {
        File.WriteAllText(Path.Combine(_root, "Program.cs"), "class P { static void Main() { var x = 1; } }");
        SignalDebugSession.QueryTimeout  = TimeSpan.FromMilliseconds(400);
        SignalDebugSession.ResumeTimeout = TimeSpan.FromMilliseconds(400);
    }

    public void Dispose()
    {
        SignalDebugSession.QueryTimeout  = TimeSpan.FromSeconds(20);
        SignalDebugSession.ResumeTimeout = TimeSpan.FromMinutes(2);
        DebugCommandSignal.ClearReady();
        _scratch.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class Approve : IApprovalService
    {
        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct, string? subject = null,
                                               Services.CodeActions.DiffInfo? diff = null, bool forcePrompt = false) =>
            Task.FromResult(true);
    }

    private DebugControlTool Tool(IDebugSession session) =>
        new(session, new Approve(), new DebugStepBudget(), () => _root);

    private static JsonElement Args(object o) => JsonSerializer.SerializeToElement(o);

    private object SetHere() => new { action = "set_breakpoint", file = Path.Combine(_root, "Program.cs"), line = 1 };

    /// <summary>Runs the tool the way the registry does: a throw becomes the model-facing failure sentence.</summary>
    private static async Task<string> RunAsync(ITool tool, object args)
    {
        try { return await tool.ExecuteAsync(Args(args), CancellationToken.None); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return ToolFailure.Describe(tool.Name, ex); }
    }

    // ── Visual Studio: the in-process driver is held and answers nothing ─────

    [Fact]
    public async Task AHeldVisualStudioDriver_IsNotReadAsARefusedBreakpoint()
    {
        DebugCommandSignal.MarkReady(Environment.ProcessId);   // ready, and mute: a dialog holds its UI thread
        var session = new SignalDebugSession();
        Assert.True(session.IsAvailable);                      // WITNESS: the session believes a driver is there

        var answer = await RunAsync(Tool(session), SetHere());

        Assert.DoesNotContain("refused", answer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("did not answer", answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHeldVisualStudioDriver_IsNotReadAsNothingPaused()
    {
        DebugCommandSignal.MarkReady(Environment.ProcessId);
        var session = new SignalDebugSession();

        var answer = await RunAsync(new GetDebuggerStateTool(session, () => _root), new { });

        Assert.DoesNotContain("No paused debug session", answer, StringComparison.Ordinal);
        Assert.Contains("did not answer", answer, StringComparison.Ordinal);
    }

    // ── VS Code: the extension's request fails ──────────────────────────────

    /// <summary>The extension side: the breakpoint list answers (empty), every other debug request fails, as an adapter
    /// that throws does.</summary>
    private sealed class FailingEditor
    {
        [JsonRpcMethod("debug/listBreakpoints")]
        public List<DebugBreakpointDto> ListBreakpoints() => [];

        [JsonRpcMethod("debug/addBreakpoint", UseSingleObjectParameterDeserialization = true)]
        public DebugBreakpointDto AddBreakpoint(DebugBreakpointParams p) =>
            throw new InvalidOperationException("the debug adapter exploded");

        [JsonRpcMethod("debug/state")]
        public DebugStopStateDto State() => throw new InvalidOperationException("the debug adapter exploded");
    }

    private static (RpcDebugSession Session, IDisposable Rpc) OverAFailingEditor()
    {
        var (hostStream, editorStream) = FullDuplexStream.CreatePair();
        var editor = HostRpc.Create(editorStream, editorStream, new FailingEditor());
        editor.StartListening();
        var host = HostRpc.Create(hostStream, hostStream);
        host.StartListening();
        return (new RpcDebugSession(host, declared: true), new CompositeDisposable(host, editor));
    }

    private sealed class CompositeDisposable(params IDisposable[] items) : IDisposable
    {
        public void Dispose() { foreach (var i in items) try { i.Dispose(); } catch { } }
    }

    [Fact]
    public async Task AFailedEditorRequest_IsNotReadAsARefusedBreakpoint()
    {
        var (session, rpc) = OverAFailingEditor();
        using (rpc)
        {
            var answer = await RunAsync(Tool(session), SetHere());

            Assert.DoesNotContain("refused", answer, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("did not answer", answer, StringComparison.Ordinal);
            Assert.Contains("the debug adapter exploded", answer, StringComparison.Ordinal);   // the cause travels
        }
    }

    [Fact]
    public async Task AFailedEditorRequest_IsNotReadAsNothingPaused()
    {
        var (session, rpc) = OverAFailingEditor();
        using (rpc)
        {
            var answer = await RunAsync(new GetDebuggerStateTool(session, () => _root), new { });

            Assert.DoesNotContain("No paused debug session", answer, StringComparison.Ordinal);
            Assert.Contains("did not answer", answer, StringComparison.Ordinal);
        }
    }

    // ── /debug says a silence as one ─────────────────────────────────────────

    [Fact]
    public async Task DebugStatus_WhenTheDriverIsHeld_SaysTheDebuggerDidNotAnswer()
    {
        DebugCommandSignal.MarkReady(Environment.ProcessId);

        var result = await Services.Commands.DebugCommandHandler.HandleAsync(
            new SignalDebugSession(), ["/debug"], CancellationToken.None);

        Assert.StartsWith(Localization.Strings.DebugNoAnswer(string.Empty).TrimEnd(), result.Message, StringComparison.Ordinal);
        Assert.Contains("a dialog is probably waiting in Visual Studio", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Localization.Strings.DebugStatusNotPaused, result.Message, StringComparison.Ordinal);
    }

    // ── A resume says what it came to, on both wires ─────────────────────────

    /// <summary>Answers the next request the way the in-process driver does.</summary>
    private static Task AnswerOnce(Func<DebugCommandRequest, DebugCommandResponse> answer) => Task.Run(async () =>
    {
        for (var i = 0; i < 400; i++)
        {
            if (DebugCommandSignal.ClaimRequest() is { } request)
            {
                DebugCommandSignal.WriteResponse(answer(request));
                return;
            }
            await Task.Delay(15);
        }
    });

    [Theory]
    [InlineData(DebugOps.Resumed.Ended,        nameof(DebugResumeOutcome.Ended))]
    [InlineData(DebugOps.Resumed.StillRunning, nameof(DebugResumeOutcome.StillRunning))]
    [InlineData(DebugOps.Resumed.NotPaused,    nameof(DebugResumeOutcome.NotPaused))]
    public async Task VisualStudio_AResumeWithoutAStop_CarriesWhy(string marker, string outcome)
    {
        var expected = Enum.Parse<DebugResumeOutcome>(outcome);
        DebugCommandSignal.MarkReady(Environment.ProcessId);
        SignalDebugSession.ResumeTimeout = TimeSpan.FromSeconds(30);
        var driver = AnswerOnce(r => new DebugCommandResponse(r.Id, Ok: true, Text: marker));

        var result = await new SignalDebugSession().ContinueAsync(CancellationToken.None);

        await driver;
        Assert.Equal(expected, result.Outcome);
    }

    [Fact]
    public async Task VisualStudio_AResumeTheDriverRefused_IsAFailure_InItsWords()
    {
        DebugCommandSignal.MarkReady(Environment.ProcessId);
        SignalDebugSession.ResumeTimeout = TimeSpan.FromSeconds(30);
        var driver = AnswerOnce(r => new DebugCommandResponse(r.Id, Ok: false, Error: "the command had no effect"));

        var result = await new SignalDebugSession().StepAsync(DebugStepKind.Over, CancellationToken.None);

        await driver;
        Assert.Equal(DebugResumeOutcome.Failed, result.Outcome);
        Assert.Equal("the command had no effect", result.Failure);
    }

    /// <summary>The extension side, answering a resume with one outcome.</summary>
    private sealed class ResumingEditor(DebugResumeDto answer)
    {
        [JsonRpcMethod("debug/continue")]
        public DebugResumeDto Continue() => answer;
    }

    [Theory]
    [InlineData("ended",      nameof(DebugResumeOutcome.Ended))]
    [InlineData("running",    nameof(DebugResumeOutcome.StillRunning))]
    [InlineData("not-paused", nameof(DebugResumeOutcome.NotPaused))]
    public async Task VsCode_AResumeWithoutAStop_CarriesWhy(string outcome, string expectedName)
    {
        var expected = Enum.Parse<DebugResumeOutcome>(expectedName);
        var (hostStream, editorStream) = FullDuplexStream.CreatePair();
        using var editor = HostRpc.Create(editorStream, editorStream, new ResumingEditor(new DebugResumeDto(null, outcome)));
        editor.StartListening();
        using var host = HostRpc.Create(hostStream, hostStream);
        host.StartListening();

        var result = await new RpcDebugSession(host, declared: true).ContinueAsync(CancellationToken.None);

        Assert.Equal(expected, result.Outcome);
    }

    /// <summary>
    /// The outcome strings cross two languages: the bridge writes them in TypeScript, the host reads them as
    /// <see cref="DebugOps.Resumed"/>. A typo on either side is a resume read as "the editor said nothing".
    /// </summary>
    [Fact]
    public void TheBridgesOutcomeStrings_AreTheHostsOwn()
    {
        var protocol = WebviewRebuildTests.TsCode("protocol.ts");
        Assert.Contains("export interface DebugResumeDto {", protocol, StringComparison.Ordinal);   // WITNESS
        Assert.Contains($"outcome: '{DebugOps.Resumed.Ended}' | '{DebugOps.Resumed.StillRunning}' | '{DebugOps.Resumed.NotPaused}' | null;",
                        protocol, StringComparison.Ordinal);

        var bridge = WebviewRebuildTests.TsCode("debugBridge.ts");
        Assert.Contains($"outcome: '{DebugOps.Resumed.NotPaused}'", bridge, StringComparison.Ordinal);
        Assert.Contains($"transition === 'ended' ? '{DebugOps.Resumed.Ended}' : '{DebugOps.Resumed.StillRunning}'",
                        bridge, StringComparison.Ordinal);
    }

    /// <summary>
    /// The in-process driver cannot run without Visual Studio: its answers are read from its source. A resume that did
    /// not stop answers WHY — ended, still running, not paused — never an empty state.
    /// </summary>
    [Fact]
    public void TheVisualStudioDriver_SaysWhyAResumeDidNotStop()
    {
        var driver = ConventionCoverageTests.CodeOnly(Path.Combine(
            ConversationPersistenceSilenceTests.RepoRoot(), "Inferpal.InProc", "GhostText", "VsDebugDriver.cs"));
        Assert.Contains("case DebugOps.Continue:", driver, StringComparison.Ordinal);   // WITNESS
        Assert.Contains("if (!IsPaused) return new(request.Id, Ok: true, Text: DebugOps.Resumed.NotPaused);", driver,
                        StringComparison.Ordinal);
        Assert.Contains("case DBGMODE.DBGMODE_Design: return (null, DebugOps.Resumed.Ended);", driver, StringComparison.Ordinal);
        Assert.Contains("return (null, ct.IsCancellationRequested ? null : DebugOps.Resumed.StillRunning);", driver,
                        StringComparison.Ordinal);
        Assert.Contains(": new(request.Id, Ok: true, Text: outcome);", driver, StringComparison.Ordinal);
    }
}
