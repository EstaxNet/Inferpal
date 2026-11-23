using System.IO;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services;
using Inferpal.Services.Agent;
using Inferpal.Services.Execution;
using Inferpal.Services.Commands;
using Inferpal.Services.Inference;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A run the closing cut is picked up again: the bubble says what goes with it, the model reads the request, what was done
/// and what changed since, and the orchestrator resumes the plan at its first step not done — no plan asked for again.
/// </summary>
[Collection(CultureSerialCollection.Name)]
public sealed class RunResumeTurnTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("inferpal-resume-turn").FullName;

    public void Dispose()
    {
        Strings.ApplyLanguage(null);
        try { Directory.Delete(_dir, recursive: true); } catch { /* cleanup */ }
    }

    private HistoryRun CutRun(RunResume resume, params string[] names)
    {
        var run = new HistoryRun("20261123-0915-a", new DateTime(2026, 11, 23, 9, 15, 0)) { Resume = resume, CutShort = true };
        foreach (var name in names)
        {
            var path = Path.Combine(_dir, name);
            File.WriteAllText(path, "after\n");
            run.RecordFirst(path, snapshot: null);
        }
        run.LastChange = new DateTime(2026, 11, 23, 9, 21, 0);
        return run;
    }

    private static RunResume AgentResume() =>
        RunResume.Start("Apply the discount before the tax, with a test", "agent")
            .WithPlan("Discount before tax", [new("Read the cart total", true), new("Move the discount", true), new("Write the test", false), new("Run the tests", false)])
            .WithStep("read_file src/cart/total.ts").WithStep("apply_diff src/cart/total.ts")
            .WithCheck(passed: false, "tests");

    [Fact]
    public void AnAgentRun_ResumesWithItsBubbleChips_ItsQuestion_AndItsPlanFromTheFirstStepNotDone()
    {
        Strings.ApplyLanguage("en");
        var run = CutRun(AgentResume(), "total.ts", "discount.ts");
        var turn = RunResumeTurn.Of(run, path => path.EndsWith("discount.ts", StringComparison.Ordinal), _dir,
                                    now: new DateTime(2026, 11, 23, 10, 0, 0))!;

        var (question, chips) = ChatTurnPolicy.SplitBubbleText(turn.Bubble);
        Assert.StartsWith("Resume the run cut at ", question);
        Assert.Equal(["plan: 2 of 4 done", "2 files changed", "last check failed", "discount.ts changed since"], chips);
        Assert.True(turn.Agent);
        Assert.Equal("Apply the discount before the tax, with a test", turn.Question);
        Assert.Equal([AgentStepStatus.Done, AgentStepStatus.Done, AgentStepStatus.Pending, AgentStepStatus.Pending],
                     turn.Plan!.Steps.Select(s => s.Status));
        Assert.Contains("The request, as the person typed it:\nApply the discount before the tax, with a test", turn.Prompt);
        Assert.Contains("3. [not done] Write the test", turn.Prompt);
        Assert.Contains("- apply_diff src/cart/total.ts", turn.Prompt);
        Assert.Contains("Changed since the cut, by the person: discount.ts. Keep their change", turn.Prompt);
        Assert.Contains("Its last check: the tests failed.", turn.Prompt);
        Assert.EndsWith("Go on from the first step not done.", turn.Prompt);

        Assert.Equal("Your question, the plan with 2 of 4 steps done and the files it changed go with it. Nothing already done is asked "
                     + "for again. Its last tests failed. Changed since the cut: discount.ts. The agent reads it again before going on, "
                     + "and keeps your change.", RunResumeTurn.CardText(run, turn.ChangedSince));
    }

    [Fact]
    public void AChatRun_HasNoPlanToResume_AndCountsItsActions()
    {
        Strings.ApplyLanguage("en");
        var run = CutRun(RunResume.Start("Move the rounding", "chat").WithStep("read_file a.cs").WithStep("write_file b.cs"), "b.cs");
        var turn = RunResumeTurn.Of(run, _ => false, _dir, now: new DateTime(2026, 11, 24, 8, 0, 0))!;
        Assert.Null(turn.Plan);
        Assert.False(turn.Agent);
        Assert.EndsWith("Go on from the last action.", turn.Prompt);
        Assert.StartsWith("This run had no plan: your question, the 2 actions it took", RunResumeTurn.CardText(run, []));
    }

    [Fact]
    public void ARunWithNothingToResume_OffersNoResume()
    {
        // REFERENCE ARM: a slash command's run (or one written before runs kept their question).
        var run = new HistoryRun("20261123-0915-b");
        Assert.Null(RunResumeTurn.Of(run, _ => false, _dir, DateTime.Now));
        Assert.Equal(string.Empty, RunResumeTurn.CardText(run, []));
    }

    [Fact]
    public void TheRunToResume_IsThisEditorsNewest_WhenTheClosingCutIt_NeverOneTheEditorRanPast()
    {
        var cut = CutRun(AgentResume(), "a.cs");
        cut.Editor = "vscode";
        var later = new HistoryRun("20261123-1000-z") { Editor = "vscode" };
        later.RecordFirst(Path.Combine(_dir, "a.cs"), snapshot: null);
        var otherEditor = new HistoryRun("20261123-1100-y") { Editor = "vs" };
        otherEditor.RecordFirst(Path.Combine(_dir, "a.cs"), snapshot: null);
        // Another editor's later run hides nothing: each editor resumes its own.
        Assert.Same(cut, ResumeRunCommandHandler.Pick([cut, otherEditor], runId: null, editor: "vscode"));
        Assert.Null(ResumeRunCommandHandler.Pick([cut, otherEditor], runId: null, editor: "vs"));
        // A later run of this editor: the person went on — neither /resume-run nor the run's name picks it up.
        Assert.Null(ResumeRunCommandHandler.Pick([cut, later], runId: null, editor: "vscode"));
        Assert.Null(ResumeRunCommandHandler.Pick([cut, later], runId: cut.Id, editor: "vscode"));
        // …unless that later run IS its resumption (Regenerate asks it again).
        later.Resume = RunResume.Start("q", "agent").CarriedFrom(AgentResume(), cut.Id, []);
        Assert.Same(cut, ResumeRunCommandHandler.Pick([cut, later], runId: cut.Id, editor: "vscode"));
        // An undone run is never resumed.
        cut.MarkUndone();
        Assert.Null(ResumeRunCommandHandler.Pick([cut], runId: cut.Id, editor: "vscode"));
    }

    [Theory]
    [InlineData("/resume-run", null, false)]
    [InlineData("/resume-run #20261123-0915-a", "20261123-0915-a", false)]
    [InlineData("/resume-run 20261123-0915-a", null, true)]
    [InlineData("/resume-run #", null, true)]
    public void TheCommand_NamesItsRunWithAHash_AndAnythingElseIsSaid(string command, string? runId, bool malformed) =>
        Assert.Equal((runId, malformed), ResumeRunCommandHandler.Arg(command));

    [Fact]
    public void AResumedRun_CarriesWhatTheRunItResumesDid_SoASecondCutKnowsAllOfIt()
    {
        var first = CutRun(AgentResume(), "total.ts", "discount.ts");
        var turn = RunResumeTurn.Of(first, _ => false, _dir, DateTime.Now)!;
        var carried = RunResume.Start(turn.Bubble, "agent").CarriedFrom(turn.Carried, turn.RunId, turn.Files);
        Assert.Equal("Apply the discount before the tax, with a test", carried.Question);
        Assert.Equal(first.Id, carried.ResumedFrom);
        Assert.Equal(2, carried.Actions);
        Assert.Equal(turn.Files, carried.EarlierFiles);
        // Cut again: its own resume names the first run's files too.
        var second = CutRun(carried, "total.test.ts");
        var again = RunResumeTurn.Of(second, _ => false, _dir, DateTime.Now)!;
        Assert.Equal("Files it changed: total.test.ts, discount.ts, total.ts.",
                     again.Prompt.Split((char)10).Single(l => l.StartsWith("Files it changed", StringComparison.Ordinal)));
    }

    [Fact]
    public void ACutActionList_IsSaidAsOne_AndTheCardCountsEveryAction()
    {
        Strings.ApplyLanguage("en");
        var resume = RunResume.Start("Rename everything", "chat");
        for (var i = 0; i < RunResume.MaxSteps + 10; i++) resume = resume.WithStep($"edit {i}");
        var run = CutRun(resume, "a.cs");
        Assert.Contains($"its last {RunResume.MaxSteps} of {RunResume.MaxSteps + 10} actions", RunResumeTurn.Of(run, _ => false, _dir, DateTime.Now)!.Prompt);
        Assert.Contains($"the {RunResume.MaxSteps + 10} actions it took", RunResumeTurn.CardText(run, []));
    }

    [Fact]
    public void AResumedStep_KeepsItsTool_AndAnUnsealedRunSaysChangesAreUnknown()
    {
        var resume = RunResume.Start("q", "agent").WithPlan("g", [new("Read", true, "read_file"), new("Edit Foo.cs", false, "apply_diff")]);
        var turn = RunResumeTurn.Of(CutRun(resume, "Foo.cs"), _ => false, _dir, DateTime.Now)!;
        Assert.Equal("apply_diff", turn.Plan!.Steps[1].ToolHint);
        Assert.Contains("2. [not done] Edit Foo.cs (tool: apply_diff)", turn.Prompt);
        // The cut run here was never sealed (a killed editor): nothing is claimed unchanged.
        Assert.Contains("any of them may have changed since", turn.Prompt);
    }

    [Fact]
    public void AFallbackPlan_IsNeverKeptAsAPlan()
    {
        var history = new FileHistoryService { WorkspaceRoot = () => _dir, UserHistoryRoot = _dir };
        history.BeginRun();
        var recorder = Services.Agent.RunResumeRecorder.Start(history, "q", agent: true);
        recorder.OnPlan(null)(new AgentPlan { Goal = "g", Steps = [new() { Index = 1, Description = "execute" }], IsFallback = true });
        recorder.OnPlan(null)(new AgentPlan { Goal = "g", Steps = [new() { Index = 1, Description = "execute" }], IsFallback = true });
        Assert.Empty(history.Runs.Last().Resume!.Plan);
    }

    [Fact]
    public void TheAwayNotice_SaysWhatResumeRunCarries_ForARunWithSomethingToResume()
    {
        Strings.ApplyLanguage("en");
        var run = CutRun(AgentResume(), "total.ts");
        run.Editor = "vscode";
        var notice = Services.Presentation.AwayNotice.Build([run], "vscode", "VS Code", [], new HashSet<string>(),
                                                            new DateTime(2026, 11, 23, 10, 0, 0), _dir)!;
        Assert.StartsWith("Your question, the plan with 2 of 4 steps done", notice.Run!.Resume);
        // REFERENCE ARM: a run with nothing to resume shows no block.
        var bare = CutRun(null!, "b.cs");
        bare.Resume = null;
        bare.Editor = "vscode";
        Assert.Null(Services.Presentation.AwayNotice.Build([bare], "vscode", "VS Code", [], new HashSet<string>(),
                                                           new DateTime(2026, 11, 23, 10, 0, 0), _dir)!.Run!.Resume);
    }

    // ── The orchestrator resumes the plan it is given ──

    private sealed class ScriptedChatClient(IEnumerable<ChatTurnResult> script) : IChatTurnClient
    {
        private readonly Queue<ChatTurnResult> _script = new(script);
        public List<List<ChatMessageDto>> SeenMessages { get; } = [];

        public Task<ChatTurnResult> SendChatAsync(
            string model, List<ChatMessageDto> messages, IToolRegistry tools, Action<string>? onToken,
            CancellationToken ct, TaskComplexity complexity = TaskComplexity.Normal, string? toolChoice = null,
            Action<string>? onThinking = null)
        {
            SeenMessages.Add([.. messages]);
            return Task.FromResult(_script.Count > 1 ? _script.Dequeue() : _script.Peek());
        }
    }

    private sealed class ReadRegistry : IToolRegistry
    {
        public IReadOnlyList<ToolDefinition> Definitions { get; } =
            [new("function", new ToolFunction("read_file", "read a file", new { }))];
        public DiffInfo? ConsumeDiff() => null;
        public Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct) => Task.FromResult("content");
    }

    [Fact]
    public async Task TheOrchestrator_AsksForNoPlan_AndStartsAtTheFirstStepNotDone()
    {
        var client = new ScriptedChatClient(
        [
            new(string.Empty, [new ToolCallDto(new ToolCallFunction("read_file", JsonDocument.Parse("""{"path":"total.test.ts"}""").RootElement.Clone()))], 0, 0),
            new("The 10 % test is written and the tests pass.", null, 0, 0),
        ]);
        var orch = new AgentOrchestrator(client, new InferpalConfig
            { ContextWindowSize = 8192, CompactionEnabled = false, AgentMaxIterations = 6, AgentModeEnabled = true });
        var plan = new AgentPlan
        {
            Goal = "g",
            Steps =
            [
                new() { Index = 1, Description = "Read", Status = AgentStepStatus.Done },
                new() { Index = 2, Description = "Edit", Status = AgentStepStatus.Done },
                new() { Index = 3, Description = "Write the test" },
            ],
        };
        var shownPlan = (AgentPlan?)null;
        var activated = new List<int>();
        var result = await orch.RunAsync(
            model: "m", history: [new("system", "s"), new("user", "resume prompt")], tools: new ReadRegistry(),
            onStep: _ => { }, onToken: null, onPlanReady: p => shownPlan = p,
            onStepUpdate: (i, s) => { if (s == AgentStepStatus.Active) activated.Add(i); },
            onToolExecuted: null, onStreamReset: null, ct: CancellationToken.None, resumedPlan: plan);

        Assert.Same(plan, shownPlan);
        // The first request is already an action: no plan was asked for, and the plan the model reads is the resumed one.
        Assert.Equal(2, client.SeenMessages.Count);
        Assert.All(client.SeenMessages, seen => Assert.NotEqual(AgentOrchestrator.PlanPrompt(new ReadRegistry(), "resume prompt"), seen[^1].Content));
        var first = client.SeenMessages[0];
        Assert.Contains(first, m => m.Role == "assistant" && m.Content!.Contains("\"Write the test\"", StringComparison.Ordinal));
        Assert.Equal(2, activated.First());
        Assert.DoesNotContain(0, activated);
        Assert.Equal("The 10 % test is written and the tests pass.", result.FinalResponse);
    }
}
