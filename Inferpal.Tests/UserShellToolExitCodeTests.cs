using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Inferpal.Config;
using Inferpal.Services;
using Inferpal.Services.Shell;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  A user-defined shell tool reports a non-zero exit code, like run_command.
//
//  The persistent shell writes the rule down — "a silent failure (`git diff --quiet`) must not read
//  as success" — and the second reader of a child's exit code dropped it entirely: a custom
//  `lint=npm run lint` that fails, `check=git diff --quiet` that finds changes, returned their
//  output (or "(no output)") with nothing to say the command failed. The custom tool is the one
//  whose exit code is most often the whole answer.
// ──────────────────────────────────────────────────────────────────────────────────────────────
[Collection(ShellSerialCollection.Name)]
public class UserShellToolExitCodeTests
{
    private sealed class Approve : IApprovalService
    {
        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct, string? subject = null, DiffInfo? diff = null, bool forcePrompt = false)
            => Task.FromResult(true);
    }

    private static readonly JsonElement NoArgs = JsonDocument.Parse("{}").RootElement;

    private static string? FindBash()
    {
        if (File.Exists("/bin/bash")) return "/bin/bash";
        foreach (var candidate in new[] { @"C:\Program Files\Git\bin\bash.exe", @"C:\Program Files\Git\usr\bin\bash.exe" })
            if (File.Exists(candidate)) return candidate;
        return null;
    }

    private static async Task<string> RunAsync(ShellOverride shell, string command)
    {
        ShellLauncher._overrideForTests = shell;
        try
        {
            return await new UserShellTool("probe", command, new Approve(), new InferpalConfig())
                .ExecuteAsync(NoArgs, CancellationToken.None);
        }
        finally { ShellLauncher._overrideForTests = null; }
    }

    [Fact]
    public async Task Posix_ASilentFailure_AndAnExit_AreReported()
    {
        var bash = FindBash();
        if (bash is null) return;   // no bash on this machine — covered by the POSIX CI legs
        var shell = new ShellOverride(ShellDialect.Posix, bash);

        Assert.Contains("[exit code 1]", await RunAsync(shell, "false"));

        var partial = await RunAsync(shell, "echo before-exit; exit 4");
        Assert.Contains("before-exit", partial);
        Assert.Contains("[exit code 4]", partial);

        // Reference arm: a success says nothing more than its output.
        var ok = await RunAsync(shell, "echo fine");
        Assert.Equal("fine", ok);
    }

    [Fact]
    public async Task PowerShell_AnExit_IsReported()
    {
        if (!OperatingSystem.IsWindows()) return;
        var shell = new ShellOverride(ShellDialect.PowerShell, "powershell.exe");

        var result = await RunAsync(shell, "Write-Output before-exit; exit 3");
        Assert.Contains("before-exit", result);
        Assert.Contains("[exit code 3]", result);

        Assert.DoesNotContain("[exit code", await RunAsync(shell, "Write-Output fine"));
    }
}
