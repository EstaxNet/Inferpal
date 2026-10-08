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
