using System.IO;
using Inferpal.Config;
using Inferpal.Services.CodeActions;
using Inferpal.Services.Execution;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  An "Always" approval is trust given to ONE workspace.
//
//  The session grants were kept by tool name alone, in an approval service that Visual Studio builds
//  once for the life of the process. "Always" on run_command, clicked in solution A, then let the
//  commands of a freshly cloned repository B run without a prompt once the workspace root moved —
//  while the shell session itself drops its state on that move. VS Code restarts its host on a root
//  change, which is why only one front-end showed it.
// ──────────────────────────────────────────────────────────────────────────────────────────────
public sealed class SessionGrantRootTests
{
    private sealed class MovingRootApproval(Func<string?> root) : ApprovalServiceBase(new InferpalConfig(), root)
    {
        public int Prompts;
        protected override Task<ApprovalDecision> PromptUserAsync(string message, DiffInfo? diff, CancellationToken ct)
        {
            Prompts++;
            return Task.FromResult(ApprovalDecision.Always);
        }
    }

    private static readonly string RepoA = Path.Combine(Path.GetTempPath(), "inferpal-grant-a");
    private static readonly string RepoB = Path.Combine(Path.GetTempPath(), "inferpal-grant-b");

    [Fact]
    public async Task AGrantGivenInOneWorkspace_DoesNotApproveInAnother()
    {
        var root     = RepoA;
        var approval = new MovingRootApproval(() => root);

        Assert.True(await approval.RequestApprovalAsync("run_command", "npm test", CancellationToken.None));   // "Always"
        Assert.True(await approval.RequestApprovalAsync("run_command", "npm run build", CancellationToken.None));
        Assert.Equal(1, approval.Prompts);                          // witness: the grant holds in A

        root = RepoB;
        await approval.RequestApprovalAsync("run_command", "./install.sh", CancellationToken.None);
        Assert.Equal(2, approval.Prompts);                          // B asks

        root = RepoA;
        await approval.RequestApprovalAsync("run_command", "npm test", CancellationToken.None);
        Assert.Equal(3, approval.Prompts);                          // and A's grant did not come back with A
    }

    /// <summary>⚠ An "Always" answered after another solution opened is trust given to the workspace the card was asked
    /// in: filed under the new root, every call in it ran unasked.</summary>
    [Fact]
    public async Task AnAlwaysAnsweredAfterASolutionSwitch_IsFiledUnderTheCardsWorkspace()
    {
        var root     = RepoA;
        var approval = new SwitchingWhileAsked(() => root, () => root = RepoB);

        Assert.True(await approval.RequestApprovalAsync("run_command", "npm test", CancellationToken.None));   // A's card
        await approval.RequestApprovalAsync("run_command", "./install.sh", CancellationToken.None);          // asked in B

        Assert.Equal(2, approval.Prompts);
    }

    /// <summary>Reference arm: that late "Always" takes nothing away from the workspace open now — a grant given there
    /// while A's card waited holds.</summary>
    [Fact]
    public async Task ALateAlwaysForAWorkspaceNoLongerOpen_KeepsTheGrantsOfTheOneOpenNow()
    {
        var root = RepoA;
        ScriptedApproval? approval = null;
        approval = new ScriptedApproval(() => root, async prompt =>
        {
            if (prompt != 1) return;
            root = RepoB;   // B opened while A's card waits, and one of B's cards answered "Always"
            await approval!.RequestApprovalAsync("write_file", "B's file", CancellationToken.None);
        });

        await approval.RequestApprovalAsync("run_command", "npm test", CancellationToken.None);   // A's card, answered last
        await approval.RequestApprovalAsync("write_file", "B's file again", CancellationToken.None);

        Assert.Equal(2, approval.Prompts);   // B's grant held
    }

    /// <summary>Answers "Always", running <paramref name="onPrompt"/> with the prompt's number first.</summary>
    private sealed class ScriptedApproval(Func<string?> root, Func<int, Task> onPrompt) : ApprovalServiceBase(new InferpalConfig(), root)
    {
        public int Prompts;
        protected override async Task<ApprovalDecision> PromptUserAsync(string message, DiffInfo? diff, CancellationToken ct)
        {
            await onPrompt(++Prompts);
            return ApprovalDecision.Always;
        }
    }

    /// <summary>Opens another solution while the first card waits, then answers "Always".</summary>
    private sealed class SwitchingWhileAsked(Func<string?> root, Action switchSolution)
        : ApprovalServiceBase(new InferpalConfig(), root)
    {
        public int Prompts;
        protected override Task<ApprovalDecision> PromptUserAsync(string message, DiffInfo? diff, CancellationToken ct)
        {
            if (Prompts++ == 0) switchSolution();
            return Task.FromResult(ApprovalDecision.Always);
        }
    }

    [Fact]
    public async Task TheSameRoot_WrittenAnotherWay_IsTheSameWorkspace()
    {
        // Reference arm: a trailing separator is the same folder; so is a different case where the volume folds it.
        var root     = RepoA;
        var approval = new MovingRootApproval(() => root);
        await approval.RequestApprovalAsync("run_command", "npm test", CancellationToken.None);

        root = RepoA + Path.DirectorySeparatorChar;
        await approval.RequestApprovalAsync("run_command", "npm test", CancellationToken.None);
        if (!OperatingSystem.IsLinux())
        {
            root = RepoA.ToUpperInvariant();
            await approval.RequestApprovalAsync("run_command", "npm test", CancellationToken.None);
        }

        Assert.Equal(1, approval.Prompts);
    }
}
