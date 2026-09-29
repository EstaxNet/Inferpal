using System.Text.Json;
using Inferpal.Config;
using Inferpal.Models;
using Inferpal.Services;
using Inferpal.Services.Agent;
using Inferpal.Services.Inference;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ The plan the model writes first stays in the thread as its own message, and small models imitate it: after a
/// tool result they write ANOTHER plan instead of the next call, and taken as the final answer it ends the run — the
/// task half done and the user reading JSON. A plan is not an answer: it is sent back (twice at most), and one still
/// standing at the end is replaced by the synthesis, like a tool refusal.
/// </summary>
public class PlanEchoTests
{
    private sealed class ScriptedChatClient(IEnumerable<ChatTurnResult> script) : IOllamaChatClient
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

    private sealed class SearchRegistry : IToolRegistry
    {
        public IReadOnlyList<ToolDefinition> Definitions { get; } =
            [new("function", new ToolFunction("web_search", "search the web", new { }))];
        public DiffInfo? ConsumeDiff() => null;
        public Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct) => Task.FromResult("result");
    }

    private const string Plan = """{"goal":"find the recipe","steps":[{"i":1,"desc":"search","tool":"web_search"}]}""";
    private const string Answer = "Here is the leek pie recipe: leeks, cream, pastry.";

    private static ChatTurnResult Text(string text) => new(text, null, 0, 0);

    private static ChatTurnResult Search(string query) =>
        new(string.Empty,
            [new ToolCallDto(new ToolCallFunction("web_search", JsonDocument.Parse($$"""{"query":"{{query}}"}""").RootElement.Clone()))],
            0, 0);

    private static async Task<(OrchestratorResult Result, ScriptedChatClient Client)> RunAsync(params ChatTurnResult[] script)
    {
        var client = new ScriptedChatClient(script);
        var orch   = new AgentOrchestrator(client, new InferpalConfig
            { ContextWindowSize = 8192, CompactionEnabled = false, AgentMaxIterations = 8, AgentModeEnabled = true });
        var result = await orch.RunAsync(
            model: "m", history: [new("system", "you are an assistant"), new("user", "Give me the leek pie recipe")],
            tools: new SearchRegistry(), onStep: _ => { }, onToken: null, onPlanReady: null, onStepUpdate: null,
            onToolExecuted: null, onStreamReset: null, ct: CancellationToken.None);
        return (result, client);
    }

    private static int NudgesSeen(ScriptedChatClient client) =>
        client.SeenMessages.Last().Count(m => m.Role == "user" && m.Content == ModelPrompts.AgentPlanEchoNudge);

    [Theory]
    [InlineData(" {   \"goal\": \"Added Invoice class\",   \"steps\": [ { \"i\": 1, \"desc\": \"Navigated\", \"tool\": null } ] }", true)]
    [InlineData("```json\n{\n  \"goal\": \"Rename Cart.ComputeTotal\",\n  \"steps\": [ {\"i\": 1, \"desc\": \"Search\"} ]\n}\n```", true)]
    [InlineData("<think>let me plan</think>{\"goal\":\"g\",\"steps\":[{\"i\":1,\"desc\":\"d\",\"tool\":null}]}", true)]
    [InlineData("{\"goal\":\"g\",\"steps\":[{\"i\":1,\"desc\":\"d\",\"tool\":\"run_tests\"}]", false)]   // broken JSON
    [InlineData("Here is the plan:\n{\"goal\":\"g\",\"steps\":[{\"i\":1,\"desc\":\"d\"}]}", false)]      // prose around it
    [InlineData("The Reset button was added to Counter.razor and the build passes.", false)]
    [InlineData("{\"goal\":\"Create an Inventory\"}", false)]                                            // no step
    public void TheDetector_TakesOnlyAWholePlan(string answer, bool plan) =>
        Assert.Equal(plan, AgentOrchestrator.LooksLikePlanEcho(answer));

    [Fact]
    public async Task APlanWrittenInsteadOfTheNextCall_IsSentBack_AndTheRunGoesOn()
    {
        var (result, client) = await RunAsync(Text(Plan), Search("leek pie"), Text(Plan), Search("leek pie pastry"), Text(Answer));

        Assert.Equal(Answer, result.FinalResponse);
        Assert.Equal(2, result.Executions.Count);   // the second call happened: the run did not end on the plan
        Assert.Equal(1, NudgesSeen(client));
    }

    [Fact]
    public async Task APlanLeftAsTheFinalAnswer_IsReplacedByTheSynthesis()
    {
        // Two nudges, then a third plan: the run ends, and the synthesis — the last scripted reply — answers.
        var (result, client) = await RunAsync(Text(Plan), Search("leek pie"), Text(Plan), Text(Plan), Text(Plan), Text(Answer));

        Assert.Equal(Answer, result.FinalResponse);
        Assert.Contains(client.SeenMessages.Last(),
            m => m.Content?.EndsWith(ModelPrompts.AgentSynthesizePrompt("Give me the leek pie recipe")) == true);
    }

    [Fact]
    public async Task ASynthesisThatWritesAPlanAgain_IsRefused()
    {
        var (result, _) = await RunAsync(Text(Plan), Search("leek pie"), Text(Plan), Text(Plan), Text(Plan), Text(Plan));

        // Empty: the front-ends then show the tool summary, never the JSON.
        Assert.Equal(string.Empty, result.FinalResponse);
    }

    /// <summary>Reference arm: an answer that SHOWS a plan as JSON, with prose around it, is an answer.</summary>
    [Fact]
    public async Task AProseAnswerShowingAJsonPlan_IsKept()
    {
        const string shown = "Here is the plan as JSON:\n```json\n" + Plan + "\n```";
        var (result, client) = await RunAsync(Text(Plan), Search("leek pie"), Text(shown));

        Assert.Equal(shown, result.FinalResponse);
        Assert.Equal(0, NudgesSeen(client));
    }
}
