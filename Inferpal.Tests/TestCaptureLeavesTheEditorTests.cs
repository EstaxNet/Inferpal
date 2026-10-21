using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// VS Code: the <c>/tdd</c> capture leaves the editor as it found it.
/// </summary>
/// <remarks>
/// ⚠ The capture runs with Just My Code off and stops where the exception is thrown — for a failed assertion, inside
/// the test framework. VS Code opens the source of that frame in front of the user's file, and nothing closed it:
/// after a <c>/tdd</c> run the editor showed xUnit's <c>EqualityAsserts.cs</c>, a file the user never opened, instead
/// of the code being fixed. The bridge notes the tabs and the active editor before the launch, then closes only what
/// the capture opened — never a tab with unsaved changes — and brings the user's editor back. These rules read the
/// TypeScript source.
/// </remarks>
public class TestCaptureLeavesTheEditorTests
{
    [Fact]
    public void TheCapture_NotesTheEditorsBeforeItLaunches_AndPutsThemBackOnceItStops()
    {
        var capture = WebviewRebuildTests.Body(WebviewRebuildTests.TsCode("debugBridge.ts"), "async captureTest(");

        var launch = capture.IndexOf("await vscode.debug.startDebugging(folder", StringComparison.Ordinal);
        var stop   = capture.LastIndexOf("await vscode.debug.stopDebugging(session);", StringComparison.Ordinal);
        Assert.True(launch > 0 && stop > launch, "the capture no longer launches and stops its session here: the rule reads nothing");   // WITNESS

        var noted   = capture.IndexOf("const shownBefore = this.shownEditors();", StringComparison.Ordinal);
        var putBack = capture.IndexOf("await this.putBackEditors(shownBefore);", StringComparison.Ordinal);
        Assert.True(noted >= 0 && noted < launch, "the editors are not noted before the capture opens its own");
        Assert.True(putBack > stop, "what the capture opened stays in front of the user's file");
    }

    [Fact]
    public void OnlyWhatTheCaptureOpened_IsClosed_NeverAnUnsavedTab_AndTheUsersEditorComesBack()
    {
        var bridge = WebviewRebuildTests.TsCode("debugBridge.ts");

        var noted = WebviewRebuildTests.Body(bridge, "private shownEditors(");
        Assert.Contains("vscode.window.tabGroups.all", noted);
        Assert.Contains("vscode.window.activeTextEditor?.document.uri", noted);

        var putBack = WebviewRebuildTests.Body(bridge, "private async putBackEditors(");
        Assert.Contains("!before.open.has(", putBack);   // a tab the user had open before stays open
        Assert.Contains("!t.isDirty", putBack);           // closing it would ask about a file they never opened
        Assert.Contains("vscode.window.tabGroups.close(", putBack);
        Assert.Contains("vscode.window.showTextDocument(before.active", putBack);
    }

    [Fact]
    public void AWorkspaceFileOpenedDuringTheCapture_StaysOpen_AndInFront()
    {
        // The capture can take minutes (the entry wait, up to twenty resumes): a file of the workspace the user opened to
        // read meanwhile is theirs, not the debugger's — the debugger opens the framework's source, outside the workspace.
        var putBack = WebviewRebuildTests.Body(WebviewRebuildTests.TsCode("debugBridge.ts"), "private async putBackEditors(");

        Assert.Contains("!vscode.workspace.getWorkspaceFolder(t.input.uri)", putBack);
        // The editor that was in front comes back only when the one in front now is a tab the capture closed.
        Assert.Contains("const frontClosed", putBack);
        Assert.Contains("if (before.active && frontClosed)", putBack);
    }
}
