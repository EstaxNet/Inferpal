using Inferpal.Services.Docs;
using Xunit;

namespace Inferpal.Tests;

public class DocSiteTests
{
    private static DocSite Site(string id) => new(id, id.ToUpperInvariant(), $"https://{id}.dev/docs");

    // ── Create / Parse ──────────────────────────────────────────────────────────

    [Fact]
    public void Create_NoTitle_UsesHostAsLabelAndSlugAsId()
    {
        var site = DocSite.Create("https://learn.microsoft.com/dotnet", title: null);

        Assert.Equal("learn.microsoft.com", site.Title);
        Assert.Equal("learn-microsoft-com", site.Id);
    }

    [Fact]
    public void Create_WithTitle_SlugifiesIt()
    {
        var site = DocSite.Create("https://docs.rs/tokio", ".NET & Tokio Docs!");

        Assert.Equal(".NET & Tokio Docs!", site.Title);
        Assert.Equal("net-tokio-docs", site.Id);
    }

    [Fact]
    public void Parse_MalformedJson_ReturnsEmpty()
    {
        Assert.Empty(DocSite.Parse("{ nope"));
        Assert.Empty(DocSite.Parse(null));
    }

    // ── IsValidHttpUrl ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("https://docs.python.org", true)]
    [InlineData("http://localhost:8080/docs", true)]
    [InlineData("ftp://docs.python.org", false)]
    [InlineData("file:///C:/docs/index.html", false)]
    [InlineData("docs.python.org", false)]        // relative — no scheme
    [InlineData("not a url", false)]
    public void IsValidHttpUrl_AcceptsOnlyAbsoluteHttp(string url, bool expected)
    {
        Assert.Equal(expected, DocSite.IsValidHttpUrl(url));
    }

    // ── Upsert / Remove ─────────────────────────────────────────────────────────

    [Fact]
    public void Upsert_NewId_Appends()
    {
        var updated = DocSite.Upsert([Site("a")], Site("b"));

        Assert.Equal(["a", "b"], updated.Select(s => s.Id));
    }

    [Fact]
    public void Upsert_ExistingId_ReplacesInsteadOfDuplicating()
    {
        var replacement = Site("a") with { StartUrl = "https://a.dev/v2" };

        var updated = DocSite.Upsert([Site("a"), Site("b")], replacement);

        Assert.Equal(2, updated.Count);
        Assert.Equal("https://a.dev/v2", updated.Single(s => s.Id == "a").StartUrl);
    }

    [Fact]
    public void Remove_KnownId_FiltersItOut()
    {
        var updated = DocSite.Remove([Site("a"), Site("b")], "a");

        Assert.NotNull(updated);
        Assert.Equal(["b"], updated.Select(s => s.Id));
    }

    [Fact]
    public void Remove_UnknownId_ReturnsNull()
    {
        Assert.Null(DocSite.Remove([Site("a")], "zzz"));
    }

    // ── FormatList ──────────────────────────────────────────────────────────────

    [Fact]
    public void FormatList_ShowsStatsAndZeroFallbackForUnindexedSites()
    {
        var listing = DocSite.FormatList(
            [Site("a"), Site("b")],
            new Dictionary<string, (int, int)> { ["a"] = (12, 340) });

        Assert.Contains("- **a** — A (12 pages, 340 chunks) · https://a.dev/docs", listing);
        Assert.Contains("- **b** — B (0 pages, 0 chunks) · https://b.dev/docs", listing);
    }
}
