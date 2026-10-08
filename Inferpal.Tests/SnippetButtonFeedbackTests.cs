using System.IO;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A code block's ⭐ button saved the snippet without showing anything: neither success nor failure.
/// Since SnippetStore.SaveAsync returns a boolean the failure is known — it stayed invisible. The button
/// therefore changes glyph according to the result. Remote UI types cannot be instantiated outside VS:
/// the rule reads the source (without comments) and the XAML.
/// </summary>
public class SnippetButtonFeedbackTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void TheSaveSnippetButton_ShowsWhetherTheSnippetWasSaved()
    {
        var toolWindow = Path.Combine(RepoRoot(), "Inferpal", "ToolWindow");
        var block = ConventionCoverageTests.CodeOnly(Path.Combine(toolWindow, "MarkdownBlock.cs"));
        var xaml  = File.ReadAllText(Path.Combine(toolWindow, "InferpalToolWindowContent.xaml"));

        // Witness: the button still saves through the store, and the XAML still binds it.
        Assert.Contains("SnippetStore.SaveAsync(", block, StringComparison.Ordinal);
        Assert.Contains("{Binding SaveSnippetCommand}", xaml, StringComparison.Ordinal);

        // The save's result is read, and rendered on the button.
        Assert.Matches(@"var\s+\w+\s*=\s*await\s+[\w.]*SnippetStore\.SaveAsync\(", block);
        Assert.Contains("SaveSnippetGlyph =", block, StringComparison.Ordinal);
        Assert.Contains("Content=\"{Binding SaveSnippetGlyph}\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void ARefusedSave_SaysWhy_OnTheBlocksOwnTooltip()
    {
        // The glyph alone says "not saved"; a full library is a reason the user can act on: it goes on the tooltip of THIS
        // block (the window's shared tooltip cannot carry one block's answer).
        var toolWindow = Path.Combine(RepoRoot(), "Inferpal", "ToolWindow");
        var block = ConventionCoverageTests.CodeOnly(Path.Combine(toolWindow, "MarkdownBlock.cs"));
        var xaml  = File.ReadAllText(Path.Combine(toolWindow, "InferpalToolWindowContent.xaml"));

        Assert.Contains("SnippetSaveResult.Full", block, StringComparison.Ordinal);
        Assert.Contains("Strings.SnippetsFull(", block, StringComparison.Ordinal);
        Assert.Contains("SaveSnippetTip   = result switch", block, StringComparison.Ordinal);
        Assert.Contains("ToolTip=\"{Binding SaveSnippetTip}\"", xaml, StringComparison.Ordinal);
    }
}
