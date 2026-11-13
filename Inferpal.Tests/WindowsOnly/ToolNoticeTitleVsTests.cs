using System.IO;
using Inferpal.Localization;
using Inferpal.ToolWindow;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Visual Studio's bubble of the chat's own notices shows their translated title — live and reloaded — and so does what a
/// screen reader reads. Windows only: the bubble is the Visual Studio adapter's.
/// </summary>
[Collection(CultureSerialCollection.Name)]   // compares localized sentences
public sealed class ToolNoticeTitleVsTests
{
    [Fact]
    public void TheBubble_ShowsTheTitle_LiveAndReloaded()
    {
        Assert.Equal(Strings.ToolTitleContextCompact, ChatMessageItem.ToolMsg(ToolNoticeTitle.ContextCompact, "notice").ToolTitle);
        Assert.Equal(Strings.ToolTitleOodaRecap, ChatMessageItem.FromSaved("tool", "recap", ToolNoticeTitle.OodaRecap).ToolTitle);
        Assert.Equal(ToolNoticeTitle.OodaRecap, ChatMessageItem.FromSaved("tool", "recap", ToolNoticeTitle.OodaRecap).ToolName);   // saved as the name
        Assert.Equal("read_file", ChatMessageItem.ToolMsg("read_file", "text").ToolTitle);   // reference arm
    }

    [Fact]
    public void TheWindow_BindsTheTitle_NeverTheName()
    {
        var xaml = File.ReadAllText(Path.Combine(ConventionCoverageTests.RepoRoot(), "Inferpal", "ToolWindow", "InferpalToolWindowContent.xaml"));

        Assert.DoesNotContain("{Binding ToolName}", xaml, StringComparison.Ordinal);
        Assert.True(System.Text.RegularExpressions.Regex.Matches(xaml, @"\{Binding ToolTitle\}").Count >= 3,
                    "witness: the title, the button's name and the row's name");
    }
}
