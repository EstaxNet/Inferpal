using System.IO;
using Inferpal.Services.Docs;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>/docs add</c> never replaces one documentation with another under an id the user did not choose.
/// </summary>
/// <remarks>
/// A source's id is its title as a slug, or the URL's host when there is no title; a title with no
/// Latin letter (Russian, Japanese, Korean, Chinese — languages the product ships) gets the fallback
/// <c>doc</c>. <see cref="DocSite.Upsert"/> replaces any source with the same id, and indexing rewrites
/// its chunks under that id: adding <c>https://docs.example.com/v2</c> after <c>/v1</c>, or a second
/// documentation with a non-Latin title, made the first one disappear, configuration and index
/// included. An explicit Latin title that lands on an existing id is still a deliberate update
/// (<c>DocSiteTests</c>), and re-adding the same URL refreshes it.
/// </remarks>
public class DocSiteIdCollisionTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void ASecondDocumentationOnTheSameHost_GetsItsOwnId()
    {
        var first  = DocSite.Create("https://docs.example.com/v1", title: null);
        var second = DocSite.CreateAmong("https://docs.example.com/v2", title: null, [first]);

        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public void TitlesWithoutLatinLetters_DoNotShareTheFallbackId()
    {
        var first  = DocSite.Create("https://a.example/docs", "Документация");
        var second = DocSite.CreateAmong("https://b.example/docs", "ドキュメント", [first]);

        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public void ReAddingTheSameUrl_KeepsItsIdSoItRefreshes()
    {
        // Witness: re-adding a source already there refreshes it, it does not split in two.
        var first = DocSite.Create("https://docs.example.com/v1", title: null);
        var again = DocSite.CreateAmong("https://docs.example.com/v1", title: null, [first]);

        Assert.Equal(first.Id, again.Id);
    }

    [Fact]
    public void AnExplicitTitle_StillUpdatesTheSourceItNames()
    {
        // Witness: a Latin title chosen by the user names the source to update.
        var first   = DocSite.Create("https://a.dev/v1", "Tokio");
        var updated = DocSite.CreateAmong("https://a.dev/v2", "Tokio", [first]);

        Assert.Equal(first.Id, updated.Id);
    }

    [Fact]
    public void DocsAdd_GivesTheNewSourceAFreeId()
    {
        var handler = ConventionCoverageTests.CodeOnly(
            Path.Combine(RepoRoot(), "Inferpal.Core", "Services", "Commands", "DocsCommandHandler.cs"));

        // Witness: adding still goes through Upsert.
        Assert.Contains("DocSite.Upsert(", handler, StringComparison.Ordinal);

        Assert.Contains("DocSite.CreateAmong(", handler, StringComparison.Ordinal);
    }
}
