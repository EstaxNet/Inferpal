using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Visual Studio's Tools › Inferpal holds the window and the settings only: what runs in the chat (Review a commit…, the
/// architecture map) is in the chat window's ⋯ menu, and Alt+' is a key, not a menu entry. A command added to the Tools
/// menu again fails here, naming it.
/// </summary>
public class InferpalToolsMenuTests
{
    private static string Code(params string[] path) =>
        ConventionCoverageTests.CodeOnly(Path.Combine([ConventionCoverageTests.RepoRoot(), .. path]));

    [Fact]
    public void ToolsInferpal_HoldsTheWindowAndTheSettings_Only()
    {
        var menu = Code("Inferpal", "Commands", "InferpalMenu.cs");
        var children = Regex.Matches(menu, @"MenuChild\.Command<(\w+)>\(\)").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(["OpenInferpalWindowCommand", "OpenSettingsCommand"], children);
    }

    [Fact]
    public void TheChatWindowsMoreMenu_ReviewsACommit_AndDrawsTheMap()
    {
        var xaml = File.ReadAllText(Path.Combine(ConventionCoverageTests.RepoRoot(), "Inferpal", "ToolWindow", "InferpalToolWindowContent.xaml"));
        Assert.Contains("Command=\"{Binding MenuReviewCommitCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding MenuProjectMapCommand}\"", xaml, StringComparison.Ordinal);

        // Through the pending prompt, as the editor's commands: the composer's draft is kept, a running turn queues it.
        var chrome = Code("Inferpal", "ToolWindow", "InferpalToolWindowData.Chrome.cs");
        Assert.Contains("_contextHolder.SetPendingPrompt(\"/map\");", chrome, StringComparison.Ordinal);
        Assert.DoesNotContain("RunSuggestionAsync(\"/map\"", chrome, StringComparison.Ordinal);
        // Alt+' keeps its key without a menu.
        Assert.Contains("new CommandShortcutConfiguration(ModifierKey.LeftAlt, Key.VK_OEM_7)",
                        Code("Inferpal", "Commands", "AskCompletionCommand.cs"), StringComparison.Ordinal);
    }

    /// <summary>Ctrl+Shift+I is the text editor's reverse incremental search, and that scope goes before the Global one an
    /// extension's key lands in: from the editor the command is never reached, while every document promises it.</summary>
    [Fact]
    public void EditWithAi_HasAKeyTheTextEditorDoesNotKeep_AndTheDocsNameIt()
    {
        Assert.Contains("new CommandShortcutConfiguration(ModifierKey.ShiftLeftAlt, Key.I)",
                        Code("Inferpal", "Commands", "InlineEditSelectionCommand.cs"), StringComparison.Ordinal);
        foreach (var doc in DocCountersTests.LivingDocs())
            Assert.DoesNotContain("Ctrl+Shift+I", File.ReadAllText(doc), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("**Shift+Alt+I**", File.ReadAllText(Path.Combine(ConventionCoverageTests.RepoRoot(), "docs", "features.md")),
                        StringComparison.Ordinal);
    }
}
