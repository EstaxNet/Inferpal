using System.IO;
using System.Text;
using System.Text.Json;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>read_file</c> pages a file longer than its 2,000,000-character ceiling by its REAL lines: the total it states is
/// the file's, and a line past the ceiling can be read.
/// </summary>
/// <remarks>
/// The file was cut at the ceiling first and paged after: the footer gave the prefix's line count as the file's, a line
/// past it — one <c>search_in_files</c>, which reads up to 8 MB, had just reported — was "past the end", and the last page
/// said "the end of the file" right under the cut marker. A 3 MB log, CSV or JSON is ordinary.
/// </remarks>
public sealed class ReadFileBeyondCapTests : IDisposable
{
    private readonly string _root =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"inferpal-readcap-{Guid.NewGuid():N}")).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private const int LineCount = 60_000;   // 60,000 lines of 36 characters: 2.16 M, past the ceiling

    private string BigFile()
    {
        var sb = new StringBuilder();
        for (var i = 1; i <= LineCount; i++) sb.Append($"line {i:000000} of the big log file....\n");
        Assert.True(sb.Length > ReadFileTool.MaxChars, "witness: the file must be past the ceiling");
        File.WriteAllText(Path.Combine(_root, "big.log"), sb.ToString());
        return "big.log";
    }

    private Task<string> Read(object args) =>
        new ReadFileTool(() => _root).ExecuteAsync(JsonSerializer.SerializeToElement(args), CancellationToken.None);

    [Fact]
    public async Task TheFirstPage_StatesTheFilesRealLineCount()
    {
        var page = await Read(new { path = BigFile() });

        Assert.StartsWith("line 000001 ", page);
        Assert.Contains($"of {LineCount} shown — call read_file with start_line=", page);
    }

    [Fact]
    public async Task ALinePastTheCeiling_IsRead()
    {
        var page = await Read(new { path = BigFile(), start_line = 58_000 });   // the cut falls near line 55,555

        Assert.StartsWith("line 058000 ", page);
        Assert.DoesNotContain("past the end", page);
    }

    [Fact]
    public async Task TheLastPage_IsTheEndOfTheFile_WithNoCutMarker()
    {
        var page = await Read(new { path = BigFile(), start_line = LineCount - 2 });

        Assert.Contains($"line {LineCount:000000} ", page);
        Assert.Contains($"lines {LineCount - 2}–{LineCount} of {LineCount} — the end of the file", page);
        Assert.DoesNotContain("are shown", page);
    }

    [Fact]
    public async Task ARangePastTheCeiling_IsCut_AndSaysHowToNarrowIt()
    {
        var read = await Read(new { path = BigFile(), start_line = 1, end_line = LineCount });

        Assert.Contains($"the first {ReadFileTool.MaxChars} are shown. Ask for a narrower start_line/end_line range", read);
    }
}
