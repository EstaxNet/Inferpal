using System.Text.Json;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  A plan the orchestrator cannot read leaves the run holding a plan that is not the model's. Ornith 1.5 writes its
//  plan, a sentence, then its first step's call as an object of its own: read from the first "{" to the last "}", that
//  is no JSON, and the one-step fallback plan, closed at the first call, made every later round say "all plan steps
//  are complete, answer WITHOUT calling any more tools" before anything was done.
// ──────────────────────────────────────────────────────────────────────────────────────────────
public sealed class LostPlanTests
{
    // Ornith 1.5's plan reply on a real Scriban bug, word for word.
    private const string OrnithPlanReply = "\n\n"
        + """{"goal":"Fix Scriban so that imported delegates respect default argument values from their delegate definition, exposing them via IScriptCustomFunction's parameter info.","steps":[{"i":1,"desc":"Locate IScriptCustomFunction interface and its implementations (RequiredParameterCount, ParameterCount, GetParameterInfo)","tool":"search_in_files"},{"i":2,"desc":"Find where delegates/MethodInfo are imported into ScriptObject and how parameters are captured","tool":"search_in_files"},{"i":3,"desc":"Read the relevant source files to understand current implementation","tool":"read_file"},{"i":4,"desc":"Modify import logic to capture default parameter values from MethodInfo","tool":"apply_edits"},{"i":5,"desc":"Add a regression test covering delegate default arguments","tool":"write_file"},{"i":6,"desc":"Build and run tests to verify the fix","tool":"run_command"}]}"""
        + "\n\nLet me start by locating the relevant code.\n\n"
        + """{"goal":"Locate IScriptCustomFunction interface and its implementations (RequiredParameterCount, ParameterCount, GetParameterInfo)","tool":"search_in_files","query":"IScriptCustomFunction"}""";

    private const string OneStep = """{"goal":"Read the page","steps":[{"i":1,"desc":"read it","tool":"read_file"}]}""";
    private const string TwoSteps =
        """{"goal":"Add a Reset button","steps":[{"i":1,"desc":"read the page","tool":"read_file"},{"i":2,"desc":"add the button","tool":"apply_diff"}]}""";

    private static AgentPlan? PlanOf(string reply, bool reasoning = false) =>
        AgentOrchestrator.PlanOf(new ChatTurnResult(reply, null, 0, 0, AnswerIsReasoning: reasoning));

    // ── The reading ───────────────────────────────────────────────────────────────

    [Fact]
    public void ThePlanOrnithWrote_IsRead_ItsStepsCallAfterItIsNot()
    {
        var plan = PlanOf(OrnithPlanReply);

        Assert.NotNull(plan);
        Assert.Equal(6, plan!.Steps.Count);
        Assert.StartsWith("Fix Scriban", plan.Goal, StringComparison.Ordinal);
        Assert.Equal("search_in_files", plan.Steps[0].ToolHint);
        Assert.False(plan.IsFallback);
    }

    [Fact]
    public void BracesInsideAString_DoNotCutThePlan()
    {
        const string reply = """{"goal":"Fix the template","steps":[{"i":1,"desc":"replace {\"x\"} by { x }}","tool":"apply_diff"}]}"""
            + "\nThen:\n" + """{"tool":"read_file"}""";

        Assert.Equal("replace {\"x\"} by { x }}", Assert.Single(PlanOf(reply)!.Steps).Description);
    }

    [Fact]
    public void AFencedPlan_WithProseAround_IsRead()
    {
        Assert.Equal(2, PlanOf("Here is the plan:\n```json\n" + TwoSteps + "\n```\nI will start with the page.")!.Steps.Count);
    }

    [Fact]
    public void AnExampleAfterThePlan_DoesNotReplaceIt()
    {
        Assert.Equal("Add a Reset button", PlanOf(TwoSteps + "\nA minimal plan would look like " + OneStep + ".")!.Goal);
    }

    [Fact]
    public void ADraftInTheReasoning_LosesToThePlanWrittenAfterIt()
    {
        Assert.Equal("Add a Reset button", PlanOf("<think>A first idea: " + OneStep + " — no, the button too.</think>\n" + TwoSteps)!.Goal);
    }

    [Fact]
    public void APlanWrittenOnlyInTheReasoning_IsTheLastOneThere()
    {
        Assert.Equal("Add a Reset button", PlanOf("<think>First: " + OneStep + " Better: " + TwoSteps + "</think>")!.Goal);
        // A reply that IS reasoning, surfaced as the answer: the plan it ends on.
        Assert.Equal("Add a Reset button", PlanOf("First: " + OneStep + " Better: " + TwoSteps, reasoning: true)!.Goal);
    }

    [Fact]
    public void AStepThatCarriesStepsOfItsOwn_DoesNotReplaceThePlan()
    {
        const string nested = """{"goal":"G","steps":[{"i":1,"desc":"Refactor","tool":"apply_edits","steps":[{"i":1,"desc":"a"},{"i":2,"desc":"b"}]},{"i":2,"desc":"Run tests","tool":"run_tests"}]}""";

        foreach (var reasoning in new[] { false, true })
        {
            var plan = PlanOf(nested, reasoning);
            Assert.Equal("G", plan!.Goal);
            Assert.Equal(["Refactor", "Run tests"], plan.Steps.Select(s => s.Description));
        }
    }

