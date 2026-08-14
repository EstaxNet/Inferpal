using System.IO;
using Inferpal.Config;
using Inferpal.Models;
using Inferpal.Services.Commands;
using Inferpal.Services.Governance;
using Xunit;

namespace Inferpal.Tests;

// Roadmap §15 — an anchored review. The single question behind every test here: can a location the
// model produced be trusted? A model asked for `file:line` always answers one; only the diff can
// say whether it points at anything. What cannot be confirmed must be labelled, never dropped and
// never dressed up as a location.
public class CheckReviewAnchorTests
{
    private const string Diff = """
        git diff --staged:
        diff --git a/src/Alpha.cs b/src/Alpha.cs
        index 1111111..2222222 100644
        --- a/src/Alpha.cs
        +++ b/src/Alpha.cs
        @@ -8,3 +10,4 @@ public class Alpha
             void A() { }
        +    void Added() { }
        @@ -40,2 +50,2 @@ public class Alpha
        -    void Old() { }
        +    void New() { }
        diff --git a/src/Beta.cs b/src/Beta.cs
        --- a/src/Beta.cs
        +++ b/src/Beta.cs
        @@ -1,0 +7,3 @@
        +// beta
        """;

    // ── DiffAnchors ───────────────────────────────────────────────────────────

    [Fact]
    public void Covers_OnlyLinesInsideHunks()
    {
        var a = DiffAnchors.Parse(Diff);

        Assert.True(a.Covers("src/Alpha.cs", 10));
        Assert.True(a.Covers("src/Alpha.cs", 13));   // 10 + 4 - 1
        Assert.False(a.Covers("src/Alpha.cs", 14));
        Assert.True(a.Covers("src/Alpha.cs", 51));
        Assert.True(a.Covers("src/Beta.cs", 9));
        Assert.False(a.Covers("src/Gamma.cs", 10));
    }

    [Fact]
    public void Nearest_SnapsToClosestChangedLine_AndOnlyWithinTheSameFile()
    {
        var a = DiffAnchors.Parse(Diff);

        Assert.Equal(13, a.Nearest("src/Alpha.cs", 20));   // between the two hunks, closer to the first
        Assert.Equal(50, a.Nearest("src/Alpha.cs", 45));
        Assert.Equal(12, a.Nearest("src/Alpha.cs", 12));   // already inside
        Assert.Null(a.Nearest("src/Gamma.cs", 3));         // never re-anchor across files
    }

    [Fact]
    public void Parse_IgnoresDeletedFilesAndSurroundingText()
    {
        var a = DiffAnchors.Parse("""
            git status:
             M src/Kept.cs
             D src/Gone.cs

            git diff (unstaged):
            --- a/src/Gone.cs
            +++ /dev/null
            @@ -1,5 +0,0 @@
            -gone
            --- a/src/Kept.cs
            +++ b/src/Kept.cs
            @@ -1 +1,2 @@
            +kept
            …(truncated)
            """);

        Assert.True(a.HasFile("src/Kept.cs"));
        Assert.False(a.HasFile("src/Gone.cs"));
        // The hunk of the deleted file must not leak onto the next file.
        Assert.False(a.Covers("src/Kept.cs", 5));
    }

    [Fact]
    public void BareFileName_ResolvesOnlyWhenUnambiguous()
    {
        var a = DiffAnchors.Parse("""
            --- a/src/one/Same.cs
            +++ b/src/one/Same.cs
            @@ -1 +1,2 @@
            +x
            --- a/src/two/Same.cs
            +++ b/src/two/Same.cs
            @@ -1 +9,2 @@
            +y
            --- a/src/Unique.cs
            +++ b/src/Unique.cs
            @@ -1 +3,2 @@
            +z
            """);

        Assert.True(a.Covers("Unique.cs", 3));    // one candidate: resolve it
        Assert.False(a.Covers("Same.cs", 1));     // two candidates: refuse rather than guess
    }

    // ── Parsing and anchoring ─────────────────────────────────────────────────

