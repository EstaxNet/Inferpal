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

// Covers the OBSERVE injection between Act iterations. Regression guard for the "recipe displaced
// by an unrequested read_file" bug: once every plan step is done, the generic observe prompt
// ("call the tool for the next step") pushed the model into a gratuitous extra tool call whose
// result then became the final answer instead of the one the user asked for. The orchestrator must
// switch to the answer-now variant (AgentObservePromptComplete) anchored on the user's request.
public class AgentOrchestratorObserveTests
{
    private sealed class ScriptedChatClient : IOllamaChatClient
    {
        private readonly Queue<ChatTurnResult> _script;
        public List<List<ChatMessageDto>> SeenMessages { get; } = new();

        public ScriptedChatClient(IEnumerable<ChatTurnResult> script) => _script = new(script);

        public Task<ChatTurnResult> SendChatAsync(
            string model, List<ChatMessageDto> messages, IToolRegistry tools, Action<string>? onToken,
            CancellationToken ct, TaskComplexity complexity = TaskComplexity.Normal, string? toolChoice = null,
            Action<string>? onThinking = null)
        {
            SeenMessages.Add(new List<ChatMessageDto>(messages));
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
        public DiffInfo? ConsumeDiff() => null;
        public Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct) =>
            Task.FromResult("search result");
    }

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static ChatTurnResult ToolCallReply(string toolName, string argsJson) =>
        new(string.Empty, [new ToolCallDto(new ToolCallFunction(toolName, Args(argsJson)))], 0, 0);

    private static List<ChatMessageDto> History() =>
    [
        new("system", "you are an assistant"),
        new("user", "Give me the apple pie recipe"),
    ];

    private static InferpalConfig Config() =>
        new() { ContextWindowSize = 8192, CompactionEnabled = false, AgentMaxIterations = 5, AgentModeEnabled = true };

    private static Task<OrchestratorResult> RunAsync(AgentOrchestrator orch, ScriptedChatClient fake) =>
        orch.RunAsync(
            model: "m", history: History(), tools: new SingleToolRegistry(),
            onStep: _ => { }, onToken: null, onPlanReady: null, onStepUpdate: null,
            onToolExecuted: null, onStreamReset: null, ct: CancellationToken.None);

    [Fact]
    public async Task PlanExhausted_ObserveAsksForFinalAnswer_AnchoredOnUserTask()
    {
        // Single-step plan: after the first tool batch, 0 steps remain — the observe message must
        // be the answer-now variant quoting the user's request, not "call the next step's tool".
        var script = new[]
        {
            new ChatTurnResult("""{"goal":"find the recipe","steps":[{"i":1,"desc":"search"}]}""", null, 0, 0),
            ToolCallReply("web_search", """{"query":"apple pie"}"""),   // batch 1 → plan exhausted
            new ChatTurnResult("Here is the apple pie recipe.", null, 0, 0),
        };
        var fake = new ScriptedChatClient(script);
        var orch = new AgentOrchestrator(fake, Config());

        var result = await RunAsync(orch, fake);

        Assert.Equal("Here is the apple pie recipe.", result.FinalResponse);
        var expected = Strings.AgentObservePromptComplete(1, 5, "web_search", "Give me the apple pie recipe");
        Assert.Contains(fake.SeenMessages.Last(), m => m.Role == "user" && m.Content == expected);
        // The generic "next step" variant must NOT have been injected.
        Assert.DoesNotContain(fake.SeenMessages.Last(),
            m => m.Content == Strings.AgentObservePrompt(1, 5, "web_search", 0));
    }

    [Fact]
    public async Task PlanExhausted_ProseAnswer_AcceptedWithoutNudgeOrRewrite()
    {
        // Regression: after the observe-complete prompt asked for a prose answer, the model's
        // visible no-tool-call reply IS that answer. The orchestrator used to fire the one-shot
        // "call a tool" nudge on it, clearing the streamed bubble (onStreamReset) and re-asking —
        // the user saw the recipe appear, vanish, then get rewritten.
        var script = new[]
        {
            new ChatTurnResult("""{"goal":"find the recipe","steps":[{"i":1,"desc":"search"}]}""", null, 0, 0),
            ToolCallReply("web_search", """{"query":"apple pie"}"""),   // batch 1 → plan exhausted
            new ChatTurnResult("Here is the apple pie recipe.", null, 0, 0),
        };
        var fake = new ScriptedChatClient(script);
        var orch = new AgentOrchestrator(fake, Config());

        var resets = 0;
        var result = await orch.RunAsync(
            model: "m", history: History(), tools: new SingleToolRegistry(),
            onStep: _ => { }, onToken: null, onPlanReady: null, onStepUpdate: null,
            onToolExecuted: null, onStreamReset: () => resets++, ct: CancellationToken.None);

        Assert.Equal("Here is the apple pie recipe.", result.FinalResponse);
        // Exactly 3 LLM calls: plan, tool batch, final answer — no extra nudge round-trip.
        Assert.Equal(3, fake.SeenMessages.Count);
        Assert.DoesNotContain(fake.SeenMessages.Last(), m => m.Content == Strings.AgentNudgeToolCall);
        // One reset after the tool batch (clears think-only tokens); the answer bubble itself
        // must never be cleared once the prose reply has streamed.
        Assert.Equal(1, resets);
    }

    [Fact]
    public async Task PlanStepsRemaining_ProseNarration_StillNudgedTowardsTools()
    {
        // The nudge guard must not over-reach: while plan steps remain pending, a prose turn is
        // still a narration stall and the one-shot nudge keeps doing its job.
        var script = new[]
        {
            new ChatTurnResult(
                """{"goal":"find the recipe","steps":[{"i":1,"desc":"search"},{"i":2,"desc":"extract"}]}""",
                null, 0, 0),
            new ChatTurnResult("I will now search the web for the recipe.", null, 0, 0), // narration, no call
            new ChatTurnResult("Here is the apple pie recipe.", null, 0, 0),
        };
        var fake = new ScriptedChatClient(script);
        var orch = new AgentOrchestrator(fake, Config());

        await RunAsync(orch, fake);

        Assert.Contains(fake.SeenMessages.Last(), m => m.Role == "user" && m.Content == Strings.AgentNudgeToolCall);
    }

    [Fact]
    public async Task PlanStepsRemaining_ObserveKeepsTheNextStepVariant()
    {
        // Two-step plan: after the first tool batch one step remains — the generic observe prompt
        // (continue with the next step) is still the right injection.
        var script = new[]
        {
            new ChatTurnResult(
                """{"goal":"find the recipe","steps":[{"i":1,"desc":"search"},{"i":2,"desc":"extract"}]}""",
                null, 0, 0),
            ToolCallReply("web_search", """{"query":"apple pie"}"""),   // batch 1 → 1 step left
            new ChatTurnResult("Here is the apple pie recipe.", null, 0, 0),
        };
        var fake = new ScriptedChatClient(script);
        var orch = new AgentOrchestrator(fake, Config());

        await RunAsync(orch, fake);

        Assert.Contains(fake.SeenMessages.Last(),
            m => m.Role == "user" && m.Content == Strings.AgentObservePrompt(1, 5, "web_search", 1));
        Assert.DoesNotContain(fake.SeenMessages.Last(),
            m => m.Content != null && m.Content.Contains(
                Strings.AgentObservePromptComplete(1, 5, "web_search", "Give me the apple pie recipe")));
    }
}
