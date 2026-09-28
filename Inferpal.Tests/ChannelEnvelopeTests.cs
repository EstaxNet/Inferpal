using System.Text;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Models;
using Inferpal.Services.Inference;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  Muse Glimmer's addressed messages are split: reasoning to the reasoning channel, the answer to the answer.
//
//  LM Studio streams Muse Glimmer's turn as content, envelope included: every answer opened on
//  " to=self<|message|>We have modified file. Need to write complete final answer…<|eom|><|start|>assistant
//  to=user<|message|>" before the actual reply — on screen, in the history the model reads back, in artifacts.
//  The chunks below are the ones the server sent, cut where it cut them (the header arrives in pieces).
// ──────────────────────────────────────────────────────────────────────────────────────────────
public class ChannelEnvelopeTests
{
    private static readonly string[] MuseChunks =
    [
        " to", "=self", "<|message|>", "We", " need", " answer", " in", " one", " sentence", ".", " `$", "\"", "Total",
        ":", " {", "x", "}\"", "`", ".\n\n", "Let's", " deliver", ".", "<|eom|>", "<|start|>", "assistant", " to",
        "=user", "<|message|>", "The", " `$", "`", " prefix", " makes", " it", " an", " interpolated", " string", ".",
    ];

    private const string MuseAnswer  = "The `$` prefix makes it an interpolated string.";
    private const string MuseThought = "We need answer in one sentence. `$\"Total: {x}\"`.\n\nLet's deliver.";

    private static (string Answer, string Thought, bool Envelope) Split(IEnumerable<string> chunks)
    {
        var envelope = new ChannelEnvelope();
        var answer   = new StringBuilder();
        var thought  = new StringBuilder();
        foreach (var chunk in chunks)
        {
            var (a, t) = envelope.Push(chunk);
            answer.Append(a);
            thought.Append(t);
        }
        var (la, lt) = envelope.Flush();
        return (answer.Append(la).ToString(), thought.Append(lt).ToString(), envelope.IsEnvelope);
    }

    /// <summary>The same text re-cut into pieces of <paramref name="size"/> characters — markers split anywhere.</summary>
    private static IEnumerable<string> Recut(string text, int size)
    {
        for (var i = 0; i < text.Length; i += size) yield return text.Substring(i, Math.Min(size, text.Length - i));
    }

    [Fact]
    public void AMuseGlimmerTurn_KeepsTheAnswer_AndMovesTheReasoningAside()
    {
        var (answer, thought, envelope) = Split(MuseChunks);

        Assert.True(envelope);
        Assert.Equal(MuseAnswer, answer);
        Assert.Equal(MuseThought, thought);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(11)]
    public void TheSplit_DoesNotDependOnWhereTheStreamIsCut(int size)
    {
        var (answer, thought, _) = Split(Recut(string.Concat(MuseChunks) + "<|eot|>", size));

        Assert.Equal(MuseAnswer, answer);
        Assert.Equal(MuseThought, thought);
    }

