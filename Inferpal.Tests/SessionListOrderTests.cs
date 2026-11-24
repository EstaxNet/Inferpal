using System.IO;
using Inferpal.Services.Persistence;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Saved sessions are listed most recently written first — not in reverse alphabetical order of their names.
/// </summary>
/// <remarks>
/// Names come from several writers (a dated archive, a model's title, an ACP thread's id, VS Code's save): sorted by
/// name, the sessions of one writer always came first in the pickers and in <c>/history</c>, whatever their age.
/// </remarks>
public sealed class SessionListOrderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("inferpal-session-order-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private async Task SaveAsync(ConversationStore store, string name, DateTime writtenUtc)
    {
        await store.SaveAsync(name, [new SavedMessage("user", "question of " + name), new SavedMessage("assistant", "an answer")],
                              CancellationToken.None, workspaceRoot: _dir);
        File.SetLastWriteTimeUtc(Path.Combine(_dir, name + ".json"), writtenUtc);
    }

    [Fact]
    public async Task TheMostRecentlyWritten_ComesFirst_WhateverItsName()
    {
        var store = new ConversationStore(_dir);
        await SaveAsync(store, "zz_thread_from_zed", new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc));
        await SaveAsync(store, "aa_session_this_morning", new DateTime(2026, 10, 15, 9, 0, 0, DateTimeKind.Utc));
        await SaveAsync(store, "mm_session_last_week", new DateTime(2026, 10, 8, 9, 0, 0, DateTimeKind.Utc));

        Assert.Equal(["aa_session_this_morning", "mm_session_last_week", "zz_thread_from_zed"], store.ListSessions());

        var listed = await store.ListWithPreviewAsync(CancellationToken.None);
        Assert.Equal(["aa_session_this_morning", "mm_session_last_week", "zz_thread_from_zed"], listed.Items.Select(s => s.Name));
    }

    [Fact]
    public async Task SessionsWrittenAtTheSameMoment_KeepAStableOrder()
    {
        // Reference arm: equal times fall back on the name, so the order does not depend on the file system.
        var store = new ConversationStore(_dir);
        var at = new DateTime(2026, 10, 15, 9, 0, 0, DateTimeKind.Utc);
        await SaveAsync(store, "session_a", at);
        await SaveAsync(store, "session_b", at);

        Assert.Equal(["session_b", "session_a"], store.ListSessions());
    }

    [Fact]
    public async Task APreview_IsOneLine_AndNeverHalfACharacter()
    {
        // As every one-line preview (ChatTurnPolicy.OneLinePreview): an emoji across the cut stays whole or goes, a CRLF
        // question leaves no stray CR.
        var store = new ConversationStore(_dir);
        var question = "first\r\nline " + new string('a', 72) + char.ConvertFromUtf32(0x1F600) + " and more";
        await store.SaveAsync("s", [new SavedMessage("user", question), new SavedMessage("assistant", "an answer")],
                              CancellationToken.None, workspaceRoot: _dir);

        var preview = Assert.Single((await store.ListWithPreviewAsync(CancellationToken.None)).Items).FirstUserPreview;

        Assert.DoesNotContain('\r', preview);
        Assert.False(char.IsHighSurrogate(preview[^2]), preview);   // the character before the ellipsis is whole
    }
}
