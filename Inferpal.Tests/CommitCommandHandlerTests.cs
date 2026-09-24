using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services;
using Inferpal.Services.Commands;
using Xunit;

namespace Inferpal.Tests;

// /commit and /commit-exec, shared by both front-ends since 1.5.0. The behaviours worth locking
// are the ones that touch the user's repository: what gets described, what gets staged, and the
// fact that proposing is never committing.
public class CommitCommandHandlerTests
{
    /// <summary>Scripted git: answers per argument string, records the call order.</summary>
    private sealed class FakeGit
    {
        public Dictionary<string, string> Answers { get; } = new(StringComparer.Ordinal);
        public List<string> Calls { get; } = [];
        public int ExitCode { get; set; }

        public GitRunner Runner => (args, _) =>
        {
            Calls.Add(args);
            return Task.FromResult((Answers.TryGetValue(args, out var o) ? o : "", ExitCode));
        };
    }

    // The proposal goes through RunAgentAsync (utility model, tools off), which the fake answers
    // from ChatResult.
    private static FakeInferenceProvider Proposing(string message) => new()
    {
        ChatResult = new ChatTurnResult(message, [], 0, 0),
    };

    /// <summary>
    /// A reply that stopped at the length limit was pre-filled into <c>/commit-exec</c> as if whole: a subject
    /// cut mid-word reads as a terse one, and sent as is it becomes history. The notice comes before it.
    /// </summary>
    [Fact]
    public async Task Propose_ACutProposal_SaysItMayBeIncomplete()
    {
        var git = new FakeGit();
        git.Answers["diff --staged"] = "diff --git a/A.cs b/A.cs\n+added";
        var client = new FakeInferenceProvider
        {
            RunAgentThroughChat = true,
            ChatResult = new ChatTurnResult("feat(rag): make the index rebu", [], 0, 0, CutAtLimit: true),
        };

        var result = await CommitCommandHandler.ProposeAsync(
            client, new InferpalConfig(), git.Runner, null, CancellationToken.None);

        Assert.Equal("feat(rag): make the index rebu", result.Proposal);
        Assert.Contains(Strings.CommitProposalCut, result.Notice);
    }

    /// <summary>An empty answer ended the command on an empty bubble (VS) or on nothing (VS Code).</summary>
    [Fact]
    public async Task Propose_AnEmptyAnswer_NamesTheModelThatGaveIt()
    {
        var git = new FakeGit();
        git.Answers["diff --staged"] = "diff --git a/A.cs b/A.cs\n+added";
        var client = new FakeInferenceProvider
        {
            RunAgentThroughChat = true,
            ChatResult = new ChatTurnResult("   ", [], 0, 0),
        };

        var result = await CommitCommandHandler.ProposeAsync(
            client, new InferpalConfig(), git.Runner, null, CancellationToken.None);

        Assert.Null(result.Proposal);
        var model = Assert.Single(client.AgentRuns).Model;
        Assert.Equal(Strings.MsgEmptyResponseFrom(model, client.ServerAddress), result.Message);
    }

    [Fact]
    public async Task Propose_WithNothingChanged_SaysSo_AndNeverAsksTheModel()
    {
        var git    = new FakeGit();
        var client = Proposing("should never be asked");

        var result = await CommitCommandHandler.ProposeAsync(
            client, new InferpalConfig(), git.Runner, null, CancellationToken.None);

        Assert.Equal(Strings.CommitNothingToCommit, result.Message);
        Assert.Null(result.Proposal);
        Assert.Empty(client.AgentRuns);
    }

    [Fact]
    public async Task Propose_WithStagedChanges_DescribesTheStagedDiff()
    {
        var git = new FakeGit();
        git.Answers["diff --staged"] = "diff --git a/A.cs b/A.cs\n+added";

        var client = Proposing("feat: add A");
        var result = await CommitCommandHandler.ProposeAsync(
            client, new InferpalConfig(), git.Runner, null, CancellationToken.None);

        Assert.Equal("feat: add A", result.Proposal);
        Assert.Null(result.Notice);
        // The working tree is never consulted when something is staged: the message must describe
        // what will actually be committed.
        Assert.DoesNotContain("status --short", git.Calls);
        Assert.Contains("+added", Assert.Single(client.AgentRuns).History[^1].Content);
    }

    [Fact]
    public async Task Propose_WithNothingStaged_FallsBackToTheWorkingTree_AndSaysSo()
    {
        var git = new FakeGit();
        git.Answers["status --short"] = " M B.cs";
        git.Answers["diff"]           = "diff --git a/B.cs b/B.cs\n+changed";

        var result = await CommitCommandHandler.ProposeAsync(
            Proposing("fix: B"), new InferpalConfig(), git.Runner, null, CancellationToken.None);

        Assert.Equal("fix: B", result.Proposal);
        Assert.Equal(Strings.CommitNothingStaged, result.Notice);
    }

