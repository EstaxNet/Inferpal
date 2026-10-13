using System.IO;
using Inferpal.Host;
using Inferpal.Services.Persistence;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ Only the adapter that keeps VS Code's auto-save slot discards what it holds: an ACP thread's <c>/clear</c> or
/// <c>/template</c> does not empty the last conversation VS Code had in the same folder.
/// </summary>
/// <remarks>
/// Every host's conversation store writes VS Code's slot, and <c>StartNewConversation</c> forgets the slot of its folder.
/// <c>/clear</c> and <c>/template</c> are served to ACP clients: used in Zed on a folder, they emptied VS Code's last
/// conversation there — unarchived, and nothing came back at VS Code's next start. The adapter now declares the slot at
/// <c>initialize</c> (<c>autoSaveSlot</c>), as it declares its editor; ACP and the settings window do not keep one.
/// </remarks>
public partial class HostServerTests
{
    private static readonly SavedMessage[] VsCodesConversation =
        [new("user", "the question asked in VS Code"), new("assistant", "its answer")];

    [Fact]
    public async Task AThreadWithoutTheSlot_LeavesVsCodesLastConversation()
    {
        // The ACP thread: initialized as AcpSession initializes it.
        using var thread = CreateHarness();
        await thread.Client.InvokeWithParameterObjectAsync<InitializeResult>(
            "initialize", new { rootDir = thread.RootDir, editorSurface = false, autoSaveSlot = false })
            .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        var store = thread.Server.CurrentSession!.Store;
        await store.AutoSaveAsync(VsCodesConversation, CancellationToken.None, thread.RootDir);

        foreach (var command in new[] { "/clear", "/template code-review" })
            await thread.Client.InvokeWithParameterObjectAsync<SlashCommandResult>("command/slash", new { text = command })
                .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        // Reference arm: VS Code's own host empties its slot, as before (forgotten in the background: polled).
        using var vscode = CreateHarness();
        await vscode.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        var vscodeStore = vscode.Server.CurrentSession!.Store;
        await vscodeStore.AutoSaveAsync(VsCodesConversation, CancellationToken.None, vscode.RootDir);
        await vscode.Client.InvokeWithParameterObjectAsync<SlashCommandResult>("command/slash", new { text = "/clear" })
            .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        SessionData? emptied = null;
        for (var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30); DateTime.UtcNow < deadline; await Task.Delay(50))
        {
            emptied = await vscodeStore.LoadAutoSaveAsync(vscode.RootDir, CancellationToken.None);
            if (emptied is null || emptied.Messages.Count == 0) break;
        }
        Assert.True(emptied is null || emptied.Messages.Count == 0, "VS Code's own /clear no longer empties its slot: the rule measures nothing.");

        // The thread's /clear and /template ran before: had they emptied the slot, it would be empty by now.
        var kept = await store.LoadAutoSaveAsync(thread.RootDir, CancellationToken.None);
        Assert.NotNull(kept);
        Assert.Equal(2, kept!.Messages.Count);
    }

    [Fact]
    public void TheAdaptersWithoutTheSlot_SayIt()
    {
        var repo = ConventionCoverageTests.RepoRoot();
        string Code(params string[] path) => ConventionCoverageTests.CodeOnly(Path.Combine([repo, "Inferpal.Host", .. path]));
        Assert.Contains("autoSaveSlot    = false,", Code("Acp", "AcpSession.cs"), StringComparison.Ordinal);
        Assert.Contains("autoSaveSlot     = false,", Code("SettingsWindow", "SettingsWindowServer.cs"), StringComparison.Ordinal);
        Assert.Contains("if (!s.KeepsAutoSave) return;", Code("HostServer.cs"), StringComparison.Ordinal);
    }
}
