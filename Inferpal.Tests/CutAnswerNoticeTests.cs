using System.IO;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Host;
using StreamJsonRpc;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services.Agent;
using Inferpal.Services.Inference;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ A chat answer that stopped at the model's length limit was shown as a finished answer. The server
/// says so (<c>finish_reason</c> / <c>done_reason</c> = <c>length</c>) and nothing downstream read it: an
/// explanation that ends mid-sentence reads as a curt one, and a code block cut halfway is copied as if
/// it were whole. The answer STAYS — it is what the model wrote — and a line after it says it is
/// incomplete, like the iteration-limit notice next door.
/// </summary>
[Collection(CultureSerialCollection.Name)]   // compares localized sentences
public class CutAnswerNoticeTests
{
    // ── The rule ──────────────────────────────────────────────────────────────

    [Fact]
    public void ACutAnswer_GetsItsNotice_AndAFinishedOneGetsNone()
    {
        Assert.Equal(Strings.AnswerCutAtLimit, ChatTurnPolicy.EndNotice(false, false, answerCut: true));
        Assert.Equal(string.Empty,             ChatTurnPolicy.EndNotice(false, false, answerCut: false));   // reference arm
    }

    [Fact]
    public void ACutAnswerAtTheIterationLimit_SaysBothFacts()
    {
        var notice = ChatTurnPolicy.EndNotice(reachedIterationLimit: true, loopDetected: false, answerCut: true);

        Assert.Contains(Strings.AgentEndedAtIterationLimit, notice);
        Assert.Contains(Strings.AnswerCutAtLimit, notice);
    }

    // ── The basic loop (chat with tools, /explain, /review) — production code ─

    private static string Stream(string finishReason) =>
        "data: " + JsonSerializer.Serialize(new { choices = new[] { new { index = 0, delta = new { content = "Here is the fix:\n```csharp\npublic int Add(int a," } } } }) + "\n\n"
        + "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"" + finishReason + "\"}]}\n\n"
        + "data: [DONE]\n\n";

    [Theory]
    [InlineData("length", true)]
    [InlineData("stop",   false)]   // reference arm
    public async Task TheBasicLoop_ReportsACutAnswer(string finishReason, bool cut)
    {
        using var server = new LoopbackHttpServer(
            path => path.StartsWith("/v1/chat/completions", StringComparison.Ordinal) ? Stream(finishReason) : null);
        var client = new OpenAiCompatibleClient(new InferpalConfig { Provider = "openai-compatible", BaseUrl = server.BaseUrl });

        var result = await client.RunAgentAsync(
            "m", [new ChatMessageDto("user", "fix it")], EmptyToolRegistry.Instance, onStep: _ => { }, onToken: null,
            CancellationToken.None);

        Assert.Contains("/v1/chat/completions", string.Join(" ", server.Paths));   // witness: the model answered
        Assert.StartsWith("Here is the fix:", result.FinalResponse);              // the answer stays
        Assert.Equal(cut, result.AnswerCut);
    }

    // ── The agent orchestrator ────────────────────────────────────────────────

    private sealed class ScriptedClient(params ChatTurnResult[] script) : IOllamaChatClient
    {
        private readonly Queue<ChatTurnResult> _script = new(script);

        public Task<ChatTurnResult> SendChatAsync(
            string model, List<ChatMessageDto> messages, IToolRegistry tools, Action<string>? onToken,
            CancellationToken ct, TaskComplexity complexity = TaskComplexity.Normal, string? toolChoice = null,
            Action<string>? onThinking = null) =>
            Task.FromResult(_script.Count > 1 ? _script.Dequeue() : _script.Peek());
    }

