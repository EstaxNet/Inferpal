using System.Globalization;
using System.IO;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Host;
using Inferpal.Localization;
using Inferpal.Services;
using Inferpal.Services.Docs;
using Inferpal.Services.Presentation;
using Inferpal.Services.Rag;
using Nerdbank.Streams;
using StreamJsonRpc;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The live blocks of the settings pages (<see cref="SettingsWidgets"/>): what they say about the index, the @Docs sites
/// and the conversation, in the states that read differently — and the one fact both editors serve from the host.
/// </summary>
[Collection(CultureSerialCollection.Name)]
public class SettingsWidgetsTests
{
    private static IndexSnapshot Snapshot(
        string root = "C:/repo", bool indexing = false, int done = 0, int total = 0, bool completed = false,
        int files = 0, int chunks = 0, int unembedded = 0, int oversize = 0, IReadOnlyList<string>? oversizeFiles = null,
        string? model = null, bool down = false, DateTime? at = null, string? failure = null, bool stopped = false) =>
        new(root, indexing, done, total, completed, files, chunks, unembedded, oversize, oversizeFiles ?? [], null,
            model, down, at, failure, stopped);

    private static readonly DateTime Now = new(2026, 10, 4, 15, 30, 0);

    [Fact]
    public void ACompleteIndex_SaysItIsUpToDate_WithItsModel_AndNothingElse()
    {
        Strings.ApplyLanguage("en");
        var card = SettingsWidgets.IndexCard(
            Snapshot(completed: true, files: 1284, chunks: 9000, model: "embeddinggemma:300m", at: Now.AddMinutes(-2)),
            ragEnabled: true, configuredModel: null, Now);

        Assert.Equal("ready", card.State);
        Assert.Equal(Strings.IndexCardReady, card.Title);
        Assert.Contains(1284.ToString("N0", CultureInfo.CurrentCulture), card.Detail, StringComparison.Ordinal);
        Assert.Equal(Strings.IndexCardModelSemantic("embeddinggemma:300m"), card.ModelLine);
        // Reference arm: a complete index carries no note at all — a note on every card is noise nobody reads.
        Assert.Empty(card.Notes);
        Assert.Equal(string.Empty, card.OversizeNote);
        Assert.True(card.CanRebuild);
    }

    [Fact]
    public void WhatTheIndexLeftOut_IsCounted_AndTheOversizeFilesAreNamed_BeyondTheCapToo()
    {
        Strings.ApplyLanguage("en");
        var card = SettingsWidgets.IndexCard(
            Snapshot(completed: true, files: 10, chunks: 40, unembedded: 4, model: "m", at: Now,
                     oversize: 3, oversizeFiles: ["big/a.json", "big/b.json"]),
            ragEnabled: true, configuredModel: null, Now);

        Assert.Contains(Strings.IndexCardHoles("4", "40"), card.Notes);
        Assert.Equal(Strings.IndexCardOversize(3, CodeChunker.MaxFileSizeKilobytes), card.OversizeNote);
        // Two names kept, three dropped: the third is counted, never silently missing from the list.
        Assert.Equal(["big/a.json", "big/b.json", Strings.IndexCardMoreFiles(1)], card.OversizeFiles);
    }

    [Fact]
    public void ARunningPass_ShowsItsProgress_AndCannotBeRestartedUnderItself()
    {
        Strings.ApplyLanguage("en");
        var card = SettingsWidgets.IndexCard(Snapshot(indexing: true, done: 12, total: 300), true, "nomic", Now);

        Assert.Equal("indexing", card.State);
        Assert.Equal(Strings.IndexCardProgress("12", "300"), card.Detail);
        Assert.False(card.CanRebuild);
        // Before a first pass has recorded one, the model is the one the settings name — not "no model installed".
        Assert.Equal(Strings.IndexCardModelSemantic("nomic"), card.ModelLine);
    }

