using System.IO;
using System.Text.Json;
using Inferpal.Services.Documents;
using Inferpal.Services.Execution;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A patch reads as its files and blocks, each judged against the workspace, and applies through apply_edits (pari 157):
/// each block where its lines stand, new files created, on the approval card; what does not apply is named, never forced.
/// </summary>
public sealed class PatchTextTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("inferpal-patch-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // What `git format-patch` writes: mail headers, then one diff per file.
    private const string FormatPatch = """
        From 3f2a1c0d9e8b7a6f5e4d3c2b1a0f9e8d7c6b5a49 Mon Sep 17 00:00:00 2001
        From: Jane Doe <jane@example.org>
        Date: Thu, 20 Nov 2026 10:00:00 +0100
        Subject: [PATCH] Round totals to the cent
         before tax

        ---
         src/Totals.cs   | 4 ++--
         src/Rounding.cs | 3 +++
         2 files changed

        diff --git a/src/Totals.cs b/src/Totals.cs
        index 1111111..2222222 100644
        --- a/src/Totals.cs
        +++ b/src/Totals.cs
        @@ -1,6 +1,6 @@
         class Totals
         {
        -    decimal Sum(decimal a, decimal b) => a + b;
        +    decimal Sum(decimal a, decimal b) => Rounding.Cent(a + b);
             decimal Tax(decimal a) => a * 0.2m;
        -    // TODO rounding
        +    // rounded to the cent
         }
        diff --git a/src/Rounding.cs b/src/Rounding.cs
        new file mode 100644
        index 0000000..3333333
        --- /dev/null
        +++ b/src/Rounding.cs
        @@ -0,0 +1,3 @@
        +static class Rounding
        +{
        +    public static decimal Cent(decimal v) => decimal.Round(v, 2); }
        diff --git a/old/Legacy.cs b/old/Legacy.cs
        deleted file mode 100644
        index 4444444..0000000
        --- a/old/Legacy.cs
        +++ /dev/null
        @@ -1 +0,0 @@
        -class Legacy { }
        diff --git a/logo.png b/logo.png
        index 5555555..6666666 100644
        Binary files a/logo.png and b/logo.png differ
        --
        2.47.0
        """;

    private const string Totals = """
        class Totals
        {
            decimal Sum(decimal a, decimal b) => a + b;
            decimal Tax(decimal a) => a * 0.2m;
            // TODO rounding
        }

        """;

    private string Write(string relative, string text)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void AFormatPatch_ReadsItsCommitAndEachFileForWhatItIs()
    {
        var patch = PatchText.Parse(FormatPatch)!;

        Assert.Equal(["Round totals to the cent before tax"], patch.Subjects);
        Assert.Equal("Jane Doe <jane@example.org>", patch.Author);
        Assert.Equal([PatchText.FileKind.Modify, PatchText.FileKind.Add, PatchText.FileKind.Delete, PatchText.FileKind.Binary],
                     patch.Files.Select(f => f.Kind));
        Assert.Equal(["src/Totals.cs", "src/Rounding.cs", "old/Legacy.cs", "logo.png"], patch.Files.Select(f => f.Path));
        Assert.Equal((2, 2), (patch.Files[0].Hunks[0].Added, patch.Files[0].Hunks[0].Removed));
    }

    [Fact]
    public void ABlock_IsPlacedAtItsLine_OrWhereItsLinesMoved_AndNeverWhereTheyAreNot()
    {
        var file = PatchText.Parse(FormatPatch)!.Files[0];

        var (atLine, exact) = PatchText.Apply(Totals, file);
        // The file's own line ends: this source file (and so Totals) is CRLF where git checks it out so.
        var nl = Totals.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var (shiftedText, shifted) = PatchText.Apply("// header" + nl + "// two" + nl + Totals, file);
        var (_, gone) = PatchText.Apply(Totals.Replace("a * 0.2m", "a * 0.21m", StringComparison.Ordinal), file);
        var (_, again) = PatchText.Apply(atLine, file);

        Assert.Contains("Rounding.Cent(a + b)", atLine, StringComparison.Ordinal);
        // The file's own line ends: this source file (and so Totals) is CRLF where git checks it out so.
        Assert.EndsWith(Totals.EndsWith("\r\n", StringComparison.Ordinal) ? "}\r\n" : "}\n", atLine, StringComparison.Ordinal);
        Assert.Equal(PatchText.Outcome.Applied, exact[0].Outcome);
        Assert.Equal((PatchText.Outcome.Shifted, 2), (shifted[0].Outcome, shifted[0].Offset));
        Assert.StartsWith("// header" + nl + "// two" + nl + "class Totals", shiftedText, StringComparison.Ordinal);
        Assert.Equal(PatchText.Outcome.Rejected, gone[0].Outcome);
        Assert.Equal(PatchText.Outcome.AlreadyApplied, again[0].Outcome);
    }

    [Fact]
    public void AFile_KeepsItsLineBreaks_AndTheEndThePatchGivesIt()
    {
        var file = PatchText.Parse(FormatPatch)!.Files[0];
        var noEol = PatchText.Parse("""
            --- a/x.txt
            +++ b/x.txt
            @@ -1,2 +1,2 @@
             one
            -two
            +three
            \ No newline at end of file
            """)!.Files[0];

        var crlf = PatchText.Apply(Totals.ReplaceLineEndings("\r\n"), file).Content;
        var cut = PatchText.Apply("one\ntwo\n", noEol).Content;

        Assert.DoesNotContain("\n", crlf.Replace("\r\n", "", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Equal("one\nthree", cut);
    }

    [Fact]
    public void Reading_SaysWhatEachBlockDoesInTheWorkspace_ThenGivesTheText()
    {
        Write("src/Totals.cs", "// moved down\n" + Totals);
        Write("src/Rounding.cs", "static class Rounding { }\n");

        var text = PatchText.Summarize("fix.patch", FormatPatch, 20_000, PatchText.Workspace(_root, overlay: null))!;

        Assert.StartsWith("Patch: 4 files, +5 −3; 1 commit: \"Round totals to the cent before tax\", by Jane Doe", text, StringComparison.Ordinal);
        Assert.Contains("M src/Totals.cs  +2 −2, 1 block — in the workspace: block 1 (line 1) applies 1 line lower", text, StringComparison.Ordinal);
        Assert.Contains("A src/Rounding.cs  +3 −0, 1 block (new file) — already exists in the workspace", text, StringComparison.Ordinal);
        Assert.Contains("D old/Legacy.cs", text, StringComparison.Ordinal);
        Assert.Contains("B logo.png", text, StringComparison.Ordinal);
        Assert.Contains("To apply it: apply_edits with patch", text, StringComparison.Ordinal);
        Assert.Contains("diff --git a/src/Totals.cs b/src/Totals.cs", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("notes.txt", FormatPatch)]
    [InlineData("readme.diff", "No diff in here, only words.")]
    public void AnyOtherFile_ReadsAsItsLines(string name, string text) =>
        Assert.Null(PatchText.Summarize(name, text, 20_000, resolve: null));

    [Theory]
    [InlineData("../outside.cs")]
    [InlineData("src/../../outside.cs")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("/etc/passwd")]
    public void APatchPath_OutsideTheWorkspace_IsRefusedUnread(string path)
    {
        var file = new PatchText.PatchFile(path, path, PatchText.FileKind.Modify, [], GitPrefixed: false);

        var (found, why) = PatchText.Locate(file, _root);

        Assert.Null(found);
        Assert.Contains("is not a path inside the workspace", why, StringComparison.Ordinal);
    }

    [Theory]
    // Lines added at the top of a file, then at its end: once applied, the old lines are still there, beside the new.
    [InlineData("--- a/x.cs\n+++ b/x.cs\n@@ -1,3 +1,4 @@\n+using X;\n alpha();\n beta();\n gamma();\n", "alpha();\nbeta();\ngamma();\n")]
    [InlineData("--- a/x.cs\n+++ b/x.cs\n@@ -1,3 +1,4 @@\n alpha();\n beta();\n gamma();\n+delta();\n", "alpha();\nbeta();\ngamma();\n")]
    public void ThePatchAppliedTwice_ChangesNothingTheSecondTime(string patchText, string file)
    {
        var patch = PatchText.Parse(patchText)!.Files[0];

        var once = PatchText.Apply(file, patch).Content;
        var (twice, results) = PatchText.Apply(once, patch);

        Assert.NotEqual(file, once);
        Assert.Equal(once, twice);
        Assert.Equal(PatchText.Outcome.AlreadyApplied, results[0].Outcome);
    }

    [Fact]
    public void ABlockWithoutContext_IsPlacedOnlyAtItsLine_NeverShifted()
    {
        // `git diff -U0`: one line inserted after line 2, nothing to recognise its place by.
        var patch = PatchText.Parse("--- a/x.txt\n+++ b/x.txt\n@@ -2,0 +3 @@\n+inserted\n")!.Files[0];

        var (atItsLine, placed) = PatchText.Apply("one\ntwo\nthree\n", patch);
        var (_, pastTheEnd) = PatchText.Apply("one\n", patch);
        var (_, again) = PatchText.Apply(atItsLine, patch);

        Assert.Equal("one\ntwo\ninserted\nthree\n", atItsLine);
        Assert.Equal(PatchText.Outcome.Applied, placed[0].Outcome);
        Assert.Equal(PatchText.Outcome.Rejected, pastTheEnd[0].Outcome);
        Assert.Equal(PatchText.Outcome.AlreadyApplied, again[0].Outcome);
    }

    [Fact]
    public void ContextLines_KeepTheFilesOwnText()
    {
        var patch = PatchText.Parse("--- a/x.txt\n+++ b/x.txt\n@@ -1,3 +1,3 @@\n first line\n-second line\n+changed line\n third line\n")!.Files[0];

        var content = PatchText.Apply("first line   \nsecond line\nthird line\n", patch).Content;

        Assert.Equal("first line   \nchanged line\nthird line\n", content);
    }

    [Fact]
    public void AGitCopy_IsNeverAnEditOfItsSource()
    {
        var patch = PatchText.Parse("""
            diff --git a/src/A.cs b/src/B.cs
            similarity index 90%
            copy from src/A.cs
            copy to src/B.cs
            --- a/src/A.cs
            +++ b/src/B.cs
            @@ -1 +1 @@
            -class A { }
            +class B { }
            """)!;

        Assert.Equal(PatchText.FileKind.Copy, patch.Files[0].Kind);
        Assert.Equal(("src/A.cs", "src/B.cs"), (patch.Files[0].OldPath, patch.Files[0].NewPath));
    }

    [Fact]
    public void AQuotedGitPath_IsReadAsTheNameItIs()
    {
        var patch = PatchText.Parse("""
            diff --git "a/docs/caf\303\251 menu.md" "b/docs/caf\303\251 menu.md"
            --- "a/docs/caf\303\251 menu.md"
            +++ "b/docs/caf\303\251 menu.md"
            @@ -1 +1 @@
            -old
            +new
            """)!;

        Assert.Equal("docs/café menu.md", patch.Files[0].Path);
    }

    [Fact]
    public void APlainDiffOfTwoNames_ChangesTheNewOne()
    {
        Write("foo.c.orig", "int x = 1;\n");
        var target = Write("foo.c", "int x = 1;\n");
        var file = PatchText.Parse("--- foo.c.orig\n+++ foo.c\n@@ -1 +1 @@\n-int x = 1;\n+int x = 2;\n")!.Files[0];

        Assert.Equal(target, PatchText.Locate(file, _root).Path);
    }

    [Fact]
    public void ASeriesThatChangesAFileTwice_IsJudgedOnWhatTheFirstCommitMadeOfIt()
    {
        Write("x.txt", "one\ntwo\nthree\n");
        var series = """
            diff --git a/x.txt b/x.txt
            --- a/x.txt
            +++ b/x.txt
            @@ -1,3 +1,3 @@
             one
            -two
            +2
             three
            diff --git a/x.txt b/x.txt
            --- a/x.txt
            +++ b/x.txt
            @@ -1,3 +1,3 @@
             one
            -2
            +deux
             three
            """;

        var text = PatchText.Summarize("series.patch", series, 20_000, PatchText.Workspace(_root, overlay: null))!;

        Assert.DoesNotContain("does NOT apply", text, StringComparison.Ordinal);
        Assert.Equal(2, text.Split("block 1 (line 1) applies").Length - 1);
    }

    private sealed class Approval(bool answer) : IApprovalService
    {
        public string? Details { get; private set; }

        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct, string? subject = null,
                                               DiffInfo? diff = null, bool forcePrompt = false)
        {
            Details = details;
            return Task.FromResult(answer);
        }
    }

    private async Task<string> ApplyAsync(IApprovalService approval, string patchFile = "fix.patch") =>
        await new ApplyEditsTool(approval, new FileHistoryService(), () => _root)
            .ExecuteAsync(JsonSerializer.SerializeToElement(new { patch = patchFile }), CancellationToken.None);

    [Fact]
    public async Task ApplyEdits_WritesWhatApplies_CreatesNewFiles_AndHandsBackWhatDoesNot()
    {
        // The Totals block no longer applies (the tax changed since), a second file's block does.
        var patch = FormatPatch + "\n" + """
                    diff --git a/src/Tax.cs b/src/Tax.cs
                    --- a/src/Tax.cs
                    +++ b/src/Tax.cs
                    @@ -1,1 +1,1 @@
                    -const decimal Rate = 0.2m;
                    +const decimal Rate = 0.21m;
                    """;
        Write("fix.patch", patch);
        var totals = Write("src/Totals.cs", Totals.Replace("a * 0.2m", "a * 0.25m", StringComparison.Ordinal));
        var tax = Write("src/Tax.cs", "const decimal Rate = 0.2m;\n");
        var legacy = Write("old/Legacy.cs", "class Legacy { }\n");
        var approval = new Approval(answer: true);

        var result = await ApplyAsync(approval);

        Assert.StartsWith("Patch applied: 2 blocks in 2 files", result, StringComparison.Ordinal);
        Assert.Equal("const decimal Rate = 0.21m;\n", File.ReadAllText(tax));
        Assert.Contains("decimal.Round(v, 2)", File.ReadAllText(Path.Combine(_root, "src", "Rounding.cs")), StringComparison.Ordinal);
        Assert.Contains("a * 0.25m", File.ReadAllText(totals), StringComparison.Ordinal);
        Assert.DoesNotContain("Rounding.Cent", File.ReadAllText(totals), StringComparison.Ordinal);
        Assert.True(File.Exists(legacy));
        Assert.Contains("src/Totals.cs: block 1 (line 1) no longer applies", result, StringComparison.Ordinal);
        Assert.Contains("-    // TODO rounding", result, StringComparison.Ordinal);   // the block handed back, as written
        Assert.Contains("old/Legacy.cs: deleted by the patch, not done here", result, StringComparison.Ordinal);
        Assert.Contains("src/Totals.cs: block 1 (line 1) no longer applies", approval.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApplyEdits_Refused_WritesNothing()
    {
        Write("fix.patch", FormatPatch);
        var totals = Write("src/Totals.cs", Totals);

        var result = await ApplyAsync(new Approval(answer: false));

        Assert.Equal(Totals, File.ReadAllText(totals));
        Assert.False(File.Exists(Path.Combine(_root, "src", "Rounding.cs")));
        Assert.DoesNotContain("Patch applied", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APatchNamingAPathOutsideTheWorkspace_TouchesNothingThere()
    {
        Write("evil.patch", """
            --- a/../outside.txt
            +++ b/../outside.txt
            @@ -0,0 +1 @@
            +written outside
            """);

        var result = await ApplyAsync(new Approval(answer: true), "evil.patch");

        Assert.StartsWith("Nothing written", result, StringComparison.Ordinal);
        Assert.Contains("is not a path inside the workspace", result, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_root)!, "outside.txt")));
    }

    [Fact]
    public async Task AWriteThatFails_RemovesTheFileTheBatchCreated()
    {
        var created = Path.Combine(_root, "new.txt");
        var folder = Directory.CreateDirectory(Path.Combine(_root, "a-folder")).FullName;   // writing a folder as a file fails

        var write = await SafeFileWriter.WriteAllOrRollBackAsync([(created, "new", ""), (folder, "x", "")]);

        Assert.False(write.Ok);
        Assert.False(File.Exists(created));
    }
}