    [Fact]
    public void Finding_InsideAHunk_IsExact()
    {
        var review = CheckReviewParser.Parse(
            "- [blocker] src/Alpha.cs:11 — secret written to the log", DiffAnchors.Parse(Diff));

        var f = Assert.Single(review.Findings);
        Assert.Equal(CheckSeverity.Blocker, f.Severity);
        Assert.Equal("src/Alpha.cs", f.File);
        Assert.Equal(11, f.Line);
        Assert.Equal(AnchorKind.Exact, f.Anchor);
        Assert.Equal("secret written to the log", f.Message);
    }

    [Fact]
    public void Finding_OffByAFewLines_IsAdjusted_AndKeepsWhatTheModelSaid()
    {
        var review = CheckReviewParser.Parse(
            "- [warning] src/Alpha.cs:20 — missing null check", DiffAnchors.Parse(Diff));

        var f = Assert.Single(review.Findings);
        Assert.Equal(AnchorKind.Adjusted, f.Anchor);
        Assert.Equal(13, f.Line);
        Assert.Equal(20, f.ReportedLine);
    }

    // The failure this whole fiche exists to prevent: a location that points nowhere, presented as
    // if it pointed somewhere.
    [Fact]
    public void Finding_OnAFileOutsideTheDiff_IsKeptButLabelledUnanchored()
    {
        var review = CheckReviewParser.Parse(
            "- [blocker] src/NotInDiff.cs:7 — hardcoded password", DiffAnchors.Parse(Diff));

        var f = Assert.Single(review.Findings);
        Assert.Equal(AnchorKind.Unanchored, f.Anchor);
        Assert.Equal(7, f.Line);

        var rendered = CheckReviewParser.Render(review);
        Assert.Contains("hardcoded password", rendered);
        Assert.Contains(Inferpal.Localization.Strings.CheckAnchorUnanchored, rendered);
    }

    // Severity is passed by name: the enum is internal to the Core and xUnit needs public
    // test signatures.
    [Theory]
    [InlineData("- [nit] src/Alpha.cs:11 — naming", "Nit")]
    [InlineData("* src/Alpha.cs:11 - naming (nit)", "Nit")]
    [InlineData("1. **src/Alpha.cs**:11 — naming", "Warning")]
    [InlineData("- `src/Alpha.cs`:11 — naming", "Warning")]
    [InlineData("- critical: src/Alpha.cs:11 — naming", "Blocker")]
    [InlineData("- src/Alpha.cs (line 11): naming", "Warning")]
    public void ShapesModelsActuallyEmit_AreAllUnderstood(string line, string expected)
    {
        var f = Assert.Single(CheckReviewParser.Parse(line, DiffAnchors.Parse(Diff)).Findings);
        Assert.Equal(expected, f.Severity.ToString());
        Assert.Equal("src/Alpha.cs", f.File);
        Assert.Equal(11, f.Line);
        Assert.Equal("naming", f.Message);
    }

    [Fact]
    public void ProseIsKept_AndALocationWithoutARemarkIsNotAFinding()
    {
        var review = CheckReviewParser.Parse("""
            Check "no secrets": passes.
            See src/Alpha.cs:11
            - [warning] src/Alpha.cs:12 — TODO left behind
            """, DiffAnchors.Parse(Diff));

        Assert.Single(review.Findings);
        Assert.Contains("passes", review.Prose);
        Assert.Contains("See src/Alpha.cs:11", review.Prose);
    }

    [Fact]
    public void Render_GroupsByFile_BlockersFirst()
    {
        var review = CheckReviewParser.Parse("""
            - [nit] src/Beta.cs:7 — spacing
            - [blocker] src/Alpha.cs:51 — injection
            - [warning] src/Alpha.cs:11 — unchecked cast
            """, DiffAnchors.Parse(Diff));

        var rendered = CheckReviewParser.Render(review);

        Assert.Equal(3, review.Findings.Count);
        // Alpha carries the blocker, so its group comes first even though Beta sorts before it.
        Assert.True(rendered.IndexOf("src/Alpha.cs**") < rendered.IndexOf("src/Beta.cs**"));
        Assert.True(rendered.IndexOf("injection") < rendered.IndexOf("unchecked cast"));
    }

