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
    public void CapDiff_TruncatesPastTheLimit()
    {
        var capped = GitCommitPolicy.CapDiff(new string('d', GitCommitPolicy.MaxDiffChars + 1));
        Assert.EndsWith("…(truncated)", capped);
        Assert.True(capped.Length < GitCommitPolicy.MaxDiffChars + 20);
    }

    [Fact]
    public void CapDiff_LeavesSmallDiffsUntouched() =>
        Assert.Equal("small", GitCommitPolicy.CapDiff("small"));

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
}
