using System.Text.Json;
using Inferpal.Config;
using Inferpal.Models;
using Inferpal.Services.Inference;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  A model whose server cannot render tools still gets them — in its system prompt.
//
//  Gemma 4's bundled chat template on LM Studio calls a macro it never defines as soon as a request carries
//  `tools`: every request with tools answered HTTP 400 "Error rendering prompt with jinja template", the same
//  request without them worked. Gemma 4 went 1/14 in the battery (plain chat only).
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
