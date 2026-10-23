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

    /// <summary>A completion server that does not answer leaves its client failing — what the key of inline completion
    /// names instead of "the model had nothing" (<see cref="AskedCompletion.Empty"/>); one that answers does not.</summary>
    [Fact]
    public async Task AServerThatDoesNotAnswer_LeavesItsClientFailing_OneThatAnswersDoesNot()
    {
        using var fim = CompletionServer();
        var answers = InferenceProviderFactory.Create(Settings("http://127.0.0.1:1", fim.BaseUrl), ServerRole.Completion);
        var down    = InferenceProviderFactory.Create(Settings("http://127.0.0.1:1", "http://127.0.0.1:1"), ServerRole.Completion);

        Assert.Equal("a + b", await CompleteAsync(answers));   // witness: the same request, to a server that answers
        Assert.Equal("", await CompleteAsync(down));

        Assert.False(answers.FimFailing);
        Assert.True(down.FimFailing);
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

    /// <summary>This machine under its network address or its name is still this machine: a completion there shares the
    /// card with a chat on localhost and steps aside for it. Another machine does not.</summary>
    [Fact]
    public void ThisMachineUnderItsNetworkAddressOrName_IsTheChatsMachine()
    {
        var own = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses.Select(a => a.Address))
            .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(a));

        // The host name, as a URL carries it: unparsable, it would be "unknown" and the check vacuous (unknown counts as
        // the same machine).
        var host = System.Net.Dns.GetHostName();
        Assert.True(Uri.TryCreate($"http://{host}:11434", UriKind.Absolute, out _), $"{host} does not make a URL");   // WITNESS
        Assert.True(RoleServers.SameMachine($"http://{host}:11434", "http://localhost:8080"));
        if (own is not null)
            Assert.True(RoleServers.SameMachine($"http://{own}:11434", "http://127.0.0.1:8080"), $"{own} is this machine's");
        Assert.False(RoleServers.SameMachine("http://192.0.2.10:11434", "http://localhost:8080"));   // reference arm
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

    /// <summary>A role's client lists ITS server's models, with its key: what a role's model box offers comes from the
    /// server the role runs on, never from the chat's.</summary>
    [Fact]
    public async Task ARolesClient_ListsItsOwnServersModels()
    {
        const string models = """{"data":[{"id":"coder-7b"},{"id":"embed-small"}]}""";
        using var chat = CompletionServer();
        using var fim = new LoopbackHttpServer(p => p.StartsWith("/v1/models", StringComparison.Ordinal) ? models : null);

        var listed = await InferenceProviderFactory.Create(Settings(chat.BaseUrl, fim.BaseUrl), ServerRole.Completion)
            .ListModelsByKindAsync(CancellationToken.None);

        Assert.Contains("coder-7b", listed.Listed);
        Assert.Empty(chat.Paths);
        Assert.NotEmpty(fim.Authorizations);
        Assert.All(fim.Authorizations, a => Assert.Equal("Bearer k-fim", a));
    }

    /// <summary>The Visual Studio window writes what it shows: each role's place by its index (the places are
    /// translated), its address and key as typed, its kind when one is selected; a place it cannot read keeps what is
    /// saved and is named.</summary>
    [Fact]
    public void TheVisualStudioWindow_SavesEachRolesServer_AndNamesAPlaceItCannotRead()
    {
        var edited = new InferpalConfig { RagEmbeddingRunsOn = "autocomplete", InlineCompletionProvider = "ollama" };
        var named = new List<string>();
        var form = new Inferpal.Services.Presentation.RoleServerForm(
            // The entry's text in the language the list was drawn in before a change: only its index reads it.
            CompletionPlace: 1, CompletionPlaceName: "Inny serwer (stara etykieta)", CompletionUrl: "http://localhost:8080",
            CompletionKind: "OpenAI-compatible (generic)", CompletionKey: "k-1",
            SearchPlace: -1, SearchPlaceName: "Unreadable", SearchUrl: "", SearchKind: null, SearchKey: "");

        Inferpal.Services.Presentation.RoleServerSettings.Write(form, edited, (text, ok, label) =>
        {
            if (!ok && !string.IsNullOrWhiteSpace(text)) named.Add(label());
        });

        Assert.Equal(("other", "http://localhost:8080", "openai-compatible", "k-1"),
            (edited.InlineCompletionRunsOn, edited.InlineCompletionBaseUrl, edited.InlineCompletionProvider, edited.InlineCompletionApiKey));
        Assert.Equal("autocomplete", edited.RagEmbeddingRunsOn);                 // unreadable: kept
        Assert.Equal("", edited.RagEmbeddingProvider);                           // no kind selected: not set
        Assert.Single(named);
    }

    /// <summary>The place lists are translated: relabelled, a list keeps the entry its selection held — and a selection
    /// the box lost (null) takes back what is SAVED, never the first entry, which would move the role to the chat's
    /// server at the next Save.</summary>
    [Fact]
    public void ARelabelledPlaceList_KeepsThePlace_EvenWhenTheBoxLostItsSelection()
    {
        var options = Inferpal.Services.Presentation.RoleServerSettings.CompletionPlaces;
        var list = new List<string> { "old: above", "old: another" };

        Assert.Equal(options[1].Name, Inferpal.Services.Presentation.RoleServerSettings.Relabel(list, options, "old: another", "chat"));
        Assert.Equal(options[1].Name, list[1]);                                         // relabelled in place
        Assert.Equal(options[1].Name, Inferpal.Services.Presentation.RoleServerSettings.Relabel(list, options, null, "other"));
        Assert.Equal(options[0].Name, Inferpal.Services.Presentation.RoleServerSettings.Relabel(list, options, null, "chat"));   // reference arm

        // What a saved place means: its code whatever its case; a value no option names is the chat's server.
        Assert.Equal("other", Inferpal.Services.Presentation.RoleServerSettings.PlaceMeaning(options, "Other"));
        Assert.Equal("chat", Inferpal.Services.Presentation.RoleServerSettings.PlaceMeaning(options, ""));
    }

    /// <summary>The Visual Studio window shows every field of the roles on the Server page, tests each role's server
    /// with its own button, and counts each field among its unsaved changes.</summary>
    [Fact]
    public void TheVisualStudioWindow_ShowsAndCountsEveryRoleField()
    {
        var root = ConversationPersistenceSilenceTests.RepoRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "Inferpal", "ToolWindow", "InferpalSettingsContent.xaml"));
        var start = xaml.IndexOf("PageServerVisible", StringComparison.Ordinal);
        var end = xaml.IndexOf("PageAgentVisible", StringComparison.Ordinal);
        Assert.True(start > 0 && end > start, "the Server page is no longer found in the window");   // WITNESS
        var page = xaml[start..end];
        var pages = ConventionCoverageTests.CodeOnly(Path.Combine(root, "Inferpal", "ToolWindow", "InferpalSettingsData.Pages.cs"));

        foreach (var property in new[] { "CompletionRunsOn", "CompletionBaseUrl", "CompletionProvider", "CompletionApiKey",
                                         "SearchRunsOn", "SearchBaseUrl", "SearchProvider", "SearchApiKey" })
        {
            Assert.Contains("{Binding " + property, page, StringComparison.Ordinal);
            Assert.Contains("nameof(" + property + ")", pages, StringComparison.Ordinal);
        }
        Assert.Contains("{Binding TestCompletionServerCommand}", page, StringComparison.Ordinal);
        Assert.Contains("{Binding TestSearchServerCommand}", page, StringComparison.Ordinal);
        Assert.Contains("fields.Concat(RoleServerChanges(saved))", pages, StringComparison.Ordinal);
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
