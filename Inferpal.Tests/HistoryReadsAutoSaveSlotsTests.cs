using System.IO;
using Inferpal.Localization;
using Inferpal.Services.Commands;
using Inferpal.Services.Persistence;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The conversation on screen lives in its auto-save slot until <c>/clear</c> archives it: <c>/history &lt;term&gt;</c>
/// answered "No sessions match" for the words just read in the chat. The slots are searched too, each said for what it
/// is — a workspace's last conversation in an editor, not a session to load by name.
/// </summary>
public sealed class HistoryReadsAutoSaveSlotsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"history-slots-{Guid.NewGuid():N}");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "inferpal-tests", "Billing");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public async Task TheConversationOnScreen_IsFound_AndSaidForWhatItIs()
    {
        var vs = new ConversationStore(_dir, ConversationStore.VisualStudioSlot);
        await vs.AutoSaveAsync([new("user", "why does the invoice rounding drift"), new("assistant", "Banker's rounding.")],
                               CancellationToken.None, _root);

        var answer = await HistoryCommandHandler.HandleAsync(vs, ["/history", "rounding"], DateTime.UtcNow, CancellationToken.None);

        Assert.DoesNotContain(Strings.HistoryNoResults("rounding"), answer, StringComparison.Ordinal);
        Assert.Contains(Strings.HistoryAutoSaveHit("Billing", "Visual Studio"), answer, StringComparison.Ordinal);
        Assert.Contains("invoice rounding drift", answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheOtherEditorsSlot_IsFoundToo_AndAWordNowhere_StillFindsNothing()
    {
        var vsCode = new ConversationStore(_dir, ConversationStore.VsCodeSlot);
        await vsCode.AutoSaveAsync([new("user", "explain the ledger close")], CancellationToken.None, _root);
        var vs = new ConversationStore(_dir, ConversationStore.VisualStudioSlot);

        var hits = (await vs.SearchAsync("ledger", CancellationToken.None)).Items;
        Assert.Equal(Strings.HistoryAutoSaveHit("Billing", "VS Code"), Assert.Single(hits).AutoSave);

        // Reference arm: a word in no conversation is still no result.
        Assert.Empty((await vs.SearchAsync("kubernetes", CancellationToken.None)).Items);
    }

    /// <summary>The slot older versions kept for both editors stays on disk once each writes its own, never read again:
    /// listed, it repeated a conversation found elsewhere, promising it would come back.</summary>
    [Fact]
    public async Task ASlotOlderVersionsKept_IsListedOnlyForTheEditorsThatStillRestoreIt()
    {
        await new ConversationStore(_dir).AutoSaveAsync([new("user", "the ledger close, from an older version")],
                                                        CancellationToken.None, _root);
        var vs = new ConversationStore(_dir, ConversationStore.VisualStudioSlot);
        await vs.AutoSaveAsync([new("user", "something else")], CancellationToken.None, _root);

        // Visual Studio wrote its own slot: only VS Code still restores the old one.
        Assert.Equal(Strings.HistoryAutoSaveHit("Billing", "VS Code"),
                     Assert.Single((await vs.SearchAsync("older version", CancellationToken.None)).Items).AutoSave);

        await new ConversationStore(_dir, ConversationStore.VsCodeSlot)
            .AutoSaveAsync([new("user", "something else again")], CancellationToken.None, _root);
        Assert.Empty((await vs.SearchAsync("older version", CancellationToken.None)).Items);
    }

    /// <summary>A window without a folder is Visual Studio's: VS Code starts no host without one, so it never brings
    /// that slot back.</summary>
    [Fact]
    public async Task AWindowWithoutAFolder_IsSaidAsSuch_InTheEditorThatRestoresIt()
    {
        var vs = new ConversationStore(_dir, ConversationStore.VisualStudioSlot);
        await vs.AutoSaveAsync([new("user", "a lone project file question")], CancellationToken.None, workspaceRoot: null);

        Assert.Equal(Strings.HistoryAutoSaveHitNoFolder("Visual Studio"),
                     Assert.Single((await vs.SearchAsync("lone project", CancellationToken.None)).Items).AutoSave);
    }

    [Fact]
    public async Task TheHitsReadMostRecentFirst_TheSlotsAmongTheSessions()
    {
        var vs = new ConversationStore(_dir, ConversationStore.VisualStudioSlot);
        await vs.SaveAsync("feature-x", [new("user", "invoice totals, archived")], CancellationToken.None, workspaceRoot: null);
        await vs.AutoSaveAsync([new("user", "invoice totals, on screen")], CancellationToken.None, _root);
        File.SetLastWriteTimeUtc(Path.Combine(_dir, "feature-x.json"), DateTime.UtcNow.AddHours(-2));
        File.SetLastWriteTimeUtc(vs.AutoSaveFile(_root), DateTime.UtcNow.AddMinutes(-1));

        var hits = (await vs.SearchAsync("invoice totals", CancellationToken.None)).Items;

        Assert.Equal(2, hits.Count);
        Assert.NotNull(hits[0].AutoSave);          // the conversation on screen, written last
        Assert.Equal("feature-x", hits[1].Name);
    }

    /// <summary>A damaged slot is named by its path: its copy set aside sits next to it, and a title would hold the commas
    /// the list of unread files is joined with.</summary>
    [Fact]
    public async Task ADamagedSlot_IsNamedByItsPath()
    {
        var vs = new ConversationStore(_dir, ConversationStore.VisualStudioSlot);
        await vs.AutoSaveAsync([new("user", "anything")], CancellationToken.None, _root);
        await File.WriteAllTextAsync(vs.AutoSaveFile(_root), "{ not json");

        var scan = await vs.SearchAsync("anything", CancellationToken.None);

        Assert.Equal(vs.AutoSaveFile(_root), Assert.Single(scan.Unreadable));
    }
}
