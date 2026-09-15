using System.IO;
using System.Text.Json;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A cap that shapes a CONCLUSION says so.
/// </summary>
/// <remarks>
/// <para>
/// The repository writes this rule in several places — <c>ReadFileTool.MaxChars</c> ("silently
/// handing back a prefix would let it conclude a symbol is absent from a file it only read the
/// start of"), <c>ScanCoverage</c>'s remarks — and <c>search_in_files</c> enforced it for the files
/// it skipped by size while staying silent about the one cap that bites every day: it returns at
/// most a hundred matches. A model asking "where is this used?" read exactly a hundred lines as the
/// complete list, and refactored on it.
/// </para>
/// <para>
/// ⚠ These are behavioural tests, not a scan rule: what makes a cap dangerous is whether the reader
/// can mistake the sample for the whole, which no syntactic pattern can tell. The rule lives in the
/// assertions below, one per tool that truncates.
/// </para>
/// </remarks>
public class ToolTruncationTests : IDisposable
{
    private readonly string _ws;

    public ToolTruncationTests()
    {
        _ws = Path.Combine(Path.GetTempPath(), "inferpal-tests", "trunc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_ws);
    }

    public void Dispose() { try { Directory.Delete(_ws, true); } catch { /* best effort */ } }

    private static Task<string> RunAsync(ITool tool, object args) =>
        tool.ExecuteAsync(JsonSerializer.SerializeToElement(args), CancellationToken.None);

    [Fact]
    public async Task SearchInFiles_StoppingAtItsResultCap_SaysTheListIsIncomplete()
    {
        // One file, more matching lines than the cap: the search stops mid-way.
        File.WriteAllLines(Path.Combine(_ws, "many.cs"),
            Enumerable.Range(0, SearchInFilesTool.MaxResults + 20).Select(i => $"var needle{i} = {i};"));

        var result = await RunAsync(new SearchInFilesTool(() => _ws),
                                    new { path = _ws, pattern = "needle", file_pattern = "*.cs" });

        Assert.Equal(SearchInFilesTool.MaxResults, result.Split('\n').Count(l => l.Contains("needle")));
        Assert.Contains("stopped at the first", result);
        Assert.Contains("NOT the complete list", result);
    }

    [Fact]
    public async Task SearchInFiles_UnderItsCap_SaysNothingExtra()
    {
        // Witness: the rule above must read as "the warning appeared", not as "some sentence
        // appeared". A search that saw everything stays silent.
        File.WriteAllLines(Path.Combine(_ws, "few.cs"), ["var needle0 = 0;", "var needle1 = 1;"]);

        var result = await RunAsync(new SearchInFilesTool(() => _ws),
                                    new { path = _ws, pattern = "needle", file_pattern = "*.cs" });

        Assert.Equal(2, result.Split('\n').Count(l => l.Contains("needle")));
        Assert.DoesNotContain("stopped at the first", result);
        Assert.DoesNotContain("could not be read", result);
    }

    [Fact]
    public async Task ListFiles_StoppingAtItsCap_SaysSo()
    {
        // The sibling that already did it right, kept honest here so the pair cannot drift apart:
        // two tools that truncate, one vocabulary.
        for (var i = 0; i < 320; i++)
            File.WriteAllText(Path.Combine(_ws, $"f{i:000}.txt"), "x");

        var result = await RunAsync(new ListFilesTool(() => _ws), new { path = _ws, pattern = "*.txt" });

        Assert.Contains("showing first", result);
    }
}
