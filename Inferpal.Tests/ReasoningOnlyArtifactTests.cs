using System.IO;
using System.Text;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services.Agent;
using Inferpal.Services.Commands;
using Inferpal.Services.Inference;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A reasoning model can spend its whole reply reasoning and write nothing — measured on Qwen3.8 at an 8K context: 6 305
/// reasoning tokens of 8 190, no summary. The OpenAI-compatible client then promotes the reasoning to the answer, so a chat
/// bubble keeps what streamed by — but a summary that replaces turns would hold the draft ("We need to summarize…"), and
/// <c>/check</c> showed it as prose followed by "no anchored finding": a clean verdict nobody gave. The same /check said
/// "no finding" of an EMPTY reply too.
/// </summary>
public class ReasoningOnlyArtifactTests : IDisposable
{
    private const string Draft = "We need to summarize the conversation. The user sent three extracts. Let me count words…";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "inferpal-reasoning-only-" + Guid.NewGuid().ToString("N")[..8]);

    public ReasoningOnlyArtifactTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    // ── The fact, where it is born: the client marks the promotion ────────────────────────────────

    private sealed class NoTools : IToolRegistry
    {
        public IReadOnlyList<ToolDefinition> Definitions { get; } = [];
        public DiffInfo? ConsumeDiff() => null;
        public Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct) => Task.FromResult("");
    }

    private static string Stream(string reasoning, string content)
    {
        var sb = new StringBuilder();
        void Chunk(object delta) => sb.Append("data: ").Append(JsonSerializer.Serialize(new { choices = new[] { new { delta } } })).Append("\n\n");
        Chunk(new { reasoning_content = reasoning });
        if (content.Length > 0) Chunk(new { content });
        sb.Append("data: [DONE]\n\n");
        return sb.ToString();
    }

    private static async Task<ChatTurnResult> SendAsync(string body)
    {
        using var server = new LoopbackHttpServer(path => path.StartsWith("/v1/chat/completions", StringComparison.Ordinal) ? body : null);
        var client = new OpenAiCompatibleClient(new InferpalConfig
            { Provider = "openai-compatible", BaseUrl = server.BaseUrl, ContextWindowSize = 8192 });
        return await client.SendChatAsync("m", [new ChatMessageDto("user", "summarize")], new NoTools(), onToken: null,
                                          CancellationToken.None);
    }

    [Fact]
    public async Task AReplyThatIsOnlyReasoning_IsPromoted_AndSaysSo()
    {
        var result = await SendAsync(Stream(Draft, ""));

        Assert.Equal(Draft, result.TextContent);   // the chat bubble keeps what the user saw
        Assert.True(result.AnswerIsReasoning);
    }

    [Fact]
    public async Task AReplyWithAnAnswer_IsNotMarked()
    {
        // Reference arm: reasoning, then an answer — the answer is the answer.
        var result = await SendAsync(Stream(Draft, "The summary."));

        Assert.Equal("The summary.", result.TextContent);
        Assert.False(result.AnswerIsReasoning);
    }

    [Fact]
    public async Task TheBasicLoop_CarriesTheFact_ToItsResult()
    {
        // The agent's basic loop (production) is how the recap, /commit and the session title reach the model.
        using var server = new LoopbackHttpServer(path => path.StartsWith("/v1/chat/completions", StringComparison.Ordinal)
                                                              ? Stream(Draft, "") : null);
        var client = new OpenAiCompatibleClient(new InferpalConfig
            { Provider = "openai-compatible", BaseUrl = server.BaseUrl, ContextWindowSize = 8192 });

        var run = await client.RunAgentAsync("m", [new ChatMessageDto("user", "recap")], new NoTools(), onStep: _ => { },
                                             onToken: null, CancellationToken.None);

        Assert.Equal(Draft, run.FinalResponse);
        Assert.True(run.AnswerIsReasoning);
    }

    [Fact]
    public async Task ASessionRecap_NeverFoldsTheReasoningIntoTheSystemPrompt()
    {
        static Task<SessionRecapResult> Recap(ChatTurnResult reply) => SessionRecap.WriteAsync(
            [new("system", "sys"), new("user", "question"), new("assistant", "answer")], previousRecap: "Goal: ship.",
            new InferpalConfig { ContextWindowSize = 8_192 },
            new FakeInferenceProvider { ChatResult = reply, RunAgentThroughChat = true }, CancellationToken.None);

        var reasoning = await Recap(new ChatTurnResult(Draft, null, 0, 0, CutAtLimit: true, AnswerIsReasoning: true));
        var written   = await Recap(new ChatTurnResult("Goal: ship. Done: the fix.", null, 0, 0));

        Assert.Null(reasoning.Recap);
        Assert.NotNull(reasoning.Failure);
        Assert.Equal("Goal: ship. Done: the fix.", written.Recap);   // reference arm
    }

    [Fact]
    public async Task ASessionTitle_IsNeverADraft()
    {
        var title = await SessionTitleGenerator.GenerateAsync(
            new FakeInferenceProvider { RunAgentThroughChat = true, ChatResult = new ChatTurnResult("We need to produce a short title", null, 0, 0, AnswerIsReasoning: true) },
            new InferpalConfig(), "the login button does nothing", CancellationToken.None);

        Assert.DoesNotContain("need", title, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(SessionManager.MakeSnippet("the login button does nothing"), title);
    }

    // ── Its readers: the compaction summary, and /check ──────────────────────────────────────────

    private static List<ChatMessageDto> LongHistory()
    {
        var h = new List<ChatMessageDto> { new("system", "sys") };
        for (var i = 0; i < 8; i++)
        {
            h.Add(new ChatMessageDto("user", $"question {i}"));
            h.Add(new ChatMessageDto("assistant", $"answer {i}"));
        }
        return h;
    }

    private static Task<ContextDecision> CompactWith(ChatTurnResult reply) =>
        ContextManager.PrepareAsync(LongHistory(),
            new InferpalConfig { ContextWindowSize = 8192, ContextWindowKeepTurns = 2, CompactionEnabled = true, KvCacheAnchorMessages = 0 },
            new FakeInferenceProvider { ChatResult = reply }, lastPromptTokens: 100_000, onStep: null, CancellationToken.None);

    [Fact]
    public async Task ACompaction_NeverKeepsTheReasoningAsItsSummary_AndNamesTheCause()
    {
        var decision = await CompactWith(new ChatTurnResult(Draft, null, 0, 0, CutAtLimit: true, AnswerIsReasoning: true));

        Assert.Equal(ContextOutcome.CompactionFellBack, decision.Outcome);
        Assert.Equal(Strings.MsgContextCompactionOnlyReasoning, decision.Notice);
        Assert.Null(decision.Summary);
    }

    [Fact]
    public async Task ACompaction_StillKeepsAWrittenSummary()
    {
        // Reference arm: the same text, written as the answer, is a summary.
        var decision = await CompactWith(new ChatTurnResult(Draft, null, 0, 0));

        Assert.Equal(ContextOutcome.Compacted, decision.Outcome);
    }

    private static async Task<List<ChatMessageDto>> RunSummaryWith(ChatTurnResult reply)
    {
        var orch = new AgentOrchestrator(new FakeInferenceProvider { ChatResult = reply },
                                         new InferpalConfig { ContextWindowSize = 8192, CompactionEnabled = true });
        var msgs = new List<ChatMessageDto> { new("system", new string('s', 4000)), new("user", "go") }
            .Concat(Enumerable.Range(0, 10).Select(_ => new ChatMessageDto("tool", new string('x', 8000)))).ToList();
        await orch.CompactRunContextAsync(msgs, anchorCount: 2, model: "m", alreadySummarized: false,
                                          onStep: _ => { }, ct: CancellationToken.None);
        return msgs;
    }

    [Fact]
    public async Task AnAgentRunsSummary_NeverKeepsTheReasoning()
    {
        var msgs = await RunSummaryWith(new ChatTurnResult(Draft, null, 0, 0, CutAtLimit: true, AnswerIsReasoning: true));

        Assert.DoesNotContain(msgs, m => (m.Content ?? "").Contains("Let me count words", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnAgentRunsSummary_StillKeepsAWrittenOne()
    {
        // Reference arm: the same text as an answer is kept as the summary of the run's earlier context.
        var msgs = await RunSummaryWith(new ChatTurnResult(Draft, null, 0, 0));

        Assert.Contains(msgs, m => (m.Content ?? "").Contains("Let me count words", StringComparison.Ordinal));
    }

    private Task<CheckCommandHandler.CheckCommandResult> CheckWith(ChatTurnResult reply)
    {
        Directory.CreateDirectory(Path.Combine(_dir, ".inferpal", "checks"));
        File.WriteAllText(Path.Combine(_dir, ".inferpal", "checks", "secrets.md"),
            "---\ndescription: no secrets\n---\nNo secret may be committed.");
        const string diff = "diff --git a/src/Alpha.cs b/src/Alpha.cs\n--- a/src/Alpha.cs\n+++ b/src/Alpha.cs\n"
                          + "@@ -8,3 +10,4 @@\n+    const string Key = \"sk-live-1234\";\n";
        return CheckCommandHandler.HandleAsync(
            new FakeInferenceProvider { OnChat = (_, _) => Task.FromResult(reply) }, new InferpalConfig { DefaultModel = "qwen3.8" },
            _dir, ["/check"], git: (args, _) => Task.FromResult((args == "diff --staged" ? diff : "", 0)),
            onProgress: null, CancellationToken.None);
    }

    [Fact]
    public async Task ACheckThatOnlyReasoned_IsNotACleanVerdict()
    {
        var result = await CheckWith(new ChatTurnResult(Draft, null, 0, 0, CutAtLimit: true, AnswerIsReasoning: true));

        Assert.Contains(Strings.CheckReviewOnlyReasoning("qwen3.8"), result.Message);
        Assert.DoesNotContain(Strings.CheckNoFindings, result.Message);
        Assert.DoesNotContain("Let me count words", result.Message);
    }

    [Fact]
    public async Task AnEmptyCheck_IsNotACleanVerdict()
    {
        var result = await CheckWith(new ChatTurnResult("", null, 0, 0));

        Assert.DoesNotContain(Strings.CheckNoFindings, result.Message);
        Assert.Contains("qwen3.8", result.Message);   // the model that produced nothing is named
    }

    [Fact]
    public async Task ACheckWithAFinding_IsRenderedAsBefore()
    {
        // Reference arm.
        var result = await CheckWith(new ChatTurnResult("- [blocker] src/Alpha.cs:10 — API key in clear text", [], 0, 0));

        Assert.Contains("API key in clear text", result.Message);
    }
}
