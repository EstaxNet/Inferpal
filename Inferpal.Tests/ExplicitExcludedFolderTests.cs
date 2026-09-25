using System.IO;
using System.Text.Json;
using Inferpal.Localization;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The walk's exclusions (<c>bin/</c>, <c>obj/</c>, <c>node_modules/</c>…) exist to keep build output and third-party
/// code out of a walk started ABOVE them. They were judged on the path below the workspace root, so a folder the model
/// named itself — <c>node_modules/left-pad</c> to read a library, <c>bin/Debug</c> to check an output — came back
/// empty from <c>list_files</c> and "no results" from <c>search_in_files</c>: "this folder is empty", about one that
/// is not. And an empty listing was an empty STRING, a tool result that says nothing at all.
/// </summary>
[Collection(CultureSerialCollection.Name)]   // compares the localized "no results"
public sealed class ExplicitExcludedFolderTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("inferpal-excluded-").FullName;

    public ExplicitExcludedFolderTests()
    {
        Write("src/a.txt", "x");
        Write("bin/Debug/net8.0/app.dll.config", "x");
        Write("node_modules/left-pad/lib/index.js", "module.exports = leftPad;");
        Write("node_modules/left-pad/node_modules/inner/i.js", "nested dependency");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private void Write(string relative, string text)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private Task<string> ListAsync(string relative, string pattern = "*") =>
        new ListFilesTool(() => _root).ExecuteAsync(
            JsonDocument.Parse(JsonSerializer.Serialize(new { path = Path.Combine(_root, relative), pattern })).RootElement,
            CancellationToken.None);

    [Theory]
    [InlineData("bin/Debug", "app.dll.config")]
    [InlineData("node_modules/left-pad", "index.js")]
    public async Task AFolderNamedExplicitly_IsListed_EvenUnderAnExcludedOne(string folder, string file)
    {
        Assert.Contains(file, await ListAsync(folder));
    }

    [Fact]
    public async Task AFolderNamedExplicitly_IsSearched_EvenUnderAnExcludedOne()
    {
        var report = await new SearchInFilesTool(() => _root).ExecuteAsync(
            JsonDocument.Parse(JsonSerializer.Serialize(new { path = Path.Combine(_root, "node_modules", "left-pad"), pattern = "leftPad" }))
                .RootElement, CancellationToken.None);

        Assert.Contains("index.js", report);
    }

    [Fact]
    public async Task AWalkFromAbove_StillLeavesBuildOutputAndDependenciesOut()
    {
        // Reference arm: the exclusions still do their job below the start — from the root, and inside a named
        // dependency (its own node_modules).
        var fromRoot = await ListAsync(".");
        Assert.Contains("a.txt", fromRoot);                                                          // witness
        Assert.DoesNotContain("app.dll.config", fromRoot);
        Assert.DoesNotContain("index.js", fromRoot);

        Assert.DoesNotContain("i.js", await ListAsync("node_modules/left-pad"));
    }

    [Fact]
    public async Task AnEmptyListing_SaysSo()
    {
        Assert.StartsWith(Strings.NoResults, await ListAsync("src", "*.cs"));
    }
}
