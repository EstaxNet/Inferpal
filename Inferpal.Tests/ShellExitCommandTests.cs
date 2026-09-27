using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Inferpal.Config;
using Inferpal.Services.Shell;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  A command that calls `exit` still reports its exit code.
//
//  The command runs inside the wrapper (eval / Invoke-Expression), and its exit code travels in the
//  state block the wrapper prints afterwards. An `exit` in the command ends the wrapper before that
//  block — the POSIX wrapper documents it as "no state captured" — and the code went with it: the
//  shell process's own exit code, the only place left holding it, was never read. `test -f out.bin
//  || exit 1`, `make || exit $?`, `exec ./server` came back as an empty output with no failure
//  note, which the model reads as a success.
// ──────────────────────────────────────────────────────────────────────────────────────────────
[Collection(ShellSerialCollection.Name)]
public class ShellExitCommandTests
{
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
            var session = new ShellSession(() => Path.GetTempPath(), new InferpalConfig());
            return await session.RunAsync(command, null, CancellationToken.None);
        }
        finally { ShellLauncher._overrideForTests = null; }
    }

    [Fact]
    public async Task Posix_AnExitInTheCommand_StillReportsItsCode()
    {
        var bash = FindBash();
        if (bash is null) return;   // no bash on this machine — covered by the POSIX CI legs
        var shell = new ShellOverride(ShellDialect.Posix, bash);

        Assert.Contains("[exit code 3]", await RunAsync(shell, "exit 3"));

        var partial = await RunAsync(shell, "echo before-exit; exit 4");
        Assert.Contains("before-exit", partial);
        Assert.Contains("[exit code 4]", partial);
    }

    [Fact]
    public async Task Posix_ReferenceArms_ASuccessfulExit_AndAnOrdinaryFailure()
    {
        var bash = FindBash();
        if (bash is null) return;
        var shell = new ShellOverride(ShellDialect.Posix, bash);

        // exit 0 is a success: no note.
        var ok = await RunAsync(shell, "echo fine; exit 0");
        Assert.Contains("fine", ok);
        Assert.DoesNotContain("[exit code", ok);

        // A failure without exit already travelled in the state block: reported once, not twice.
        var failed = await RunAsync(shell, "false");
        Assert.Contains("[exit code 1]", failed);
        Assert.Equal(1, CountOf(failed, "[exit code"));
    }

    [Fact]
    public async Task PowerShell_AnExitInTheCommand_StillReportsItsCode()
    {
        if (!OperatingSystem.IsWindows()) return;   // Windows PowerShell is the dialect under test
        var shell = new ShellOverride(ShellDialect.PowerShell, "powershell.exe");

        var result = await RunAsync(shell, "Write-Output before-exit; exit 3");
        Assert.Contains("before-exit", result);
        Assert.Contains("[exit code 3]", result);
        Assert.Equal(1, CountOf(result, "[exit code"));

        var ok = await RunAsync(shell, "Write-Output fine; exit 0");
        Assert.DoesNotContain("[exit code", ok);
    }

    private static int CountOf(string text, string part)
    {
        var n = 0;
        for (var i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + 1, StringComparison.Ordinal)) n++;
        return n;
    }
}
