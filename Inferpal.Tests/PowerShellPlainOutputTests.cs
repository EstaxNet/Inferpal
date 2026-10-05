using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Services;
using Inferpal.Services.Shell;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  What PowerShell 7 prints for the model is plain text.
//
//  pwsh 7 writes its table headers with ANSI colour sequences — "\e[32;1mPath\e[0m" — even into a
//  redirected stream, and pwsh is the shell this product prefers wherever it is on the PATH (Linux,
//  macOS, and Windows machines that have it). Every run_command, every custom tool, every validator
//  sent those bytes to the model and into the tool bubbles. Windows PowerShell 5.1 has no $PSStyle
//  and writes none.
// ──────────────────────────────────────────────────────────────────────────────────────────────
[Collection(ShellSerialCollection.Name)]
public class PowerShellPlainOutputTests
{
    [Fact]
    public void EveryPowerShellLaunch_AsksForPlainText()
    {
        var psi     = ShellLauncher.BuildStartInfo(ShellDialect.PowerShell, "pwsh", "Get-Location");
        var encoded = psi.Arguments[(psi.Arguments.LastIndexOf(' ') + 1)..];
        var script  = Encoding.Unicode.GetString(Convert.FromBase64String(encoded));

        Assert.Contains("$PSStyle.OutputRendering = 'PlainText'", script, StringComparison.Ordinal);
        // Before the command, or the command's own output is still coloured.
        Assert.True(script.IndexOf("OutputRendering", StringComparison.Ordinal)
                    < script.IndexOf("Get-Location", StringComparison.Ordinal));
    }

    private sealed class Approve : IApprovalService
    {
        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct,
                                               string? subject = null, DiffInfo? diff = null, bool forcePrompt = false)
            => Task.FromResult(true);
    }

    [Fact]
    public async Task ATableFromPwsh7_ReachesTheModel_WithoutEscapeSequences()
    {
        // A custom tool runs its command as written, so the table comes straight from pwsh's formatter.
        var pwsh = ShellLauncher.FindOnPath(OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh");
        if (pwsh is null) return;   // no PowerShell 7 on this machine — measured on the CI legs that have it

        ShellLauncher._overrideForTests = new ShellOverride(ShellDialect.PowerShell, pwsh);
        try
        {
            var output = await new UserShellTool("probe", "Get-Location", new Approve(), new InferpalConfig(),
                                                 () => Environment.CurrentDirectory)
                .ExecuteAsync(JsonDocument.Parse("{}").RootElement, CancellationToken.None);

            // Witness: the table is there — its header names the column.
            Assert.Contains("Path", output, StringComparison.Ordinal);
            Assert.DoesNotContain('\u001b', output);
        }
        finally { ShellLauncher._overrideForTests = null; }
    }

    [Fact]
    public async Task RunCommandsSession_ReachesTheModel_WithoutEscapeSequences()
    {
        var pwsh = ShellLauncher.FindOnPath(OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh");
        if (pwsh is null) return;   // no PowerShell 7 on this machine — measured on the CI legs that have it

        ShellLauncher._overrideForTests = new ShellOverride(ShellDialect.PowerShell, pwsh);
        try
        {
            var session = new ShellSession(() => Environment.CurrentDirectory, new InferpalConfig());
            var output  = await session.RunAsync("Get-Location", workDirOverride: null, CancellationToken.None);

            Assert.Contains("Path", output, StringComparison.Ordinal);   // witness: the table is there
            Assert.DoesNotContain('\u001b', output);
        }
        finally { ShellLauncher._overrideForTests = null; }
    }
}
