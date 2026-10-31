using Inferpal.Models;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

// ModelCatalog.FormatVramBadge — the header's VRAM figure: what the loaded models occupy together, or nothing.
// Never the models' names: the header read "VRAM text-embedding-…" on a server that reports no size.
public class VramBadgeTests
{
    private static RunningModelInfo Model(string name, long vram) =>
        new(name, vram, ExpiresAt: "");

    private const long TwoGb = 2L * 1_073_741_824;

    [Fact]
    public void Empty_YieldsEmptyString() =>
        Assert.Equal("", ModelCatalog.FormatVramBadge([]));

    [Fact]
    public void OneModel_ShowsItsSizeWithOneDecimal()
    {
        var badge = ModelCatalog.FormatVramBadge([Model("llama3.1:8b", TwoGb)]);
        Assert.Equal("2.0 GB VRAM", badge);
    }

    [Fact]
    public void SeveralModels_AreAddedUp_TheOnesOnTheCpuAddNothing()
    {
        var badge = ModelCatalog.FormatVramBadge([
            Model("qwen2.5:7b", TwoGb),
            Model("nomic-embed", 0),
            Model("granite4:8b", TwoGb),
        ]);
        Assert.Equal("4.0 GB VRAM", badge);
    }

    [Fact]
    public void AServerThatReportsNoSize_HasNoBadge_NeverTheModelsNames()
    {
        // LM Studio lists what is loaded without saying what it occupies.
        var badge = ModelCatalog.FormatVramBadge([
            Model("qwen/qwen3.8-27b", RunningModelInfo.VramNotReported),
            Model("text-embedding-qwen3-embedding-0.6b", RunningModelInfo.VramNotReported),
        ]);
        Assert.Equal("", badge);
    }

    [Fact]
    public void OneSizeUnknown_HasNoBadge_NeverAPartShownAsTheWhole()
    {
        var badge = ModelCatalog.FormatVramBadge([
            Model("qwen2.5:7b", TwoGb),
            Model("text-embedding", RunningModelInfo.VramNotReported),
        ]);
        Assert.Equal("", badge);
    }

    [Fact]
    public void OnlyCpuModels_HaveNoBadge() =>
        Assert.Equal("", ModelCatalog.FormatVramBadge([Model("nomic-embed", 0)]));
}
