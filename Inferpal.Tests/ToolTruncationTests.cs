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

    // ── "Nothing matched" and "I could not look" are not the same answer ─────────
    //
    // WorkspaceScan.EnumerateFiles used to end on `catch { return []; }` — muted, against this
    // repository's own rule that a silent catch is for pure cleanup only. Every scanning tool
    // inherited the confusion, and the two that turn an empty walk into a SENTENCE turned it into
    // the wrong one: search_in_files said "no match" (a conclusion the model acts on: it stops
    // looking) and list_files blamed a directory that exists.
    //
    // The walk is lazy, so the catch only fires while CONSTRUCTING it — an invalid pattern, a start
    // directory the file system refuses. Both tools take their path from the MODEL, which is what
    // makes this reachable.

    [Fact]
    public void WorkspaceScan_AWalkThatCannotStart_SaysSoInsteadOfAnsweringEmpty()
    {
        var files = Inferpal.Services.WorkspaceScan
            .EnumerateFiles(Path.Combine(_ws, "no-such-directory"), "*.cs", out var failed)
            .ToList();

        Assert.True(failed, "a walk that cannot start must say so, not answer an empty list.");
        Assert.Empty(files);
    }

    [Fact]
    public void WorkspaceScan_AWalkThatSimplyMatchesNothing_IsNotAFailure()
    {
        // Witness: the flag must mean "could not look", not "found nothing". An existing directory
        // with no match is an ordinary empty answer.
        var files = Inferpal.Services.WorkspaceScan
            .EnumerateFiles(_ws, "*.nothing-matches-this", out var failed)
            .ToList();

        Assert.False(failed);
        Assert.Empty(files);
    }

    // ⚠ The two TOOL branches that read this flag are deliberately not tested here, and the reason
    // is measured rather than assumed: both search_in_files and list_files pre-validate their
    // pattern (WorkspaceScan.NormalizeFilePattern) and check Directory.Exists, so the only way left
    // for their walk to fail is a file system refusing an existing, readable-looking directory — a
    // dropped network share, a permission change mid-run. No portable test forces that. Those
    // branches are defensive; what the two tests above hold is the funnel that feeds them, which is
    // where the answer used to be invented.
}
