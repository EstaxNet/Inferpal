using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

// Covers the pure /commit (and partly /check) formatting extracted from the tool-window
// VM: diff-context assembly with its size cap, the proposal request shape, the
// proposal clean-up, and the git argument escaping. Running git and the chat bubbles
// stay in the VM and are not tested here.
public class GitCommitPolicyTests
{
    // ── Diff context ───────────────────────────────────────────────────────────

    [Fact]
    public void BuildStagedContext_LabelsTheDiff() =>
        Assert.Equal("git diff --staged:\n+added line",
            GitCommitPolicy.BuildStagedContext("+added line"));

    [Fact]
    public void BuildUnstagedContext_IncludesDiff_WhenPresent() =>
        Assert.Equal("git status:\n M Foo.cs\n\ngit diff (unstaged):\n+x",
            GitCommitPolicy.BuildUnstagedContext(" M Foo.cs", "+x"));

    [Theory]
    [InlineData("")]
    [InlineData("   \n")]
    public void BuildUnstagedContext_SkipsBlankDiffSection(string blankDiff) =>
        Assert.Equal("git status:\n?? New.cs",
            GitCommitPolicy.BuildUnstagedContext("?? New.cs", blankDiff));

    [Fact]
    public void CapDiff_TruncatesPastTheLimit_AndSaysByHowMuch()
    {
        var capped = GitCommitPolicy.CapDiff(new string('d', GitCommitPolicy.MaxDiffChars + 500));

        // The marker tells the MODEL, and it names the amount: "truncated" on its own cannot be
        // weighed, and the sibling cap in get_git_status has always named it.
        Assert.Contains("…(truncated", capped.Text, StringComparison.Ordinal);
        Assert.Contains("500", capped.Text, StringComparison.Ordinal);
        Assert.True(capped.Text.Length < GitCommitPolicy.MaxDiffChars + 60);

        // And the count is what lets each caller tell the HUMAN.
        Assert.True(capped.IsTruncated);
        Assert.Equal(GitCommitPolicy.MaxDiffChars, capped.Kept);
        Assert.Equal(GitCommitPolicy.MaxDiffChars + 500, capped.Total);
        Assert.Equal(500, capped.Cut);
    }

    /// <summary>Reference arm: a diff that fits is untouched and reports no cut, or every commit
    /// would carry a warning about nothing.</summary>
    [Fact]
    public void CapDiff_LeavesSmallDiffsUntouched()
    {
        var capped = GitCommitPolicy.CapDiff("small");

        Assert.Equal("small", capped.Text);
        Assert.False(capped.IsTruncated);
        Assert.Equal(0, capped.Cut);
    }

    // ── Proposal request / clean-up ────────────────────────────────────────────

    [Fact]
    public void BuildProposalRequest_SystemPlusUserWithDiff()
    {
        var request = GitCommitPolicy.BuildProposalRequest("the-diff-context");
        Assert.Equal(2, request.Count);
        Assert.Equal("system", request[0].Role);
        Assert.Contains("commit message", request[0].Content);
        Assert.Equal("user", request[1].Role);
        Assert.Contains("the-diff-context", request[1].Content);
    }

    [Theory]
    [InlineData("fix: bug",                                  "fix: bug")]
    [InlineData("  `fix: bug`  ",                            "fix: bug")]
    [InlineData("\"fix: bug\"",                              "fix: bug")]
    [InlineData("<think>hmm reasoning</think>fix: bug",      "fix: bug")]
    [InlineData("`\"fix: bug\"`",                            "fix: bug")]
    public void CleanProposal_StripsThinkTagsBackticksAndQuotes(string raw, string expected) =>
        Assert.Equal(expected, GitCommitPolicy.CleanProposal(raw));

    [Fact]
    public void CleanProposal_NullResponse_GivesEmptyString() =>
        Assert.Equal(string.Empty, GitCommitPolicy.CleanProposal(null));

    // ── EscapeMessage ──────────────────────────────────────────────────────────

    [Fact]
    public void EscapeMessage_EscapesDoubleQuotesAndTrims() =>
        Assert.Equal("say \\\"hi\\\"", GitCommitPolicy.EscapeMessage("  say \"hi\"  "));

    [Fact]
    public void EscapeMessage_DoublesTrailingBackslashes_SoTheClosingQuoteSurvives()
    {
        // Win32/MSVCRT rule: a backslash escapes only when it precedes a quote. A message ending
        // in `bin\` produced `…bin\"` — the closing quote was swallowed and the remaining git
        // arguments merged into the message.
        Assert.Equal(@"move to bin\\", GitCommitPolicy.EscapeMessage(@"move to bin\"));
        Assert.Equal(@"a\\\\", GitCommitPolicy.EscapeMessage(@"a\\"));
    }

    [Fact]
    public void EscapeMessage_DoublesBackslashesBeforeAnEmbeddedQuote()
    {
        // `\"` in the message: the original backslash must double AND the quote gets its own.
        Assert.Equal("path \\\\\\\"x", GitCommitPolicy.EscapeMessage("path \\\"x"));
        // A backslash NOT followed by a quote stays literal.
        Assert.Equal(@"a\b", GitCommitPolicy.EscapeMessage(@"a\b"));
    }
}
