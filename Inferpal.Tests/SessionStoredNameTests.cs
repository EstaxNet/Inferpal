using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Inferpal.Services.Persistence;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  A session saved under a typed name is the session listed under that name.
//
//  VS Code's "Save session" accepted any name, the store wrote ':' '/' '*' … as '_', and every check
//  ran on the typed name: "feat: login" saved twice replaced the first conversation without the
//  "replace?" question, and /branch took the saved conversation for an unsaved one.
// ──────────────────────────────────────────────────────────────────────────────────────────────
public sealed class SessionStoredNameTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("inferpal-storedname-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public async Task TheStoredName_IsTheNameTheListingShows()
    {
        // '/' cannot be in a file name on any system.
        var store = new ConversationStore(_dir);
        await store.SaveAsync("feat/login", [new SavedMessage("user", "q")], CancellationToken.None);

        var listed = await store.ListWithPreviewAsync(CancellationToken.None);

        Assert.Equal(ConversationStore.StoredName("feat/login"), Assert.Single(listed.Items).Name);
        Assert.Equal("feat_login", ConversationStore.StoredName("feat/login"));
        // Reference arm: an ordinary name is its own stored name.
        Assert.Equal("feature-x", ConversationStore.StoredName("feature-x"));
    }

    [Fact]
    public void VsCodesSaveBox_RefusesTheCharactersTheStoreRewrites()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var source = SettingsSchemaDriftTests.NeutralizeTypeScriptComments(
            File.ReadAllText(Path.Combine(dir!.FullName, "vscode", "src", "chatViewProvider.ts")));

        var box = source.IndexOf("prompt: t('Session name')", StringComparison.Ordinal);
        Assert.True(box >= 0, "The save box was not found.");
        var options = source.Substring(box, Math.Min(600, source.Length - box));
        Assert.Contains("validateInput:", options, StringComparison.Ordinal);
        Assert.Contains(@"/[\\/:*?""<>|\x00-\x1f]/", options, StringComparison.Ordinal);
    }
}
