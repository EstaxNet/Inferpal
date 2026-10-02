using System.IO;
using System.Text.Json;
using Inferpal.Localization;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ No file searched is not "no match". A <c>file_pattern</c> for a language the project does not hold — <c>*.cs</c>
/// in a JavaScript project, the slip a model makes after a C# task — selects no file, and <c>search_in_files</c>
/// answered "No results", read as "the text is not in the code". It now says nothing was searched, and why.
/// </summary>
[Collection(CultureSerialCollection.Name)]   // compares the localized "no results"
public sealed class SearchFilterSelectsNothingTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("inferpal-search-filter-").FullName;

    public SearchFilterSelectsNothingTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "src"));
        File.WriteAllText(Path.Combine(_dir, "src", "pricing.js"),
            "function applyDiscount(price, percent) { return price - price * percent / 10; }\nmodule.exports = { applyDiscount };\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private Task<string> SearchAsync(object args) =>
        new SearchInFilesTool(() => _dir).ExecuteAsync(JsonSerializer.SerializeToElement(args), CancellationToken.None);

    [Fact]
    public async Task AFilterForALanguageTheProjectDoesNotHold_SaysNothingWasSearched()
    {
        var report = await SearchAsync(new { path = _dir, pattern = "applyDiscount", file_pattern = "*.cs" });

        Assert.DoesNotContain(Strings.NoResults, report, StringComparison.Ordinal);
        Assert.Contains("matches file_pattern '*.cs'", report, StringComparison.Ordinal);
        Assert.Contains("nothing was searched", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSameSearchWithoutTheFilter_FindsTheText()
    {
        // Witness: the text the filtered search could not reach is there.
        Assert.Contains("pricing.js:1:", await SearchAsync(new { path = _dir, pattern = "applyDiscount" }));
    }

    [Fact]
    public async Task AFilterThatSelectsFiles_AndATextThatIsNotThere_IsStillNoResults()
    {
        // Reference arm: files were searched and the text is absent — the ordinary answer, unchanged.
        var report = await SearchAsync(new { path = _dir, pattern = "computeTotal", file_pattern = "*.js" });

        Assert.StartsWith(Strings.NoResults, report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFolderWithNoFile_SaysSo()
    {
        var empty = Directory.CreateDirectory(Path.Combine(_dir, "empty")).FullName;

        var report = await SearchAsync(new { path = empty, pattern = "applyDiscount" });

        Assert.DoesNotContain(Strings.NoResults, report, StringComparison.Ordinal);
        Assert.Contains("No file was found", report, StringComparison.Ordinal);
    }
}
