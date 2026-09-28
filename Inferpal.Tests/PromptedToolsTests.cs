using System.Text.Json;
using Inferpal.Config;
using Inferpal.Models;
using Inferpal.Services.Inference;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  A model whose server cannot render tools still gets them — in its system prompt.
//
//  Gemma 4's bundled chat template on LM Studio fails as soon as a request carries `tools`: every request with tools
//  is refused with "Error rendering prompt with jinja template", the same request without them works. LM Studio
//  answers HTTP 200 and streams the refusal as an error event; another server may send it as a status — the fallback
//  holds for every door a server refuses by.
// ──────────────────────────────────────────────────────────────────────────────────────────────
public class PromptedToolsTests
{
    private const string TemplateRefusal =
        "{\"error\":\"Error rendering prompt with jinja template: \\\"Cannot call something that is not a function: got UndefinedValue\\\".\"}";

    private sealed class Tools : IToolRegistry
    {
        public IReadOnlyList<ToolDefinition> Definitions { get; } =
        [
            new("function", new ToolFunction("read_file", "Read a file.",
                new { type = "object", properties = new { path = new { type = "string" } }, required = new[] { "path" } })),
        ];
        public DiffInfo? ConsumeDiff() => null;
        public Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct) => Task.FromResult("ok");
    }

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void TheRewrite_PutsTheToolsInTheSystemPrompt_AndLeavesNoToolMessageBehind()
    {
        var history = new List<ChatMessageDto>
        {
            new("system", "You are Inferpal."),
            new("user", "Fix it."),
            new("assistant", "", [new ToolCallDto(new ToolCallFunction("read_file", Args("{\"path\":\"a.cs\"}")))]),
            new("tool", "class A {}"),
            new("user", "[OBSERVE] Continue."),
        };

        var rewritten = PromptedTools.Rewrite(OpenAiCompatibleClient.MapMessages(history), new Tools().Definitions);

        Assert.Equal(["system", "user", "assistant", "user"], rewritten.Select(m => m.Role));
        Assert.StartsWith("You are Inferpal.", rewritten[0].Content);
        Assert.Contains("\"name\":\"read_file\"", rewritten[0].Content);           // the tool, declared
        Assert.Contains("<tool_call>", rewritten[0].Content);                       // how to call it
        Assert.Contains("<tool_call>\n{\"name\": \"read_file\", \"arguments\": {\"path\":\"a.cs\"}}\n</tool_call>", rewritten[2].Content);
        // MapMessages already folds the observe note into the tool result; both reach the model, as text.
        Assert.StartsWith("<tool_response>\nclass A {}", rewritten[3].Content);
        Assert.Contains("[OBSERVE] Continue.", rewritten[3].Content);
        Assert.All(rewritten, m => Assert.Null(m.ToolCalls));
        Assert.All(rewritten, m => Assert.Null(m.ToolCallId));
    }

    [Theory]
    [InlineData("HTTP 400 (Bad Request): {\"error\":\"Error rendering prompt with jinja template: \\\"Cannot call…\\\"\"}", true)]
    [InlineData("HTTP 400 (Bad Request): {\"error\":\"request (9000 tokens) exceeds the available context size\"}", false)]
    [InlineData("HTTP 404 (Not Found): model not found", false)]
    public void OnlyATemplateRefusal_IsRead_AsOne(string message, bool expected) =>
        Assert.Equal(expected, PromptedTools.IsTemplateRefusal(message));

    // ── The production client, a real socket ────────────────────────────────

    private static string Sse(string content) =>
        "data: " + JsonSerializer.Serialize(new { choices = new[] { new { index = 0, delta = new { content } } } }) + "\n\n"
        + "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";

    /// <summary>A server whose template fails on tools: 400 when the request carries them, an answer otherwise.</summary>
    private static LoopbackHttpServer GemmaOnLmStudio(string answer) => new(
        (path, request) => !path.StartsWith("/v1/chat/completions", StringComparison.Ordinal) ? null
                         : request.Contains("\"tools\":[", StringComparison.Ordinal) ? TemplateRefusal : Sse(answer),
        (_, request) => request.Contains("\"tools\":[", StringComparison.Ordinal) ? 400 : 200);

    private static OpenAiCompatibleClient Client(LoopbackHttpServer server) =>
        new(new InferpalConfig { Provider = "openai-compatible", BaseUrl = server.BaseUrl });

    /// <summary>The chat requests the server received — not the client's GET probes of the loaded window.</summary>
    private static List<string> Chats(LoopbackHttpServer server) =>
        server.Bodies.Where(b => b.Contains("\"messages\"", StringComparison.Ordinal)).ToList();

    [Fact]
    public async Task ARefusedTemplate_IsAskedAgainWithTheToolsInThePrompt_AndTheCallIsRead()
    {
        using var server = GemmaOnLmStudio("<tool_call>\n{\"name\": \"read_file\", \"arguments\": {\"path\": \"a.cs\"}}\n</tool_call>");
        var model  = "gemma-" + Guid.NewGuid().ToString("N")[..8];   // the session cache is per server and model

        var turn = await Client(server).SendChatAsync(model, [new ChatMessageDto("user", "Read a.cs")], new Tools(),
                                                      onToken: null, CancellationToken.None, toolChoice: "required");

        var call = Assert.Single(turn.ToolCalls!);
        Assert.Equal("read_file", call.Function.Name);
        Assert.Equal("a.cs", call.Function.Arguments.GetProperty("path").GetString());
        // First the ordinary request, then the one with the tools in the system prompt.
        var chats = Chats(server);
        Assert.Equal(2, chats.Count);
        Assert.Contains("\"tools\":[", chats[0]);
        Assert.DoesNotContain("\"tools\":[", chats[1]);
        Assert.DoesNotContain("tool_choice", chats[1]);
        Assert.Contains("Available tools", chats[1]);
    }

    [Fact]
    public async Task TheNextRequest_GoesStraightToThePrompt()
    {
        using var server = GemmaOnLmStudio("Nothing to call.");
        var model  = "gemma-" + Guid.NewGuid().ToString("N")[..8];
        var client = Client(server);

        await client.SendChatAsync(model, [new ChatMessageDto("user", "one")], new Tools(), null, CancellationToken.None);
        var second = await client.SendChatAsync(model, [new ChatMessageDto("user", "two")], new Tools(), null, CancellationToken.None);

        Assert.Equal("Nothing to call.", second.TextContent);
        Assert.Equal(3, Chats(server).Count);                           // refused once, then prompted twice
        Assert.DoesNotContain("\"tools\":[", Chats(server)[2]);
    }

    /// <summary>The refusal as LM Studio streams it, verbatim: HTTP 200, an error event, the error in its data line.</summary>
    private const string StreamedTemplateRefusal =
        "event: error\ndata: {\"error\":{\"message\":\"Error rendering prompt with jinja template: \\\"Cannot call something "
        + "that is not a function: got UndefinedValue\\\".\\n\\nThis is usually an issue with the model's prompt template.\"}}\n\n";

    /// <summary>The same refusal as a bare error object in the stream, without a data prefix.</summary>
    private const string BareTemplateRefusal =
        "{\"error\":\"Error rendering prompt with jinja template: \\\"Cannot call something that is not a function\\\".\"}\n";

    [Theory]
    [InlineData(StreamedTemplateRefusal)]
    [InlineData(BareTemplateRefusal)]
    public async Task ARefusalStreamedAfterA200_IsAskedAgainWithTheToolsInThePrompt(string refusal)
    {
        using var server = new LoopbackHttpServer(
            (path, request) => !path.StartsWith("/v1/chat/completions", StringComparison.Ordinal) ? null
                             : request.Contains("\"tools\":[", StringComparison.Ordinal) ? refusal
                             : Sse("<tool_call>\n{\"name\": \"read_file\", \"arguments\": {\"path\": \"a.cs\"}}\n</tool_call>"),
            (_, _) => 200);
        var model = "gemma-" + Guid.NewGuid().ToString("N")[..8];

        var turn = await Client(server).SendChatAsync(model, [new ChatMessageDto("user", "Read a.cs")], new Tools(),
                                                      onToken: null, CancellationToken.None);

        Assert.Equal("read_file", Assert.Single(turn.ToolCalls!).Function.Name);
        var chats = Chats(server);
        Assert.Equal(2, chats.Count);
        Assert.Contains("\"tools\":[", chats[0]);
        Assert.Contains("Available tools", chats[1]);
    }

    // Gemma, its tools in the prompt, writes its call and goes on: an invented response, another call, another
    // invented response — the shape the raw stream showed, cut here to its first steps.
    private const string InventedResponse =
        "<tool_call>\n{\"name\": \"read_file\", \"arguments\": {\"path\": \"a.cs\"}}\n</tool_call>\n"
        + "<tool_response>\n{\"content\": \"class Invented {}\"}\n</tool_response>\n"
        + "<tool_call>\n{\"name\": \"read_file\", \"arguments\": {\"path\": \"invented.cs\"}}\n</tool_call>";
    private const string InventedNativeResponse =
        "<|tool_call>call:read_file{path:<|\"|>a.cs<|\"|>}<tool_call|><|tool_response>response:read_file{content:<|\"|>class Invented {}<|\"|>}<tool_response|>"
        + "<|tool_call>call:read_file{path:<|\"|>invented.cs<|\"|>}<tool_call|>";

    [Theory]
    [InlineData(InventedResponse)]
    [InlineData(InventedNativeResponse)]
    public async Task AModelThatWritesTheToolsResponseItself_IsStoppedAtItsCall(string reply)
    {
        // Streamed in small pieces: the marker arrives cut in two, as a server sends it.
        var chunks = Enumerable.Range(0, (reply.Length + 6) / 7).Select(i => reply.Substring(i * 7, Math.Min(7, reply.Length - i * 7)));
        var sse = string.Concat(chunks.Select(c => "data: " + JsonSerializer.Serialize(new { choices = new[] { new { index = 0, delta = new { content = c } } } }) + "\n\n"))
                  + "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
        using var server = new LoopbackHttpServer(
            (path, request) => !path.StartsWith("/v1/chat/completions", StringComparison.Ordinal) ? null
                             : request.Contains("\"tools\":[", StringComparison.Ordinal) ? TemplateRefusal : sse,
            (_, request) => request.Contains("\"tools\":[", StringComparison.Ordinal) ? 400 : 200);
        var model = "gemma-" + Guid.NewGuid().ToString("N")[..8];

        var turn = await Client(server).SendChatAsync(model, [new ChatMessageDto("user", "Read a.cs")], new Tools(),
                                                      onToken: null, CancellationToken.None);

        var call = Assert.Single(turn.ToolCalls!);                                   // the real call, not the invented one
        Assert.Equal("a.cs", call.Function.Arguments.GetProperty("path").GetString());
        Assert.DoesNotContain("Invented", turn.TextContent);
        // The server is asked to stop there too.
        var stop = JsonDocument.Parse(Chats(server)[1]).RootElement.GetProperty("stop");
        Assert.Equal(["<tool_response>", "<|tool_response>"], stop.EnumerateArray().Select(s => s.GetString()));
    }

    [Fact]
    public async Task AnOrdinaryRequest_AsksForNoStop()
    {
        // Reference arm: a server that renders tools gets them as tools, and no stop sequence is imposed on the model.
        using var server = new LoopbackHttpServer(
            (path, _) => path.StartsWith("/v1/chat/completions", StringComparison.Ordinal) ? Sse("Done.") : null);
        var model = "plain-" + Guid.NewGuid().ToString("N")[..8];

        var turn = await Client(server).SendChatAsync(model, [new ChatMessageDto("user", "x")], new Tools(), null, CancellationToken.None);

        Assert.Equal("Done.", turn.TextContent);
        Assert.Contains("\"tools\":[", Assert.Single(Chats(server)));
        Assert.DoesNotContain("\"stop\"", Chats(server)[0]);
    }

    [Fact]
    public async Task AnotherStreamedRefusal_IsNotRetried()
    {
        // Reference arm, streamed: an error event that is not a template refusal fails the turn as before.
        using var server = new LoopbackHttpServer(
            (path, _) => path.StartsWith("/v1/chat/completions", StringComparison.Ordinal)
                ? "event: error\ndata: {\"error\":{\"message\":\"request (9000 tokens) exceeds the available context size\"}}\n\n"
                : null,
            (_, _) => 200);
        var model = "other-" + Guid.NewGuid().ToString("N")[..8];

        await Assert.ThrowsAsync<AgentHttpException>(() => Client(server).SendChatAsync(
            model, [new ChatMessageDto("user", "x")], new Tools(), null, CancellationToken.None));

        Assert.Single(Chats(server));
    }

    [Fact]
    public async Task AnotherServerRefusal_IsNotRetried()
    {
        // Reference arm: a context overflow is a refusal of the request itself — asking again changes nothing.
        using var server = new LoopbackHttpServer(
            path => path.StartsWith("/v1/chat/completions", StringComparison.Ordinal)
                ? "{\"error\":\"request (9000 tokens) exceeds the available context size (8192 tokens)\"}" : null,
            _ => 400);
        var model = "other-" + Guid.NewGuid().ToString("N")[..8];

        await Assert.ThrowsAsync<AgentHttpException>(() => Client(server).SendChatAsync(
            model, [new ChatMessageDto("user", "x")], new Tools(), null, CancellationToken.None));

        Assert.Single(Chats(server));
    }
}
