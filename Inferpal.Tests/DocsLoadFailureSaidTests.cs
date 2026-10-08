using System.IO;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Services.Commands;
using Inferpal.Services.Docs;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Indexed documentation that cannot be loaded at start is said — in the conversation, and above <c>/docs</c>'s
/// listing — instead of reading as sources never indexed.
/// </summary>
/// <remarks>
/// ⚠ The load's failure only set <c>Status</c>, which nobody reads: every source showed "0 pages", <c>search_docs</c>
/// was never offered, and nothing said why.
/// </remarks>
[Collection(DocsDatabaseCollection.Name)]
public sealed class DocsLoadFailureSaidTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("inferpal-docs-load-").FullName;

    public DocsLoadFailureSaidTests() => DocsDatabase.OverrideDirForTests = _dir;

    public void Dispose()
    {
        DocsDatabase.OverrideDirForTests = null;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static Task<string> ListAsync(DocsIndexService docs) => DocsCommandHandler.HandleAsync(
        new InferpalConfig { DocSitesJson = DocSite.Serialize([new DocSite("react", "React", "https://react.dev/learn")]) },
        docs, ["/docs"], new Progress<string>(_ => { }), CancellationToken.None);

    [Fact]
    public async Task ADatabaseThatCannotBeLoaded_IsSaid_AtStartAndAboveTheListing()
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "docs.db"), "this is not a database, it was cut mid-write");
        var docs = new DocsIndexService(new FakeInferenceProvider(), new InferpalConfig());

        var notice = await docs.LoadAsync(CancellationToken.None);

        Assert.NotNull(notice);
        Assert.StartsWith(Strings.DocsNotLoaded("\u0000").Split('\u0000')[0], notice);
        Assert.StartsWith(notice + "\n\n", await ListAsync(docs));
    }

    /// <summary>Reference arm: nothing indexed yet is not a failure — no notice, the listing as it was.</summary>
    [Fact]
    public async Task NothingIndexedYet_SaysNothing()
    {
        var docs = new DocsIndexService(new FakeInferenceProvider(), new InferpalConfig());

        Assert.Null(await docs.LoadAsync(CancellationToken.None));
        Assert.Null(docs.LoadFailure);
        Assert.DoesNotContain(Strings.DocsNotLoaded("\u0000").Split('\u0000')[0], await ListAsync(docs));
    }

    [Fact]
    public void BothEditors_SayItInTheConversation()
    {
        var root = ConversationPersistenceSilenceTests.RepoRoot();
        var host = ConventionCoverageTests.CodeOnly(Path.Combine(root, "Inferpal.Host", "HostServer.cs"));
        Assert.Contains("_ = LoadDocsAsync(docs);", host);
        Assert.Contains("if (await docs.LoadAsync(CancellationToken.None) is { } notice)", host);

        var vm = ConventionCoverageTests.CodeOnly(Path.Combine(root, "Inferpal", "ToolWindow", "InferpalToolWindowData.Mentions.cs"));
        Assert.Contains("if (await _docsIndex.LoadAsync(CancellationToken.None) is { } notice)", vm);
        var construction = ConventionCoverageTests.CodeOnly(Path.Combine(root, "Inferpal", "ToolWindow", "InferpalToolWindowData.Construction.cs"));
        Assert.Contains("_ = LoadDocsAsync();", construction);
    }
}
