using System.Text.Json;
using Inferpal.Config;
using Inferpal.Models;
using Inferpal.Services.Inference;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  Ollama refuses a request that carries tools for a model whose template declares none — HTTP 400
//  "<full model name> does not support tools", decided before the model is even loaded (server/routes.go,
//  scheduleRunner → CheckCapabilities). Every agent-mode question to such a model failed on it, while the same
//  refusal from LM Studio's template falls back to the tools written in the system prompt (PromptedTools). A model
//  that cannot be sent tools can still be told about them: the fallback holds for both servers, through both clients.
// ──────────────────────────────────────────────────────────────────────────────────────────────
public class OllamaToolRefusalTests
{
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

    private const string Call = "<tool_call>\n{\"name\": \"read_file\", \"arguments\": {\"path\": \"a.cs\"}}\n</tool_call>";

    /// <summary>The refusal, as Ollama writes it: the model's full name, then "does not support tools".</summary>
    private static string Refusal(string model) =>
        JsonSerializer.Serialize(new { error = $"registry.ollama.ai/library/{model}:latest does not support tools" });

    private static string Ndjson(string content) =>
        JsonSerializer.Serialize(new { message = new { role = "assistant", content }, done = false }) + "\n"
        + JsonSerializer.Serialize(new { message = new { role = "assistant", content = "" }, done = true, done_reason = "stop" }) + "\n";

    private static bool CarriesTools(string request) => request.Contains("\"tools\":[", StringComparison.Ordinal);

    /// <summary>Ollama with a model whose template has no tools: 400 when the request carries them, an answer otherwise.</summary>
    private static LoopbackHttpServer ToollessOnOllama(string model, string answer) => new(
        (path, request) => !path.StartsWith("/api/chat", StringComparison.Ordinal) ? null
                         : CarriesTools(request) ? Refusal(model) : Ndjson(answer),
        (_, request) => CarriesTools(request) ? 400 : 200);

    private static OllamaClient Ollama(LoopbackHttpServer server) =>
        new(new InferpalConfig { Provider = "ollama", BaseUrl = server.BaseUrl, ContextWindowSize = 8192 });

    private static List<string> Chats(LoopbackHttpServer server) =>
        server.Bodies.Where(b => b.Contains("\"messages\"", StringComparison.Ordinal)).ToList();

