using System.Text;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Models;
using Inferpal.Services.Inference;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A tool call the model writes as TEXT never reaches the editor's stream: the chat bubble, an ACP client and the saved
/// answer showed Gemma's raw <c>&lt;|tool_call&gt;call:search_codebase{…}&lt;tool_call|&gt;</c> above the card of the call
/// it had become. Held from its marker, dropped once read, released when it was no call (<see cref="ToolCallTextGate"/>).
/// </summary>
public sealed class ToolCallTextStreamTests
{
    // Gemma 4 on LM Studio, the call split where a stream splits it: inside the opening marker and inside the arguments.
    private static readonly string[] GemmaCall =
        ["Je cherche.\n", "<|tool", "_call>call:search_codebase{query:", "<|\"|>remise PELICAN<|\"|>}", "<tool_call|>"];

    private static string Sse(IEnumerable<string> contents) =>
        string.Concat(contents.Select(c => "data: " + JsonSerializer.Serialize(new { choices = new[] { new { index = 0, delta = new { content = c } } } }) + "\n\n"))
        + "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";

    private static string Ndjson(IEnumerable<string> contents) =>
        string.Concat(contents.Select(c => JsonSerializer.Serialize(new { message = new { role = "assistant", content = c }, done = false }) + "\n"))
        + JsonSerializer.Serialize(new { message = new { role = "assistant", content = "" }, done = true, done_reason = "stop" }) + "\n";

    private static async Task<(ChatTurnResult Turn, string Streamed)> OpenAiAsync(IEnumerable<string> chunks, IToolRegistry tools)
    {
        using var server = new LoopbackHttpServer(path => path.StartsWith("/v1/chat/completions", StringComparison.Ordinal) ? Sse(chunks) : null);
        var streamed = new StringBuilder();
        var turn = await new OpenAiCompatibleClient(new InferpalConfig { Provider = "openai-compatible", BaseUrl = server.BaseUrl })
            .SendChatAsync("gemma-4-12b-it-qat", [new ChatMessageDto("user", "q")], tools, t => streamed.Append(t), CancellationToken.None);
        Assert.Contains(server.Paths, p => p.StartsWith("/v1/chat/completions", StringComparison.Ordinal));   // witness
        return (turn, streamed.ToString());
    }

    [Fact]
    public async Task AGemmaCall_IsRun_AndOnlyTheTextBeforeItIsStreamed()
    {
        var (turn, streamed) = await OpenAiAsync(GemmaCall, new SearchTool());

        Assert.Equal("search_codebase", Assert.Single(turn.ToolCalls!).Function.Name);
        Assert.Equal("Je cherche.\n", streamed);
    }

    [Fact]
    public async Task TheOllamaClient_HoldsTheCallToo()
    {
        using var server = new LoopbackHttpServer(path => path.StartsWith("/api/chat", StringComparison.Ordinal) ? Ndjson(GemmaCall) : null);
        var streamed = new StringBuilder();

        var turn = await new OllamaClient(new InferpalConfig { Provider = "ollama", BaseUrl = server.BaseUrl })
            .SendChatAsync("gemma4:12b", [new ChatMessageDto("user", "q")], new SearchTool(), t => streamed.Append(t), CancellationToken.None);

        Assert.Contains(server.Paths, p => p.StartsWith("/api/chat", StringComparison.Ordinal));   // witness
        Assert.Equal("search_codebase", Assert.Single(turn.ToolCalls!).Function.Name);
        Assert.Equal("Je cherche.\n", streamed.ToString());
    }

    [Fact]
    public async Task TheTextAfterACall_IsStreamed_EvenWhenTheTurnOpensWithBlankLines()
    {
        // Qwen on LM Studio opens every turn with "\n\n", and the parser returns its content trimmed.
        string[] chunks =
            ["\n\nChecking.\n", "<tool_call>{\"name\":\"search_codebase\",", "\"arguments\":{\"query\":\"x\"}}</tool_call>", "\nThen I summarize."];

        var (turn, streamed) = await OpenAiAsync(chunks, new SearchTool());

        Assert.Equal("search_codebase", Assert.Single(turn.ToolCalls!).Function.Name);
        Assert.StartsWith("\n\nChecking.\n", streamed, StringComparison.Ordinal);
        Assert.EndsWith("Then I summarize.", streamed, StringComparison.Ordinal);
        Assert.DoesNotContain("tool_call", streamed, StringComparison.Ordinal);
    }

    [Fact]
    public void ATurnEndingOnAnException_ShowsWhatWasHeld()
    {
        var streamed = new StringBuilder();
        var gate = new ToolCallTextGate(t => streamed.Append(t), toolsOffered: true);
        gate.Push("Qwen writes ");
        gate.Push("<tool_call> tags around its calls.");

        Assert.Equal("Qwen writes ", streamed.ToString());   // held from the marker
        gate.Release();
        Assert.Equal("Qwen writes <tool_call> tags around its calls.", streamed.ToString());
    }

    [Fact]
    public async Task AMarkerThatIsNoCall_IsStreamedWhole_AtTheEnd()
    {
        string[] chunks = ["Gemma writes its calls between ", "<|tool_call> and <tool_call|>", " tags, without a body."];

        var (turn, streamed) = await OpenAiAsync(chunks, new SearchTool());

        Assert.Null(turn.ToolCalls);
        Assert.Equal(string.Concat(chunks), streamed);
    }

    [Fact]
    public async Task WithoutToolsOffered_ACallShownIsContent_AndStreamsAsItComes()
    {
        var (turn, streamed) = await OpenAiAsync(GemmaCall, EmptyToolRegistry.Instance);

        Assert.Null(turn.ToolCalls);
        Assert.Equal(string.Concat(GemmaCall), streamed);
    }

    [Theory]
    [InlineData("Bonjour <", 1)]
    [InlineData("Bonjour <|tool_ca", 9)]
    [InlineData("Bonjour [TOOL_", 6)]
    [InlineData("a < b", 0)]
    [InlineData("Bonjour", 0)]
    public void AMarkerCutByTheStream_IsHeldUntilItIsWhole(string text, int held) =>
        Assert.Equal(held, ToolCallTextGate.PartialOpenerLength(text));

    private sealed class SearchTool : IToolRegistry
    {
        public IReadOnlyList<ToolDefinition> Definitions { get; } =
            [new("function", new ToolFunction("search_codebase", "Search the code.", new { type = "object" }))];
        public DiffInfo? ConsumeDiff() => null;
        public Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct) => Task.FromResult("ok");
    }
}
