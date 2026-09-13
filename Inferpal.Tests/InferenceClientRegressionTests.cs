using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Inferpal.Config;
using Inferpal.Models;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// What the inference clients actually read on the wire, against a real socket
/// (<see cref="LoopbackHttpServer"/>): the stream shapes a test on DTOs never sees.
/// </summary>
[Collection("Diagnostics")]
public class InferenceClientRegressionTests
{
    private sealed class CountingRegistry(string toolName) : IToolRegistry
    {
        private int _count;
        public int ExecuteCount => _count;
        public IReadOnlyList<ToolDefinition> Definitions { get; } =
            [new("function", new ToolFunction(toolName, "test tool", new { }))];
        public DiffInfo? ConsumeDiff() => null;
        public Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct)
        {
            Interlocked.Increment(ref _count);
            return Task.FromResult("ran");
        }
    }

    private static InferpalConfig Config(string provider, string url) =>
        new() { Provider = provider, BaseUrl = url, AgentMaxIterations = 3 };

    // ── Ollama ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Ollama ≥ 0.8 emits each tool call in its own chunk, as soon as it is parsed: the client kept
    /// the LAST one, and the model believed it had read a file it never read.
    /// </summary>
    [Fact]
    public async Task Ollama_ToolCallsStreamedInSeparateChunks_AreAllKept()
    {
        const string ndjson = """
            {"message":{"role":"assistant","content":"","tool_calls":[{"function":{"name":"read_file","arguments":{"path":"A.cs"}}}]},"done":false}
            {"message":{"role":"assistant","content":"","tool_calls":[{"function":{"name":"read_file","arguments":{"path":"B.cs"}}}]},"done":false}
            {"message":{"role":"assistant","content":""},"done":true,"prompt_eval_count":10,"eval_count":5}
            """;
        using var server = new LoopbackHttpServer(p => p == "/api/chat" ? ndjson : null);
        var client = new OllamaClient(Config("ollama", server.BaseUrl));

        var turn = await client.SendChatAsync(
            "m", [new ChatMessageDto("user", "go")], EmptyToolRegistry.Instance, null, CancellationToken.None);

        Assert.NotNull(turn.ToolCalls);
        Assert.Equal(["A.cs", "B.cs"],
                     turn.ToolCalls!.Select(c => c.Function.Arguments.GetProperty("path").GetString()));
    }

    /// <summary>
    /// A runner failure AFTER the 200 headers arrives as a <c>{"error":…}</c> line: it read as an
    /// empty chunk, and the turn ended on "no response".
    /// </summary>
    [Fact]
    public async Task Ollama_ErrorInsideTheStream_IsRaised_NotAnEmptyTurn()
    {
        const string ndjson = """
            {"message":{"role":"assistant","content":""},"done":false}
            {"error":"model runner has unexpectedly stopped"}
            """;
        using var server = new LoopbackHttpServer(p => p == "/api/chat" ? ndjson : null);
        var client = new OllamaClient(Config("ollama", server.BaseUrl));

        var ex = await Assert.ThrowsAsync<AgentHttpException>(() => client.SendChatAsync(
            "m", [new ChatMessageDto("user", "go")], EmptyToolRegistry.Instance, null, CancellationToken.None));

        Assert.Contains("model runner has unexpectedly stopped", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ollama_PullThatReportsAnError_IsNotASuccess()
    {
        const string ndjson = """
            {"status":"pulling manifest"}
            {"error":"pull model manifest: file does not exist"}
            """;
        using var server = new LoopbackHttpServer(p => p == "/api/pull" ? ndjson : null);
        var client = new OllamaClient(Config("ollama", server.BaseUrl));
        var statuses = new List<string>();

        var ok = await client.PullModelAsync("nope", statuses.Add, CancellationToken.None);

        Assert.False(ok);
        Assert.Contains(statuses, s => s.Contains("file does not exist", StringComparison.Ordinal));
    }

    /// <summary>Witness: a pull that ends on <c>success</c> is still a success.</summary>
    [Fact]
    public async Task Ollama_PullThatEndsOnSuccess_IsASuccess()
    {
        const string ndjson = """
            {"status":"pulling manifest"}
            {"status":"success"}
            """;
        using var server = new LoopbackHttpServer(p => p == "/api/pull" ? ndjson : null);
        var client = new OllamaClient(Config("ollama", server.BaseUrl));

        Assert.True(await client.PullModelAsync("ok", _ => { }, CancellationToken.None));
    }

    // ── OpenAI-compatible ──────────────────────────────────────────────────────

    /// <summary>
    /// Truncated arguments (a turn cut off at <c>finish_reason: length</c>) became <c>{}</c>:
    /// <c>run_tests {"filter":"Foo…</c> ran the WHOLE suite. A malformed call never becomes another
    /// operation — it is handed back to the model, unexecuted.
    /// </summary>
    [Fact]
    public async Task OpenAi_TruncatedToolArguments_AreNotExecutedWithDefaults()
    {
        const string toolCall = """
            data: {"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"c1","function":{"name":"run_tests","arguments":"{\"filter\":\"Foo"}}]},"finish_reason":"length"}]}

            data: [DONE]

            """;
        const string answer = """
            data: {"choices":[{"index":0,"delta":{"content":"done"},"finish_reason":"stop"}]}

            data: [DONE]

            """;
        var calls = 0;
        using var server = new LoopbackHttpServer(p => p.StartsWith("/v1/chat/completions", StringComparison.Ordinal)
            ? (Interlocked.Increment(ref calls) == 1 ? toolCall : answer)
            : null);
        var client = new OpenAiCompatibleClient(Config("openai-compatible", server.BaseUrl));
        var tools  = new CountingRegistry("run_tests");

        var result = await client.RunAgentAsync(
            "m", [new ChatMessageDto("system", "sys"), new ChatMessageDto("user", "go")],
            tools, _ => { }, null, CancellationToken.None);

        Assert.Equal(0, tools.ExecuteCount);
        var toolMsg = Assert.Single(result.UpdatedHistory, m => m.Role == "tool");
        Assert.Contains("run_tests", toolMsg.Content, StringComparison.Ordinal);
        Assert.Contains("JSON", toolMsg.Content, StringComparison.Ordinal);
    }

    /// <summary>
    /// A server that sends several complete calls all at index 0 (distinct ids): the names overwrote
    /// each other and the arguments concatenated into <c>{..}{..}</c>.
    /// </summary>
    [Fact]
    public async Task OpenAi_TwoCompleteCallsOnTheSameIndex_WithDistinctIds_StayTwoCalls()
    {
        const string sse = """
            data: {"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"a","function":{"name":"read_file","arguments":"{\"path\":\"A.cs\"}"}}]}}]}

            data: {"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"b","function":{"name":"read_file","arguments":"{\"path\":\"B.cs\"}"}}]},"finish_reason":"tool_calls"}]}

            data: [DONE]

            """;
        using var server = new LoopbackHttpServer(p => p.StartsWith("/v1/chat/completions", StringComparison.Ordinal) ? sse : null);
        var client = new OpenAiCompatibleClient(Config("openai-compatible", server.BaseUrl));

        var turn = await client.SendChatAsync(
            "m", [new ChatMessageDto("user", "go")], new CountingRegistry("read_file"), null, CancellationToken.None);

        Assert.NotNull(turn.ToolCalls);
        Assert.Equal(["A.cs", "B.cs"],
                     turn.ToolCalls!.Select(c => c.Function.Arguments.GetProperty("path").GetString()));
    }

    /// <summary>Witness: the fragments of ONE call (id, then continuations without id) stay one call.</summary>
    [Fact]
    public async Task OpenAi_FragmentsOfOneCall_StayOneCall()
    {
        const string sse = """
            data: {"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"a","function":{"name":"read_file","arguments":"{\"pa"}}]}}]}

            data: {"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"th\":\"A.cs\"}"}}]},"finish_reason":"tool_calls"}]}

            data: [DONE]

            """;
        using var server = new LoopbackHttpServer(p => p.StartsWith("/v1/chat/completions", StringComparison.Ordinal) ? sse : null);
        var client = new OpenAiCompatibleClient(Config("openai-compatible", server.BaseUrl));

        var turn = await client.SendChatAsync(
            "m", [new ChatMessageDto("user", "go")], new CountingRegistry("read_file"), null, CancellationToken.None);

        var call = Assert.Single(turn.ToolCalls!);
        Assert.Equal("A.cs", call.Function.Arguments.GetProperty("path").GetString());
    }

    [Fact]
    public async Task OpenAi_ListModels_PropagatesTheCallersCancellation()
    {
        using var server = new LoopbackHttpServer(_ => """{"data":[]}""");
        var client = new OpenAiCompatibleClient(Config("openai-compatible", server.BaseUrl));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ListModelsAsync(cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ListInstalledModelsAsync(cts.Token));
    }

    // ── LM Studio ──────────────────────────────────────────────────────────────

    private const string ChatAnswer = """
        data: {"choices":[{"index":0,"delta":{"content":"ok"},"finish_reason":"stop"}]}

        data: [DONE]

        """;

    private static string LoadedModel(string key, int ctx) =>
        "{\"models\":[{\"key\":\"" + key + "\",\"loaded_instances\":[{\"id\":\"" + key
        + "\",\"config\":{\"context_length\":" + ctx + "}}]}]}";

    /// <summary>
    /// Matching by PREFIX read the n_ctx of another loaded model: asking for <c>qwen/qwen3-4b</c>
    /// while <c>qwen/qwen3-4b-thinking-2507</c> runs with a small context refused a valid request
    /// before even sending it.
    /// </summary>
    [Fact]
    public async Task LmStudio_ContextGuard_DoesNotBorrowTheWindowOfAModelWhoseNameMerelyStartsTheSame()
    {
        using var server = new LoopbackHttpServer(p => p switch
        {
            "/api/v1/models" => LoadedModel("qwen/qwen3-4b-thinking-2507", 50),
            _ when p.StartsWith("/v1/chat/completions", StringComparison.Ordinal) => ChatAnswer,
            _ => null,
        });
        var client = new LmStudioClient(Config("lmstudio", server.BaseUrl));

        var turn = await client.SendChatAsync(
            "qwen/qwen3-4b", [new ChatMessageDto("user", new string('x', 2000))],
            EmptyToolRegistry.Instance, null, CancellationToken.None);

        Assert.Equal("ok", turn.TextContent);
    }

    /// <summary>Witnesses: the model itself, and its <c>@quant</c> variant, are still guarded.</summary>
    [Theory]
    [InlineData("qwen/qwen3-27b", "qwen/qwen3-27b")]
    [InlineData("qwen/qwen3-27b", "qwen/qwen3-27b@q4")]
    public async Task LmStudio_ContextGuard_StillRefusesTheLoadedModelAndItsVariant(string loaded, string requested)
    {
        using var server = new LoopbackHttpServer(p => p switch
        {
            "/api/v1/models" => LoadedModel(loaded, 50),
            _ when p.StartsWith("/v1/chat/completions", StringComparison.Ordinal) => ChatAnswer,
            _ => null,
        });
        var client = new LmStudioClient(Config("lmstudio", server.BaseUrl));

        await Assert.ThrowsAsync<AgentHttpException>(() => client.SendChatAsync(
            requested, [new ChatMessageDto("user", new string('x', 2000))],
            EmptyToolRegistry.Instance, null, CancellationToken.None));
    }
}

/// <summary>
/// A direct call to <c>SendChatAsync</c> — code actions, inline edit, the host's plain chat, /check,
/// /onboard — holds the GPU like an agent run: indexing yields and ghost text stays quiet.
/// </summary>
[Collection(SignalCollection.Name)]
public class SendChatGpuLeaseTests : IDisposable
{
    private readonly SignalScratchDir _scratch = new();

    public void Dispose() => _scratch.Dispose();

    [Fact]
    public async Task Ollama_DirectSendChat_HoldsTheChatLeaseWhileTheRequestIsInFlight()
    {
        bool? active = null;
        using var server = new LoopbackHttpServer(p =>
        {
            if (p != "/api/chat") return null;
            active = GpuScheduler.IsChatActive;
            return """{"message":{"role":"assistant","content":"ok"},"done":true}""";
        });
        var client = new OllamaClient(new InferpalConfig { Provider = "ollama", BaseUrl = server.BaseUrl });

        await client.SendChatAsync("m", [new ChatMessageDto("user", "go")], EmptyToolRegistry.Instance, null, CancellationToken.None);

        Assert.True(active);
    }

    [Fact]
    public async Task OpenAi_DirectSendChat_HoldsTheChatLeaseWhileTheRequestIsInFlight()
    {
        bool? active = null;
        using var server = new LoopbackHttpServer(p =>
        {
            if (!p.StartsWith("/v1/chat/completions", StringComparison.Ordinal)) return null;
            active = GpuScheduler.IsChatActive;
            return "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
        });
        var client = new OpenAiCompatibleClient(new InferpalConfig { Provider = "openai-compatible", BaseUrl = server.BaseUrl });

        await client.SendChatAsync("m", [new ChatMessageDto("user", "go")], EmptyToolRegistry.Instance, null, CancellationToken.None);

        Assert.True(active);
    }
}
