using System.IO;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A file attached as a VS Code chip is skipped by the RAG auto-context, as in Visual Studio: the host receives the
/// file's path, never the chip's label.
/// </summary>
/// <remarks>
/// <c>attachedPaths</c> carried the chip's NAME (<c>📄 src/x.ts</c>), which the host resolves against the root — a path
/// that names no file, so the auto-context injected chunks of a file whose whole content was already in the prompt.
/// Visual Studio dedupes on <c>AttachmentItem.SourcePath</c>, set for whole-file attachments only: a selection or a
/// synthetic chip (clipboard, problems) is not that file, and claiming it would drop chunks the prompt does not hold.
/// </remarks>
public class ChipAttachmentDedupeTests
{
    private static string Chat()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Inferpal.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var path = Path.Combine(dir!.FullName, "vscode", "src", "chatViewProvider.ts");
        Assert.True(File.Exists(path), "chatViewProvider.ts moved: this test reads nothing.");
        return SettingsSchemaDriftTests.NeutralizeTypeScriptComments(File.ReadAllText(path));
    }

    [Fact]
    public void TheHost_ReceivesTheChipsFilePath_NeverItsLabel()
    {
        var chat = Chat();
        Assert.Contains("for (const a of chips)", chat, StringComparison.Ordinal);   // witness: the chips the turn sends

        Assert.Contains("attachedPaths.push(a.sourcePath)", chat, StringComparison.Ordinal);
        Assert.DoesNotContain("attachedPaths.push(a.name)", chat, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyWholeFileChips_ClaimAPath()
    {
        var chat = Chat();
        // The active file and a picked file carry their path...
        Assert.Contains("editor.document.getText(),\n                     editor.document.uri.fsPath)", chat.Replace("\r\n", "\n"), StringComparison.Ordinal);
        Assert.Contains("doc.getText(), picked[0].fsPath)", chat, StringComparison.Ordinal);
        // ...a selection does not (reference arm: claiming it would drop chunks the prompt does not hold).
        Assert.Contains("this.addChip('✂ ' + vscode.workspace.asRelativePath(editor.document.uri), text);", chat, StringComparison.Ordinal);
    }
}
