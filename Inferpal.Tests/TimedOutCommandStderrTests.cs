using System.IO;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Services;
using Inferpal.Services.Shell;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  A command killed at its budget hands back its stderr too.
//
//  run_command salvaged the partial stdout of a timed-out command and dropped what it had written to stderr — where a
//  build or a test run writes its errors (cargo, pytest's tracebacks, npm). The custom shell tool kept both.
// ──────────────────────────────────────────────────────────────────────────────────────────────
[Collection(ShellSerialCollection.Name)]
public sealed class TimedOutCommandStderrTests
{
    private sealed class AutoApprove : IApprovalService
    {
        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct, string? subject = null,
            DiffInfo? diff = null, bool forcePrompt = false) => Task.FromResult(true);
    }

    [Fact]
    public async Task AKilledCommand_KeepsWhatItWroteToStderr()
    {
        var command = ShellLauncher.Resolve().Dialect == ShellDialect.PowerShell
            ? "Write-Output 'line-on-stdout'; [Console]::Error.WriteLine('boom-on-stderr'); Start-Sleep -Seconds 30"
            : "echo line-on-stdout; echo boom-on-stderr 1>&2; sleep 30";
        var tool = new RunCommandTool(new AutoApprove(), new InferpalConfig { CommandTimeoutSeconds = 3 }, Path.GetTempPath);

        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { command }), CancellationToken.None);

        Assert.Contains("timed out after 3s", result);                       // WITNESS: it was killed
        Assert.Contains("line-on-stdout", result);
        Assert.Contains("boom-on-stderr", result);
        Assert.Contains("[stderr]", result);
    }
}
