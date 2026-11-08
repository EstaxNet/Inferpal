using System.Text.Json;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A green last check followed by edits that nothing checked is not the last word: the result bar says "tests passed,
/// then N files changed without a check", with the info mark. What wrote is the registry's count, carried by each call
/// (<c>ToolExecution.Wrote</c>): a refusal at the card does not read as a failure.
/// </summary>
public sealed class RunCheckedThenEditedTests
{
    private const string Passed = "✓ Tests passed (dotnet): 3 passed";
    private static ToolExecution Tests(string output = Passed, string input = "{}") => new("run_tests", input, output);
    private static ToolExecution Edit(bool? wrote, string tool = "apply_diff", string output = "Change applied in app.js.",
                                      string path = "app.js") =>
        new(tool, $$"""{"path":"{{path}}"}""", output, Wrote: wrote);

    [Fact]
    public void AGreenThenAnEditThatWrote_SaysTheEdit_AndIsNoCheckMark()
    {
        var summary = RunSummary.Build([Tests(), Edit(wrote: true), Edit(wrote: true, path: "lib.js")], null)!;

        Assert.Equal(RunCheck.PassedThenEdited, summary.Check);
        Assert.Equal(Strings.RunCheckedThenEdited(Strings.RunTestsPassed, 2), summary.CheckText);
        Assert.Contains(summary.CheckText, summary.Detail);
    }

    [Fact]
    public void AGreenBuild_ThenAnEdit_SaysTheEdit()
    {
        var summary = RunSummary.Build([new("get_diagnostics", "{}", Strings.DiagBuildOk("App.csproj")), Edit(wrote: true)], null)!;

        Assert.Equal(RunCheck.PassedThenEdited, summary.Check);
        Assert.Equal(Strings.RunCheckedThenEdited(Strings.RunBuildPassed, 1), summary.CheckText);
    }

    [Fact]
    public void ThreeEditsOfOneFile_AreOneFile_AsTheLineCountsThem()
    {
        var summary = RunSummary.Build([Tests(), Edit(wrote: true), Edit(wrote: true), Edit(wrote: true, path: "./app.js")], null)!;

        Assert.Equal(Strings.RunCheckedThenEdited(Strings.RunTestsPassed, 1), summary.CheckText);
    }

    [Fact]
    public void OneBatchOfEdits_CountsEachFileItWrote()
    {
        var batch = new ToolExecution("apply_edits", """{"edits":[{"path":"a.js"},{"path":"b.js"},{"path":"a.js"}]}""",
                                      "Applied 3 edits.", Wrote: true);

        var summary = RunSummary.Build([Tests(), batch], null)!;

        Assert.Equal(Strings.RunCheckedThenEdited(Strings.RunTestsPassed, 2), summary.CheckText);
    }

    [Fact]
    public void AnEditRefusedAtItsCard_IsNoEdit()
    {
        // "Change cancelled by user." is no failure by its text: the registry wrote nothing, and the call says so.
        var summary = RunSummary.Build([Tests(), Edit(wrote: false, output: Strings.DiffCancelled)], null)!;

        Assert.Equal(RunCheck.TestsPassed, summary.Check);
    }

    [Fact]
    public void AnEditThatCarriesItsOwnGreenBuild_IsTheLastCheck()
    {
        var summary = RunSummary.Build([Tests(), Edit(wrote: true, output: "Change applied.\n\n" + Strings.SmartFixBuildOk)], null)!;

        Assert.Equal(RunCheck.BuildPassed, summary.Check);
    }

    [Fact]
    public void ARedThenAnEdit_StaysRed()
    {
        var summary = RunSummary.Build([Tests("✗ Tests failed (dotnet): 1 failed"), Edit(wrote: true)], null)!;

        Assert.Equal(RunCheck.TestsFailed, summary.Check);
    }

    [Fact]
    public void TheMemory_IsNoEdit()
    {
        var summary = RunSummary.Build([Tests(), Edit(wrote: null, tool: "update_memory", output: "Memory updated.")], null)!;

        Assert.Equal(RunCheck.TestsPassed, summary.Check);
    }

    [Fact]
    public void ACheckThatProvedNothing_AfterTheEdit_DoesNotHideIt()
    {
        var summary = RunSummary.Build([Tests(), Edit(wrote: true), Tests(Services.Tools.RunTestsTool.NoTestFound)], null)!;

        Assert.Equal(RunCheck.PassedThenEdited, summary.Check);
    }

    [Fact]
    public void AGreenOnPartOfTheSuite_ThenAnEdit_SaysBoth()
    {
        var summary = RunSummary.Build([Tests(input: """{"filter":"OneTest"}"""), Edit(wrote: true)], null)!;

        Assert.Equal(RunCheck.PassedThenEdited, summary.Check);
        Assert.Equal(Strings.RunCheckedThenEdited(Strings.RunTestsPassedPart, 1), summary.CheckText);
    }

