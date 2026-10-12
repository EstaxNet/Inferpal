using System.IO;
using Inferpal.Services.Persistence;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Visual Studio and VS Code on one folder each find their own last conversation: the auto-save slot is per workspace
/// AND per editor.
/// </summary>
/// <remarks>
/// ⚠ Both editors open on one repository have the same workspace root, and the slot was the workspace's. The editor that
/// wrote last owned it: Visual Studio closed after a conversation, the next question in VS Code wrote over it, and Visual
/// Studio reopened on VS Code's conversation — its own gone, never archived. VS Code's "new conversation" emptied the
/// slot even when it held Visual Studio's. And a restarted VS Code host took Visual Studio's session name from it, so its
/// next <c>/branch</c> rewrote Visual Studio's named session.
/// </remarks>
public sealed class AutoSavePerEditorTests : IDisposable
{
    private readonly string _dir  = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"autosave-editor-{Guid.NewGuid():N}");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "inferpal-tests", "SameRepo");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static List<SavedMessage> Turn(string question) =>
        [new("user", question), new("assistant", "answer to " + question)];

    private ConversationStore VisualStudio() => new(_dir, ConversationStore.VisualStudioSlot);
    private ConversationStore VsCode() => new(_dir, ConversationStore.VsCodeSlot);

    [Fact]
    public async Task BothEditorsOnOneFolder_EachGetTheirOwnLastConversationBack()
    {
        await VisualStudio().AutoSaveAsync(Turn("asked in Visual Studio"), CancellationToken.None, _root, currentName: "vs-session");
        await VsCode().AutoSaveAsync(Turn("asked in VS Code"), CancellationToken.None, _root);

        var vs = await VisualStudio().LoadAutoSaveAsync(_root, CancellationToken.None);
        Assert.Equal("asked in Visual Studio", vs!.Messages[0].Content);
        Assert.Equal("vs-session", vs.CurrentName);
        // VS Code's slot does not carry Visual Studio's session name: its /branch cannot rewrite that file.
        Assert.Null((await VsCode().LoadAutoSaveAsync(_root, CancellationToken.None))!.CurrentName);
    }

    [Fact]
    public async Task ANewConversationInOneEditor_DoesNotEmptyTheOthers()
    {
        await VisualStudio().AutoSaveAsync(Turn("asked in Visual Studio"), CancellationToken.None, _root);
        await VsCode().AutoSaveAsync(Turn("asked in VS Code"), CancellationToken.None, _root);

        await VsCode().ForgetAutoSaveAsync(_root, CancellationToken.None);

        Assert.Empty((await VsCode().LoadAutoSaveAsync(_root, CancellationToken.None))!.Messages);
        Assert.Equal("asked in Visual Studio", (await VisualStudio().LoadAutoSaveAsync(_root, CancellationToken.None))!.Messages[0].Content);
    }

    [Fact]
    public async Task TheSlotBothEditorsShared_StillComesBack_UntilAnEditorWritesItsOwn()
    {
        // Continuity: the slot an earlier version wrote for the workspace is each editor's last conversation.
        await new ConversationStore(_dir).AutoSaveAsync(Turn("asked before the update"), CancellationToken.None, _root);

        Assert.Equal("asked before the update",
                     (await VisualStudio().LoadAutoSaveAsync(_root, CancellationToken.None))!.Messages[0].Content);

        await VisualStudio().AutoSaveAsync(Turn("asked after"), CancellationToken.None, _root);
        Assert.Equal("asked after", (await VisualStudio().LoadAutoSaveAsync(_root, CancellationToken.None))!.Messages[0].Content);
        // A conversation discarded in VS Code does not come back from the old slot.
        await VsCode().ForgetAutoSaveAsync(_root, CancellationToken.None);
        Assert.Empty((await VsCode().LoadAutoSaveAsync(_root, CancellationToken.None))!.Messages);
    }

    [Fact]
    public void EachEditor_WritesItsOwnSlot()
    {
        var root = ConversationPersistenceSilenceTests.RepoRoot();
        var vs   = ConventionCoverageTests.CodeOnly(Path.Combine(root, "Inferpal", "ToolWindow", "InferpalToolWindowData.cs"));
        var host = ConventionCoverageTests.CodeOnly(Path.Combine(root, "Inferpal.Host", "HostSession.cs"));

        Assert.Contains("_store = new(slotOwner: ConversationStore.VisualStudioSlot);", vs);
        Assert.Contains("Store { get; } = new(slotOwner: ConversationStore.VsCodeSlot);", host);
        Assert.NotEqual(ConversationStore.VisualStudioSlot, ConversationStore.VsCodeSlot);
    }
}