    [Theory]
    [InlineData("Hello, the build is green.")]
    [InlineData("to=do: rename the method")]           // starts like an address, is not one
    [InlineData("<div>markup</div> in an answer")]      // starts like a marker, is not one
    [InlineData(" t")]
    [InlineData("<think>reasoning</think>answer")]      // another format: left to the <think> readers
    [InlineData("A reply that names <|start|> as text, later.")]
    public void OrdinaryContent_PassesThroughUntouched(string text)
    {
        // Reference arm: what is not an envelope is not touched, however it is cut.
        foreach (var size in new[] { 1, 2, 7, 1000 })
        {
            var (answer, thought, envelope) = Split(Recut(text, size));
            Assert.Equal(text, answer);
            Assert.Equal(string.Empty, thought);
            Assert.False(envelope);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(1000)]
    public void GemmasThoughtChannel_IsReasoning_WhereverTheStreamIsCut(int size)
    {
        // Gemma 4 opens its turn with <|channel>thought…<channel|> (Google's prompt-formatting guide).
        var (answer, thought, envelope) = Split(Recut("<|channel>thought\nThe user wants the sum.<channel|>The sum is 3.", size));

        Assert.True(envelope);
        Assert.Equal("The sum is 3.", answer);
        Assert.Equal("\nThe user wants the sum.", thought);
    }

    [Fact]
    public void GemmasEmptyThoughtChannel_LeavesTheAnswerAlone()
    {
        // With thinking off, the 31B still writes an empty block.
        var (answer, thought, _) = Split(["<|channel>thought\n<channel|>", "Done."]);

        Assert.Equal("Done.", answer);
        Assert.Equal("\n", thought);
    }

    [Fact]
    public void AnAnswerWithoutReasoning_IsAllAnswer()
    {
        var (answer, thought, _) = Split([" to=user<|message|>", "Done.", "<|eot|>"]);

        Assert.Equal("Done.", answer);
        Assert.Equal(string.Empty, thought);
    }

    [Fact]
    public void TextWithNoAddressAfterTheReasoning_IsTakenForTheAnswer_NotHidden()
    {
        var (answer, thought, _) = Split([" to=self<|message|>", "thinking", "<|eom|>", "The answer."]);

        Assert.Equal("thinking", thought);
        Assert.Equal("The answer.", answer);
    }

    // ── gpt-oss: OpenAI's Harmony channels ──────────────────────────────────
    // The battery recorded both leaks: a plan whose answer opened on "<|channel|>final <|constrain|>json<|message|>", and
    // a call left as text — the whole answer was "<|channel|>commentary to=repo_browser.search code<|message|>{…}".

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(1000)]
    public void GptOssFinalChannel_LeavesTheAnswerOnly_WhereverTheStreamIsCut(int size)
    {
        var (answer, thought, envelope) = Split(Recut("<|channel|>final <|constrain|>json<|message|>{\"goal\":\"Rename\",\"steps\":[]}", size));

        Assert.True(envelope);
        Assert.Equal("{\"goal\":\"Rename\",\"steps\":[]}", answer);
        Assert.Equal(string.Empty, thought);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(1000)]
    public void GptOssAnalysisChannel_IsReasoning_AndTheFinalOneTheAnswer(int size)
    {
        var (answer, thought, _) = Split(Recut(
            "<|channel|>analysis<|message|>Need to plan the rename.<|end|><|start|>assistant<|channel|>final<|message|>Done.", size));

        Assert.Equal("Done.", answer);
        Assert.Equal("Need to plan the rename.", thought);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(1000)]
    public void GptOssCall_BecomesACallTheParserReads_WithTheNameFromTheHeader(int size)
    {
        var (answer, _, _) = Split(Recut(
            "<|channel|>commentary to=functions.run_command <|constrain|>json<|message|>{\"command\":\"dotnet --list-sdks\"}<|call|>", size));

        var call = Assert.Single(Inferpal.Services.Agent.InlineToolCallParser.TryParse(answer).Calls!);
        Assert.Equal("run_command", call.Function.Name);
        Assert.Equal("dotnet --list-sdks", call.Function.Arguments.GetProperty("command").GetString());
    }

    [Fact]
    public async Task GptOssCallLeftAsText_ReachesTheRegistry_EvenUnderAnInventedName()
    {
        // Verbatim from the battery: the whole answer, no <|call|>, a tool gpt-oss was trained with and Inferpal lacks.
        // As text it ended the turn; as a call it gets "Unknown tool" and the list of the real ones, and the run goes on.
        string[] chunks =
        [
            "<|channel|>commentary to=repo_browser.search code<|message|>",
            "{\"path\": \"/ws\",\"query\":\"computeTotal\",\"max_results\":20}\n",
        ];
        using var server = new LoopbackHttpServer(path => path.StartsWith("/v1/chat/completions", StringComparison.Ordinal) ? Sse(chunks) : null);

        var turn = await new OpenAiCompatibleClient(new InferpalConfig { Provider = "openai-compatible", BaseUrl = server.BaseUrl })
            .SendChatAsync("openai/gpt-oss-20b", [new ChatMessageDto("user", "Rename computeTotal")], new ToolsWithRunCommand(),
                           null, CancellationToken.None);

        var call = Assert.Single(turn.ToolCalls!);
        Assert.Equal("repo_browser.search", call.Function.Name);
        Assert.Equal("computeTotal", call.Function.Arguments.GetProperty("query").GetString());
        Assert.Equal(string.Empty, turn.TextContent.Trim());
    }

    // ── The production client, a real socket ────────────────────────────────

    private static string Sse(IEnumerable<string> contents) =>
        string.Concat(contents.Select(c => "data: " + JsonSerializer.Serialize(new { choices = new[] { new { index = 0, delta = new { content = c } } } }) + "\n\n"))
        + "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";

