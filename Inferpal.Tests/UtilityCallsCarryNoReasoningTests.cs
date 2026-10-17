using System.Text.Json;
using Inferpal.Config;
using Inferpal.Models;
using Inferpal.Services;
using Inferpal.Services.Agent;
using Inferpal.Services.Commands;
using Inferpal.Services.Execution;
using Inferpal.Services.Inference;
using Inferpal.Services.Persistence;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A session title, a commit message, a compaction summary, a run's own summary and the session recap answer a
/// background task: they ask for no reasoning — and only LM Studio, for a model whose native entry publishes "off", is
/// sent anything. To a model that publishes no such setting the field is not a lower effort: GLM 4.7 Flash, sent
/// "none", reasons six times longer, and sent "low" answers nothing.
/// </summary>
public sealed class UtilityCallsCarryNoReasoningTests
{
    // ── The five background tasks ask for no reasoning ──────────────────────────

    private static readonly ChatTurnResult Answer = new("Rename the parser", [], 0, 0);

    [Fact]
    public async Task ASessionTitle_AsksForNoReasoning()
    {
        var client = new FakeInferenceProvider { ChatResult = Answer };

        await SessionTitleGenerator.GenerateAsync(client, new InferpalConfig(), "How do I rename the parser?", CancellationToken.None);

        Assert.Equal(ReasoningEffort.Off, Assert.Single(client.Efforts));
    }

    [Fact]
    public async Task ACommitMessage_AsksForNoReasoning()
    {
        var client = new FakeInferenceProvider { ChatResult = Answer };
        GitRunner git = (args, _) => Task.FromResult((args == "diff --staged" ? "diff --git a/A.cs b/A.cs\n+added" : "", 0));

        await CommitCommandHandler.ProposeAsync(client, new InferpalConfig(), git, null, CancellationToken.None);

        Assert.Equal(ReasoningEffort.Off, Assert.Single(client.Efforts));
    }

    [Fact]
    public async Task TheSessionRecap_AsksForNoReasoning()
    {
        var client = new FakeInferenceProvider { ChatResult = Answer };
        List<ChatMessageDto> history = [new("user", "rename the parser"), new("assistant", "done")];

        await SessionRecap.WriteAsync(history, null, new InferpalConfig(), client, CancellationToken.None);

        Assert.Equal(ReasoningEffort.Off, Assert.Single(client.Efforts));
    }

    [Fact]
    public async Task ACompactionSummary_AsksForNoReasoning()
    {
        var client = new FakeInferenceProvider { ChatResult = Answer };
        var config = new InferpalConfig
        {
            ContextWindowSize = 1000, ContextWindowKeepTurns = 2, CompactionEnabled = true, KvCacheAnchorMessages = 0,
            CompactionTimeoutSeconds = 10,
        };
        var history = new List<ChatMessageDto> { new("system", "sys") };
        for (var i = 0; i < 20; i++) { history.Add(new("user", $"question {i}")); history.Add(new("assistant", $"answer {i}")); }

        await ContextManager.PrepareAsync(history, config, client, lastPromptTokens: 100_000, onStep: null, ct: CancellationToken.None);

        Assert.Equal(ReasoningEffort.Off, Assert.Single(client.Efforts));
    }

    [Fact]
    public async Task ARunsOwnSummary_AsksForNoReasoning()
    {
        var client = new FakeInferenceProvider { ChatResult = Answer };
        var orchestrator = new AgentOrchestrator(client, new InferpalConfig { ContextWindowSize = 8192, CompactionEnabled = true });
        var messages = new List<ChatMessageDto> { new("system", new string('s', 4000)), new("user", "go") };
        messages.AddRange(Enumerable.Range(0, 10).Select(_ => new ChatMessageDto("tool", new string('x', 8000))));

        await orchestrator.CompactRunContextAsync(messages, anchorCount: 2, model: "m", alreadySummarized: false,
                                                  onStep: _ => { }, ct: CancellationToken.None);

        Assert.Equal(ReasoningEffort.Off, Assert.Single(client.Efforts));
    }

    // ── What the clients send ───────────────────────────────────────────────────

    private const string Sse =
        "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"ok\"}}]}\n\n"
        + "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";

    /// <summary>LM Studio's native model list, the model publishing <paramref name="options"/> (none: no reasoning entry).</summary>
    private static string NativeModels(string model, params string[] options) =>
        JsonSerializer.Serialize(new
        {
            models = new[]
            {
                options.Length == 0
                    ? (object)new { key = model, type = "llm", loaded_instances = Array.Empty<object>() }
                    : new
                    {
                        key = model, type = "llm", loaded_instances = Array.Empty<object>(),
                        capabilities = new { reasoning = new { allowed_options = options, @default = options[^1] } },
                    },
            },
        });

