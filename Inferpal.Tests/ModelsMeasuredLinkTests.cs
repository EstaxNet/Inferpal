using System.IO;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Beside "Suggest the best models", the settings page of each front-end links the page the suggestion draws from: which
/// model for which card, and how each was measured (<c>docs/models.md</c>, at the version installed). One page for the
/// three renderings: Visual Studio's window, VS Code's panel and the browser page of ACP editors.
/// </summary>
public sealed class ModelsMeasuredLinkTests
{
    [Fact]
    public void TheLink_LeadsToASectionThatExists()
    {
        Assert.EndsWith("/docs/" + DocsLinks.ModelsByCardPage, DocsLinks.ModelsByCard);
        var page   = DocsLinks.ModelsByCardPage.Split('#');
        var models = File.ReadAllText(Path.Combine(ConventionCoverageTests.RepoRoot(), "docs", page[0]));
        Assert.Contains($"<a id=\"{page[1]}\"></a>", models);
    }

    [Fact]
    public void VisualStudiosWindow_LinksIt()
    {
        var root = ConventionCoverageTests.RepoRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "Inferpal", "ToolWindow", "InferpalSettingsContent.xaml"));
        Assert.Contains("Command=\"{Binding OpenModelsMeasuredCommand}\"", xaml);
        Assert.Contains("{Binding LinkModelsMeasured, Mode=OneWay}", xaml);
        var vm = ConventionCoverageTests.CodeOnly(Path.Combine(root, "Inferpal", "ToolWindow", "InferpalSettingsData.Models.cs"));
        Assert.Contains("LinkModelsMeasured = Strings.LinkModelsMeasured;", vm);
        Assert.Contains("Services.DocsLinks.ModelsByCard", vm);
    }

    [Fact]
    public void VsCodeAndTheBrowserPage_AskForTheSamePage_AndBothShellsOpenIt()
    {
        Assert.Contains($"host.post({{ type: 'openDocs', page: '{DocsLinks.ModelsByCardPage}' }})",
                        WebviewRebuildTests.TsCode("webview/settingsWidgets.ts"));
        Assert.Contains("this.shell.openDocs(msg.page);", WebviewRebuildTests.TsCode("settingsBridge.ts"));
        // The command the VS Code panel calls opens the page it is given, not the docs index.
        Assert.Contains("typeof page === 'string' && page ? page : undefined", WebviewRebuildTests.TsCode("extension.ts"));
        Assert.Contains("openDocs: (page) => void vscode.commands.executeCommand('inferpal.openDocs', page)",
                        WebviewRebuildTests.TsCode("settingsPanel.ts"));
        Assert.Contains("window.open(page.docs + docsPage", WebviewRebuildTests.TsCode("webview/settingsStandaloneShell.ts"));
        var server = ConventionCoverageTests.CodeOnly(Path.Combine(ConventionCoverageTests.RepoRoot(),
                                                                   "Inferpal.Host", "SettingsWindow", "SettingsWindowServer.cs"));
        Assert.Contains("docs     = Services.DocsLinks.For(\"\", Services.DocsLinks.InstalledVersion)", server);
    }
}