    [Fact]
    public void AFailedPass_NamesItsCause_AndAnIndexOffByChoice_IsNotCalledBroken()
    {
        Strings.ApplyLanguage("en");
        var failed = SettingsWidgets.IndexCard(Snapshot(failure: "SQLite could not be loaded."), true, null, Now);
        Assert.Equal("failed", failed.State);
        Assert.Equal(Strings.IndexCardFailedDetail("SQLite could not be loaded"), failed.Detail);

        var off = SettingsWidgets.IndexCard(Snapshot(), ragEnabled: false, configuredModel: null, Now);
        Assert.Equal("notBuilt", off.State);
        Assert.Equal(Strings.IndexCardNotBuiltDetail, off.Detail);
        Assert.Equal(Strings.IndexCardModelOff, off.ModelLine);
        Assert.Equal(Strings.IndexCardBuild, off.ButtonLabel);

        var none = SettingsWidgets.IndexCard(Snapshot(root: ""), true, null, Now);
        Assert.Equal("noWorkspace", none.State);
        Assert.False(none.CanRebuild);
    }

    [Theory]
    [InlineData("fr-CA", "Français")]
    [InlineData("fr",    "Français")]
    [InlineData("zh-CN", "中文(简体)")]
    [InlineData("pt-BR", "English")]
    [InlineData("zh-TW", "English")]
    public void TheAutomaticLanguage_IsTheOneTheResourcesActuallyResolveTo(string culture, string expected)
    {
        // The parent chain is the resource manager's own fallback: zh-TW has no satellite on its chain, so the panel
        // speaks English there, and naming "中文" would promise a language it does not get.
        Assert.Equal(expected, SettingsSchema.AutoLanguageName(CultureInfo.GetCultureInfo(culture)));
    }

    [Fact]
    public void ContextUsage_SplitsTheWindow_IntoItsThreeShares()
    {
        Strings.ApplyLanguage("en");
        var usage = SettingsWidgets.ContextUsage(new XRayPanelModel(
            [], TotalTokens: 1_400, HistoryTokens: 9_800, ContextWindow: 32_768, FillPercent: 0, OverheadWarning: false,
            RawPrompt: string.Empty, ToolTokens: 4_300));

        Assert.Equal(Strings.ContextUsageTokens(15_500.ToString("N0", CultureInfo.CurrentCulture), 32_768.ToString("N0", CultureInfo.CurrentCulture)), usage.Summary);
        Assert.Equal(["instructions", "tools", "conversation"], usage.Parts.Select(p => p.Key));
        Assert.Equal(1.4.ToString("0.0", CultureInfo.CurrentCulture) + "k", usage.Parts[0].Amount);
        Assert.Equal(100.0 * 9_800 / 32_768, usage.Parts[2].Percent, 3);
    }

    [Fact]
    public void ADocsSite_SaysWhatItsCrawlDid()
    {
        Strings.ApplyLanguage("en");
        var ef     = DocSite.Create("https://learn.microsoft.com/ef/core/", "EF Core");
        var pytest = DocSite.Create("https://docs.pytest.org/en/stable/", "pytest");
        var fresh  = DocSite.Create("https://example.org/docs", "Example");
        var rows = SettingsWidgets.DocsSites(
            [ef, pytest, fresh], [(ef, 50, 400), (pytest, 38, 300)],
            new Dictionary<string, int> { [pytest.Id] = 4 }, indexingId: null,
            new Dictionary<string, string> { [fresh.Id] = "Docs: no readable pages found" });

        Assert.Equal("learn.microsoft.com/ef/core", rows[0].Address);
        Assert.Equal(Strings.DocsSiteIndexed(50), rows[0].Status);
        Assert.Equal(string.Empty, rows[0].HoleNote);
        Assert.Equal(Strings.DocsSiteHoles(38, 4), rows[1].Status);
        Assert.Equal(Strings.DocsSiteHolesNote(4), rows[1].HoleNote);
        // A crawl that ended with nothing keeps the one sentence that says why.
        Assert.Equal(Strings.IndexCardNotBuilt, rows[2].Status);
        Assert.Equal("Docs: no readable pages found", rows[2].HoleNote);
    }
}

