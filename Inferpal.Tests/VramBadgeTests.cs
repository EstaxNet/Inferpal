using Inferpal.Models;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

// ModelCatalog.FormatVramBadge — the compact header badge whose formatting (tag stripping,
// GB rounding, CPU-loaded models, separator) used to be inline in the VM's OnModelsRefreshed.
public class VramBadgeTests
{
    private static RunningModelInfo Model(string name, long vram) =>
        new(name, vram, ExpiresAt: "");

    private const long TwoGb = 2L * 1_073_741_824;

    [Fact]
    public void Empty_YieldsEmptyString() =>
        Assert.Equal("", ModelCatalog.FormatVramBadge([]));

    [Fact]
    public void StripsTagSuffix_AndShowsGbWithOneDecimal()
    {
        var badge = ModelCatalog.FormatVramBadge([Model("llama3.1:8b", TwoGb)]);
        Assert.Equal("llama3.1 · 2.0 GB", badge);
    }

    [Fact]
    public void CpuLoadedModel_ShowsNameWithoutSize()
    {
        var badge = ModelCatalog.FormatVramBadge([Model("nomic-embed", 0)]);
        Assert.Equal("nomic-embed", badge);
    }

    [Fact]
    public void MultipleModels_JoinedWithSeparator()
    {
        var badge = ModelCatalog.FormatVramBadge([
            Model("qwen2.5:7b", TwoGb),
            Model("nomic-embed", 0),
        ]);
        Assert.Equal("qwen2.5 · 2.0 GB │ nomic-embed", badge);
    }
}
