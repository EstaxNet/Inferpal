using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

// Covers the final-synthesis pass: when the Plan→Act→Observe loop ends without a printable answer
// but tools ran (web_search, fetch_url…), the orchestrator must make one last no-tools call to turn
// the gathered results into the actual answer — instead of returning an empty FinalResponse that the
// UI renders only as "✓ Done — <tools>". Regression guard for the "recipe never delivered" bug.
public class AgentOrchestratorSynthesisTests
{
    // ── Scripted chat client: returns a queued response per call ──────────────
    private sealed class ScriptedChatClient : IOllamaChatClient
    {
        private readonly Queue<ChatTurnResult> _script;
        public int Calls { get; private set; }
        public List<List<ChatMessageDto>> SeenMessages { get; } = new();

        public ScriptedChatClient(IEnumerable<ChatTurnResult> script) => _script = new(script);

        public Task<ChatTurnResult> SendChatAsync(
            string model, List<ChatMessageDto> messages, IToolRegistry tools, Action<string>? onToken,
            CancellationToken ct, TaskComplexity complexity = TaskComplexity.Normal, string? toolChoice = null,
            Action<string>? onThinking = null)
        {
            Calls++;
            SeenMessages.Add(new List<ChatMessageDto>(messages));
            // Last scripted reply repeats once the queue runs dry (defensive).
            var reply = _script.Count > 1 ? _script.Dequeue() : _script.Peek();
            return Task.FromResult(reply);
        }
    }

