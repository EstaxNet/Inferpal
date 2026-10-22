using System.IO;
using System.Text;
using Inferpal.Config;
using Inferpal.Services;
using Inferpal.Services.Hardware;
using Inferpal.Services.Inference;
using Inferpal.Services.Lsp;
using Inferpal.Services.Rag;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Completions and code search on a server of their own (<see cref="RoleServers"/>): each role talks to its server with
/// its server's key, steps aside for a chat only on the chat's machine, and never borrows the chat's model there.
/// </summary>
[Collection(SignalCollection.Name)]
public sealed class ServerPerRoleTests : IDisposable
{
    // Ghost text yields to a chat turn marked busy in the signal folder: never the real one.
    private readonly SignalScratchDir _signals = new();

    public void Dispose() => _signals.Dispose();

    private const string Completion =
        "data: {\"choices\":[{\"text\":\"a + b\",\"finish_reason\":null}]}\n\ndata: {\"choices\":[{\"text\":\"\",\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";

    /// <summary>An OpenAI-compatible completion server (not a llama-server: no <c>/props</c>).</summary>
    private static LoopbackHttpServer CompletionServer() =>
        new(p => p.StartsWith("/v1/completions", StringComparison.Ordinal) ? Completion : null);

    private static InferpalConfig Settings(string chatUrl, string completionUrl, string model = "coder") => new()
    {
        Provider = "openai-compatible", BaseUrl = chatUrl, ApiKey = "k-0",
        InlineCompletionRunsOn = "other", InlineCompletionBaseUrl = completionUrl,
        InlineCompletionProvider = "openai-compatible", InlineCompletionApiKey = "k-fim", InlineCompletionModel = model,
    };

    private static async Task<string> CompleteAsync(IInferenceProvider client)
    {
        var text = new StringBuilder();
        await client.StreamFimAsync("return ", ";\n}", 16, 0.2, t => text.Append(t), CancellationToken.None);
        return text.ToString();
    }

    /// <summary>A completion goes to the completion's server with that server's key — the chat's server sees nothing,
    /// and the chat's key goes nowhere but to the chat's.</summary>
    [Fact]
    public async Task ACompletion_GoesToItsServer_WithItsKeyOnly()
    {
        using var chat = CompletionServer();
        using var fim = CompletionServer();
        var client = InferenceProviderFactory.Create(Settings(chat.BaseUrl, fim.BaseUrl), ServerRole.Completion);

        var text = await CompleteAsync(client);

        Assert.Equal("a + b", text);
        Assert.Contains(fim.Paths, p => p.StartsWith("/v1/completions", StringComparison.Ordinal));
        Assert.Empty(chat.Paths);
        Assert.All(fim.Authorizations, a => Assert.Equal("Bearer k-fim", a));
        Assert.NotEmpty(fim.Authorizations);
    }

    /// <summary>A chat answering on another machine leaves the card here free: the completion runs. On the chat's machine
    /// it steps aside, as before.</summary>
    [Fact]
    public async Task AChatElsewhere_DoesNotSilenceACompletionHere_OneOnThisMachineDoes()
    {
        using var fim = CompletionServer();
        var elsewhere = InferenceProviderFactory.Create(Settings("http://192.0.2.10:8000", fim.BaseUrl), ServerRole.Completion);
        var here = InferenceProviderFactory.Create(Settings("http://127.0.0.1:1", fim.BaseUrl), ServerRole.Completion);

        using var lease = GpuScheduler.AcquireChatLease();
        var whileChatElsewhere = await CompleteAsync(elsewhere);
        var whileChatHere = await CompleteAsync(here);

        Assert.Equal("a + b", whileChatElsewhere);
        Assert.Equal("", whileChatHere);
        Assert.False(elsewhere.SharesGpuWithChat);   // and so the index embedding there does not wait for the chat either
        Assert.True(here.SharesGpuWithChat);
    }

