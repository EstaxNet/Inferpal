using System;
using System.Collections.Generic;
using System.Text;
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
    public void Build_CutsALineLongerThanTheBudget_AndSaysHowMuch()
    {
        var prompt = FixPromptBuilder.Build(
            "C:\\p\\Big.cs(1,1): error CS1: x",
            _ => new string('x', 5000));
        Assert.Contains("of 5000 characters", prompt);
        Assert.DoesNotContain(new string('x', 4001), prompt);
    }

    // ── What is shown of a file that does not fit ──────────────────────────────

    // ⚠ The first 4,000 characters are not "the file": measured on this repository, half of the C#
    // files are longer and 56 % of all source lines sit past that cut — so for one diagnostic in two,
    // the line in error was not in what the model read, under a bare "…(truncated)" that did not say
    // so. The chat's Fix button can send the prompt with no tool to read further: the model guessed.

    private static string LongFile(int lines)
    {
        var sb = new StringBuilder("using System;\nusing System.Linq;\n");
        for (var i = 3; i <= lines; i++) sb.Append($"    var line{i:D4} = Compute({i}); // statement {i}\n");
        return sb.ToString();
    }

    private static string SectionOf(string prompt, string path) =>
        prompt[prompt.IndexOf($"### {path}", StringComparison.Ordinal)..];

    [Fact]
    public void Build_ShowsTheLineInError_WhenItIsPastTheStartOfALongFile()
    {
        var file   = LongFile(600);                                   // ~27,000 characters
        var prompt = FixPromptBuilder.Build("C:\\p\\Big.cs(300,9): error CS0103: The name 'x' does not exist", _ => file);
        var shown  = SectionOf(prompt, "C:\\p\\Big.cs");

        Assert.True(file.Length > 20_000);                            // witness: the file does not fit
        Assert.Contains("var line0300 = ", shown);                    // the line in error
        Assert.Contains("var line0290 = ", shown);                    // and what surrounds it
        Assert.Contains("using System.Linq;", shown);                 // the head: what a missing-type error needs
        Assert.Contains("of 600", shown);                             // the cut is said, with the count
        Assert.DoesNotContain("var line0200 = ", shown);              // far from any diagnostic: left out
        Assert.True(shown.Length < 5_000, $"{shown.Length} characters shown for one file");
    }

    [Fact]
    public void Build_ShowsEveryDiagnosticSiteOfAFile()
    {
        var shown = SectionOf(FixPromptBuilder.Build(
            "C:\\p\\Big.cs(120,1): error CS1: a\nC:\\p\\Big.cs(480,1): error CS2: b", _ => LongFile(600)),
            "C:\\p\\Big.cs");

        Assert.Contains("var line0120 = ", shown);
        Assert.Contains("var line0480 = ", shown);
    }

    [Fact]
    public void Build_SaysWhichDiagnosticLinesDidNotFitTheBudget()
    {
        var errors = string.Join('\n', Enumerable.Range(1, 30).Select(i => $"C:\\p\\Big.cs({i * 60},1): error CS1: e{i}"));
        var shown  = SectionOf(FixPromptBuilder.Build(errors, _ => LongFile(2_000)), "C:\\p\\Big.cs");

        Assert.Contains("var line0060 = ", shown);                    // the first sites are shown...
        Assert.Matches(@"line\(s\) [\d, …]+ not shown", shown);       // ...and the ones left out are named
        Assert.True(shown.Length < 5_000, $"{shown.Length} characters shown for one file");
    }

    [Fact]
    public void Build_AFileThatFits_IsShownWhole_WithNothingAdded()
    {
        // Reference arm: a note under every file is the noise that gets it ignored.
        var file   = LongFile(60);
        var shown  = SectionOf(FixPromptBuilder.Build("C:\\p\\Small.cs(50,1): error CS1: x", _ => file), "C:\\p\\Small.cs");

        Assert.Contains(file.TrimEnd('\n'), shown);
        Assert.DoesNotContain("read_file", shown);
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