    private sealed class SingleToolRegistry : IToolRegistry
    {
        public IReadOnlyList<ToolDefinition> Definitions { get; } =
        [
            new("function", new ToolFunction("web_search", "search the web", new { })),
        ];
        public string Result { get; set; } = "search result";
        public DiffInfo? ConsumeDiff() => null;
        public Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct) =>
            Task.FromResult(Result);
    }

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static ChatTurnResult PlanReply() =>
        new("""{"goal":"find the recipe","steps":[{"i":1,"desc":"search"}]}""", null, 0, 0);

    private static ChatTurnResult ToolCallReply(string toolName, string argsJson) =>
        new(string.Empty, [new ToolCallDto(new ToolCallFunction(toolName, Args(argsJson)))], 0, 0);

    private static List<ChatMessageDto> History() =>
    [
        new("system", "you are an assistant"),
        new("user", "Give me the leek pie recipe"),
    ];

    private static InferpalConfig Config() =>
        new() { ContextWindowSize = 8192, CompactionEnabled = false, AgentMaxIterations = 5, AgentModeEnabled = true };

    private static Task<OrchestratorResult> RunAsync(AgentOrchestrator orch, IToolRegistry tools) =>
        orch.RunAsync(
            model: "m", history: History(), tools: tools,
            onStep: _ => { }, onToken: null, onPlanReady: null, onStepUpdate: null,
            onToolExecuted: null, onStreamReset: null, ct: CancellationToken.None);

    [Fact]
    public async Task LoopDetected_WithWork_SynthesisesFinalAnswer()
    {
        // PLAN, then the model re-issues the identical web_search until loop detection trips. With
        // work already done, the orchestrator must synthesise rather than return an empty response.
        const string recipe = "Here is the leek pie recipe: ...";
        var script = new[]
        {
            PlanReply(),
            ToolCallReply("web_search", """{"query":"leek pie"}"""),  // iteration 0
            ToolCallReply("web_search", """{"query":"leek pie"}"""),  // iteration 1 → repeat
            ToolCallReply("web_search", """{"query":"leek pie"}"""),  // repeat → loop
            new ChatTurnResult(recipe, null, 0, 0),                             // synthesis call
        };
        var fake = new ScriptedChatClient(script);
        var orch = new AgentOrchestrator(fake, Config());

        var result = await RunAsync(orch, new SingleToolRegistry());

        Assert.Equal(recipe, result.FinalResponse);
        Assert.NotEmpty(result.Executions);
        // The synthesis call ran, and its prompt quotes the user's latest request so the model
        // anchors on THAT question (guards against replaying an earlier turn's answer). The prompt is
        // the tail of the synthesis user turn — the gathered tool digest is prepended to it.
        Assert.Contains(fake.SeenMessages.Last(),
            m => m.Content != null && m.Content.EndsWith(Strings.AgentSynthesizePrompt("Give me the leek pie recipe")));
    }

    [Fact]
    public async Task EmptyFinalTurn_WithWork_SynthesisesFinalAnswer()
    {
        // After the tool round the model stops calling tools but emits only a <think> block (no
        // printable answer). Synthesis must kick in instead of returning the think-only content.
        const string recipe = "Full leek pie recipe.";
        var script = new[]
        {
            PlanReply(),
            ToolCallReply("web_search", """{"query":"leek pie"}"""),  // iteration 0
            new ChatTurnResult("<think>I have everything</think>", null, 0, 0),  // think-only final
            new ChatTurnResult(recipe, null, 0, 0),                             // synthesis call
        };
        var fake = new ScriptedChatClient(script);
        var orch = new AgentOrchestrator(fake, Config());

        var result = await RunAsync(orch, new SingleToolRegistry());

        Assert.Equal(recipe, result.FinalResponse);
    }

    [Fact]
    public async Task Synthesis_QuotesTheLatestUserRequest_NotAnEarlierTurn()
    {
        // Regression guard for the "agent replays its previous answer" bug: on a multi-turn
        // conversation the synthesis prompt must anchor on the LAST user message, not the first.
        var history = new List<ChatMessageDto>
        {
            new("system", "you are an assistant"),
            new("user", "Summarize open-source LLMs"),
            new("assistant", "Here is a summary: Kimi, DeepSeek, GLM."),
            new("user", "What about devstral:24b?"),
        };
        var script = new[]
        {
            PlanReply(),
            ToolCallReply("web_search", """{"query":"devstral 24b"}"""),  // iteration 0
            new ChatTurnResult("<think>done</think>", null, 0, 0),        // think-only → synthesis
            new ChatTurnResult("devstral:24b is …", null, 0, 0),          // synthesis call
        };
        var fake = new ScriptedChatClient(script);
        var orch = new AgentOrchestrator(fake, Config());

        var result = await orch.RunAsync(
            model: "m", history: history, tools: new SingleToolRegistry(),
            onStep: _ => { }, onToken: null, onPlanReady: null, onStepUpdate: null,
            onToolExecuted: null, onStreamReset: null, ct: CancellationToken.None);

        Assert.Equal("devstral:24b is …", result.FinalResponse);
        Assert.Contains(fake.SeenMessages.Last(),
            m => m.Content != null && m.Content.EndsWith(Strings.AgentSynthesizePrompt("What about devstral:24b?")));
    }

    [Fact]
    public void TaskSnippet_TailTruncatesLongTasks()
    {
        // Injected context (workspace, mentions) is PREPENDED to the user turn, so the actual
        // question sits at the end — the snippet must keep the tail, not the head.
        var longTask = new string('x', 1000) + " the actual question?";
        var snippet  = AgentOrchestrator.TaskSnippet(longTask);
        Assert.True(snippet.Length <= 401);
        Assert.StartsWith("…", snippet);
        Assert.EndsWith("the actual question?", snippet);
        Assert.Equal("short question", AgentOrchestrator.TaskSnippet("short question"));
    }

    [Fact]
    public async Task PrintableFinalTurn_DoesNotTriggerSynthesis()
    {
        // When the model already produced a usable answer, no extra synthesis call should happen.
        const string answer = "Direct answer without extra synthesis.";
        var script = new[]
        {
            PlanReply(),
            ToolCallReply("web_search", """{"query":"leek pie"}"""),  // iteration 0
            new ChatTurnResult(answer, null, 0, 0),                            // genuine final answer
        };
        var fake = new ScriptedChatClient(script);
        var orch = new AgentOrchestrator(fake, Config());

        var result = await RunAsync(orch, new SingleToolRegistry());

        Assert.Equal(answer, result.FinalResponse);
        // No synthesis pass: the model's own printable answer is returned verbatim.
        Assert.DoesNotContain(fake.SeenMessages,
            set => set.Any(m => m.Content != null && m.Content.EndsWith(Strings.AgentSynthesizePrompt("Give me the leek pie recipe"))));
    }

    [Fact]
    public async Task VisibleRefusalFinalTurn_WithWork_TriggersSynthesis()
    {
        // Regression guard for the "migrate to GitHub returned nothing" bug: a tool-oriented model
        // (devstral) answers the prose-now turn with a degenerate "I don't have the tools" refusal.
        // That is not a real answer — the orchestrator must synthesise from the gathered results.
        const string answer = "Here is how to migrate to GitHub: create a repo, then push.";
        var script = new[]
        {
            PlanReply(),
            ToolCallReply("web_search", """{"query":"github"}"""),                       // iteration 0
            new ChatTurnResult("I currently don't have the tools needed to assist with your request.", null, 0, 0),
            new ChatTurnResult(answer, null, 0, 0),                                       // synthesis call
        };
        var fake = new ScriptedChatClient(script);
        var orch = new AgentOrchestrator(fake, Config());

        var result = await RunAsync(orch, new SingleToolRegistry());

        Assert.Equal(answer, result.FinalResponse);
    }

    [Fact]
    public async Task SynthesisItselfRefuses_FallsBackInsteadOfSurfacingRefusal()
    {
        // Even when the synthesis call ALSO degenerates into a refusal, it must not be surfaced as the
        // final answer — the caller falls back (empty here → the UI shows the "✓ Done" tool summary).
        var script = new[]
        {
            PlanReply(),
            ToolCallReply("web_search", """{"query":"x"}"""),  // iteration 0
            ToolCallReply("web_search", """{"query":"x"}"""),  // iteration 1 → repeat
            ToolCallReply("web_search", """{"query":"x"}"""),  // repeat → loop
            new ChatTurnResult("I don't have the tools needed.", null, 0, 0),  // synthesis refusal
        };
        var fake = new ScriptedChatClient(script);
        var orch = new AgentOrchestrator(fake, Config());

        var result = await RunAsync(orch, new SingleToolRegistry());

        Assert.DoesNotContain("don't have the tools", result.FinalResponse ?? string.Empty);
    }

    // ── BuildToolDigest ───────────────────────────────────────────────────────
    [Fact]
    public void BuildToolDigest_Empty_ReturnsEmpty() =>
        Assert.Equal(string.Empty, AgentOrchestrator.BuildToolDigest(Array.Empty<ToolExecution>(), 10000));

    [Fact]
    public void BuildToolDigest_IncludesNameAndOutput()
    {
        var execs  = new[] { new ToolExecution("web_search", "{}", "the result") };
        var digest = AgentOrchestrator.BuildToolDigest(execs, 10000);
        Assert.Contains("### web_search", digest);
        Assert.Contains("the result", digest);
    }

    [Fact]
    public void BuildToolDigest_OverBudget_KeepsMostRecentDropsOldest()
    {
        var execs = new[]
        {
            new ToolExecution("old",    "{}", new string('a', 5000)),
            new ToolExecution("recent", "{}", "fresh result"),
        };
        var digest = AgentOrchestrator.BuildToolDigest(execs, 1000);
        Assert.Contains("### recent", digest);
        Assert.Contains("fresh result", digest);
        Assert.DoesNotContain("### old", digest);
    }

    [Fact]
    public void BuildToolDigest_KeepsMostRecentEvenIfAloneOverBudget()
    {
        var execs  = new[] { new ToolExecution("big", "{}", new string('a', 5000)) };
        var digest = AgentOrchestrator.BuildToolDigest(execs, 100);
        Assert.Contains("### big", digest);
    }

    // ── LooksLikeToolRefusal ──────────────────────────────────────────────────
    [Theory]
    [InlineData("I'm here to help, but I currently don't have the tools needed to assist with your request.")]
    [InlineData("Sorry, I do not have access to the tools required.")]
    [InlineData("I don't have the necessary tools for this.")]
    public void LooksLikeToolRefusal_DetectsDegenerateRefusals(string s) =>
        Assert.True(AgentOrchestrator.LooksLikeToolRefusal(s));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Here is how to migrate to GitHub: first create a repository, then push.")]
    public void LooksLikeToolRefusal_AllowsRealAnswers(string? s) =>
        Assert.False(AgentOrchestrator.LooksLikeToolRefusal(s));

    [Fact]
    public void LooksLikeToolRefusal_IgnoresLongAnswersThatMentionTools()
    {
        // A long, genuine answer that merely mentions tools must not be suppressed.
        var s = "You don't have the tools installed yet, so here is the full setup guide: " + new string('x', 500);
        Assert.False(AgentOrchestrator.LooksLikeToolRefusal(s));
    }
}
