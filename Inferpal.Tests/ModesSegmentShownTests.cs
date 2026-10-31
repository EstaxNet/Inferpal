using System.IO;
using System.Text.RegularExpressions;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The composer's "Modes" segment shows in both editors with or without a mode found, and its list links the page on
/// writing one.
/// </summary>
/// <remarks>
/// Hidden without a mode, nothing in the window said modes exist: the only clue was <c>/modes</c>, typed. The host's
/// half (the words and the address) is <c>HostServerTests.ModeList_WithNoModeHere_*</c>; these hold the two views.
/// </remarks>
public sealed class ModesSegmentShownTests
{
    private static string Repo(params string[] parts) =>
        Path.Combine([ConversationPersistenceSilenceTests.RepoRoot(), .. parts]);

    [Fact]
    public void VisualStudio_TheSegment_HasNoVisibilityBinding_AndTheListLinksThePage()
    {
        var xaml = File.ReadAllText(Repo("Inferpal", "ToolWindow", "InferpalToolWindowContent.xaml"));
        // The segment's button, from its opening tag to the end of its attributes.
        var segment = Regex.Match(xaml, @"<Button[^>]*Command=""\{Binding ToggleModeMenuCommand\}""[^>]*>");
        Assert.True(segment.Success, "the segment's button was not found");   // witness: the reading is alive
        Assert.DoesNotContain("Visibility=", segment.Value, StringComparison.Ordinal);

        Assert.Contains(@"Command=""{Binding WriteModeCommand}""", xaml, StringComparison.Ordinal);
        Assert.Contains(@"Visibility=""{Binding HasNoFoundModes, Converter={StaticResource b2v}}""", xaml, StringComparison.Ordinal);

        // Code only: a commented-out call would read as present.
        var vm = ConventionCoverageTests.CodeOnly(Repo("Inferpal", "ToolWindow", "InferpalToolWindowData.Modes.cs"));
        Assert.Contains("var docs = DocsLinks.Modes;", vm, StringComparison.Ordinal);
    }

    [Fact]
    public void VsCode_TheSegment_IsHiddenOnlyBeforeTheHostsWords_AndTheListLinksThePage()
    {
        var path = Repo("vscode", "src", "webview", "main.ts");
        var update = ConversationPersistenceSilenceTests.TsMethodBody(path, "function updateFoundModes(");
        Assert.Contains("foundGroup.hidden = modeLabels === null && heldMode === null;", update, StringComparison.Ordinal);

        var render = ConversationPersistenceSilenceTests.TsMethodBody(path, "function renderModeMenu(");
        Assert.Contains("link.href = modeLabels.writeOneUrl;", render, StringComparison.Ordinal);
        Assert.Contains("none.textContent = modeLabels.none;", render, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLink_IsTheModesSectionOfThisVersionsConfigurationPage_WhichExists()
    {
        Assert.Equal(DocsLinks.For("configuration.md#modes", DocsLinks.InstalledVersion), DocsLinks.Modes);
        var page = File.ReadAllText(Repo("docs", "configuration.md"));
        Assert.Matches(new Regex(@"^#{1,6} Modes\r?$", RegexOptions.Multiline), page);
    }
}
