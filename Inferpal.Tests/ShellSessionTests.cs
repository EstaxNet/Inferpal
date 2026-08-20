using System.IO;
using System.Text;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Services;
using Inferpal.Services.Shell;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

// Axis 7 — persistent shell session (cwd/env preserved across calls) + background jobs.
// The protocol parsing is pure/unit-tested; the integration tests actually spawn the resolved
// PowerShell (powershell.exe on Windows, pwsh elsewhere — every CI matrix runner ships one) to
// prove cwd/env really persist and that a background job can be launched, polled and stopped.
// On a pwsh-less POSIX machine they skip silently: the same contract runs in bash via
// PosixShellTests (§23).
[Collection(ShellSerialCollection.Name)]
public class ShellSessionTests
{
    /// <summary>PS-syntax integration tests only make sense when the resolved shell speaks PowerShell.</summary>
    private static bool PowerShellAvailable => ShellLauncher.Resolve().Dialect == ShellDialect.PowerShell;

    private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));

    private sealed class AutoApprove : IApprovalService
    {
        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct, string? subject = null, DiffInfo? diff = null, bool forcePrompt = false)
            => Task.FromResult(true);
    }

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;

    // ── Pure protocol ────────────────────────────────────────────────────────────

    [Fact]
    public void ParseForeground_SplitsOutputAndState()
    {
        const string marker = "INFERPAL_STATE_abc";
        var stdout =
            "line one\n" +
            "line two\n" +
            marker + "\n" +
            "CWD=" + B64(@"C:\work\sub") + "\n" +
            "ENV=" + B64("FOO") + "|" + B64("bar") + "\n";

        var state = ShellStateProtocol.ParseForeground(stdout, marker);

        Assert.True(state.StateCaptured);
        Assert.Equal("line one\nline two", state.Output);
        Assert.Equal(@"C:\work\sub", state.Cwd);
        Assert.Equal("bar", state.EnvFull["FOO"]);
    }

    [Fact]
    public void ParseForeground_MarkerGluedToUnterminatedOutput_StillSplits()
    {
        // A command whose output has no trailing newline (`printf 'hello-gate'`,
        // `base64 -d`, a native exe) glues the marker to its last line. Seen live on the
        // §23 Linux battery: the raw state block leaked into the visible output and the
        // cwd/env state of the call was silently lost.
        const string marker = "INFERPAL_STATE_abc";
        var stdout =
            "hello-gate" + marker + "\n" +
            "CWD=" + B64("/tmp/sub") + "\n" +
            "ENV=" + B64("FOO") + "|" + B64("bar") + "\n";

        var state = ShellStateProtocol.ParseForeground(stdout, marker);

        Assert.True(state.StateCaptured);
        Assert.Equal("hello-gate", state.Output);
        Assert.Equal("/tmp/sub", state.Cwd);
        Assert.Equal("bar", state.EnvFull["FOO"]);
    }

    [Fact]
    public void ForegroundScripts_TerminatePendingOutputLineBeforeMarker()
    {
        // Belt to the parser's braces: both dialects emit a newline of their own before the
        // marker, so a well-behaved shell never glues it in the first place.
        var env = new Dictionary<string, string>();
        var posix = ShellStateProtocol.BuildForegroundScript(ShellDialect.Posix, "/w", env, "true", "M");
        Assert.Contains("printf '\\n%s\\n' 'M'", posix);
        var ps = ShellStateProtocol.BuildForegroundScript(ShellDialect.PowerShell, @"C:\w", env, "echo hi", "M");
        Assert.Contains("Write-Output ''\n  Write-Output 'M'", ps);
    }

    [Fact]
    public void ParseForeground_WhenMarkerMissing_TreatsAllAsOutput()
    {
        var state = ShellStateProtocol.ParseForeground("just output\n", "INFERPAL_STATE_x");

        Assert.False(state.StateCaptured);
        Assert.Equal("just output", state.Output);
        Assert.Null(state.Cwd);
    }

    [Fact]
    public void ParseForeground_DecodesValuesWithSpecialCharacters()
    {
        const string marker = "M";
        var tricky = "a=b|c\nd"; // contains the '=' and '|' separators and a newline
        var stdout = marker + "\nENV=" + B64("WEIRD") + "|" + B64(tricky) + "\n";

        var state = ShellStateProtocol.ParseForeground(stdout, marker);

        Assert.Equal(tricky, state.EnvFull["WEIRD"]);
    }

    [Fact]
    public void ComputeOverrides_KeepsOnlyAddedOrChangedVars()
    {
        var baseline = new Dictionary<string, string> { ["PATH"] = "/usr/bin", ["HOME"] = "/home" };
        var full     = new Dictionary<string, string> { ["PATH"] = "/usr/bin", ["HOME"] = "/changed", ["NEW"] = "x" };

        var overrides = ShellStateProtocol.ComputeOverrides(baseline, full);

        Assert.False(overrides.ContainsKey("PATH"));       // unchanged → dropped
        Assert.Equal("/changed", overrides["HOME"]);        // changed → kept
        Assert.Equal("x", overrides["NEW"]);                // added → kept
    }

    [Fact]
    public void BuildScripts_EmbedCommand_AndDoNotInjectRawEnvValues()
    {
        var env = new Dictionary<string, string> { ["EVIL"] = "'; rm -rf /; #" };
        var fg  = ShellStateProtocol.BuildForegroundScript(@"C:\x", env, "Get-Date", "MARK");

        Assert.Contains("MARK", fg);
        Assert.Contains("Invoke-Expression", fg);
        // The dangerous value is base64-encoded, never present verbatim in the script.
        Assert.DoesNotContain("rm -rf /", fg);
    }

    // ── Integration (spawns real powershell.exe) ─────────────────────────────────

    private static RunCommandTool NewTool(string root) =>
        new(new AutoApprove(), new InferpalConfig { CommandTimeoutSeconds = 60 }, () => root);

    [Fact]
    public async Task EnvVar_PersistsAcrossCalls()
    {
        if (!PowerShellAvailable) return;
        var tool = NewTool(Path.GetTempPath());

        await tool.ExecuteAsync(Args("""{"command":"$env:INFERPAL_TEST_VAR='hello42'"}"""), CancellationToken.None);
        var result = await tool.ExecuteAsync(Args("""{"command":"Write-Output $env:INFERPAL_TEST_VAR"}"""), CancellationToken.None);

        Assert.Contains("hello42", result);
    }

    [Fact]
    public async Task WorkingDirectory_PersistsAcrossCalls()
    {
        if (!PowerShellAvailable) return;
        var root = Path.Combine(Path.GetTempPath(), "inferpal_shell_" + Guid.NewGuid().ToString("N"));
        var sub  = Path.Combine(root, "sub");
        Directory.CreateDirectory(sub);
        try
        {
            var tool = NewTool(root);

            await tool.ExecuteAsync(Args("""{"command":"Set-Location sub"}"""), CancellationToken.None);
            var result = await tool.ExecuteAsync(Args("""{"command":"(Get-Location).Path"}"""), CancellationToken.None);

            Assert.Contains("sub", result);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Background_StartsPollsAndStops()
    {
        if (!PowerShellAvailable) return;
        var tool = NewTool(Path.GetTempPath());

        var start = await tool.ExecuteAsync(
            Args("""{"command":"Write-Output ready; Start-Sleep -Seconds 30","background":true}"""),
            CancellationToken.None);
        Assert.Contains("bg1", start);

        // Poll until the first line shows up (the job streams output as it runs).
        string poll = string.Empty;
        for (var i = 0; i < 50 && !poll.Contains("ready"); i++)
        {
            await Task.Delay(100);
            poll = await tool.ExecuteAsync(Args("""{"action":"poll","id":"bg1"}"""), CancellationToken.None);
        }
        Assert.Contains("ready", poll);
        Assert.Contains("still running", poll);

        var stop = await tool.ExecuteAsync(Args("""{"action":"stop","id":"bg1"}"""), CancellationToken.None);
        Assert.Contains("Stopped", stop);
    }

    [Fact]
    public async Task Poll_UnknownId_ReturnsError()
    {
        var tool = NewTool(Path.GetTempPath());
        var result = await tool.ExecuteAsync(Args("""{"action":"poll","id":"bg999"}"""), CancellationToken.None);
        Assert.Contains("no background job", result);
    }
}