    private static async Task<(ChatTurnResult Turn, string Streamed, string Thinking)> SendAsync(IEnumerable<string> contents)
    {
        using var server = new LoopbackHttpServer(path => path.StartsWith("/v1/chat/completions", StringComparison.Ordinal) ? Sse(contents) : null);
        var client   = new OpenAiCompatibleClient(new InferpalConfig { Provider = "openai-compatible", BaseUrl = server.BaseUrl });
        var streamed = new StringBuilder();
        var thinking = new StringBuilder();
        var turn = await client.SendChatAsync("meta/muse-glimmer", [new ChatMessageDto("user", "explain")],
                                              EmptyToolRegistry.Instance, onToken: t => streamed.Append(t), CancellationToken.None,
                                              onThinking: t => thinking.Append(t));
        Assert.Contains(server.Paths, p => p.StartsWith("/v1/chat/completions", StringComparison.Ordinal));   // witness
        return (turn, streamed.ToString(), thinking.ToString());
    }

    [Fact]
    public async Task TheClient_ReturnsAndStreamsTheAnswerOnly_AndTheReasoningAsReasoning()
    {
        var (turn, streamed, thinking) = await SendAsync(MuseChunks);

        Assert.Equal(MuseAnswer, turn.TextContent);
        Assert.Equal(MuseAnswer, streamed);
        Assert.Equal(MuseThought, thinking);
    }

    [Fact]
    public async Task ACallLmStudioLeftAsText_IsRunAsACall()
    {
        // Without tool_choice "required", LM Studio streams Muse Glimmer's call as text in its addressed message — the
        // chunks below are the shape the battery recorded, the call cut the way the server cuts it.
        string[] chunks =
        [
            " to", "=self", "<|message|>", "We should use run_command.", "<|eom|>", "<|start|>", "assistant", " to",
            "=run_command", "<|message|>", "<atem:function_calls>\n<atem:invoke name=\"run_command\">\n",
            "<atem:parameter name=\"command\">dotnet --list-sdks</atem:parameter>\n", "</atem:invoke>\n</atem:function_calls>",
        ];
        var tools = new ToolsWithRunCommand();
        using var server = new LoopbackHttpServer(path => path.StartsWith("/v1/chat/completions", StringComparison.Ordinal) ? Sse(chunks) : null);

        var turn = await new OpenAiCompatibleClient(new InferpalConfig { Provider = "openai-compatible", BaseUrl = server.BaseUrl })
            .SendChatAsync("meta/muse-glimmer", [new ChatMessageDto("user", "Which SDK?")], tools, null, CancellationToken.None);

        var call = Assert.Single(turn.ToolCalls!);
        Assert.Equal("run_command", call.Function.Name);
        Assert.Equal("dotnet --list-sdks", call.Function.Arguments.GetProperty("command").GetString());
        Assert.Equal(string.Empty, turn.TextContent.Trim());   // nothing of the envelope or the call is left to show
    }

    [Fact]
    public async Task TheOllamaClient_SplitsTheEnvelopeToo()
    {
        var body = string.Concat(MuseChunks.Select(c =>
            JsonSerializer.Serialize(new { message = new { role = "assistant", content = c }, done = false }) + "\n"))
            + JsonSerializer.Serialize(new { message = new { role = "assistant", content = "" }, done = true, done_reason = "stop" }) + "\n";
        using var server = new LoopbackHttpServer(path => path.StartsWith("/api/chat", StringComparison.Ordinal) ? body : null);
        var thinking = new StringBuilder();

        var turn = await new OllamaClient(new InferpalConfig { Provider = "ollama", BaseUrl = server.BaseUrl })
            .SendChatAsync("muse-glimmer", [new ChatMessageDto("user", "explain")], EmptyToolRegistry.Instance, null,
                           CancellationToken.None, onThinking: t => thinking.Append(t));

        Assert.Contains(server.Paths, p => p.StartsWith("/api/chat", StringComparison.Ordinal));   // witness
        Assert.Equal(MuseAnswer, turn.TextContent);
        Assert.Equal(MuseThought, thinking.ToString());
    }

    private sealed class ToolsWithRunCommand : IToolRegistry
    {
        public IReadOnlyList<ToolDefinition> Definitions { get; } =
            [new("function", new ToolFunction("run_command", "Run a command.", new { type = "object" }))];
        public DiffInfo? ConsumeDiff() => null;
        public Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct) => Task.FromResult("ok");
    }

    [Fact]
    public async Task TheClient_LeavesAnOrdinaryAnswerAlone()
    {
        var (turn, streamed, thinking) = await SendAsync(["The build", " is green."]);

        Assert.Equal("The build is green.", turn.TextContent);
        Assert.Equal("The build is green.", streamed);
        Assert.Equal(string.Empty, thinking);
    }
}
