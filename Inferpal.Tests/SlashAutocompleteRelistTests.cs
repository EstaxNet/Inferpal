using System;
using System.IO;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  The VS Code slash autocomplete re-reads the commands as one starts being typed.
//
//  Its list was read at start-up and after a settings save: a prompt file made by /prompts init — whose
//  answer says it shows up in the autocomplete at once — or by hand never appeared, while Visual Studio
//  re-reads the templates at every keystroke.
// ──────────────────────────────────────────────────────────────────────────────────────────────
public class SlashAutocompleteRelistTests
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
    public void StartingACommand_AsksForTheList()
    {
        var detect = WebviewRebuildTests.Body(Ts(Path.Combine("webview", "main.ts")), "function detectSlash(");
        Assert.Contains("post({ type: 'listCommands' })", detect, StringComparison.Ordinal);
    }

    [Fact]
    public void TheProvider_RereadsTheCommands_AndSendsThemBack()
    {
        var provider = Ts("chatViewProvider.ts");
        Assert.Matches(@"case 'listCommands':\s*await this\.refreshCommandList\(\);", provider);

        var refresh = WebviewRebuildTests.Body(provider, "private async refreshCommandList(");
        var read    = refresh.IndexOf("host.commandList()", StringComparison.Ordinal);
        var sent    = refresh.IndexOf("type: 'commands'", StringComparison.Ordinal);
        Assert.True(read >= 0 && sent > read, "the commands are read from the host, then sent to the view");
    }

    [Fact]
    public void TheView_RedrawsThePopup_WithTheListItReceives()
    {
        var handler = WebviewRebuildTests.Body(Ts(Path.Combine("webview", "main.ts")), "case 'commands':");
        Assert.Contains("slashCommands = msg.commands", handler, StringComparison.Ordinal);
        Assert.Contains("detectSlash()", handler, StringComparison.Ordinal);
    }
}
