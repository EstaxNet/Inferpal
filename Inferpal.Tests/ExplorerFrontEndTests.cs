using System.IO;
using System.Text.Json;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The Explorer's Inferpal submenu — Solution Explorer's project, folder and file menus, VS Code's file explorer:
/// Explain asks the Core's question without tools, Review Changes sends <c>/check</c> on the item, Attach to Chat makes
/// the <c>@file</c> / <c>@folder</c> chip — each with the item clicked.
/// </summary>
/// <remarks>The menus and the sends are held at the source: no test drives an editor. The Core's half is tested where it
/// runs (<see cref="ExplorerExplainTests"/>, <see cref="CheckPathScopeTests"/>, <c>HostServerTests.ExplorerExplain_*</c>).</remarks>
public sealed class ExplorerFrontEndTests
{
    private static string Root => ConversationPersistenceSilenceTests.RepoRoot();

    [Fact]
    public void VisualStudiosSubmenu_SitsInTheProjectFolderAndFileMenus()
    {
        var menus    = ConventionCoverageTests.CodeOnly(Path.Combine(Root, "Inferpal", "Commands", "VsctMenus.cs"));
        var commands = ConventionCoverageTests.CodeOnly(Path.Combine(Root, "Inferpal", "Commands", "SolutionExplorerCommands.cs"));

        // guidSHLMainMenu / IDM_VS_CTXT_PROJNODE, ITEMNODE, FOLDERNODE (vsshlids.h).
        foreach (var (menu, id) in new[] { ("ProjectContext", "0x0402"), ("ItemContext", "0x0430"), ("FolderContext", "0x0431") })
        {
            Assert.Matches($@"{menu}\s*=\s*{id};", menus);
            Assert.Matches($@"GroupPlacement\.VsctParent\(VsctMenus\.ShlMainMenu, VsctMenus\.{menu}, 0\)\)\s*\{{\s*Children = \[GroupChild\.Menu\(Submenu\)\]",
                           commands);
        }
        Assert.Matches(@"MenuChild\.Command<ExplainItemCommand>\(\),\s*MenuChild\.Command<ReviewItemCommand>\(\),\s*MenuChild\.Command<AttachItemCommand>\(\)",
                       commands);
    }

    [Fact]
    public void VisualStudiosCommands_SendTheChosenItem_EachTheWayItsGestureNeeds()
    {
        var commands = ConventionCoverageTests.CodeOnly(Path.Combine(Root, "Inferpal", "Commands", "SolutionExplorerCommands.cs"));

        // The item from the out-of-process client context; a node without a file is said.
        Assert.Matches(@"GetSelectedPathAsync\(ct\) is \{ IsFile: true \} uri[\s\S]*?ShowPromptAsync\(Strings\.ExplorerNoSelection", commands);
        // Explain: the ignore files, a budget for the code-actions model, a refusal said, else a code action (never a null
        // model: that is the chat with its tools).
        Assert.Matches(@"WorkspaceExclusions\.Follow\(\)[\s\S]*?ModelRole\.CodeActions[\s\S]*?ExplorerExplain\.ForAsync\(ExplorerExplain\.ItemOf\(path\)[\s\S]*?"
                       + @"CodeExcerpt\.BudgetFor\(window\)[\s\S]*?ShowPromptAsync\(refusal[\s\S]*?"
                       + @"SetPendingPrompt\(ask\.Question, modelOverride: _config\.CodeActionsModel", commands);
        // Review: /check on the item, a project being its folder.
        Assert.Contains("SetPendingPrompt($\"/check \\\"{ExplorerExplain.ItemOf(path)}\\\"\")", commands, StringComparison.Ordinal);
        // Attach: the ignore files declared, then the chat window makes the chip.
        Assert.Matches(@"WorkspaceExclusions\.Follow\(\);\s*_contextHolder\.SetPendingAttach\(ExplorerExplain\.ItemOf\(path\)\)", commands);
    }

    [Fact]
    public void VisualStudiosChatWindow_MakesTheChipOfAPendingAttachment_WithoutTouchingTheDraft()
    {
        var mentions     = ConventionCoverageTests.CodeOnly(Path.Combine(Root, "Inferpal", "ToolWindow", "InferpalToolWindowData.Mentions.cs"));
        var construction = ConventionCoverageTests.CodeOnly(Path.Combine(Root, "Inferpal", "ToolWindow", "InferpalToolWindowData.Construction.cs"));

        Assert.Matches(@"_contextHolder\.ConsumePendingAttach\(\)[\s\S]*?SelectFolderMentionAsync\(path, CancellationToken\.None, typed: false\)"
                       + @"[\s\S]*?SelectFileMentionAsync\(path, CancellationToken\.None, typed: false\)", mentions);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(mentions, @"if \(typed\) StripMentionToken\(\);").Count);
        // A binary file: no text — refused, never its bytes as a chip.
        Assert.Contains("MentionController.FileRefusal(filePath, _indexService.RootDir) ?? MentionController.BinaryRefusal(filePath)",
                        mentions, StringComparison.Ordinal);
        // Asked before the window existed, or while it is open.
        Assert.Matches(@"PendingAttachAvailable \+= \(_, _\) => ConsumePendingAttach\(\);\s*ConsumePendingAttach\(\);", construction);
    }

    [Fact]
    public void VsCodesExplorer_HasTheSubmenu_AndItsCommandsStayOutOfThePalette()
    {
        using var package = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "vscode", "package.json")));
        var contributes = package.RootElement.GetProperty("contributes");
        var menus = contributes.GetProperty("menus");

        Assert.Equal("inferpal.explorer", menus.GetProperty("explorer/context").EnumerateArray().Single().GetProperty("submenu").GetString());
        Assert.Equal(["inferpal.explainItem", "inferpal.reviewItem", "inferpal.attachItem"],
                     menus.GetProperty("inferpal.explorer").EnumerateArray().Select(m => m.GetProperty("command").GetString()));
        foreach (var command in new[] { "inferpal.explainItem", "inferpal.reviewItem", "inferpal.attachItem" })
            Assert.Equal("false", menus.GetProperty("commandPalette").EnumerateArray()
                                       .Single(m => m.GetProperty("command").GetString() == command).GetProperty("when").GetString());
    }

    [Fact]
    public void VsCodesGestures_SendTheItemClicked()
    {
        var provider = Path.Combine(Root, "vscode", "src", "chatViewProvider.ts");

        Assert.Contains("this.askWithChip((host) => host.explorerExplain(uri.fsPath, this.model || undefined), 'explain', true)",
                        ConversationPersistenceSilenceTests.TsMethodBody(provider, "explainItem(uri: vscode.Uri)"), StringComparison.Ordinal);
        Assert.Contains("this.runSlashCommand(`/check \"${uri.fsPath}\"`)",
                        ConversationPersistenceSilenceTests.TsMethodBody(provider, "reviewItem(uri: vscode.Uri)"), StringComparison.Ordinal);
        var attach = ConversationPersistenceSilenceTests.TsMethodBody(provider, "async attachItem(uri: vscode.Uri)");
        Assert.Matches(@"FileType\.Directory\)\s*\{\s*await this\.resolveMention\('folder', uri\.fsPath\)", attach);
        Assert.Contains("this.addChip('📄 ' + vscode.workspace.asRelativePath(uri), doc.getText(), uri.fsPath, uri.fsPath)", attach,
                        StringComparison.Ordinal);
        Assert.Contains("this.gestureFailed('attach to chat', err)", attach, StringComparison.Ordinal);
    }
}
