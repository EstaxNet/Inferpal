using System.IO;
using Inferpal.Host;
using Inferpal.Localization;
using Inferpal.Services.Persistence;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A discarded conversation is archived BEFORE the auto-save slot is emptied, and the slot keeps it when the archive
/// cannot be written.
/// </summary>
/// <remarks>
/// Both editors emptied the slot at once and archived after the model named the conversation — up to 15 s later. An
/// editor closed, or a host stopped, in those seconds lost a conversation that was in no slot and under no name. The
/// archive is now written first, under its first question's words, and the title only renames it.
/// </remarks>
public partial class HostServerTests
{
    private static async Task<List<SessionData>> ArchivesHolding(ConversationStore store, string text)
    {
        var found = new List<SessionData>();
        foreach (var name in store.ListSessions())
        {
            try
            {
                if (await store.LoadAsync(name, CancellationToken.None) is { } s
                    && s.Messages.Any(m => m.Content.Contains(text, StringComparison.Ordinal)))
                    found.Add(s);
            }
            catch (UnreadableSessionException) { /* another test's damaged file */ }
        }
        return found;
    }

    private static async Task SaveSlotAsync(Harness h, string question) =>
        await h.Client.InvokeWithParameterObjectAsync(
            "session/save", new SessionSaveParams("last_session",
                [new SavedMessageDto("user", question), new SavedMessageDto("assistant", "the answer")]))
            .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

    [Fact]
    public async Task ANewConversation_ArchivesTheSlotBeforeEmptyingIt_AndTheScreenWritesOverThatArchive()
    {
        using var h = CreateHarness();
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        var question = $"archive me {Guid.NewGuid():N}";
        await SaveSlotAsync(h, question);
        var s = h.Server.CurrentSession!;

        await h.Client.InvokeAsync("chat/reset").WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        // When chat/reset answers, the slot is empty and the conversation it held is already archived.
        var slot = await s.Store.LoadAutoSaveAsync(s.RootDir, CancellationToken.None);
        Assert.True(slot is null || slot.Messages.Count == 0, "the discarded conversation is still in the slot.");
        Assert.Single(await ArchivesHolding(s.Store, question));

        // The screen's version (VS Code archives what it showed) writes over that archive: one per conversation.
        var screenOnly = $"shown on screen {Guid.NewGuid():N}";
        await h.Client.InvokeWithParameterObjectAsync<SessionArchiveResult>("session/archive", new SessionArchiveParams(
                [new SavedMessageDto("user", question), new SavedMessageDto("assistant", "the answer"),
                 new SavedMessageDto("assistant", screenOnly)]))
            .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        var archive = Assert.Single(await ArchivesHolding(s.Store, question));
        Assert.Contains(archive.Messages, m => m.Content == screenOnly);
    }

    [Fact]
    public async Task AnArchiveThatCannotBeWritten_LeavesTheConversationInTheSlot_AndSaysSo()
    {
        using var h = CreateHarness();
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        var question = $"blocked {Guid.NewGuid():N}";
        await SaveSlotAsync(h, question);
        var s = h.Server.CurrentSession!;
        // A folder where the archive's file would go: the write fails (this minute's name, and the next one's).
        var blockers = new[] { DateTime.Now, DateTime.Now.AddMinutes(1) }
            .Select(at => Path.Combine(ConversationStore.OverrideDirForTests!,
                                       ConversationStore.StoredName(ConversationArchive.FirstName(s.Store, at, question)) + ".json"))
            .ToList();
        blockers.ForEach(b => Directory.CreateDirectory(b));
        try
        {
            await h.Client.InvokeAsync("chat/reset").WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

            var slot = await s.Store.LoadAutoSaveAsync(s.RootDir, CancellationToken.None);
            Assert.True(slot is not null && slot.Messages.Any(m => m.Content == question),   // kept: no archive holds it
                        "the conversation left the slot although no archive holds it.");
            // Said in the thread, in the words of the notice (the language is the process's: compared to the resource).
            var said = Strings.SessionArchiveFailedSlotKept("\u0001").Split('\u0001')[0];
            for (var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
                 !h.Target.HostNotices.Any(n => n.StartsWith(said, StringComparison.Ordinal)) && DateTime.UtcNow < deadline;)
                await Task.Delay(20);
            Assert.True(h.Target.HostNotices.Any(n => n.StartsWith(said, StringComparison.Ordinal)),
                        "notices: " + string.Join(" | ", h.Target.HostNotices));
        }
        finally
        {
            foreach (var b in blockers) { try { Directory.Delete(b); } catch { /* best-effort cleanup */ } }
        }
    }
}

