using System.IO;
using Inferpal.Services.Persistence;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Visual Studio builds its chat window before the solution is known: nothing is restored then, and the first question's
/// save, bound to the solution that opened meanwhile, replaced that solution's conversation of the day before. A
/// conversation that never read a slot archives what the slot kept before writing it; once the root is known, the slot is
/// restored while nothing was asked; and a question is kept as it is sent.
/// </summary>
public sealed class UnreadSlotKeptTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"unread-slot-{Guid.NewGuid():N}");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "inferpal-tests", "Billing");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static List<SavedMessage> Turn(string question) => [new("user", question), new("assistant", "an answer")];

    [Fact]
    public async Task YesterdaysConversation_IsArchived_BeforeTheSlotIsWrittenOver()
    {
        var store = new ConversationStore(_dir, "vs");
        await store.AutoSaveAsync(Turn("what does the invoice total do"), CancellationToken.None, _root);

        var archived = await ConversationArchive.KeepUnreadSlotAsync(store, _root, CancellationToken.None);
        await store.AutoSaveAsync(Turn("today's question"), CancellationToken.None, _root);

        Assert.NotNull(archived);
        Assert.Equal("what does the invoice total do", archived!.FirstQuestion);
        var kept = await store.LoadAsync(archived.Name, CancellationToken.None);
        Assert.Equal("what does the invoice total do", kept!.Messages[0].Content);
        Assert.Equal("today's question", (await store.LoadAutoSaveAsync(_root, CancellationToken.None))!.Messages[0].Content);
    }

    [Fact]
    public async Task NoSlot_OrASlotWithoutAQuestion_ArchivesNothing()
    {
        var store = new ConversationStore(_dir, "vs");
        Assert.Null(await ConversationArchive.KeepUnreadSlotAsync(store, _root, CancellationToken.None));

        await store.AutoSaveAsync([new("assistant", "a notice", SessionManager.NoticeMarker)], CancellationToken.None, _root);
        Assert.Null(await ConversationArchive.KeepUnreadSlotAsync(store, _root, CancellationToken.None));
        Assert.Empty(store.ListSessions().Where(n => n != ConversationStore.AutoSaveSlot));
    }

    /// <summary>A slot that cannot be read now (held by another writer) is not "empty": the keep throws, and the caller
    /// writes nothing over it. A DAMAGED slot throws the exception that names the copy the store kept of it, for the
    /// window to say.</summary>
    [Fact]
    public async Task ASlotThatCannotBeReadNow_Throws_ADamagedOne_NamesItsCopy()
    {
        var store = new ConversationStore(_dir, "vs");
        await store.AutoSaveAsync(Turn("held"), CancellationToken.None, _root);
        var file = store.AutoSaveFile(_root);

        if (OperatingSystem.IsWindows())   // FileShare.None is advisory elsewhere
        {
            using var held = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            await Assert.ThrowsAnyAsync<IOException>(() => ConversationArchive.KeepUnreadSlotAsync(store, _root, CancellationToken.None));
        }

        File.WriteAllText(file, "{ not json");
        var damaged = await Assert.ThrowsAsync<UnreadableSessionException>(
            () => ConversationArchive.KeepUnreadSlotAsync(store, _root, CancellationToken.None));
        Assert.NotNull(damaged.Copy);
        Assert.True(File.Exists(damaged.Copy));
    }

    private static string Code(params string[] path) =>
        ConventionCoverageTests.CodeOnly(Path.Combine([ConventionCoverageTests.RepoRoot(), .. path]));

    /// <summary>The window does not run in the suite: its gestures are held on its source.</summary>
    [Fact]
    public void VisualStudio_ArchivesAnUnreadSlot_RestoresItLate_AndKeepsTheQuestionAsItLeaves()
    {
        var save = Code("Inferpal", "ToolWindow", "InferpalToolWindowData.PendingPrompt.cs");
        var binds = save.IndexOf("bindsNow = !_conversationRoot.IsBound;", StringComparison.Ordinal);
        var keep  = save.IndexOf("kept = await ConversationArchive.KeepUnreadSlotAsync(_store, root, CancellationToken.None);", StringComparison.Ordinal);
        var write = save.IndexOf("await _store.AutoSaveAsync(snapshot", StringComparison.Ordinal);
        Assert.True(binds >= 0 && keep > binds && write > keep, "the save writes the slot before keeping what it held");
        // A keep that failed unbinds again: nothing is written over the slot, the next save tries again.
        Assert.Contains("await RunOnVMContextAsync(_conversationRoot.Discarded);", save, StringComparison.Ordinal);
        Assert.Contains("Strings.SessionSlotKept(kept.Name)", save, StringComparison.Ordinal);
        Assert.Contains("ChatMessageItem.NoticeMsg(damaged.Message)", save, StringComparison.Ordinal);
        // The archive's title waits for the answer: a model call during the turn competes with it.
        Assert.Contains("_ = RetitleAfterTheTurnAsync(kept);", save, StringComparison.Ordinal);

        var connection = Code("Inferpal", "ToolWindow", "InferpalToolWindowData.Connection.cs");
        var pin = connection.IndexOf("PinWorkspaceRoot();", StringComparison.Ordinal);
        Assert.True(pin >= 0 && connection.IndexOf("await RestoreLateSlotAsync(ct);", pin, StringComparison.Ordinal) > pin,
                    "the heartbeat never restores the slot of a root that arrived late");
        // Checked again where the conversation is replaced: a question sent meanwhile is never wiped, and a window that
        // held a conversation (emptied by /clear on purpose) is never filled behind the person's back.
        Assert.Contains("if (_conversationRoot.EverBound || AskedOnScreen()) return;", connection, StringComparison.Ordinal);
        // /clear waits for the auto-saves already started.
        Assert.Contains("try { await ClearUnderSaveGateAsync(ct); }", connection, StringComparison.Ordinal);

        var turn = Code("Inferpal", "ToolWindow", "InferpalToolWindowData.ChatTurn.cs");
        var asked = turn.IndexOf("Messages.Insert(Messages.Count - 2, userItem);", StringComparison.Ordinal);
        var context = turn.IndexOf("historyText = ", asked, StringComparison.Ordinal);
        var saved = turn.IndexOf("_ = AutoSaveAsync();", asked, StringComparison.Ordinal);
        Assert.True(asked >= 0 && context > asked && saved > asked && saved < context,
                    "the question is not saved as it leaves (before the context is built)");
    }

    [Fact]
    public void VsCode_KeepsTheQuestionAsItLeaves_AndTheHostSavesOneAtATime()
    {
        var chat = SettingsSchemaDriftTests.NeutralizeTypeScriptComments(
            File.ReadAllText(Path.Combine(ConventionCoverageTests.RepoRoot(), "vscode", "src", "chatViewProvider.ts")).Replace("\r\n", "\n"));
        // In the model's turn (slash commands never reach it as typed), once its attachments are named under it and its
        // notes are added, before the request leaves.
        var turn = chat.IndexOf("private async chatTurn(", StringComparison.Ordinal);
        var named = chat.IndexOf("this.nameAttachmentsInQuestion(named);", turn, StringComparison.Ordinal);
        var sent = chat.IndexOf("const result = await host.chatSend({", turn, StringComparison.Ordinal);
        var saved = chat.IndexOf("this.autoSaveLast();", named < 0 ? turn : named, StringComparison.Ordinal);
        Assert.True(turn >= 0 && named > turn && sent > named && saved > named && saved < sent,
                    "the question is not saved as it leaves, with what it carries");

        var host = Code("Inferpal.Host", "HostServer.cs");
        Assert.Contains("await s.SaveGate.WaitAsync(ct);", host, StringComparison.Ordinal);
        // A new conversation empties the slot behind the saves already sent.
        Assert.Contains("try { if (await ArchiveTheSlotAsync(s)) await ForgetAutoSaveAsync(s); }", host, StringComparison.Ordinal);
    }
}