    [Fact]
    public void NoFinding_SaysSo_RatherThanShowingAnEmptyList()
    {
        var rendered = CheckReviewParser.Render(
            CheckReviewParser.Parse("Everything passes.", DiffAnchors.Parse(Diff)));

        Assert.Contains("Everything passes.", rendered);
        Assert.Contains(Inferpal.Localization.Strings.CheckNoFindings, rendered);
    }

    // ── The command, end to end ───────────────────────────────────────────────

    private static string NewRootWithCheck()
    {
        var root = Path.Combine(Path.GetTempPath(), "ob-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".inferpal", "checks"));
        File.WriteAllText(Path.Combine(root, ".inferpal", "checks", "secrets.md"),
            "---\ndescription: no secrets\n---\nNo secret may be committed.");
        return root;
    }

    private static FakeInferenceProvider Answering(string text) => new()
    {
        OnChat = (_, _) => Task.FromResult(new ChatTurnResult(text, [], 0, 0)),
    };

    [Fact]
    public async Task Handle_ReviewsTheStagedDiff_AndAnchorsTheAnswer()
    {
        var root = NewRootWithCheck();
        try
        {
            var asked = new List<string>();
            var result = await CheckCommandHandler.HandleAsync(
                Answering("- [blocker] src/Alpha.cs:11 — API key in clear text"),
                new InferpalConfig(), root, ["/check"],
                git: (args, _) =>
                {
                    asked.Add(args);
                    return Task.FromResult((args == "diff --staged" ? RawDiff : "", 0));
                },
                onProgress: null, CancellationToken.None);

            Assert.Contains("diff --staged", asked);
            Assert.DoesNotContain("status --short", asked);   // staged wins, like /commit
            Assert.Contains("src/Alpha.cs:11", result.Message);
            Assert.Contains("API key in clear text", result.Message);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Handle_WithNothingToReview_SaysSoWithoutCallingTheModel()
    {
        var root = NewRootWithCheck();
        var client = Answering("should never be asked");
        try
        {
            var result = await CheckCommandHandler.HandleAsync(
                client, new InferpalConfig(), root, ["/check"],
                git: (_, _) => Task.FromResult(("", 0)), onProgress: null, CancellationToken.None);

            Assert.Equal(Inferpal.Localization.Strings.CheckNoDiff, result.Message);
            Assert.Empty(client.ChatModels);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Handle_Init_AsksForTheScaffold_InsteadOfReviewing()
    {
        var root = NewRootWithCheck();
        try
        {
            var result = await CheckCommandHandler.HandleAsync(
                Answering(""), new InferpalConfig(), root, ["/check", "init"],
                git: (_, _) => Task.FromResult(("", 0)), onProgress: null, CancellationToken.None);

            Assert.NotNull(result.Scaffold);
            Assert.Null(result.Message);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Handle_UnknownCheckName_IsRejectedBeforeAnyGitCall()
    {
        var root = NewRootWithCheck();
        try
        {
            bool gitCalled = false;
            var result = await CheckCommandHandler.HandleAsync(
                Answering(""), new InferpalConfig(), root, ["/check", "nope"],
                git: (_, _) => { gitCalled = true; return Task.FromResult(("", 0)); },
                onProgress: null, CancellationToken.None);

            Assert.Equal(Inferpal.Localization.Strings.CheckUnknownName("nope"), result.Message);
            Assert.False(gitCalled);
        }
        finally { Directory.Delete(root, true); }
    }

    private const string RawDiff = """
        diff --git a/src/Alpha.cs b/src/Alpha.cs
        --- a/src/Alpha.cs
        +++ b/src/Alpha.cs
        @@ -8,3 +10,4 @@
        +    const string Key = "sk-live-1234";
        """;
}