/// <summary>
/// The host loads the documentation index when it starts, as Visual Studio does when its window opens: the index only
/// ever hydrates, so a host that never loaded it served none of the sites indexed in an earlier session.
/// </summary>
[Collection(DocsDatabaseCollection.Name)]
public sealed class HostDocsLoadTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"hostdocs-{Guid.NewGuid():N}");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"hostdocs-root-{Guid.NewGuid():N}");

    public HostDocsLoadTests()
    {
        DocsDatabase.OverrideDirForTests = _dir;
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        DocsDatabase.OverrideDirForTests = null;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task ASiteIndexedInAnEarlierSession_IsServedByTheNextHost()
    {
        Strings.ApplyLanguage("en");
        var site = DocSite.Create("https://docs.example.com/", "Example");
        var earlier = new DocsIndexService(new FakeInferenceProvider { Embedding = [0.1f, 0.2f] },
                                           new InferpalConfig { RagEmbeddingModel = "embed" })
        {
            CrawlForTests = (_, _) => Task.FromResult(new List<DocCrawler.Page>
            {
                new("https://docs.example.com/a", "Retries", "How retries work. " + string.Join(" ", Enumerable.Repeat("retry", 300))),
                new("https://docs.example.com/b", "Timeouts", "How timeouts work. " + string.Join(" ", Enumerable.Repeat("timeout", 300))),
            }),
        };
        await earlier.AddOrReindexAsync(site, progress: null, CancellationToken.None);
        Assert.True(earlier.ChunkCount > 0, "the fixture indexed nothing: the test would measure nothing.");

        var (clientStream, serverStream) = FullDuplexStream.CreatePair();
        var server = new HostServer(_ => new FakeInferenceProvider(), () => new InferpalConfig
        {
            RagEnabled = false,
            RagEmbeddingModel = "embed",
            DocSitesJson = DocSite.Serialize([site]),
        });
        var serverRpc = HostRpc.Create(serverStream, serverStream, server);
        server.Attach(serverRpc);
        serverRpc.StartListening();
        using var client = HostRpc.Create(clientStream, clientStream, new object());
        client.StartListening();
        try
        {
            await client.InvokeWithParameterObjectAsync<object>("initialize", new { rootDir = _root });

            // The load is not awaited by initialize (docs.db can be large): 30 s, never a few — the runner is busy.
            string status = string.Empty;
            for (var waited = 0; waited < 30_000; waited += 100)
            {
                var docs = await client.InvokeAsync<JsonElement>("settings/docsSites");
                status = docs.GetProperty("sites")[0].GetProperty("status").GetString() ?? string.Empty;
                if (status == Strings.DocsSiteIndexed(2)) break;
                await Task.Delay(100);
            }
            Assert.Equal(Strings.DocsSiteIndexed(2), status);
        }
        finally
        {
            try { serverRpc.Dispose(); } catch { }
            server.Dispose();
        }
    }
}

/// <summary>
/// Reading a project's profile never re-applies the machine's language: the code index reads it whenever a workspace
/// is set, and the VS Code host speaks the EDITOR's language, which the machine configuration does not know.
/// </summary>
[Collection(CultureSerialCollection.Name)]
public sealed class ProfileKeepsTheLanguageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"profile-lang-{Guid.NewGuid():N}");

    public ProfileKeepsTheLanguageTests() => Directory.CreateDirectory(Path.Combine(_root, ".inferpal"));

    public void Dispose()
    {
        Strings.ApplyLanguage(null);
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void IndexingAWorkspaceWithAProfile_KeepsTheLanguageTheEditorSet()
    {
        File.WriteAllText(Path.Combine(_root, ".inferpal", "project.json"), """{ "indexExclude": ["docs/generated/**"] }""");
        // The editor's language, as the host applies it from the VS Code locale — not the configured one.
        Strings.ApplyLanguage("de");

        var profile = Inferpal.Services.Governance.ProjectProfile.Load(_root);

        Assert.Equal(["docs/generated/**"], profile.IndexExcludes);   // witness: the profile was read
        Assert.Equal("de", Strings.OverrideCulture?.Name);
    }
}
