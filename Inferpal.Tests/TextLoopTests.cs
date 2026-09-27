using System.Text;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services.Agent;
using Inferpal.Services.Inference;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  A model that loops on plain text is stopped, and the notice says it was repeating itself.
//
//  Measured on LM Studio with glm-4.7-flash: its reasoning cycled on the same ~300 characters ("Let's try
//  this set (3 sentences): **Sentence A**…") until OutputBound stopped it — 8 to 10 minutes of "Thinking…",
//  and the notice then advised increasing the context length, the one remedy that cannot help. In the
//  recorded text the last 200 characters occurred 5 times in 1 500; in every other answer, once.
// ──────────────────────────────────────────────────────────────────────────────────────────────
public class TextLoopTests
{
    private const string Cycle =
        "Let's try this set (3 sentences): **Sentence A**: **IEnumerable<T> executes all LINQ operations immediately " +
        "on client-side objects within memory as you iterate over them.** (Sentence 1) This is a definition of one. " +
        "Then I need to define/queryable vs IEnumerable? Or just say: \"However, `IQueryable` uses expression trees... ";

    private static bool Feed(TextLoopDetector detector, string text, int chunk = 37)
    {
        for (var i = 0; i < text.Length; i += chunk)
            if (detector.Repeats(text.Substring(i, Math.Min(chunk, text.Length - i)))) return true;
        return false;
    }

    private static string Varied(int words)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < words; i++) sb.Append("token").Append(i * 7919 % 100003).Append(' ');
        return sb.ToString();
    }

    [Fact]
    public void ALoopingPassage_IsCaught_WithinAFewRepeats()
    {
        var detector = new TextLoopDetector();
        var text = string.Concat(Enumerable.Repeat(Cycle, 12));

        Assert.True(Feed(detector, text));
    }

    [Fact]
    public void LongVariedText_AndCodeQuotedThreeTimes_AreLeftAlone()
    {
        // Reference arms: an ordinary long answer, and a model that re-reads the same code while it thinks.
        Assert.False(Feed(new TextLoopDetector(), Varied(4000)));

        var code = "public decimal ComputeTotal() => _prices.Sum();\n" + new string('/', 0) +
                   "public decimal ComputeTotalWithDiscount(int percent) => Pricing.ApplyDiscount(ComputeTotal(), percent);\n" +
                   "public void Add(decimal price) => _prices.Add(price);\n";
        var text = Varied(300) + code + Varied(120) + code + Varied(120) + code;
        Assert.False(Feed(new TextLoopDetector(), text));
    }

    [Fact]
    public void TheNotice_NamesTheRepeat_NotTheContextLength()
    {
        var notice = ChatTurnPolicy.EndNotice(false, false, answerCut: true, answerRepeating: true);

        Assert.Contains(Strings.AnswerStoppedRepeating, notice);
        Assert.DoesNotContain(Strings.AnswerCutAtLimit, notice);
        // Reference arm: a response cut at the length limit keeps its own sentence.
        Assert.Contains(Strings.AnswerCutAtLimit, ChatTurnPolicy.EndNotice(false, false, answerCut: true));
    }

    // ── The production clients, a real socket ───────────────────────────────

    private static string OpenAiStream(string field, string text, int chunk = 60)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < text.Length; i += chunk)
        {
            var piece = text.Substring(i, Math.Min(chunk, text.Length - i));
            object delta = field == "content" ? new { content = piece } : new { reasoning_content = piece };
            sb.Append("data: ").Append(JsonSerializer.Serialize(new { choices = new[] { new { index = 0, delta } } })).Append("\n\n");
        }
        return sb.Append("data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n").ToString();
    }

    private static OpenAiCompatibleClient OpenAi(LoopbackHttpServer server) =>
        new(new InferpalConfig { Provider = "openai-compatible", BaseUrl = server.BaseUrl, ContextWindowSize = 262_144 });

    private static LoopbackHttpServer Serve(string body) =>
        new(path => path.StartsWith("/v1/chat/completions", StringComparison.Ordinal) ? body : null);

    [Theory]
    [InlineData("reasoning")]
    [InlineData("content")]
    public async Task ALoopInEitherChannel_IsStopped_AndSaidToBeRepeating(string field)
    {
        var full = string.Concat(Enumerable.Repeat(Cycle, 200));   // ~60 000 characters; the bound is far away
        using var server = Serve(OpenAiStream(field, full));

        var turn = await OpenAi(server).SendChatAsync("m", [new ChatMessageDto("user", "explain")], EmptyToolRegistry.Instance,
                                                      onToken: null, CancellationToken.None);

        Assert.True(turn.CutAtLimit);          // incomplete — every reader that refuses a cut answer refuses this one
        Assert.True(turn.StoppedRepeating);
        Assert.True(turn.TextContent.Length < full.Length / 4, $"read {turn.TextContent.Length} of {full.Length}");
    }

    [Fact]
    public async Task ALongVariedAnswer_IsNeitherStoppedNorSaidToRepeat()
    {
        var text = Varied(6000);
        using var server = Serve(OpenAiStream("content", text));

        var turn = await OpenAi(server).SendChatAsync("m", [new ChatMessageDto("user", "explain")], EmptyToolRegistry.Instance,
                                                      onToken: null, CancellationToken.None);

        Assert.False(turn.StoppedRepeating);
        Assert.False(turn.CutAtLimit);
        Assert.Equal(text, turn.TextContent);
    }

    [Fact]
    public async Task TheBasicLoop_CarriesTheRepeat_ToItsResult()
    {
        using var server = Serve(OpenAiStream("content", string.Concat(Enumerable.Repeat(Cycle, 200))));

        var run = await OpenAi(server).RunAgentAsync("m", [new ChatMessageDto("user", "explain")], EmptyToolRegistry.Instance,
                                                     onStep: _ => { }, onToken: null, CancellationToken.None);

        Assert.True(run.AnswerCut);
        Assert.True(run.AnswerRepeating);
    }

    [Fact]
    public async Task Ollama_AThinkingLoop_IsStopped_AndSaidToBeRepeating()
    {
        var sb = new StringBuilder();
        foreach (var piece in Enumerable.Repeat(Cycle, 200))
            sb.Append(JsonSerializer.Serialize(new { message = new { role = "assistant", content = "", thinking = piece }, done = false })).Append('\n');
        using var server = new LoopbackHttpServer(path => path.StartsWith("/api/chat", StringComparison.Ordinal) ? sb.ToString() : null);
        var client = new OllamaClient(new InferpalConfig { Provider = "ollama", BaseUrl = server.BaseUrl, ContextWindowSize = 262_144 });

        var turn = await client.SendChatAsync("m", [new ChatMessageDto("user", "explain")], EmptyToolRegistry.Instance,
                                              onToken: null, CancellationToken.None);

        Assert.True(turn.CutAtLimit);
        Assert.True(turn.StoppedRepeating);
    }
}
