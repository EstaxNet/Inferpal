using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Inferpal.Config;
using Inferpal.Models;
using Inferpal.Services;
using Inferpal.Services.CodeActions;
using Inferpal.Services.Shell;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Line endings of a CRLF file — the default under Visual Studio — in what edits write, and
/// restoring a file from its history.
/// </summary>
public sealed class EditLineEndingAndRestoreTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "inferpal-edits-" + Guid.NewGuid().ToString("N"));

    public EditLineEndingAndRestoreTests() => Directory.CreateDirectory(_base);

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); } catch { /* best-effort */ }
    }

    // ── T4 — apply_diff on a CRLF file ─────────────────────────────────────────

    /// <summary>
    /// An LF <c>old_content</c> ending with a newline added an empty line to the target of the
    /// tolerant fallback: the edit failed as "not found — check the whitespace".
    /// </summary>
    [Fact]
    public void ApplyDiff_OldContentInLfWithTrailingNewline_MatchesACrlfFile_AndKeepsCrlf()
    {
        var r = ApplyDiffMatcher.Resolve("a\r\nb\r\nc\r\n", "b\n", "B\n", null);

        Assert.Equal("a\r\nB\r\nc\r\n", r.Modified);
    }

    /// <summary>When it did match, the replacement went into a CRLF file in LF: mixed line endings,
    /// and the last replaced line lost its <c>\r</c>.</summary>
    [Fact]
    public void ApplyDiff_MultiLineEditOnACrlfFile_KeepsCrlfEverywhere()
    {
        var r = ApplyDiffMatcher.Resolve("a\r\nb\r\nc\r\n", "a\nb", "A\nB", null);

        Assert.Equal("A\r\nB\r\nc\r\n", r.Modified);
    }

    [Fact]
    public void ApplyDiff_SingleLineMatchReplacedByMultipleLines_OnACrlfFile_KeepsCrlf()
    {
        var r = ApplyDiffMatcher.Resolve("x\r\ny\r\n", "x", "x1\nx2", null);

        Assert.Equal("x1\r\nx2\r\ny\r\n", r.Modified);
    }

    /// <summary>Witness: an LF file stays LF.</summary>
    [Fact]
    public void ApplyDiff_OnALfFile_StaysLf()
    {
        var r = ApplyDiffMatcher.Resolve("a\nb\nc\n", "b\n", "B\n", null);

        Assert.Equal("a\nB\nc\n", r.Modified);
    }

    // ── T12 — blank old_content, tolerant ambiguity ────────────────────────────

    /// <summary>A blank <c>old_content</c> matched, through the tolerant fallback, the file's only empty
    /// line — which was replaced.</summary>
    [Fact]
    public void ApplyDiff_BlankOldContent_MatchesNothing()
    {
        var r = ApplyDiffMatcher.Resolve("a\n\nb", "   ", "X", null);

        Assert.Null(r.Modified);
    }

    /// <summary>Several tolerant matches were reported as "not found" instead of "ambiguous": the model
    /// looked for a whitespace mistake that did not exist.</summary>
    [Fact]
    public void ApplyDiff_SeveralTolerantMatches_AreReportedAsAmbiguous()
    {
        var r = ApplyDiffMatcher.Resolve("  foo\nbar\n    foo\n", "foo ", "baz", null);

        Assert.Null(r.Modified);
        Assert.Equal(2, r.Count);
    }

    // ── T10 — code actions on a CRLF document ──────────────────────────────────

    private static Task<CodeActionRun> RunCodeActionAsync(string reply, string docText) =>
        CodeActionPipeline.RunAsync(new FakeInferenceProvider { ChatResult = new ChatTurnResult(reply, null, 0, 0) },
                                    "m", "system", "instruction", docText, 0, 0, true, CancellationToken.None);

    /// <summary>The model sends the code back unchanged, in LF, for a CRLF document: that is not an
    /// edit, it is "nothing to change".</summary>
    [Fact]
    public async Task CodeAction_AnUnchangedEchoInLf_OnACrlfDocument_IsNoChange()
    {
        var run = await RunCodeActionAsync("int x = 1;\nint y = 2;", "int x = 1;\r\nint y = 2;");

        Assert.Equal(CodeActionOutcome.NoChangeNeeded, run.Outcome);
    }

    [Fact]
    public async Task CodeAction_AnEditInLf_OnACrlfDocument_IsWrittenInCrlf()
    {
        var run = await RunCodeActionAsync("A\nB", "a\r\nb");

        Assert.Equal("A\r\nB", run.NewDocText);
    }

    // ── T9 — restore_file ──────────────────────────────────────────────────────

    private sealed class Approve : IApprovalService
    {
        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct,
                                               string? subject = null, DiffInfo? diff = null, bool forcePrompt = false)
            => Task.FromResult(true);
    }

    private static Task<string> RestoreAsync(RestoreFileTool tool, object args) =>
        tool.ExecuteAsync(JsonDocument.Parse(JsonSerializer.Serialize(args)).RootElement.Clone(), CancellationToken.None);

    /// <summary>
    /// Backups live under the GIT root, confinement happens under the SOLUTION root: with
    /// <c>repo\src\App.sln</c>, the "Backup: …" path every write hands the model was refused as
    /// "outside the workspace" by <c>restore_file</c>.
    /// </summary>
    [Fact]
    public async Task RestoreFile_ASnapshotKeptAtTheGitRoot_CanBeRestoredFromASolutionBelowIt()
    {
        Directory.CreateDirectory(Path.Combine(_base, ".git"));
        var ws   = Path.Combine(_base, "src", "app");
        Directory.CreateDirectory(ws);
        var file = Path.Combine(ws, "a.txt");
        File.WriteAllText(file, "v1");
        var history = new FileHistoryService();
        var snap    = await history.SnapshotAsync(file, CancellationToken.None);
        File.WriteAllText(file, "v2");

        string result;
        try { result = await RestoreAsync(new RestoreFileTool(new Approve(), history, () => ws), new { path = file, snapshot = snap }); }
        catch (Exception ex) { result = ex.Message; }

        Assert.Equal("v1", File.ReadAllText(file));
    }

    /// <summary>Security witness: an arbitrary file outside the workspace is not a backup.</summary>
    [Fact]
    public async Task RestoreFile_AnArbitraryFileOutsideTheWorkspace_IsNotAcceptedAsASnapshot()
    {
        Directory.CreateDirectory(Path.Combine(_base, ".git"));
        var ws   = Path.Combine(_base, "src", "app");
        Directory.CreateDirectory(ws);
        var file   = Path.Combine(ws, "a.txt");
        var secret = Path.Combine(_base, "secret.txt");
        File.WriteAllText(file, "mine");
        File.WriteAllText(secret, "SECRET");

        try { await RestoreAsync(new RestoreFileTool(new Approve(), new FileHistoryService(), () => ws), new { path = file, snapshot = secret }); }
        catch { /* refused */ }

        Assert.Equal("mine", File.ReadAllText(file));
    }

    /// <summary>
    /// Two <c>restore_file</c> calls without a named backup went back and forth: the second took the
    /// backup the FIRST had just taken, instead of stepping back once more.
    /// </summary>
    [Fact]
    public async Task RestoreFile_CalledTwice_StepsBackTwice_InsteadOfTogglingBack()
    {
        var file    = Path.Combine(_base, "a.txt");
        var history = new FileHistoryService();
        File.WriteAllText(file, "v0");
        await history.SnapshotAsync(file, CancellationToken.None);
        File.WriteAllText(file, "v1");
        await Task.Delay(20);
        await history.SnapshotAsync(file, CancellationToken.None);
        File.WriteAllText(file, "v2");
        var tool = new RestoreFileTool(new Approve(), history, () => _base);

        await Task.Delay(20);
        await RestoreAsync(tool, new { path = file });
        Assert.Equal("v1", File.ReadAllText(file));

        await Task.Delay(20);
        await RestoreAsync(tool, new { path = file });
        Assert.Equal("v0", File.ReadAllText(file));
    }
}

