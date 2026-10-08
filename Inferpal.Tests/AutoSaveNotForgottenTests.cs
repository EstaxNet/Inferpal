using System.IO;
using Inferpal.Localization;
using Inferpal.Services.Persistence;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A new conversation that cannot empty the auto-save slot says so — the conversation just discarded would come back at
/// the next start of either editor.
/// </summary>
/// <remarks>
/// ⚠ Both editors swallowed the failure (Visual Studio awaited and traced it, the VS Code host fired and forgot it): the
/// one gesture meant to get rid of a conversation failed without a word, and the conversation was back at the next start.
/// </remarks>
public sealed class AutoSaveNotForgottenTests : IDisposable
{
    private readonly string _dir  = Directory.CreateTempSubdirectory("inferpal-forget-slot-").FullName;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "inferpal-forget-root");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private async Task<ConversationStore> StoreWithAConversationAsync()
    {
        var store = new ConversationStore(_dir);
        await store.AutoSaveAsync([new SavedMessage("user", "the conversation to leave")], CancellationToken.None, _root);
        return store;
    }

    [Fact]
    public async Task ASlotThatCannotBeEmptied_IsSaid()
    {
        // FileShare.None is Windows' lock; elsewhere it is advisory and this arm proves nothing.
        if (!OperatingSystem.IsWindows()) return;
        var store = await StoreWithAConversationAsync();

        string? notice;
        using (new FileStream(store.AutoSaveFile(_root), FileMode.Open, FileAccess.Read, FileShare.None))
            notice = await store.ForgetAutoSaveOrSayAsync(_root, CancellationToken.None);

        Assert.NotNull(notice);
        Assert.StartsWith(Strings.AutoSaveNotForgotten("\u0000").Split('\u0000')[0], notice);
        Assert.NotEmpty((await store.LoadAutoSaveAsync(_root, CancellationToken.None))!.Messages);   // witness: still there
    }

    /// <summary>Reference arm: an ordinary new conversation empties the slot and says nothing.</summary>
    [Fact]
    public async Task AnEmptiedSlot_SaysNothing()
    {
        var store = await StoreWithAConversationAsync();

        Assert.Null(await store.ForgetAutoSaveOrSayAsync(_root, CancellationToken.None));
        Assert.Empty((await store.LoadAutoSaveAsync(_root, CancellationToken.None))!.Messages);
    }

    [Fact]
    public void BothEditors_SayIt()
    {
        var root = ConversationPersistenceSilenceTests.RepoRoot();

        var host = ConventionCoverageTests.CodeOnly(Path.Combine(root, "Inferpal.Host", "HostServer.cs"));
        Assert.Contains("s.Store.ForgetAutoSaveOrSayAsync(s.RootDir, CancellationToken.None) is { } notice", host);
        Assert.Contains("Notify(\"host/notice\", new { text = notice });", host);
        Assert.Contains("conn.onNotification('host/notice'", WebviewRebuildTests.TsCode("hostClient.ts"));

        var vm = ConventionCoverageTests.CodeOnly(Path.Combine(root, "Inferpal", "ToolWindow", "InferpalToolWindowData.Connection.cs"));
        var clear  = vm.IndexOf("Messages.Clear();", vm.IndexOf("ForgetAutoSaveOrSayAsync", StringComparison.Ordinal), StringComparison.Ordinal);
        var notice = vm.IndexOf("InsertThemed(ChatMessageItem.NoticeMsg(notForgotten))", StringComparison.Ordinal);
        Assert.True(clear > 0, "the clear moved — the rule reads nothing");   // WITNESS
        Assert.True(notice > clear, "the notice is said before the clear that would wipe it");
    }
}
