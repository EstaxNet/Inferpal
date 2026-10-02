using System.Text.Json;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services;
using Inferpal.Services.Agent;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ A reply the client stopped because the model kept repeating itself is shown — with its notice — but never kept as
/// content that outlives it. Four readers keep a reply as content, and three of them where nothing compacts it: the
/// plan in the run's anchored head, the session recap in every following system prompt, the run summary and the
/// conversation summary in place of the turns they replace. A reasoning model that loops while planning has its whole
/// reasoning surfaced as the reply — anchored, it was sent with every request of the run and each one overflowed the
/// window. A reply cut at the length limit is another fact: a fragment, kept and said (<c>CutSummaryTests</c>).
/// </summary>
[Collection(CultureSerialCollection.Name)]   // compares localized sentences
public class LoopedReplyIsNotKeptTests
{
    // A long-period loop, as a reasoning model writes it while planning: no plan JSON anywhere.
    private static readonly string LoopedReasoning = string.Concat(Enumerable.Repeat(
        "Wait, I need to check the conventions of the folder first. Let me reconsider which file to read, then plan "
        + "the steps again from the beginning because the order matters here. ", 1_000));

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

    private sealed class ReadRegistry : IToolRegistry
    {
        public IReadOnlyList<ToolDefinition> Definitions { get; } =
            [new("function", new ToolFunction("read_file", "read a file", new { }))];
        public DiffInfo? ConsumeDiff() => null;
        public Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct) => Task.FromResult("content");
    }

    private static async Task<ScriptedChatClient> RunWithPlanReplyAsync(ChatTurnResult planReply)
    {
        var client = new ScriptedChatClient([planReply, new ChatTurnResult("The file is created.", null, 0, 0)]);
        var orch   = new AgentOrchestrator(client, new InferpalConfig
            { ContextWindowSize = 32_768, CompactionEnabled = false, AgentMaxIterations = 4, AgentModeEnabled = true });
        await orch.RunAsync(
            model: "m", history: [new("system", "you are an assistant"), new("user", "Create utils/date.js")],
            tools: new ReadRegistry(), onStep: _ => { }, onToken: null, onPlanReady: null, onStepUpdate: null,
            onToolExecuted: null, onStreamReset: null, ct: CancellationToken.None);
        return client;
    }

    /// <summary>The plan reply as the first ACT request carries it: the assistant message after the plan request.</summary>
    private static string AnchoredPlan(ScriptedChatClient client)
    {
        Assert.True(client.SeenMessages.Count >= 2, "the run never got past its plan");               // witness
        var act  = client.SeenMessages[1];
        var plan = act.FindIndex(m => m.Role == "user" && m.Content == AgentOrchestrator.PlanPrompt(new ReadRegistry()));
        Assert.True(plan >= 0, "no plan request in the first ACT request");                         // witness
        Assert.Equal("assistant", act[plan + 1].Role);
        return act[plan + 1].Content ?? string.Empty;
    }

    [Fact]
    public async Task APlanReplyStoppedForRepeating_IsNotAnchored_TheFallbackPlanIs()
    {
        var client = await RunWithPlanReplyAsync(
            new ChatTurnResult(LoopedReasoning, null, 0, 0, CutAtLimit: true, StoppedRepeating: true));

        var anchored = AnchoredPlan(client);
        Assert.Equal(Strings.AgentPlanFallbackGoal, AgentPlan.TryParse(anchored)?.Goal);
        // Every later request of the run carries the head: none may carry the loop.
        Assert.All(client.SeenMessages.Skip(1), request =>
            Assert.True(request.Sum(m => (m.Content ?? "").Length) < 20_000,
                        $"a request of the run carried {request.Sum(m => (m.Content ?? "").Length)} characters"));
    }

    [Fact]
    public async Task APlanReplyCutWhoseJsonWasRead_IsAnchoredAsThatPlan()
    {
        const string reply = "Here is my plan, kept short:\n"
            + """{"goal":"Créer utils/date.js","steps":[{"i":1,"desc":"Lire un fichier voisin","tool":"read_file"}]}""";
        var client = await RunWithPlanReplyAsync(new ChatTurnResult(reply, null, 0, 0, CutAtLimit: true));

        var anchored = AnchoredPlan(client);
        Assert.Equal(AgentPlan.TryParse(reply)!.ToJson(), anchored);
        Assert.DoesNotContain("Here is my plan", anchored);
        Assert.Equal("read_file", Assert.Single(AgentPlan.TryParse(anchored)!.Steps).ToolHint);
    }

    [Fact]
    public async Task AnOrdinaryPlanReply_IsAnchoredVerbatim()
    {
        // Reference arm: a complete reply of ordinary size reaches the head byte for byte, fences and all.
        const string reply = "```json\n{\"goal\":\"Create utils/date.js\",\"steps\":[{\"i\":1,\"desc\":\"Read a sibling\",\"tool\":\"read_file\"}]}\n```";
        var client = await RunWithPlanReplyAsync(new ChatTurnResult(reply, null, 0, 0));

        Assert.Equal(reply, AnchoredPlan(client));
    }

    [Fact]
    public async Task ALongCompletePlanReply_IsCapped_AndKeepsThePlanAtItsEnd()
    {
        // Reasoning surfaced as the reply but complete: bounded like a tool result, the plan written last kept.
        const string planJson = """{"goal":"Create utils/date.js","steps":[{"i":1,"desc":"Read a sibling","tool":"read_file"}]}""";
        var client = await RunWithPlanReplyAsync(new ChatTurnResult(LoopedReasoning[..60_000] + planJson, null, 0, 0));

        var anchored = AnchoredPlan(client);
        Assert.True(anchored.Length <= AgentOrchestrator.MaxToolResultCharsInContext + 300, $"{anchored.Length} characters anchored");
        Assert.EndsWith(planJson, anchored);
    }

    [Fact]
    public void ThePlanJson_IsWhatTheParserReads_InReadableText()
    {
        var plan = new AgentPlan
        {
            Goal  = "Ajouter « Créer » — 日付",
            Steps = [new AgentPlanStep { Index = 1, Description = "Lire <Foo> & 'bar'", ToolHint = null }],
        };

        var json = plan.ToJson();
        Assert.Contains("« Créer » — 日付", json);                            // not \u-escaped: a model reads it
        Assert.Contains("<Foo> & 'bar'", json);
        Assert.DoesNotContain("\"tool\"", json);                               // no step tool: no null to imitate
        var back = AgentPlan.TryParse(json);
        Assert.Equal(plan.Goal, back?.Goal);
        Assert.Equal("Lire <Foo> & 'bar'", Assert.Single(back!.Steps).Description);
    }

    // ── The run summary ─────────────────────────────────────────────────────────

    private sealed class OneReplyClient(ChatTurnResult reply) : IOllamaChatClient
    {
        public Task<ChatTurnResult> SendChatAsync(
            string model, List<ChatMessageDto> messages, IToolRegistry tools, Action<string>? onToken,
            CancellationToken ct, TaskComplexity complexity = TaskComplexity.Normal, string? toolChoice = null,
            Action<string>? onThinking = null) => Task.FromResult(reply);
    }

    private static async Task<List<ChatMessageDto>> CompactRunAsync(ChatTurnResult summary)
    {
        var orch = new AgentOrchestrator(new OneReplyClient(summary),
                                         new InferpalConfig { ContextWindowSize = 8192, CompactionEnabled = true });
        var msgs = new List<ChatMessageDto> { new("system", new string('s', 4000)), new("user", "go") }
            .Concat(Enumerable.Range(0, 10).Select(_ => new ChatMessageDto("tool", new string('x', 8000))))
            .ToList();
        await orch.CompactRunContextAsync(msgs, anchorCount: 2, model: "m", alreadySummarized: false,
                                          onStep: _ => { }, ct: CancellationToken.None);
        return msgs;
    }

    [Fact]
    public async Task ARunSummaryStoppedForRepeating_IsNotKept_TheOldTurnsAreElided()
    {
        var msgs = await CompactRunAsync(new ChatTurnResult(LoopedReasoning[..20_000], null, 0, 0,
                                                            CutAtLimit: true, StoppedRepeating: true));

        Assert.DoesNotContain(msgs, m => (m.Content ?? "").Contains("Wait, I need to check"));
        Assert.Contains(msgs, m => m.Role == "tool" && m.Content!.Length < 200);   // witness: compacted, by elision
    }

    [Fact]
    public async Task ARunSummaryCutAtTheLengthLimit_IsStillKept()
    {
        // Reference arm: a summary missing its end is a summary.
        var msgs = await CompactRunAsync(new ChatTurnResult("Read Foo.cs; the bug is in", null, 0, 0, CutAtLimit: true));

        Assert.Contains(msgs, m => m.Role == "assistant" && (m.Content ?? "").StartsWith("Read Foo.cs; the bug is in"));
    }

    // ── The conversation summary ────────────────────────────────────────────────

    private static Task<ContextDecision> CompactConversationAsync(ChatTurnResult summary)
    {
        var history = new List<ChatMessageDto> { new("system", "sys") };
        for (var i = 0; i < 8; i++)
        {
            history.Add(new ChatMessageDto("user", $"question {i}"));
            history.Add(new ChatMessageDto("assistant", $"answer {i}"));
        }
        return ContextManager.PrepareAsync(history, new InferpalConfig
            {
                ContextWindowSize = 8_192, ContextWindowKeepTurns = 2, CompactionEnabled = true,
                KvCacheAnchorMessages = 0, CompactionTimeoutSeconds = 10,
            },
            new FakeInferenceProvider { ChatResult = summary }, lastPromptTokens: 100_000, onStep: null,
            ct: CancellationToken.None);
    }

    [Fact]
    public async Task AConversationSummaryStoppedForRepeating_FallsBack_AndSaysWhy()
    {
        var decision = await CompactConversationAsync(new ChatTurnResult(LoopedReasoning[..20_000], null, 0, 0,
                                                                          CutAtLimit: true, StoppedRepeating: true));

        Assert.Equal(ContextOutcome.CompactionFellBack, decision.Outcome);
        Assert.Equal(Strings.MsgContextCompactionRepeating, decision.Notice);
        Assert.True(decision.IsDegraded);
    }

    [Fact]
    public async Task AConversationSummaryCutAtTheLengthLimit_IsStillKept()
    {
        // Reference arm.
        var decision = await CompactConversationAsync(new ChatTurnResult("The user asked about the parser, then", null, 0, 0,
                                                                          CutAtLimit: true));

        Assert.Equal(ContextOutcome.Compacted, decision.Outcome);
        Assert.StartsWith("The user asked about the parser", decision.Summary);
    }

    // ── The session recap ───────────────────────────────────────────────────────

    private static Task<SessionRecapResult> RecapAsync(ChatTurnResult reply) =>
        SessionRecap.WriteAsync(
            [new("system", "sys"), new("user", "question"), new("assistant", "answer")], previousRecap: "Goal: ship.",
            new InferpalConfig { ContextWindowSize = 8_192 },
            new FakeInferenceProvider { ChatResult = reply, RunAgentThroughChat = true }, CancellationToken.None);

    [Fact]
    public async Task ASessionRecapStoppedForRepeating_IsNotKept_AndSaysWhy()
    {
        var recap = await RecapAsync(new ChatTurnResult(LoopedReasoning[..20_000], null, 0, 0,
                                                        CutAtLimit: true, StoppedRepeating: true));

        Assert.Null(recap.Recap);
        Assert.Contains("repeating", recap.Failure);
    }

    [Fact]
    public async Task ASessionRecapCutAtTheLengthLimit_IsStillKept()
    {
        // Reference arm.
        var recap = await RecapAsync(new ChatTurnResult("Goal: ship. Progress: parser", null, 0, 0, CutAtLimit: true));

        Assert.StartsWith("Goal: ship. Progress: parser", recap.Recap);
        Assert.EndsWith(HistoryCompaction.CutSummaryMarker, recap.Recap);
        Assert.Null(recap.Failure);
    }
}