    /// <summary>The index embeds on another machine while a chat answers here; on the chat's machine it waits for the
    /// answer, and goes on once the chat is done.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndexingOnAnotherMachine_DoesNotWaitForTheChat_OnTheChatsMachineItDoes(bool sharesTheCard)
    {
        TestRagStore.Redirect();
        var root = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"role-index-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "Alpha.cs"), "public class Alpha\n{\n    public int One() => 1;\n}\n");
        var search = new FakeInferenceProvider { Embedding = [0.1f, 0.2f], SharesGpuWithChat = sharesTheCard };
        using var index = new ProjectIndexService(new FakeInferenceProvider(),
                                                  new InferpalConfig { RagEnabled = true, RagEmbeddingModel = "embed" },
                                                  new LspSemanticProvider(), () => search);
        try
        {
            var lease = GpuScheduler.AcquireChatLease();
            try
            {
                index.StartIndexing(root);
                var finished = await PassFinishedAsync(index, sharesTheCard ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(30));
                Assert.Equal(!sharesTheCard, finished);
            }
            finally
            {
                lease.Dispose();
            }
            Assert.True(await PassFinishedAsync(index, TimeSpan.FromSeconds(30)), index.Status);   // and nothing was lost
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    private static async Task<bool> PassFinishedAsync(ProjectIndexService index, TimeSpan budget)
    {
        var deadline = DateTime.UtcNow + budget;
        while (DateTime.UtcNow < deadline)
        {
            if (index.Status.Contains('✅')) return true;
            await Task.Delay(50);
        }
        return false;
    }

    /// <summary>On a server of its own, a completion with no model chosen asks nothing — the chat's model is not there —
    /// and says why once.</summary>
    [Fact]
    public async Task ACompletionServerWithoutAModel_IsNotAskedTheChatsModel_AndSaysWhy()
    {
        using var fim = CompletionServer();
        var settings = Settings("http://192.0.2.10:8000", fim.BaseUrl, model: "");
        settings.DefaultModel = "qwen3.8:27b";
        var client = InferenceProviderFactory.Create(settings, ServerRole.Completion);

        var text = await CompleteAsync(client);

        Assert.Equal("", text);
        Assert.DoesNotContain(fim.Paths, p => p.StartsWith("/v1/completions", StringComparison.Ordinal));
        Assert.Contains(Diagnostics.Snapshot(), e => e.Context == "Fim" && e.Detail.Contains(fim.BaseUrl, StringComparison.Ordinal));
    }

    /// <summary>Each role's client talks to its server: code search can run on the completion's server, and a role left on
    /// the chat's server is served by the chat's own client.</summary>
    [Fact]
    public void EachRole_IsServedOnItsServer()
    {
        var settings = Settings("http://192.0.2.10:8000", "http://localhost:11434");
        settings.RagEmbeddingRunsOn = "autocomplete";
        var chat = new FakeInferenceProvider();
        var roles = new RoleClients(chat, settings);

        Assert.Same(chat, roles.For(ServerRole.Chat));
        Assert.Equal("http://localhost:11434", roles.For(ServerRole.Completion).ServerAddress);
        Assert.Equal("http://localhost:11434", roles.For(ServerRole.Search).ServerAddress);

        settings.RagEmbeddingRunsOn = "chat";
        Assert.Same(chat, roles.For(ServerRole.Search));                 // moved back: the chat's client again, at once
    }

    /// <summary>What the settings leave to a fallback is said: "another server" with no address runs on the chat's, a
    /// server whose kind is not set is spoken to as the chat's kind.</summary>
    [Fact]
    public void AFallbackOfARolesSettings_IsSaid()
    {
        var noAddress = new InferpalConfig { BaseUrl = "http://192.0.2.10:8000", InlineCompletionRunsOn = "other" };
        var chat = new FakeInferenceProvider();
        Assert.Same(chat, new RoleClients(chat, noAddress).For(ServerRole.Completion));
        Assert.Contains(Diagnostics.Snapshot(), e => e.Context == "Settings"
            && e.Detail.StartsWith("Inline completions are set to run on another server, but no address", StringComparison.Ordinal));

        var noKind = new InferpalConfig
        {
            Provider = "lmstudio", RagEmbeddingRunsOn = "other", RagEmbeddingBaseUrl = $"http://127.0.0.1:{Random.Shared.Next(20000, 60000)}",
        };
        new RoleClients(chat, noKind).For(ServerRole.Search);
        Assert.Contains(Diagnostics.Snapshot(), e => e.Context == "Settings"
            && e.Detail.Contains(noKind.RagEmbeddingBaseUrl, StringComparison.Ordinal) && e.Detail.Contains("(lmstudio)", StringComparison.Ordinal));
    }

    /// <summary>Another address or key is another server: a new client, which carries nothing it learned of the last one
    /// (a breaker opened on a wrong address). The same server keeps its client.</summary>
    [Fact]
    public void AnotherAddressOrKey_IsANewClient_TheSameServerKeepsItsOwn()
    {
        var settings = Settings("http://192.0.2.10:8000", "http://localhost:11434");
        var roles = new RoleClients(new FakeInferenceProvider(), settings);
        var first = roles.For(ServerRole.Completion);

        Assert.Same(first, roles.For(ServerRole.Completion));
        settings.InlineCompletionBaseUrl = "http://localhost:11435";
        var second = roles.For(ServerRole.Completion);
        Assert.NotSame(first, second);
        settings.InlineCompletionApiKey = "k-2";
        Assert.NotSame(second, roles.For(ServerRole.Completion));
    }
}
