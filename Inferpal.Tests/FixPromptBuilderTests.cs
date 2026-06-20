using System;
using System.Collections.Generic;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

// Covers the pure build-fix formatting/parsing extracted from the tool-window VM:
// the error-path extraction from compiler diagnostics, the enriched fix prompt with
// per-file caps, and the one-line "Build Failed" banner preview. The file reader is
// injected; real file access stays in the VM and is not tested here.
public class FixPromptBuilderTests
{
    private const string TwoErrors =
        "C:\\proj\\Foo.cs(12,5): error CS0001: something broke\n" +
        "C:\\proj\\Bar.cs(3,1): warning CS9999: meh\n" +
        "C:\\proj\\Foo.cs(40,2): error CS0002: also broke";

    // ── ExtractErrorPaths ──────────────────────────────────────────────────────

    [Fact]
    public void ExtractErrorPaths_DistinctInFirstAppearanceOrder()
    {
        var paths = FixPromptBuilder.ExtractErrorPaths(TwoErrors);
        Assert.Equal(["C:\\proj\\Foo.cs", "C:\\proj\\Bar.cs"], paths);
    }

    [Fact]
    public void ExtractErrorPaths_DeduplicatesCaseInsensitively()
    {
        var output = "C:\\p\\A.cs(1,1): error CS1: x\nC:\\p\\a.cs(2,2): error CS2: y";
        Assert.Single(FixPromptBuilder.ExtractErrorPaths(output));
    }

    [Fact]
    public void ExtractErrorPaths_CapsAtFiveFiles()
    {
        var lines = new List<string>();
        for (var i = 0; i < 8; i++)
            lines.Add($"C:\\p\\File{i}.cs(1,1): error CS1: x");
        var paths = FixPromptBuilder.ExtractErrorPaths(string.Join('\n', lines));
        Assert.Equal(5, paths.Count);
    }

    [Fact]
    public void ExtractErrorPaths_IgnoresNonDiagnosticLines()
    {
        var output = "Build started\nRestore complete\nsomething.cs without location";
        Assert.Empty(FixPromptBuilder.ExtractErrorPaths(output));
    }

    // ── Build ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Build_AppendsReadableFilesAsFencedBlocks()
    {
        var prompt = FixPromptBuilder.Build(TwoErrors,
            path => path.EndsWith("Foo.cs") ? "class Foo {}" : null);

        Assert.Contains("something broke", prompt);     // raw errors embedded
        Assert.Contains("Affected files:", prompt);
        Assert.Contains("### C:\\proj\\Foo.cs", prompt);
        Assert.Contains("class Foo {}", prompt);
        Assert.DoesNotContain("Bar.cs\n```", prompt);   // unreadable file skipped
    }

    [Fact]
    public void Build_OmitsAffectedFilesSection_WhenNothingReadable()
    {
        var prompt = FixPromptBuilder.Build(TwoErrors, _ => null);
        Assert.DoesNotContain("Affected files:", prompt);
    }

    [Fact]
    public void Build_TruncatesLongFileContent()
    {
        var prompt = FixPromptBuilder.Build(
            "C:\\p\\Big.cs(1,1): error CS1: x",
            _ => new string('x', 5000));
        Assert.Contains("…(truncated)", prompt);
        Assert.DoesNotContain(new string('x', 4001), prompt);
    }

    // ── FirstErrorLine ─────────────────────────────────────────────────────────

    [Fact]
    public void FirstErrorLine_PicksFirstNonBlankTrimmedLine() =>
        Assert.Equal("error CS1: x",
            FixPromptBuilder.FirstErrorLine("\n   \n  error CS1: x  \nerror CS2: y"));

    [Fact]
    public void FirstErrorLine_EmptyInput_GivesEmptyString() =>
        Assert.Equal(string.Empty, FixPromptBuilder.FirstErrorLine("   \n  "));

    [Fact]
    public void FirstErrorLine_CapsWidthWithEllipsis()
    {
        var line = FixPromptBuilder.FirstErrorLine(new string('e', 200));
        Assert.Equal(118, line.Length); // 117 chars + ellipsis, as the banner expects
        Assert.EndsWith("…", line);
    }

    [Fact]
    public void FirstErrorLine_LeavesShortLinesUntouched() =>
        Assert.Equal("short", FixPromptBuilder.FirstErrorLine("short"));
}