    private static LoopbackHttpServer LmStudio(string model, params string[] options) =>
        new((path, _) => path.StartsWith("/api/v1/models", StringComparison.Ordinal) ? NativeModels(model, options)
                       : path.StartsWith("/v1/chat/completions", StringComparison.Ordinal) ? Sse
                       : null);

    private static IEnumerable<string> ChatBodies(LoopbackHttpServer server) =>
        server.Paths.Zip(server.Bodies).Where(p => p.First.StartsWith("/v1/chat/completions", StringComparison.Ordinal))
              .Select(p => p.Second);

    private static Task<ChatTurnResult> AskAsync(IChatTurnClient client, string model, ReasoningEffort effort,
                                                 IToolRegistry? tools = null) =>
        client.SendChatAsync(model, [new("user", "summarize")], tools ?? EmptyToolRegistry.Instance, null, effort,
                             CancellationToken.None);

    [Fact]
    public async Task LmStudio_SendsNone_ToAModelThatPublishesOff_AndNothingWhenTheServerDecides()
    {
        using var server = LmStudio("qwen/qwen3.8-27b", "off", "low", "medium", "on");
        var client = new LmStudioClient(new InferpalConfig { Provider = "lmstudio", BaseUrl = server.BaseUrl });

        await AskAsync(client, "qwen/qwen3.8-27b", ReasoningEffort.Off);
        await AskAsync(client, "qwen/qwen3.8-27b", ReasoningEffort.Server);

        var bodies = ChatBodies(server).ToList();
        Assert.Equal(2, bodies.Count);                                       // witness: both requests were read
        Assert.Equal("none", JsonDocument.Parse(bodies[0]).RootElement.GetProperty("reasoning_effort").GetString());
        Assert.False(JsonDocument.Parse(bodies[1]).RootElement.TryGetProperty("reasoning_effort", out _));
        // One read of the model list serves the loaded window and the reasoning settings.
        Assert.Equal(1, server.Paths.Count(p => p.StartsWith("/api/v1/models", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("openai/gpt-oss-20b", "low,medium,high")]     // reasons at its lowest already; "none" gains nothing
    [InlineData("lmstudio-community/glm-4.7-flash", "")]      // publishes nothing: "none" makes it reason six times longer
    public async Task LmStudio_SendsNothing_ToAModelThatDoesNotPublishOff(string model, string options)
    {
        using var server = LmStudio(model, options.Split(',', StringSplitOptions.RemoveEmptyEntries));
        var client = new LmStudioClient(new InferpalConfig { Provider = "lmstudio", BaseUrl = server.BaseUrl });

        await AskAsync(client, model, ReasoningEffort.Off);

        var body = Assert.Single(ChatBodies(server));
        Assert.False(JsonDocument.Parse(body).RootElement.TryGetProperty("reasoning_effort", out _));
    }

    [Fact]
    public async Task AGenericServer_IsSentNoEffort()
    {
        using var server = new LoopbackHttpServer(
            (path, _) => path.StartsWith("/v1/chat/completions", StringComparison.Ordinal) ? Sse : null);
        var client = new OpenAiCompatibleClient(new InferpalConfig { Provider = "openai", BaseUrl = server.BaseUrl + "/v1" });

        await AskAsync(client, "some-model", ReasoningEffort.Off);

        var body = Assert.Single(ChatBodies(server));
        Assert.False(JsonDocument.Parse(body).RootElement.TryGetProperty("reasoning_effort", out _));
    }

    [Fact]
    public async Task AnEffortTheServerRefuses_IsDropped_TheRequestAskedAgain_AndNotSentAgain()
    {
        var model = "qwen/refusing-" + Guid.NewGuid().ToString("N");
        using var server = new LoopbackHttpServer(
            (path, body) => path.StartsWith("/api/v1/models", StringComparison.Ordinal) ? NativeModels(model, "off", "on")
                          : !path.StartsWith("/v1/chat/completions", StringComparison.Ordinal) ? null
                          : body.Contains("reasoning_effort", StringComparison.Ordinal)
                              ? "{\"error\":{\"message\":\"Invalid 'reasoning_effort' value: 'none'.\"}}"
                              : Sse,
            (path, body) => path.StartsWith("/v1/chat/completions", StringComparison.Ordinal)
                            && body.Contains("reasoning_effort", StringComparison.Ordinal) ? 400 : 200);
        var client = new LmStudioClient(new InferpalConfig { Provider = "lmstudio", BaseUrl = server.BaseUrl });

        var first = await AskAsync(client, model, ReasoningEffort.Off);
        await AskAsync(client, model, ReasoningEffort.Off);

        Assert.Equal("ok", first.TextContent);
        var bodies = ChatBodies(server).ToList();
        Assert.Equal(3, bodies.Count);                                       // refused, asked again, then the next call
        Assert.Contains("reasoning_effort", bodies[0], StringComparison.Ordinal);
        Assert.DoesNotContain("reasoning_effort", bodies[1], StringComparison.Ordinal);
        Assert.DoesNotContain("reasoning_effort", bodies[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnotherRefusal_IsNotAskedAgain()
    {
        var model = "qwen/other-refusal-" + Guid.NewGuid().ToString("N");
        using var server = new LoopbackHttpServer(
            (path, _) => path.StartsWith("/api/v1/models", StringComparison.Ordinal) ? NativeModels(model, "off", "on")
                       : path.StartsWith("/v1/chat/completions", StringComparison.Ordinal)
                           ? "{\"error\":{\"message\":\"Model unloaded.\"}}" : null,
            (path, _) => path.StartsWith("/v1/chat/completions", StringComparison.Ordinal) ? 400 : 200);
        var client = new LmStudioClient(new InferpalConfig { Provider = "lmstudio", BaseUrl = server.BaseUrl });

        await Assert.ThrowsAsync<AgentHttpException>(() => AskAsync(client, model, ReasoningEffort.Off));

        Assert.Contains("reasoning_effort", Assert.Single(ChatBodies(server)), StringComparison.Ordinal);  // witness
    }

    [Fact]
    public async Task AnEffortRefusedInTheStream_IsDroppedToo()
    {
        // LM Studio answers 200 and streams its error: the refusal comes through the stream, not the status.
        var model = "qwen/stream-refusal-" + Guid.NewGuid().ToString("N");
        using var server = new LoopbackHttpServer(
            (path, body) => path.StartsWith("/api/v1/models", StringComparison.Ordinal) ? NativeModels(model, "off", "on")
                          : !path.StartsWith("/v1/chat/completions", StringComparison.Ordinal) ? null
                          : body.Contains("reasoning_effort", StringComparison.Ordinal)
                              ? "data: {\"error\":{\"message\":\"Invalid 'reasoning_effort' value: 'none'.\"}}\n\n"
                              : Sse);
        var client = new LmStudioClient(new InferpalConfig { Provider = "lmstudio", BaseUrl = server.BaseUrl });

        var answer = await AskAsync(client, model, ReasoningEffort.Off);

        Assert.Equal("ok", answer.TextContent);
        var bodies = ChatBodies(server).ToList();
        Assert.Equal(2, bodies.Count);
        Assert.DoesNotContain("reasoning_effort", bodies[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANativeListThatCannotBeRead_IsReadOncePerCall_NotOncePerProbe()
    {
        // A native API that fails costs its budget once: the reasoning settings take the loaded window's failed read.
        using var server = new LoopbackHttpServer(
            (path, _) => path.StartsWith("/api/v1/models", StringComparison.Ordinal) ? "{ not json"
                       : path.StartsWith("/v1/chat/completions", StringComparison.Ordinal) ? Sse : null);
        var client = new LmStudioClient(new InferpalConfig { Provider = "lmstudio", BaseUrl = server.BaseUrl });

        await AskAsync(client, "qwen/qwen3.8-27b", ReasoningEffort.Off);

        Assert.Equal(1, server.Paths.Count(p => p.StartsWith("/api/v1/models", StringComparison.Ordinal)));
        Assert.DoesNotContain("reasoning_effort", Assert.Single(ChatBodies(server)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheLoadedWindow_OfAModelLoadedSinceTheLastRead_IsReadFresh()
    {
        var loaded = false;
        using var server = new LoopbackHttpServer(
            (path, _) => !path.StartsWith("/api/v1/models", StringComparison.Ordinal)
                ? (path.StartsWith("/v1/chat/completions", StringComparison.Ordinal) ? Sse : null)
                : JsonSerializer.Serialize(new
                {
                    models = new object[]
                    {
                        new { key = "a", type = "llm", loaded_instances = new[] { new { id = "a", config = new { context_length = 8192 } } } },
                        loaded
                            ? new { key = "b", type = "llm", loaded_instances = new[] { new { id = "b", config = new { context_length = 4096 } } } }
                            : (object)new { key = "b", type = "llm", loaded_instances = Array.Empty<object>() },
                    },
                }));
        var client = new LmStudioClient(new InferpalConfig { Provider = "lmstudio", BaseUrl = server.BaseUrl, ContextWindowSize = 32768 });

        await AskAsync(client, "a", ReasoningEffort.Off);                    // the list is read, b not loaded yet
        loaded = true;                                                       // then b is loaded, by another client

        Assert.Equal(4096, await client.GetLoadedContextWindowAsync("b", CancellationToken.None));
    }

    [Fact]
    public async Task ATemplateErrorOnARequestWithTheEffort_DropsTheEffortFirst_AndKeepsTheToolsNative()
    {
        var model = "qwen/template-" + Guid.NewGuid().ToString("N");
        using var server = new LoopbackHttpServer(
            (path, body) => path.StartsWith("/api/v1/models", StringComparison.Ordinal) ? NativeModels(model, "off", "on")
                          : !path.StartsWith("/v1/chat/completions", StringComparison.Ordinal) ? null
                          : body.Contains("reasoning_effort", StringComparison.Ordinal)
                              ? "{\"error\":{\"message\":\"Error rendering prompt with jinja template: unknown effort\"}}"
                              : Sse,
            (path, body) => path.StartsWith("/v1/chat/completions", StringComparison.Ordinal)
                            && body.Contains("reasoning_effort", StringComparison.Ordinal) ? 400 : 200);
        var client = new LmStudioClient(new InferpalConfig { Provider = "lmstudio", BaseUrl = server.BaseUrl });
        var tools = new OneTool();

        await AskAsync(client, model, ReasoningEffort.Off, tools);

        var bodies = ChatBodies(server).ToList();
        Assert.Equal(2, bodies.Count);
        var retried = JsonDocument.Parse(bodies[1]).RootElement;
        Assert.False(retried.TryGetProperty("reasoning_effort", out _));
        Assert.True(retried.TryGetProperty("tools", out _), "the tools moved into the prompt for a field one request carried");
    }

    [Fact]
    public async Task ATemplateErrorTheEffortDidNotCause_IsNotHeldAgainstTheEffort()
    {
        // The template fails on the tools, effort or not: the effort is dropped first, then the tools move into the prompt
        // — and the effort, not the cause, is still asked for on the next request.
        var model = "qwen/tools-template-" + Guid.NewGuid().ToString("N");
        using var server = new LoopbackHttpServer(
            (path, body) => path.StartsWith("/api/v1/models", StringComparison.Ordinal) ? NativeModels(model, "off", "on")
                          : !path.StartsWith("/v1/chat/completions", StringComparison.Ordinal) ? null
                          : body.Contains("\"tools\"", StringComparison.Ordinal)
                              ? "{\"error\":{\"message\":\"Error rendering prompt with jinja template: tools\"}}"
                              : Sse,
            (path, body) => path.StartsWith("/v1/chat/completions", StringComparison.Ordinal)
                            && body.Contains("\"tools\"", StringComparison.Ordinal) ? 400 : 200);
        var client = new LmStudioClient(new InferpalConfig { Provider = "lmstudio", BaseUrl = server.BaseUrl });

        await AskAsync(client, model, ReasoningEffort.Off, new OneTool());
        await AskAsync(client, model, ReasoningEffort.Off, new OneTool());

        var last = JsonDocument.Parse(ChatBodies(server).Last()).RootElement;
        Assert.Equal("none", last.GetProperty("reasoning_effort").GetString());
        Assert.False(last.TryGetProperty("tools", out _));                  // witness: the tools did move into the prompt
    }

    [Fact]
    public void ThePlainTurn_CannotBeOverriddenPastTheOneTheLoopSends()
    {
        var plain = typeof(OpenAiCompatibleClient).GetMethods()
            .Single(m => m.Name == nameof(OpenAiCompatibleClient.SendChatAsync)
                         && m.GetParameters().All(p => p.ParameterType != typeof(ReasoningEffort)));

        Assert.True(plain.IsFinal, "a subclass overriding the plain turn would be passed by: the agent loop sends the other");
    }

    private sealed class OneTool : IToolRegistry
    {
        public IReadOnlyList<ToolDefinition> Definitions { get; } = [new("function", new ToolFunction("read_file", "read", new { }))];
        public DiffInfo? ConsumeDiff() => null;
        public Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct) => Task.FromResult("result");
    }

    [Fact]
    public async Task ANativeApiThatDoesNotAnswer_SendsNoEffort()
    {
        using var server = new LoopbackHttpServer(
            (path, _) => path.StartsWith("/v1/chat/completions", StringComparison.Ordinal) ? Sse : "{}",
            (path, _) => path.StartsWith("/api/", StringComparison.Ordinal) ? 500 : 200);
        var client = new LmStudioClient(new InferpalConfig { Provider = "lmstudio", BaseUrl = server.BaseUrl });

        await AskAsync(client, "qwen/qwen3.8-27b", ReasoningEffort.Off);

        var body = Assert.Single(ChatBodies(server));
        Assert.False(JsonDocument.Parse(body).RootElement.TryGetProperty("reasoning_effort", out _));
    }
}
