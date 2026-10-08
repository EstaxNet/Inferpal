using System.IO;
using Inferpal.Localization;
using Inferpal.Services.Persistence;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A session file that cannot be read is kept — a copy out of the session list, named to the user — instead of being
/// written over by the next auto-save; and VS Code says so when the start's restore fails.
/// </summary>
/// <remarks>
/// ⚠ A damaged <c>last_session.json</c> made the restore throw: Visual Studio said "could not be loaded", VS Code only
/// logged it, and in both the first turn's auto-save rewrote the slot — the conversation gone for good.
/// </remarks>
public sealed class DamagedSessionKeptTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("inferpal-damaged-session-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public async Task ADamagedSession_IsKeptAside_AndTheMessageSaysWhere()
    {
        var store = new ConversationStore(_dir);
        var file  = Path.Combine(_dir, "last_session.json");
        await File.WriteAllTextAsync(file, "{ \"messages\": [ { \"role\": \"user\", \"content\": \"the plan we");   // cut mid-write

        var first = await Assert.ThrowsAsync<UnreadableSessionException>(() => store.LoadAsync("last_session", CancellationToken.None));

        Assert.NotNull(first.Copy);
        Assert.Equal(await File.ReadAllBytesAsync(file), await File.ReadAllBytesAsync(first.Copy!));
        Assert.Equal(Strings.SessionUnreadableKept("last_session", first.Copy!), first.Message);

        // Loaded again (each start of each editor), the same copy: none piles up.
        var second = await Assert.ThrowsAsync<UnreadableSessionException>(() => store.LoadAsync("last_session", CancellationToken.None));
        Assert.Equal(first.Copy, second.Copy);
        Assert.Single(Directory.EnumerateFiles(_dir, "*.unreadable-*"));

        // Out of the session list: the copy is not another session to pick.
        Assert.DoesNotContain(store.ListSessions(), n => n.Contains("unreadable", StringComparison.Ordinal));
    }

    /// <summary>Reference arm: a session that is not there is not damaged — nothing kept, nothing thrown.</summary>
    [Fact]
    public async Task AMissingSession_IsNull_AndKeepsNothing()
    {
        Assert.Null(await new ConversationStore(_dir).LoadAsync("nothing-here", CancellationToken.None));
        Assert.Empty(Directory.EnumerateFiles(_dir));
    }

    [Fact]
    public void BothEditors_SayIt()
    {
        var root = ConversationPersistenceSilenceTests.RepoRoot();
        var vm = ConventionCoverageTests.CodeOnly(Path.Combine(root, "Inferpal", "ToolWindow", "InferpalToolWindowData.Connection.cs"));
        Assert.Contains("ex is UnreadableSessionException damaged ? damaged.Message", vm);

        var ready = WebviewRebuildTests.Body(WebviewRebuildTests.TsCode("chatViewProvider.ts"), "async onHostReady(");
        var restore = ready.IndexOf("const last = await host.sessionLoad('last_session');", StringComparison.Ordinal);
        Assert.True(restore > 0, "the start's restore moved — the rule reads nothing");   // WITNESS
        Assert.Contains("t('⚠ The last conversation could not be restored: {0}'", ready[restore..]);
    }
}
