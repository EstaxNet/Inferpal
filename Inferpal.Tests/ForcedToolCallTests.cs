using System.Text.Json;
using Inferpal.Config;
using Inferpal.Models;
using Inferpal.Services.Inference;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A call the agent forces (<c>tool_choice: "required"</c>) reaches LM Studio only for a model it does not publish as
/// untrained for tool use: forced, such a model writes LM Studio's <c>[TOOL_REQUEST]</c> form inside its reasoning, which
/// nothing reads, while unforced it keeps a form Inferpal reads. A trained model, a model the server says nothing about,
/// and a generic server keep the forced call.
/// </summary>
public sealed class ForcedToolCallTests
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

    /// <summary>LM Studio's native model list, the model published as trained for tool use or not (null: no flag).</summary>
    private static string NativeModels(string model, bool? trained) =>
        JsonSerializer.Serialize(new
        {
            models = new[]
            {
                trained is null
                    ? (object)new { key = model, type = "llm", loaded_instances = Array.Empty<object>() }
                    : new
                    {
                        key = model, type = "llm", loaded_instances = Array.Empty<object>(),
                        capabilities = new { trained_for_tool_use = trained.Value },
                    },
            },
        });

    private static LoopbackHttpServer LmStudio(string model, bool? trained) =>
        new((path, _) => path.StartsWith("/api/v1/models", StringComparison.Ordinal) ? NativeModels(model, trained)
                       : path.StartsWith("/v1/chat/completions", StringComparison.Ordinal) ? UtilityCallsCarryNoReasoningTests.Sse
                       : null);

    /// <summary>The <c>tool_choice</c> the one chat request carried when the caller forced a call, or <c>null</c>.</summary>
    private static async Task<string?> SentToolChoiceAsync(LoopbackHttpServer server, IChatTurnClient client, string model)
    {
        await client.SendChatAsync(model, [new ChatMessageDto("user", "Read a.cs")], new Tools(), onToken: null,
                                   CancellationToken.None, toolChoice: "required");
        var body = Assert.Single(UtilityCallsCarryNoReasoningTests.ChatBodies(server));
        Assert.Contains("\"tools\"", body, StringComparison.Ordinal);   // witness: the tools went with the request
        return JsonDocument.Parse(body).RootElement.TryGetProperty("tool_choice", out var choice) ? choice.GetString() : null;
    }

    [Fact]
    public async Task AModelLmStudioPublishesAsNotTrainedForTools_IsNotForced()
    {
        using var server = LmStudio("nanbeige_nanbeige4.3-33b", trained: false);
        var client = new LmStudioClient(new InferpalConfig { Provider = "lmstudio", BaseUrl = server.BaseUrl });

        Assert.Null(await SentToolChoiceAsync(server, client, "nanbeige_nanbeige4.3-33b"));
    }

    [Theory]
    [InlineData(true)]    // trained: the server reads its calls back
    [InlineData(null)]    // the server says nothing: forced as before
    public async Task AModelTrainedForTools_OrOneTheServerSaysNothingAbout_IsForced(bool? trained)
    {
        using var server = LmStudio("qwen/qwen3.8-27b", trained);
        var client = new LmStudioClient(new InferpalConfig { Provider = "lmstudio", BaseUrl = server.BaseUrl });

        Assert.Equal("required", await SentToolChoiceAsync(server, client, "qwen/qwen3.8-27b"));
    }

    [Fact]
    public async Task AGenericServer_IsForced()
    {
        using var server = new LoopbackHttpServer(
            (path, _) => path.StartsWith("/v1/chat/completions", StringComparison.Ordinal) ? UtilityCallsCarryNoReasoningTests.Sse : null);
        var client = new OpenAiCompatibleClient(new InferpalConfig { Provider = "openai", BaseUrl = server.BaseUrl + "/v1" });

        Assert.Equal("required", await SentToolChoiceAsync(server, client, "some-model"));
    }

    [Theory]
    [InlineData("{\"trained_for_tool_use\":false}", false)]
    [InlineData("{\"trained_for_tool_use\":true}", true)]
    [InlineData("{\"trained_for_tool_use\":\"no\"}", null)]   // not a boolean: not a statement
    [InlineData("{\"reasoning\":{}}", null)]
    public void TheFlag_IsReadOnlyWhenTheServerStatesIt(string capabilities, bool? expected) =>
        Assert.Equal(expected, LmStudioClient.TrainedForToolUseOf(JsonDocument.Parse(capabilities).RootElement.Clone()));
}