    private sealed class OneTool : IToolRegistry
    {
        public IReadOnlyList<ToolDefinition> Definitions { get; } = [new("function", new ToolFunction("read_file", "read", new { }))];
        public DiffInfo? ConsumeDiff() => null;
        public Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct) => Task.FromResult("file content");
    }

    private static ChatTurnResult Plan() => new("{\"goal\":\"g\",\"steps\":[{\"i\":1,\"desc\":\"read\"}]}", null, 0, 0);
    private static ChatTurnResult Call() =>
        new(string.Empty, [new ToolCallDto(new ToolCallFunction("read_file", JsonDocument.Parse("{\"path\":\"a.cs\"}").RootElement.Clone()))], 0, 0);

    private static Task<OrchestratorResult> RunAsync(params ChatTurnResult[] script) =>
        new AgentOrchestrator(new ScriptedClient(script),
                              new InferpalConfig { ContextWindowSize = 8192, CompactionEnabled = false, AgentMaxIterations = 6, AgentModeEnabled = true })
            .RunAsync(model: "m", history: [new ChatMessageDto("system", "s"), new ChatMessageDto("user", "explain a.cs")],
                      tools: new OneTool(), onStep: _ => { }, onToken: null, onPlanReady: null, onStepUpdate: null,
                      onToolExecuted: null, onStreamReset: null, ct: CancellationToken.None);

    [Fact]
    public async Task TheOrchestrator_ReportsACutFinalAnswer()
    {
        var result = await RunAsync(Plan(), Call(), new ChatTurnResult("a.cs reads the file and", null, 0, 0, CutAtLimit: true));

        Assert.Single(result.Executions);                     // witness: the run did real work
        Assert.StartsWith("a.cs reads the file", result.FinalResponse);
        Assert.True(result.AnswerCut);
    }

    [Fact]
    public async Task TheOrchestrator_ReportsACutSynthesis()
    {
        // The final turn is empty, so the answer is SYNTHESISED — by a second call that can be cut too.
        var result = await RunAsync(Plan(), Call(), new ChatTurnResult("", null, 0, 0),
                                    new ChatTurnResult("From what was read, a.cs", null, 0, 0, CutAtLimit: true));

        Assert.StartsWith("From what was read", result.FinalResponse);
        Assert.True(result.AnswerCut);
    }

    [Fact]
    public async Task TheOrchestrator_AFinishedAnswer_IsNotCut()
    {
        var result = await RunAsync(Plan(), Call(), new ChatTurnResult("a.cs reads the file.", null, 0, 0));

        Assert.False(result.AnswerCut);
    }

    // ── Both front-ends read it ───────────────────────────────────────────────

    [Theory]
    [InlineData("Inferpal", "ToolWindow", "InferpalToolWindowData.ChatTurn.cs")]
    [InlineData("Inferpal.Host", "HostServer.cs")]
    public void BothFrontEnds_TakeTheNoticeFromThePolicy(params string[] parts)
    {
        var code = ConventionCoverageTests.CodeOnly(Path.Combine(RepoRoot(), Path.Combine(parts)));

        Assert.Contains("ReachedIterationLimit", code, StringComparison.Ordinal);   // WITNESS: the end-of-run site
        Assert.Contains("ChatTurnPolicy.EndNotice(", code, StringComparison.Ordinal);
        Assert.Contains("AnswerCut", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// ⚠ The notices are shown AFTER the answer, in both front-ends — and the two older ones said
    /// "what follows summarises…", in ten languages, pointing the reader at nothing. Each names the
    /// answer ABOVE. Read from the English resource file itself: the sentence, not a culture.
    /// </summary>
    [Theory]
    [InlineData("AgentEndedAtIterationLimit")]
    [InlineData("AgentEndedOnRepeat")]
    [InlineData("AnswerCutAtLimit")]
    public void EveryEndNotice_PointsAtTheAnswerAbove_WhereItIsShown(string key)
    {
        var resx  = File.ReadAllText(Path.Combine(RepoRoot(), "Inferpal.Core", "Localization", "Strings.resx"));
        var match = System.Text.RegularExpressions.Regex.Match(
            resx, "<data name=\"" + key + "\" xml:space=\"preserve\"><value>(.*?)</value>");

        Assert.True(match.Success, $"{key} is not in Strings.resx");                // WITNESS: the sentence was read
        Assert.Contains("above", match.Groups[1].Value, StringComparison.Ordinal);
        Assert.DoesNotContain("follow", match.Groups[1].Value, StringComparison.OrdinalIgnoreCase);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}

public partial class HostServerTests
{
    /// <summary>The notice crosses the wire to the VS Code screen, after the answer that stays.</summary>
    [Fact]
    public async Task ChatSend_ACutAnswer_ComesWithItsNotice()
    {
        using var h = CreateHarness();
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        h.Fake.ChatResult = new ChatTurnResult("Here is the fix:\n```csharp\npublic int Add(int a,", null, 0, 0, CutAtLimit: true);

        var result = await h.Client.InvokeWithParameterObjectAsync<ChatSendResult>(
            "chat/send", new { prompt = "fix it", agentMode = false }).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        Assert.StartsWith("Here is the fix:", result.Text);
        Assert.Equal(Strings.AnswerCutAtLimit, result.EndNotice);
    }

    [Fact]
    public async Task ChatSend_AFinishedAnswer_HasNoNotice()
    {
        using var h = CreateHarness();
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        h.Fake.ChatResult = new ChatTurnResult("Here is the fix.", null, 0, 0);

        var result = await h.Client.InvokeWithParameterObjectAsync<ChatSendResult>(
            "chat/send", new { prompt = "fix it", agentMode = false }).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        Assert.Null(result.EndNotice);
    }
}
