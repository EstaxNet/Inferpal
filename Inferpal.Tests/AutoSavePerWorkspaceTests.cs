using System.IO;
using Inferpal.Services;
using Inferpal.Services.Persistence;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The last conversation is kept per workspace: two windows on two solutions each find their own at their next start.
/// </summary>
/// <remarks>
/// The auto-save slot was one file for every window of both editors. Two solutions open side by side, the slot held the
/// conversation of whichever wrote last; the first one closed found nothing at its next start (another workspace's
/// conversation is refused), and its own — never archived — was gone.
/// </remarks>
public sealed class AutoSavePerWorkspaceTests : IDisposable
{
    private readonly string _dir   = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"autosave-{Guid.NewGuid():N}");
    private readonly string _rootA = Path.Combine(Path.GetTempPath(), "inferpal-tests", "Shop");
    private readonly string _rootB = Path.Combine(Path.GetTempPath(), "inferpal-tests", "Billing");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static List<SavedMessage> Turn(string question) =>
        [new("user", question), new("assistant", "answer to " + question)];

    [Fact]
    public async Task TwoWorkspaces_EachKeepTheirLastConversation()
    {
        var store = new ConversationStore(_dir);

        await store.AutoSaveAsync(Turn("question from Shop"), CancellationToken.None, _rootA);
        await store.AutoSaveAsync(Turn("question from Billing"), CancellationToken.None, _rootB);

        Assert.Equal("question from Shop", (await store.LoadAutoSaveAsync(_rootA, CancellationToken.None))!.Messages[0].Content);
        Assert.Equal("question from Billing", (await store.LoadAutoSaveAsync(_rootB, CancellationToken.None))!.Messages[0].Content);
        // The slots are not sessions: the list and the pickers never show them.
        Assert.Empty(store.ListSessions());
        Assert.Empty((await store.ListWithPreviewAsync(CancellationToken.None)).Items);
    }

    [Fact]
    public async Task TheSlotOfOlderVersions_IsRestoredToItsWorkspace_UntilItsOwnSlotIsWritten()
    {
        var store = new ConversationStore(_dir);
        // An older version wrote the one shared slot, recording the workspace it belonged to.
        await store.SaveAsync("older", Turn("question from before the update"), CancellationToken.None, workspaceRoot: _rootA);
        File.Move(Path.Combine(_dir, "older.json"), Path.Combine(_dir, "last_session.json"));

        Assert.Equal("question from before the update",
                     (await store.LoadAutoSaveAsync(_rootA, CancellationToken.None))!.Messages[0].Content);
        Assert.Null(await store.LoadAutoSaveAsync(_rootB, CancellationToken.None));          // not Billing's

        // /clear in Shop: the conversation must not come back from the old slot at the next start.
        await store.ForgetAutoSaveAsync(_rootA, CancellationToken.None);
        var after = await store.LoadAutoSaveAsync(_rootA, CancellationToken.None);
        Assert.True(after is null || after.Messages.Count == 0, "the forgotten conversation came back from the older slot");

        await store.AutoSaveAsync(Turn("new question in Shop"), CancellationToken.None, _rootA);
        Assert.Equal("new question in Shop", (await store.LoadAutoSaveAsync(_rootA, CancellationToken.None))!.Messages[0].Content);
    }

    [Fact]
    public async Task WithoutAWorkspace_TheSharedSlotIsKept()
    {
        var store = new ConversationStore(_dir);

        await store.AutoSaveAsync(Turn("no solution open"), CancellationToken.None, workspaceRoot: null);

        Assert.True(File.Exists(Path.Combine(_dir, "last_session.json")));
        Assert.Equal("no solution open", (await store.LoadAutoSaveAsync(null, CancellationToken.None))!.Messages[0].Content);
    }

    [Fact]
    public async Task OneWorkspace_IsOneSlot_HoweverItsPathIsSpelled()
    {
        var store = new ConversationStore(_dir);

        await store.AutoSaveAsync(Turn("spelled with a trailing separator"), CancellationToken.None,
                                  _rootA + Path.DirectorySeparatorChar);
        var spelled = PathComparer.Comparison == StringComparison.Ordinal ? _rootA : _rootA.ToUpperInvariant();

        Assert.Equal("spelled with a trailing separator",
                     (await store.LoadAutoSaveAsync(spelled, CancellationToken.None))!.Messages[0].Content);
    }

    [Fact]
    public void NoEditor_LoadsTheSlotByItsFileName()
    {
        // Loaded by name, the slot is the shared file of older versions: the workspace's own is never read.
        var sites = new[]
        {
            Path.Combine("Inferpal", "ToolWindow", "InferpalToolWindowData.Connection.cs"),
            Path.Combine("Inferpal.Host", "HostServer.cs"),
        };
        foreach (var site in sites)
        {
            var code = ConventionCoverageTests.CodeOnly(Path.Combine(ConventionCoverageTests.RepoRoot(), site));
            Assert.Contains("LoadAutoSaveAsync(", code, StringComparison.Ordinal);                 // witness
            Assert.DoesNotContain("LoadAsync(\"last_session\"", code, StringComparison.Ordinal);
        }
    }
}
