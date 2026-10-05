using System;
using System.IO;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  The VS Code model picker re-reads the server's list when it opens, as Visual Studio does.
//
//  Its list was read at start-up, after a settings save and when the backend came back — never
//  after /models pull or /models delete. A fresh install's first pull was adopted and shown in the
//  header while the menu said "no model listed — is the backend reachable?"; a deleted model stayed
//  offered, and picking it sent every question to a model that no longer existed.
// ──────────────────────────────────────────────────────────────────────────────────────────────
public class ModelPickerRelistTests
{
    private static string Ts(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return SettingsSchemaDriftTests.NeutralizeTypeScriptComments(
            File.ReadAllText(Path.Combine(dir!.FullName, "vscode", "src", relative)));
    }

    [Fact]
    public void OpeningTheMenu_AsksTheServerAgain()
    {
        var open = WebviewRebuildTests.Body(Ts(Path.Combine("webview", "main.ts")), "function openModelMenu(");
        Assert.Contains("post({ type: 'listModels' })", open, StringComparison.Ordinal);
    }

    [Fact]
    public void TheProvider_RereadsTheList_AndSendsItBack()
    {
        var provider = Ts("chatViewProvider.ts");
        // The case has no braces of its own: it is read as the statement that follows it.
        Assert.Matches(@"case 'listModels':\s*await this\.refreshModelList\(\);", provider);

        var refresh = WebviewRebuildTests.Body(provider, "private async refreshModelList(");
        var read    = refresh.IndexOf("host.modelsList()", StringComparison.Ordinal);
        var sent    = refresh.IndexOf("type: 'models'", StringComparison.Ordinal);
        Assert.True(read >= 0 && sent > read, "the list is read from the server, then sent to the view");
    }

    [Fact]
    public void TheView_RedrawsAnOpenMenu_WithTheListItReceives()
    {
        var handler = WebviewRebuildTests.Body(Ts(Path.Combine("webview", "main.ts")), "case 'models':");
        Assert.Contains("models = msg.models", handler, StringComparison.Ordinal);
        Assert.Contains("renderModelMenu()", handler, StringComparison.Ordinal);
    }
}
