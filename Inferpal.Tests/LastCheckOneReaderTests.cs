using System.IO;
using System.Text.Json;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services.Agent;
using Inferpal.Services.Editor;
using Inferpal.Services.Presentation;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The last check of a turn has ONE reader: the end-of-turn notice and the run's result bar say the same thing.
/// </summary>
/// <remarks>
/// Two defects of one kind. The result bar ignored a test run through the shell that the notice counted — "the last
/// test or build of this turn failed" printed beside "✓ build passed". And VS Code's Problems panel, served by
/// <c>get_diagnostics</c> only when it lists an error, read as "not built": with the source between the severity and
/// the code (<c>error ts 2304:</c>), no error line matched — no notice, and a ✓ on the run of a turn that ended on a
/// file that no longer compiles.
/// </remarks>
[Collection(CultureSerialCollection.Name)]   // reads localized notes
public class LastCheckOneReaderTests
{
    private sealed class PanelEditor(string panel) : IEditorSurface
    {
        public bool IsAvailable => true;
        public string? ActiveDocumentPath => null;
        public IReadOnlyList<string> GetOpenDocumentPaths() => [];
        public Task<ActiveDocument?> GetActiveDocumentAsync(CancellationToken ct) => Task.FromResult<ActiveDocument?>(null);
        public Task<string?> InsertAtCursorAsync(string text, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task<EditorEditResult?> ReplaceSelectionAsync(string text, CancellationToken ct) => Task.FromResult<EditorEditResult?>(null);
        public Task<string?> GetEditorDiagnosticsAsync(CancellationToken ct) => Task.FromResult<string?>(panel);
    }

    private static ToolExecution Edit(string note) =>
        new("apply_diff", "{\"path\":\"src/a.ts\"}", "Applied 1 edit to src/a.ts.\n\n" + note);

    private static ToolExecution Shell(string command, int exitCode) =>
        new("run_command", JsonSerializer.Serialize(new { command }),
            exitCode == 0 ? "ok" : $"1 failed\n[exit code {exitCode}]");

    [Fact]
    public async Task VsCodesProblemsPanel_ListingAnError_IsAFailedBuild_ForTheNoticeAndTheBar()
    {
        // The exact line shape vscode/src/editorBridge.ts writes.
        var panel  = "src/a.ts(1,1): error ts 2304: Cannot find name 'foo'.";
        var answer = await new GetDiagnosticsTool(new PanelEditor(panel), () => Path.GetTempPath())
            .ExecuteAsync(JsonDocument.Parse("{}").RootElement, CancellationToken.None);
        Assert.StartsWith(Strings.DiagFromEditor, answer);                     // witness: the panel answered

        var turn = new[] { Edit(Strings.SmartFixBuildOk), new ToolExecution("get_diagnostics", "{}", answer) };

        Assert.Equal(GetDiagnosticsTool.BuildVerdict.Errors, GetDiagnosticsTool.ReadVerdict(answer));
        Assert.True(ChatTurnPolicy.LastCheckFailed(turn, filesChangedInRun: 1));
        Assert.Equal(RunCheck.BuildFailed, RunSummary.LastCheck(turn));
        Assert.True(GetDiagnosticsTool.OutputHasErrors(answer));               // "Fix with AI" is offered
    }

    [Fact]
    public void ATestRunThroughTheShell_IsTheBarsLastCheckToo()
    {
        var turn = new[] { Edit(Strings.SmartFixBuildOk), Shell("dotnet test", 1) };

        Assert.True(ChatTurnPolicy.LastCheckFailed(turn, filesChangedInRun: 1));
        Assert.Equal(RunCheck.TestsFailed, RunSummary.LastCheck(turn));
    }

    [Fact]
    public void ABuildThroughTheShell_ThatPassedAfterFailingTests_IsABuildPassed()
    {
        var turn = new[]
        {
            new ToolExecution("run_tests", "{}", "✗ FAILED — Failed: 1, Passed: 2, Skipped: 0, Total: 3"),
            Shell("cargo build", 0),
        };

        Assert.False(ChatTurnPolicy.LastCheckFailed(turn, filesChangedInRun: 1));
        Assert.Equal(RunCheck.BuildPassed, RunSummary.LastCheck(turn));
    }

    [Fact]
    public void AnOrdinaryCommand_IsNoCheck_AndTheEditsBuildStaysTheVerdict()
    {
        // Reference arm: what was read before is read the same.
        var turn = new[] { Edit(Strings.SmartFixBuildOk), Shell("ls -la", 0) };

        Assert.False(ChatTurnPolicy.LastCheckFailed(turn, filesChangedInRun: 1));
        Assert.Equal(RunCheck.BuildPassed, RunSummary.LastCheck(turn));
        Assert.Equal(RunCheck.None, RunSummary.LastCheck([new ToolExecution("read_file", "{}", "x")]));
    }
}