/// <summary>
/// A shell process that has finished is not a process whose pipes are closed, and a non-zero exit
/// code is not a silent success.
/// </summary>
[Collection(ShellSerialCollection.Name)]
public class ShellExitRegressionTests
{
    private static bool PowerShell => ShellLauncher.Resolve().Dialect == ShellDialect.PowerShell;

    /// <summary>
    /// A grandchild started in the background (<c>cmd &amp;</c>, <c>start /b</c>) inherits the output:
    /// the child had finished, and reading waited for the grandchild to end — past the timeout and
    /// past Stop, which reached neither.
    /// </summary>
    [Fact]
    public async Task ChildProcess_ReturnsWhenTheChildExits_EvenIfABackgroundGrandchildHoldsItsOutput()
    {
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", "/c start /b ping -n 25 127.0.0.1 >nul & echo started")
            : new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", "sleep 25 & echo started" } };

        var sw  = Stopwatch.StartNew();
        var run = await ChildProcess.RunAsync(psi, TimeSpan.FromSeconds(60), CancellationToken.None);
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), $"returned after {sw.Elapsed}");
        Assert.Contains("started", run.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunCommand_ReturnsWhenTheShellExits_EvenIfABackgroundGrandchildHoldsItsOutput()
    {
        var session = new ShellSession(() => Path.GetTempPath(), new InferpalConfig());
        var command = PowerShell
            ? "cmd /c \"start /b ping -n 25 127.0.0.1 >nul\"; Write-Output started"
            : "sleep 25 & echo started";

        var sw     = Stopwatch.StartNew();
        var output = await session.RunAsync(command, null, CancellationToken.None);
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), $"returned after {sw.Elapsed}");
        Assert.Contains("started", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The exit code was never reported: the PowerShell wrapper exits 0 through its <c>finally</c>,
    /// the POSIX one overwrites <c>$?</c> with its <c>printf</c>s. A failing, silent
    /// <c>git diff --quiet</c> or <c>test -f x</c> returned "nothing", read as a success.
    /// </summary>
    [Fact]
    public async Task RunCommand_ANonZeroExitCode_IsReported()
    {
        var session = new ShellSession(() => Path.GetTempPath(), new InferpalConfig());

        var output = await session.RunAsync(PowerShell ? "cmd /c exit 3" : "(exit 3)", null, CancellationToken.None);

        Assert.Contains("exit code 3", output, StringComparison.Ordinal);
    }

    /// <summary>Witness: a success carries no exit-code note.</summary>
    [Fact]
    public async Task RunCommand_ASuccess_CarriesNoExitCodeNote()
    {
        var session = new ShellSession(() => Path.GetTempPath(), new InferpalConfig());

        var output = await session.RunAsync(PowerShell ? "cmd /c exit 0" : "true", null, CancellationToken.None);

        Assert.DoesNotContain("exit code", output, StringComparison.Ordinal);
    }
}