    private static string Unique(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N")[..8];

    [Theory]
    [InlineData("HTTP 400 (Bad Request): {\"error\":\"registry.ollama.ai/library/gemma3:latest does not support tools\"}", true)]
    [InlineData("HTTP 400 (Bad Request): {\"error\":\"Error rendering prompt with jinja template: \\\"Cannot call…\\\"\"}", true)]
    [InlineData("HTTP 400 (Bad Request): {\"error\":\"\\\"gemma3\\\" does not support thinking\"}", false)]
    [InlineData("HTTP 400 (Bad Request): {\"error\":\"\\\"nomic-embed-text\\\" does not support chat\"}", false)]
    [InlineData("HTTP 404 (Not Found): {\"error\":\"model \\\"x\\\" not found, try pulling it first\"}", false)]
    public void OnlyARefusalOfTheTools_IsRead_AsOne(string message, bool expected) =>
        Assert.Equal(expected, PromptedTools.IsToolRefusal(message));

    [Fact]
    public async Task ARefusedToolRequest_IsAskedAgainWithTheToolsInThePrompt_AndTheCallIsRead()
    {
        var model = Unique("gemma3");
        using var server = ToollessOnOllama(model, Call);

        var turn = await Ollama(server).SendChatAsync(model, [new ChatMessageDto("user", "Read a.cs")], new Tools(),
                                                      onToken: null, CancellationToken.None);

        var call = Assert.Single(turn.ToolCalls!);
        Assert.Equal("read_file", call.Function.Name);
        Assert.Equal("a.cs", call.Function.Arguments.GetProperty("path").GetString());
        var chats = Chats(server);
        Assert.Equal(2, chats.Count);                 // the ordinary request, then the one with the tools in the prompt
        Assert.True(CarriesTools(chats[0]));
        Assert.False(CarriesTools(chats[1]));
        Assert.Contains("Available tools", chats[1]);
    }

    [Fact]
    public async Task TheNextRequest_GoesStraightToThePrompt()
    {
        var model = Unique("gemma3");
        using var server = ToollessOnOllama(model, "Nothing to call.");
        var client = Ollama(server);

        await client.SendChatAsync(model, [new ChatMessageDto("user", "one")], new Tools(), null, CancellationToken.None);
        var second = await client.SendChatAsync(model, [new ChatMessageDto("user", "two")], new Tools(), null, CancellationToken.None);

        Assert.Equal("Nothing to call.", second.TextContent);
        Assert.Equal(3, Chats(server).Count);         // refused once, then prompted twice
        Assert.False(CarriesTools(Chats(server)[2]));
    }

    [Fact]
    public async Task ThePromptedRequest_AsksOllamaToStopWhereTheModelWouldInventTheResponse()
    {
        var model = Unique("gemma3");
        using var server = ToollessOnOllama(model, Call + "\n<tool_response>\n{\"content\": \"class Invented {}\"}\n</tool_response>");

        var turn = await Ollama(server).SendChatAsync(model, [new ChatMessageDto("user", "Read a.cs")], new Tools(),
                                                      onToken: null, CancellationToken.None);

        Assert.Equal("a.cs", Assert.Single(turn.ToolCalls!).Function.Arguments.GetProperty("path").GetString());
        Assert.DoesNotContain("Invented", turn.TextContent);
        var stop = JsonDocument.Parse(Chats(server)[1]).RootElement.GetProperty("options").GetProperty("stop");
        Assert.Equal(PromptedTools.ResponseMarkers, stop.EnumerateArray().Select(s => s.GetString()!));
    }

    [Fact]
    public async Task AnotherRefusal_IsNotRetried()
    {
        // Reference arm: a model that is not installed is refused for itself — asking again changes nothing.
        var model = Unique("missing");
        using var server = new LoopbackHttpServer(
            path => path.StartsWith("/api/chat", StringComparison.Ordinal)
                ? $"{{\"error\":\"model \\\"{model}\\\" not found, try pulling it first\"}}" : null,
            _ => 404);

        await Assert.ThrowsAsync<AgentHttpException>(() => Ollama(server).SendChatAsync(
            model, [new ChatMessageDto("user", "x")], new Tools(), null, CancellationToken.None));

        Assert.Single(Chats(server));
    }

    [Fact]
    public async Task AModelThatTakesTools_GetsThemAsTools()
    {
        // Reference arm: nothing is rewritten for a model whose template has tools.
        var model = Unique("qwen3");
        using var server = new LoopbackHttpServer(
            path => path.StartsWith("/api/chat", StringComparison.Ordinal) ? Ndjson("Done.") : null);

        var turn = await Ollama(server).SendChatAsync(model, [new ChatMessageDto("user", "x")], new Tools(), null, CancellationToken.None);

        Assert.Equal("Done.", turn.TextContent);
        Assert.True(CarriesTools(Assert.Single(Chats(server))));
    }

    [Fact]
    public async Task OllamasOpenAiEndpoint_FallsBackToo()
    {
        // The same server reached through its /v1 surface with the OpenAI-compatible provider: same refusal, same remedy.
        var model = Unique("gemma3");
        var sse = "data: " + JsonSerializer.Serialize(new { choices = new[] { new { index = 0, delta = new { content = Call } } } }) + "\n\n"
                  + "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
        using var server = new LoopbackHttpServer(
            (path, request) => !path.StartsWith("/v1/chat/completions", StringComparison.Ordinal) ? null
                             : CarriesTools(request)
                                 ? JsonSerializer.Serialize(new { error = new { message = $"registry.ollama.ai/library/{model}:latest does not support tools", type = "api_error" } })
                                 : sse,
            (_, request) => CarriesTools(request) ? 400 : 200);
        var client = new OpenAiCompatibleClient(new InferpalConfig { Provider = "openai-compatible", BaseUrl = server.BaseUrl });

        var turn = await client.SendChatAsync(model, [new ChatMessageDto("user", "Read a.cs")], new Tools(), null, CancellationToken.None);

        Assert.Equal("read_file", Assert.Single(turn.ToolCalls!).Function.Name);
    }
}
