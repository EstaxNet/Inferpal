using System.IO;
using Inferpal.Config;
using Inferpal.Services.Commands;
using Inferpal.Services.Docs;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Two windows (Visual Studio and VS Code, or two of either) each keep their own copy of the settings. A documentation
/// source's id must be chosen among the sources the FILE holds, not among those the window read at its start.
/// </summary>
/// <remarks>
/// ⚠ Both windows adding a page of the same host without a title chose the same "free" id. The element-wise merge of the
/// settings then kept one of the two, and the documentation index, keyed by id, replaced the other's pages: a source lost
/// without a word.
/// </remarks>
[Collection(DocsDatabaseCollection.Name)]
public sealed class DocsTwoWindowsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"docs-windows-{Guid.NewGuid():N}");
    private readonly string _configFile;

    public DocsTwoWindowsTests()
    {
        Directory.CreateDirectory(_dir);
        _configFile = Path.Combine(_dir, "config.json");
        DocsDatabase.OverrideDirForTests = Path.Combine(_dir, "docs");
    }

    public void Dispose()
    {
        DocsDatabase.OverrideDirForTests = null;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* cleanup */ }
    }

    /// <summary>A window: its own copy of the settings file, read once.</summary>
    private InferpalConfig Window()
    {
        var config = new InferpalConfig { SavePathForTests = _configFile };
        config.Save();
        return config;
    }

    private static DocsIndexService Docs(InferpalConfig config) =>
        new(new FakeInferenceProvider(), config) { CrawlForTests = (_, _) => Task.FromResult(new List<DocCrawler.Page>()) };

    private static Task<string> Run(InferpalConfig config, params string[] parts) =>
        DocsCommandHandler.HandleAsync(config, Docs(config), ["/docs", .. parts], new Progress<string>(), CancellationToken.None);

    private List<DocSite> InTheFile()
    {
        var onDisk = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(_configFile))!["docSitesJson"]!.GetValue<string>();
        Assert.True(DocSite.TryParse(onDisk, out var sites, out var problem), problem);
        return [.. sites];
    }

    [Fact]
    public async Task TwoWindowsAddingTheSameHost_KeepBothSources_UnderTwoIds()
    {
        var first  = Window();
        var second = Window();   // read before the first one adds anything

        await Run(first, "add", "https://learn.example.com/dotnet");
        await Run(second, "add", "https://learn.example.com/aspnet");

        var sites = InTheFile();
        Assert.Equal(2, sites.Count);
        Assert.Equal(2, sites.Select(s => s.Id).Distinct().Count());
        Assert.Contains(sites, s => s.StartUrl == "https://learn.example.com/dotnet");
        Assert.Contains(sites, s => s.StartUrl == "https://learn.example.com/aspnet");
    }

    [Fact]
    public async Task ASourceAddedInAnotherWindow_CanBeRemovedHere()
    {
        var first  = Window();
        var second = Window();
        await Run(first, "add", "https://learn.example.com/dotnet");
        var id = InTheFile().Single().Id;

        var answer = await Run(second, "remove", id);

        Assert.DoesNotContain(id, InTheFile().Select(s => s.Id));
        Assert.Equal(Inferpal.Localization.Strings.DocsRemoved(id), answer);
    }

    [Fact]
    public async Task OneWindow_AddsAsBefore()
    {
        // Reference arm: alone, two sources of one host get two ids, and both stay.
        var only = Window();
        await Run(only, "add", "https://learn.example.com/dotnet");
        await Run(only, "add", "https://learn.example.com/aspnet");

        Assert.Equal(2, InTheFile().Select(s => s.Id).Distinct().Count());
    }
}