    [Fact]
    public void TwoStepsThatCarryStepsOfTheirOwn_DoNotReplaceThePlan()
    {
        const string nested = """{"goal":"G","steps":[{"desc":"A","steps":[{"desc":"a"}]},{"desc":"B","steps":[{"desc":"b"}]}]}""";

        foreach (var reasoning in new[] { false, true })
            Assert.Equal(["A", "B"], PlanOf(nested, reasoning)!.Steps.Select(s => s.Description));
    }

    [Fact]
    public void APlanInsideAnObjectThatIsNotOne_IsRead()
    {
        foreach (var reasoning in new[] { false, true })
            Assert.Equal("Add a Reset button", PlanOf("{\"answer\":" + TwoSteps + "}", reasoning)!.Goal);
    }

    [Fact]
    public void AnEcho_IsAPlanInItsWhole_AndOnlyThen()
    {
        Assert.True(AgentOrchestrator.LooksLikePlanEcho(TwoSteps));
        Assert.True(AgentOrchestrator.LooksLikePlanEcho(
            """{"goal":"Créer utils/date.js","steps":[{"i":1,"desc":"Lire un fichier voisin","tool":"read_file"}]}"""));
        // A plan with a sentence and another object after it is no echo, as before: an answer may show a JSON example.
        Assert.False(AgentOrchestrator.LooksLikePlanEcho(OrnithPlanReply));
    }

    // ── The run ───────────────────────────────────────────────────────────────────

    private sealed class Scripted(IEnumerable<ChatTurnResult> script) : IChatTurnClient
    {
        private readonly Queue<ChatTurnResult> _script = new(script);
        public List<List<ChatMessageDto>> Seen { get; } = [];

        public Task<ChatTurnResult> SendChatAsync(string model, List<ChatMessageDto> messages, IToolRegistry tools,
            Action<string>? onToken, CancellationToken ct, TaskComplexity complexity = TaskComplexity.Normal,
            string? toolChoice = null, Action<string>? onThinking = null)
        {
            Seen.Add([.. messages]);
            return Task.FromResult(_script.Count > 1 ? _script.Dequeue() : _script.Peek());
        }
    }

