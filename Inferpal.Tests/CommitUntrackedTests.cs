using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services;
using Inferpal.Services.Commands;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ With nothing staged, <c>/commit</c> described <c>git status --short</c> — untracked files
/// (<c>??</c>) included — while <c>/commit-exec</c> stages with <c>git add -u</c>, tracked files only,
/// on purpose. The message named a new class, the commit did not contain it: a history entry that
/// describes code which is not in it, and a build broken at that commit.
/// </summary>
[Collection(CultureSerialCollection.Name)]   // compares localized notices
public class CommitUntrackedTests
{
    private sealed class FakeGit
    {
        public Dictionary<string, string> Answers { get; } = new(StringComparer.Ordinal);
        public GitRunner Runner => (args, _) =>
            Task.FromResult((Answers.TryGetValue(args, out var o) ? o : "", 0));
    }

    private static FakeInferenceProvider Proposing(string message) => new()
    {
        ChatResult = new ChatTurnResult(message, [], 0, 0),
    };

    [Fact]
    public async Task AnUntrackedFile_IsNotDescribed_AndTheUserIsToldItStaysOut()
    {
        var git = new FakeGit();
        git.Answers["status --short"] = " M src/Program.cs\n?? src/NewFeature.cs";
        git.Answers["diff"]           = "diff --git a/src/Program.cs b/src/Program.cs\n+new NewFeature().Run();";
        var client = Proposing("feat: wire NewFeature");

        var result = await CommitCommandHandler.ProposeAsync(client, new InferpalConfig(), git.Runner, null, CancellationToken.None);

        var asked = Assert.Single(client.AgentRuns).History[^1].Content!;
        Assert.Contains("src/Program.cs", asked);                  // witness: the tracked change IS described
        Assert.DoesNotContain("NewFeature.cs", asked);             // the file /commit-exec will not commit is not
        Assert.Contains("src/NewFeature.cs", result.Notice);       // and the user is told it stays out
        Assert.Contains(Strings.CommitNothingStaged, result.Notice);
    }

    [Fact]
    public async Task OnlyUntrackedFiles_AreNotProposedForACommitThatWouldHoldNothing()
    {
        var git = new FakeGit();
        git.Answers["status --short"] = "?? src/NewFeature.cs";
        var client = Proposing("should never be asked");

        var result = await CommitCommandHandler.ProposeAsync(client, new InferpalConfig(), git.Runner, null, CancellationToken.None);

        Assert.Null(result.Proposal);
        Assert.Empty(client.AgentRuns);
        Assert.Contains("src/NewFeature.cs", result.Message);
    }

    [Fact]
    public async Task TrackedChangesOnly_KeepTheirNotice()
    {
        // Reference arm: nothing untracked, nothing more said.
        var git = new FakeGit();
        git.Answers["status --short"] = " M B.cs";
        git.Answers["diff"]           = "+changed";

        var result = await CommitCommandHandler.ProposeAsync(Proposing("fix: B"), new InferpalConfig(), git.Runner, null, CancellationToken.None);

        Assert.Equal(Strings.CommitNothingStaged, result.Notice);
    }
}