    [Fact]
    public void WithoutARegistryCount_TheResultIsTheWitness()
    {
        // A registry that does not count its writes leaves Wrote unset: an edit that did not fail reads as written.
        var summary = RunSummary.Build([Tests(), Edit(wrote: null)], null)!;

        Assert.Equal(RunCheck.PassedThenEdited, summary.Check);
    }

#if WINDOWS   // the window's items: off Windows the adapter does not exist, and this file still compiles
    [Fact]
    public void VisualStudio_DrawsIt_WithTheInfoMark_AndAFailedStepBeforeTheGreen_DoesNotTurnItRed()
    {
        // A build that failed, the fix, the tests green, then a README edited: the run is not red, and not ✓ either.
        var summary = RunSummary.Build([Tests(), Edit(wrote: true)], null)!;
        var line = Inferpal.ToolWindow.ChatMessageItem.RunMsg(open: false);
        line.AddStep(new Inferpal.ToolWindow.ChatMessageItem { Role = "tool", HasErrors = true });

        line.EndRun(summary);

        Assert.False(line.RunFailed);
        Assert.Equal(Inferpal.ToolWindow.Glyphs.Info, line.RunGlyph);
        Assert.True(Inferpal.ToolWindow.ChatMessageItem.ResultMsg(summary, null, null, _ => { }).CheckUnproven);
    }
#endif

    // ── The loop sets the fact ──────────────────────────────────────────────────────────────

    private sealed class Scripted(IEnumerable<ChatTurnResult> script) : IChatTurnClient
    {
        private readonly Queue<ChatTurnResult> _script = new(script);
        public Task<ChatTurnResult> SendChatAsync(string model, List<ChatMessageDto> messages, IToolRegistry tools,
            Action<string>? onToken, CancellationToken ct, TaskComplexity complexity = TaskComplexity.Normal,
            string? toolChoice = null, Action<string>? onThinking = null) =>
            Task.FromResult(_script.Count > 1 ? _script.Dequeue() : _script.Peek());
    }

    /// <summary>apply_diff writes and counts it; write_file is refused at its card and writes nothing.</summary>
    private sealed class Counting : IToolRegistry
    {
        private int _writes;
        public IReadOnlyList<ToolDefinition> Definitions { get; } =
            new[] { "read_file", "apply_diff", "write_file" }.Select(n => new ToolDefinition("function", new ToolFunction(n, n, new { }))).ToList();
        public int? WritesInRun => _writes;
        public DiffInfo? ConsumeDiff() => null;
        public Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct) => Task.FromResult(name switch
        {
            "apply_diff" => Wrote(),
            "write_file" => Strings.DiffCancelled,
            _            => "contents",
        });
        private string Wrote() { _writes++; return "Change applied in app.js."; }
    }

    private static int _n;
    private static ChatTurnResult Call(string tool) =>
        new(string.Empty, [new ToolCallDto(new ToolCallFunction(tool, JsonSerializer.SerializeToElement(new { path = $"f{Interlocked.Increment(ref _n)}.js" })))], 0, 0);

    [Fact]
    public async Task TheOrchestratedLoop_SaysWhichEditWrote()
    {
        const string plan = """{"goal":"edit","steps":[{"i":1,"desc":"edit","tool":"apply_diff"}]}""";
        var result = await new AgentOrchestrator(new Scripted([new(plan, null, 0, 0), Call("read_file"), Call("apply_diff"), Call("write_file"),
                                                               new("Done.", null, 0, 0)]),
                                                 new InferpalConfig { ContextWindowSize = 8192, CompactionEnabled = false, AgentMaxIterations = 8 })
            .RunAsync(model: "m", history: [new("system", "s"), new("user", "Edit it.")], tools: new Counting(), onStep: _ => { },
                      onToken: null, onPlanReady: null, onStepUpdate: null, onToolExecuted: null, onStreamReset: null, ct: CancellationToken.None);

        Assert.Null(result.Executions.Single(e => e.Name == "read_file").Wrote);
        Assert.True(result.Executions.Single(e => e.Name == "apply_diff").Wrote);
        Assert.False(result.Executions.Single(e => e.Name == "write_file").Wrote);
    }

    private static string CallStream(string tool, string path) =>
        "data: " + JsonSerializer.Serialize(new
        {
            choices = new[] { new { index = 0, delta = new { tool_calls = new[]
            {
                new { index = 0, id = "c" + path, type = "function", function = new { name = tool, arguments = JsonSerializer.Serialize(new { path }) } },
            } } } },
        }) + "\n\n" + "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n\n" + "data: [DONE]\n\n";

    private static string AnswerStream() =>
        "data: " + JsonSerializer.Serialize(new { choices = new[] { new { index = 0, delta = new { content = "Done." } } } }) + "\n\n"
        + "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n" + "data: [DONE]\n\n";

    [Fact]
    public async Task TheBasicLoop_SaysWhichEditWrote()
    {
        (string Tool, string Path)[] calls = [("apply_diff", "a.js"), ("write_file", "b.js")];
        var requests = 0;
        using var server = new LoopbackHttpServer(path =>
        {
            if (!path.StartsWith("/v1/chat/completions", StringComparison.Ordinal)) return null;
            var n = Interlocked.Increment(ref requests);
            return n <= calls.Length ? CallStream(calls[n - 1].Tool, calls[n - 1].Path) : AnswerStream();
        });
        var client = new OpenAiCompatibleClient(new InferpalConfig { Provider = "openai-compatible", BaseUrl = server.BaseUrl });

        var result = await client.RunAgentAsync("m", [new ChatMessageDto("user", "Edit it.")], new Counting(), onStep: _ => { },
                                                onToken: null, CancellationToken.None);

        Assert.True(result.Executions.Single(e => e.Name == "apply_diff").Wrote);
        Assert.False(result.Executions.Single(e => e.Name == "write_file").Wrote);
    }
}
