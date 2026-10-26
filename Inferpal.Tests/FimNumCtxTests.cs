using Inferpal.Config;
using Inferpal.Services.Inference;
using Inferpal.Services.Signals;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// An inline completion asked of the chat's own model, on the chat's Ollama, asks for the chat's context window.
/// </summary>
/// <remarks>
/// Ollama keeps one loaded runner per model AND per window: a completion sent without <c>num_ctx</c> is served at the
/// server's own default size, a chat at the configured one, so the same model was loaded again at every switch between
/// typing and asking — the default path, where the completion model is the chat's. A completion model of its own keeps
/// the server's default: given the chat's window, it would take the memory that window costs for nothing.
/// </remarks>
[Collection(SignalCollection.Name)]
public sealed class FimNumCtxTests : IDisposable
{
    // Ghost text yields to a chat turn marked busy in the signal folder: never the real one.
    private readonly SignalScratchDir _signals = new();

    public void Dispose() => _signals.Dispose();

    private static async Task<string> GenerateBodyAsync(InferpalConfig config, string? asked = null, bool ownServer = false)
    {
        using var server = new LoopbackHttpServer(p => p.StartsWith("/api/generate", StringComparison.Ordinal)
            ? "{\"response\":\"x\",\"done\":true}\n"
            : null);
        config.Provider = "ollama";
        if (ownServer)
        {
            // The chat answers elsewhere; the completion client is its role's, on the stand-in.
            config.BaseUrl = "http://chat.invalid:11434";
            config.InlineCompletionRunsOn = RoleServers.OnOther;
            config.InlineCompletionBaseUrl = server.BaseUrl;
        }
        else
            config.BaseUrl = server.BaseUrl;
        var client = new OllamaClient(config, ownServer ? ServerRole.Completion : ServerRole.Chat);

        await client.StreamFimAsync("def add(a, b):\n    return ", "", 8, 0.2, _ => { }, CancellationToken.None, asked);

        var body = Assert.Single(server.Bodies, b => b.Contains("\"prompt\"", StringComparison.Ordinal));   // WITNESS
        return body;
    }

    [Fact]
    public async Task TheChatsModel_IsAskedAtTheChatsWindow()
    {
        var body = await GenerateBodyAsync(new InferpalConfig { DefaultModel = "granite4.2:8b", ContextWindowSize = 24576 });

        Assert.Contains("\"model\":\"granite4.2:8b\"", body, StringComparison.Ordinal);
        Assert.Contains("\"num_ctx\":24576", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheChatsModel_NamedAsTheCompletionModel_IsAskedAtTheChatsWindow_CaseAside()
    {
        var body = await GenerateBodyAsync(new InferpalConfig
        {
            DefaultModel = "granite4.2:8b", InlineCompletionModel = "Granite4.2:8B", ContextWindowSize = 8192,
        });

        Assert.Contains("\"num_ctx\":8192", body, StringComparison.Ordinal);
    }

    /// <summary>A model the chat answers with for a task (here the agent's) is loaded at the chat's window too: completing
    /// with it at another size reloads it the same way.</summary>
    [Fact]
    public async Task ATasksModel_UsedForCompletions_IsAskedAtTheChatsWindow()
    {
        var body = await GenerateBodyAsync(new InferpalConfig
        {
            DefaultModel = "granite4.2:8b", AgentModel = "qwen3.8:27b", InlineCompletionModel = "qwen3.8:27b",
            ContextWindowSize = 24576,
        });

        Assert.Contains("\"num_ctx\":24576", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACompletionModelOfItsOwn_KeepsTheServersDefault()
    {
        var body = await GenerateBodyAsync(new InferpalConfig
        {
            DefaultModel = "qwen3.8:27b", InlineCompletionModel = "qwen2.5-coder:7b", ContextWindowSize = 32768,
        });

        Assert.Contains("\"model\":\"qwen2.5-coder:7b\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("num_ctx", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoConfiguredWindow_SendsNone_LikeTheChat()
    {
        var body = await GenerateBodyAsync(new InferpalConfig { DefaultModel = "granite4.2:8b", ContextWindowSize = 0 });

        Assert.DoesNotContain("num_ctx", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompletionsOnAServerOfTheirOwn_KeepThatServersDefault()
    {
        // The same model name on another server is another runner: the chat's window would size it for nothing.
        var body = await GenerateBodyAsync(new InferpalConfig
        {
            DefaultModel = "granite4.2:8b", ContextWindowSize = 8192, InlineCompletionModel = "granite4.2:8b",
        }, ownServer: true);

        Assert.DoesNotContain("num_ctx", body, StringComparison.Ordinal);
    }
}