/// <summary>The two adapters that cannot be driven from here hold the same order: archive, then empty the slot.</summary>
public sealed class ArchiveOrderSourceTests
{
    [Fact]
    public void VisualStudio_EmptiesTheSlot_OnlyOnceTheArchiveIsWritten()
    {
        var path = Path.Combine(ConventionCoverageTests.RepoRoot(), "Inferpal", "ToolWindow", "InferpalToolWindowData.Connection.cs");
        var clear = ConventionCoverageTests.CodeOnly(path);
        var save   = clear.IndexOf("await _store.SaveAsync(name, snapshot, CancellationToken.None, root", StringComparison.Ordinal);
        var forget = clear.IndexOf("ForgetAutoSaveOrSayAsync(root", StringComparison.Ordinal);
        Assert.True(save > 0 && forget > 0, "the archive or the forget moved: this test reads nothing.");   // witness
        Assert.True(save < forget, "the slot is emptied before the archive is written.");
        Assert.Contains("if (inASlot && archiveFailed is null) notForgotten", clear, StringComparison.Ordinal);
    }

    [Fact]
    public void VsCode_WaitsForNoModelBeforeItArchives()
    {
        var body = ConversationPersistenceSilenceTests.TsMethodBody(
            Path.Combine(ConventionCoverageTests.RepoRoot(), "vscode", "src", "chatViewProvider.ts"), "private archiveConversation(");
        Assert.Contains("sessionArchive(", body, StringComparison.Ordinal);   // witness
        Assert.DoesNotContain("sessionTitle(", body, StringComparison.Ordinal);
    }
}

/// <summary>The archive's two names: its first question's words at once, then the model's title.</summary>
public sealed class ConversationArchiveNamesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"archive-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static readonly DateTime At = new(2026, 10, 15, 21, 30, 0);

    [Fact]
    public async Task TheTitleRenamesTheArchive_UnderAFreeName()
    {
        var store = new ConversationStore(_dir);
        var first = ConversationArchive.FirstName(store, At, "how do I read a file in C#?");
        await store.SaveAsync(first, [new SavedMessage("user", "how do I read a file in C#?")], CancellationToken.None, null);
        await store.SaveAsync(SessionManager.SessionFileName(At, "Reading files"), [], CancellationToken.None, null);   // taken

        var renamed = ConversationArchive.Retitle(store, first, At, "Reading files");

        Assert.Equal(SessionManager.SessionFileName(At, "Reading files") + "_2", renamed);
        Assert.DoesNotContain(first, store.ListSessions());
        Assert.Equal("how do I read a file in C#?", (await store.LoadAsync(renamed, CancellationToken.None))!.Messages[0].Content);
    }

    [Fact]
    public async Task ATitleThatChangesNothing_LeavesTheArchiveWhereItIs()
    {
        // Reference arm: the model unreachable, the title is the snippet — the name stays.
        var store = new ConversationStore(_dir);
        var question = "how do I read a file in C#?";
        var first = ConversationArchive.FirstName(store, At, question);
        await store.SaveAsync(first, [new SavedMessage("user", question)], CancellationToken.None, null);

        Assert.Equal(first, ConversationArchive.Retitle(store, first, At, SessionManager.MakeSnippet(question)));
        Assert.Contains(first, store.ListSessions());
    }
}