    /// <summary>A registry whose edits write, answering with the Smart Fix note they are given; the rest reads.</summary>
    private sealed class Registry(string editOutput = "Change applied.") : IToolRegistry
    {
        private int _writes;
        public IReadOnlyList<ToolDefinition> Definitions { get; } =
            new[] { "read_file", "search_in_files", "get_diagnostics", "apply_diff" }
                .Select(n => new ToolDefinition("function", new ToolFunction(n, n, new { }))).ToList();
        public int? WritesInRun => _writes;
        public DiffInfo? ConsumeDiff() => null;
        public Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct)
        {
            if (name == "get_diagnostics") return Task.FromResult(BuildFailing);
            if (name != "apply_diff") return Task.FromResult("@page \"/counter\"");
            _writes++;
            return Task.FromResult(editOutput);
        }
    }

    private static int _n;
    // Each call names another target: identical calls would be stopped by the loop detector, which is not the subject.
    private static ChatTurnResult Call(string tool) =>
        new(string.Empty, [new ToolCallDto(new ToolCallFunction(tool,
            JsonSerializer.SerializeToElement(new { path = $"Counter{Interlocked.Increment(ref _n)}.razor" })))], 0, 0);

    private static ChatTurnResult Say(string text) => new(text, null, 0, 0);

    private static string Broken => "Change applied in Counter.razor.\n\n" +
        Strings.SmartFixBuildErrors(4, "Counter.razor(13,7): error RZ1006: The code block is missing a closing \"}\" character.");

    private const string ProsePlan = "I will read the counter page first, then add the button.";

    private const string BuildFailing = "Counter.razor(13,7): error RZ1006: The code block is missing a closing \"}\" character.";

    private static Task<(Scripted Fake, OrchestratorResult Result)> RunAsync(
        string planReply, Registry tools, params ChatTurnResult[] acts) => RunAsync(planReply, tools, 8, null, acts);

    private static async Task<(Scripted Fake, OrchestratorResult Result)> RunAsync(
        string planReply, Registry tools, int maxIterations, Action<int, AgentStepStatus>? onStepUpdate, params ChatTurnResult[] acts)
    {
        var fake = new Scripted([Say(planReply), .. acts]);
        var result = await new AgentOrchestrator(fake, new InferpalConfig { ContextWindowSize = 8192, CompactionEnabled = false, AgentMaxIterations = maxIterations })
            .RunAsync(model: "m", history: [new("system", "s"), new("user", "Add a Reset button to the Counter page.")],
                      tools: tools, onStep: _ => { }, onToken: null, onPlanReady: null, onStepUpdate: onStepUpdate,
                      onToolExecuted: null, onStreamReset: null, ct: CancellationToken.None);
        return (fake, result);
    }

    // What the model read last before the act of index i (the plan request is Seen[0]).
    private static string LastUserBefore(Scripted fake, int act) => fake.Seen[act + 1].Last(m => m.Role == "user").Content!;
    private static bool AnyRequestSays(Scripted fake, string text) =>
        fake.Seen.Any(request => request.Any(m => (m.Content ?? string.Empty).Contains(text, StringComparison.Ordinal)));

    [Fact]
    public async Task ARunWhosePlanReplyIsOrniths_FollowsItsSixSteps()
    {
        var (fake, _) = await RunAsync(OrnithPlanReply, new Registry(), Call("search_in_files"), Say("Found it."));

        Assert.Contains("Remaining plan steps: 5", LastUserBefore(fake, 1));
        Assert.False(AnyRequestSays(fake, "All plan steps are complete"));
    }

    [Fact]
    public async Task ARunWhosePlanIsProse_IsNeverToldItsPlanIsComplete_AndEndsOnItsAnswer()
    {
        const string answer = "The page has no Reset button yet: add one that sets currentCount to 0.";
        var (fake, result) = await RunAsync(ProsePlan, new Registry(), Call("read_file"), Say(answer));

        Assert.True(result.Plan!.IsFallback);
        Assert.Contains("If my most recent request still needs work", LastUserBefore(fake, 1));
        Assert.False(AnyRequestSays(fake, "All plan steps are complete"));
        Assert.False(AnyRequestSays(fake, "WITHOUT calling any more tools"));
        Assert.False(AnyRequestSays(fake, ModelPrompts.AgentNudgeToolCall));
        // The text may be the answer or an intention: asked once, with both ways out, then kept as the answer.
        Assert.Equal(ModelPrompts.AgentNudgePlanUnknown, LastUserBefore(fake, 2));
        Assert.Equal(4, fake.Seen.Count);
        Assert.Equal(answer, result.FinalResponse);
    }

    [Fact]
    public async Task ARunWhosePlanIsProse_ThatStatesItsNextMove_IsAskedToMakeIt()
    {
        var (_, result) = await RunAsync(ProsePlan, new Registry(), Call("read_file"),
                                         Say("Next I will add the button to Counter.razor."), Call("apply_diff"),
                                         Say("Done: the Reset button is in Counter.razor."));

        Assert.Contains(result.Executions, e => e.Name == "apply_diff");
        Assert.Equal("Done: the Reset button is in Counter.razor.", result.FinalResponse);
    }

    [Fact]
    public async Task ARunWhosePlanIsProse_ThatNarratesAtTheStartAndAgainLater_IsAskedBothTimes()
    {
        var (_, result) = await RunAsync(ProsePlan, new Registry(), Say("Let me look at the page first."), Call("read_file"),
                                         Say("Next I will add the button to Counter.razor."), Call("apply_diff"), Say("Done."));

        Assert.Contains(result.Executions, e => e.Name == "apply_diff");
        Assert.Equal("Done.", result.FinalResponse);
    }

    [Fact]
    public async Task ARunWhosePlanIsProse_ThatStartsOnABrokenBuild_IsNotToldItsEditBrokeIt()
    {
        // Witness: what the call returns does read as a failed build, or this test would prove nothing.
        Assert.Equal(Inferpal.Services.Tools.GetDiagnosticsTool.BuildVerdict.Errors,
                     Inferpal.Services.Tools.GetDiagnosticsTool.ReadVerdict(BuildFailing));

        var (fake, _) = await RunAsync(ProsePlan, new Registry(), Call("get_diagnostics"), Call("read_file"), Say("Fixed."));

        Assert.DoesNotContain("no longer builds", LastUserBefore(fake, 1));
        Assert.Contains("If my most recent request still needs work", LastUserBefore(fake, 1));
    }

    [Fact]
    public async Task AtTheIterationLimit_ThePlanItCouldNotReadIsNotLeftRunning()
    {
        var updates = new List<(int Step, AgentStepStatus Status)>();
        var (_, result) = await RunAsync(ProsePlan, new Registry(), 2, (i, s) => updates.Add((i, s)),
                                         Call("read_file"), Call("read_file"), Call("read_file"));

        Assert.True(result.ReachedIterationLimit);
        Assert.Equal(AgentStepStatus.Skipped, result.Plan!.Steps[0].Status);
        Assert.Equal((0, AgentStepStatus.Skipped), updates[^1]);
    }

    [Fact]
    public async Task ARunWhosePlanIsProse_AndWhoseEditBreaksTheBuild_IsStillAskedToRepairIt()
    {
        var (fake, _) = await RunAsync(ProsePlan, new Registry(Broken), Call("apply_diff"), Say("Done."));

        Assert.Contains("no longer builds", LastUserBefore(fake, 1));
    }

    [Fact]
    public async Task AfterTheRepairPrompt_AProseReplyIsTheAnswer_InARunWhosePlanIsProse()
    {
        const string answer = "These errors were there before my change.";
        var (fake, result) = await RunAsync(ProsePlan, new Registry(Broken), Call("apply_diff"), Say(answer));

        Assert.Equal(answer, result.FinalResponse);
        Assert.Equal(3, fake.Seen.Count);
    }
}
