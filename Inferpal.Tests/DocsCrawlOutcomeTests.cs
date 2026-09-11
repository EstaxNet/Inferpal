using Inferpal.Services.Docs;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// What a `/docs index` ACTUALLY got - the sentence the user reads.
/// </summary>
/// <remarks>
/// An address refused by the SSRF guard produced "no readable pages found", the message of an
/// empty site: on a 100 % local product, pointing @Docs at a local documentation server is the
/// most likely case, and the user went off to check a site that works fine in their
/// browser. And the page cap was not stated, so @Docs answered "not in the documentation"
/// about half a site believed to be indexed in full.
/// </remarks>
public class DocsCrawlOutcomeTests
{
    [Fact]
    public void ARefusedAddress_SaysSo_InsteadOfLookingLikeAnEmptySite()
    {
        var refused = DocsIndexService.DescribeCrawlOutcome("http://localhost:8000/docs", 0, refused: true);
        var empty   = DocsIndexService.DescribeCrawlOutcome("https://example.com/docs", 0, refused: false);

        Assert.NotEqual(refused, empty);
        Assert.Contains("refused", refused, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("localhost:8000", refused);   // witness: it names the address at fault
        Assert.DoesNotContain("refused", empty, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheCrawlLimit_IsStatedWhenItIsReached_AndNotBefore()
    {
        var capped = DocsIndexService.DescribeCrawlOutcome("https://example.com", DocCrawler.MaxPages, refused: false);
        var under  = DocsIndexService.DescribeCrawlOutcome("https://example.com", DocCrawler.MaxPages - 1, refused: false);

        Assert.Contains("limit", capped, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("limit", under, System.StringComparison.OrdinalIgnoreCase);
        // Witness: both do state the page count - the cap is not the only content.
        Assert.Contains(DocCrawler.MaxPages.ToString(), capped);
        Assert.Contains((DocCrawler.MaxPages - 1).ToString(), under);
    }
}
