using System.IO;
using System.Text.Json;
using Inferpal.Services.Agent;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>list_files</c> lists up to 300 paths and <c>search_in_files</c> up to 100 matches — both well over the loop's
/// context cap, which cuts a long result in its MIDDLE. Files and matches vanished from the middle of a listing still
/// announced as "the first 300 files", and the model concluded they did not exist or did not use the symbol. A
/// listing stops where it fits and says what it did not show.
/// </summary>
public sealed class ListingFitsTheContextTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"listing-{Guid.NewGuid():N}");

    public ListingFitsTheContextTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static JsonElement Args(object o) => JsonDocument.Parse(JsonSerializer.Serialize(o)).RootElement;

    [Fact]
    public async Task ALongFileListing_ReachesTheModelWhole_AndSaysWhatItDidNotShow()
    {
        var dir = Path.Combine(_root, "src", "Features", "Orders", "Handlers");
        Directory.CreateDirectory(dir);
        for (var i = 0; i < 250; i++)
            File.WriteAllText(Path.Combine(dir, $"PlaceOrderCommandHandlerForRegion{i:D3}.cs"), "// x\n");

        var listing = await new ListFilesTool(() => _root).ExecuteAsync(Args(new { path = _root }), CancellationToken.None);

        Assert.Contains("PlaceOrderCommandHandlerForRegion000.cs", listing);                           // witness
        Assert.Matches(@"\(showing the first \d+ of 250 files — the rest would not fit the context", listing);
        Assert.Equal(listing, AgentOrchestrator.CapForContext(listing));
    }

    [Fact]
    public async Task ManyMatches_ReachTheModelWhole_AndNameTheFilesOfThoseNotShown()
    {
        for (var f = 0; f < 10; f++)
        {
            var body = string.Concat(Enumerable.Range(0, 10).Select(i =>
                $"var total{i} = Compute(\"a fairly long argument that makes this match line wide, number {i}\");\n"));
            File.WriteAllText(Path.Combine(_root, $"File{f}.cs"), body);
        }

        var answer = await new SearchInFilesTool(() => _root)
            .ExecuteAsync(Args(new { path = _root, pattern = "Compute" }), CancellationToken.None);

        Assert.Contains("File0.cs:1:", answer);                                                         // witness
        Assert.Matches(@"\(showing the first \d+ of 100 match\(es\) found — the rest would not fit the context; they are in: [^)]*File9\.cs", answer);
        Assert.Equal(answer, AgentOrchestrator.CapForContext(answer));
    }

    [Fact]
    public async Task AShortListing_SaysNothingAboutTheContext()
    {
        File.WriteAllText(Path.Combine(_root, "a.cs"), "var x = Compute(1);\n");

        var listing = await new ListFilesTool(() => _root).ExecuteAsync(Args(new { path = _root }), CancellationToken.None);
        var answer  = await new SearchInFilesTool(() => _root)
            .ExecuteAsync(Args(new { path = _root, pattern = "Compute" }), CancellationToken.None);

        Assert.Equal("a.cs", listing);
        Assert.Equal("a.cs:1: var x = Compute(1);", answer);
    }
}