    [Fact]
    public async Task Propose_CommitsNothing()
    {
        var git = new FakeGit();
        git.Answers["diff --staged"] = "+x";

        await CommitCommandHandler.ProposeAsync(
            Proposing("chore: x"), new InferpalConfig(), git.Runner, null, CancellationToken.None);

        Assert.DoesNotContain(git.Calls, c => c.StartsWith("commit", StringComparison.Ordinal));
        Assert.DoesNotContain(git.Calls, c => c.StartsWith("add", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Execute_WithSomethingStaged_CommitsWithoutStagingMore()
    {
        var git = new FakeGit();
        git.Answers["diff --staged --name-only"] = "A.cs";

        var result = await CommitCommandHandler.ExecuteAsync("feat: a", git.Runner, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.DoesNotContain("add -u", git.Calls);
        Assert.Contains(git.Calls, c => c == "commit -m \"feat: a\"");
    }

    // `add -u` and not `add -A`: sweeping untracked files in is how a build output or a secrets
    // file ends up committed by a command the user only asked to write a message.
    [Fact]
    public async Task Execute_WithNothingStaged_StagesTrackedFilesOnly()
    {
        var git = new FakeGit();

        await CommitCommandHandler.ExecuteAsync("fix: b", git.Runner, CancellationToken.None);

        Assert.Contains("add -u", git.Calls);
        Assert.DoesNotContain(git.Calls, c => c is "add -A" or "add ." or "add --all");
    }

    [Fact]
    public async Task Execute_ReportsFailure_RatherThanClaimingSuccess()
    {
        var git = new FakeGit { ExitCode = 1 };
        git.Answers["commit -m \"nope\""] = "nothing to commit, working tree clean";

        var result = await CommitCommandHandler.ExecuteAsync("nope", git.Runner, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("nothing to commit", result.Output);
    }

    [Theory]
    [InlineData("say \"hi\"")]
    [InlineData("path\\to\\thing")]
    public async Task Execute_EscapesTheMessage_SoQuotesCannotBreakOutOfTheCommand(string message)
    {
        var git = new FakeGit();
        git.Answers["diff --staged --name-only"] = "A.cs";

        await CommitCommandHandler.ExecuteAsync(message, git.Runner, CancellationToken.None);

        var commit = Assert.Single(git.Calls, c => c.StartsWith("commit ", StringComparison.Ordinal));
        Assert.Equal($"commit -m \"{GitCommitPolicy.EscapeMessage(message)}\"", commit);
    }

    // ── A message written from a fifth of the change ───────────────────────────

    /// <summary>
    /// The cap is 12 000 characters and five of this repository's last six commits exceed it — one
    /// by more than four times. So the ordinary case is a proposal written from part of the change,
    /// pre-filled into <c>/commit-exec</c>, naming a scope taken from whatever fit.
    /// </summary>
    [Fact]
    public async Task Propose_FromADiffTooLargeToFit_SaysSoBeforeTheMessage()
    {
        var git    = new FakeGit();
        var staged = new string('d', GitCommitPolicy.MaxDiffChars + 8_000);
        git.Answers["diff --staged"] = staged;
        // The cap applies to the assembled context, not to git's raw output.
        var total = GitCommitPolicy.BuildStagedContext(staged).Length;

        var result = await CommitCommandHandler.ProposeAsync(
            Proposing("feat: something"), new InferpalConfig(), git.Runner, null, CancellationToken.None);

        Assert.Equal("feat: something", result.Proposal);   // the proposal still happens
        Assert.Equal(Strings.CommitDiffTruncated(GitCommitPolicy.MaxDiffChars, total), result.Notice);
    }

    /// <summary>Both facts are about the same proposal and neither replaces the other.</summary>
    [Fact]
    public async Task Propose_FromAnUnstagedDiffTooLargeToFit_KeepsBothNotices()
    {
        var git      = new FakeGit();
        var unstaged = new string('d', GitCommitPolicy.MaxDiffChars + 1);
        git.Answers["status --short"] = " M A.cs";
        git.Answers["diff"]           = unstaged;
        var total = GitCommitPolicy.BuildUnstagedContext(" M A.cs", unstaged).Length;

        var result = await CommitCommandHandler.ProposeAsync(
            Proposing("chore: x"), new InferpalConfig(), git.Runner, null, CancellationToken.None);

        Assert.Contains(Strings.CommitNothingStaged, result.Notice!);
        Assert.Contains(Strings.CommitDiffTruncated(GitCommitPolicy.MaxDiffChars, total), result.Notice!);
    }

    /// <summary>Reference arm: an ordinary diff carries no notice at all, or the warning becomes
    /// the noise people stop reading.</summary>
    [Fact]
    public async Task Propose_FromADiffThatFits_SaysNothingAboutTheCap()
    {
        var git = new FakeGit();
        git.Answers["diff --staged"] = "diff --git a/A.cs b/A.cs\n+added";

        var result = await CommitCommandHandler.ProposeAsync(
            Proposing("feat: add"), new InferpalConfig(), git.Runner, null, CancellationToken.None);

        Assert.Null(result.Notice);
    }
}
