using System.IO;
using System.Windows.Input;
using Inferpal.GhostText;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ Tab is also the key that commits the C# completion list, which opens as an identifier is typed: consumed by the
/// ghost text, it inserted the suggestion and the list item was never committed; Escape hid the suggestion and left the
/// list open; Shift+Tab (outdent) inserted the suggestion. Plain Tab and Escape only, and an open completion list keeps
/// its keys.
/// </summary>
/// <remarks>
/// ⚠ And the keys must REACH the ghost text: Visual Studio turns Tab and Escape into editor commands before WPF sees
/// them, so a PreviewKeyDown listener alone never runs in a live Visual Studio: Tab inserts a tab under a visible
/// suggestion, Escape leaves it up. The editor command chain is the path.
/// </remarks>
public class GhostTextKeysTests
{
    [Theory]
    [InlineData(Key.Tab,    ModifierKeys.None,  false, "Accept")]
    [InlineData(Key.Escape, ModifierKeys.None,  false, "Dismiss")]
    [InlineData(Key.Tab,    ModifierKeys.None,  true,  "LeaveToCompletion")]
    [InlineData(Key.Escape, ModifierKeys.None,  true,  "LeaveToCompletion")]
    [InlineData(Key.Tab,    ModifierKeys.Shift, false, "None")]
    [InlineData(Key.Tab,    ModifierKeys.Control, false, "None")]
    [InlineData(Key.Enter,  ModifierKeys.None,  false, "None")]
    public void TheKeys_OfASuggestion(Key key, ModifierKeys modifiers, bool completionActive, string expected) =>
        Assert.Equal(expected, GhostTextKeys.Decide(key, modifiers, completionActive).ToString());

    [Fact]
    public void TheController_DecidesThroughTheRule_WithTheRealModifiersAndTheCompletionList()
    {
        var dir        = Path.Combine(ConversationPersistenceSilenceTests.RepoRoot(), "Inferpal.InProc", "GhostText");
        var controller = ConventionCoverageTests.CodeOnly(Path.Combine(dir, "GhostTextController.cs"));
        var keyDown    = controller[controller.IndexOf("private void OnPreviewKeyDown(", StringComparison.Ordinal)..];
        keyDown        = keyDown[..keyDown.IndexOf("private bool CompletionActive(", StringComparison.Ordinal)];

        Assert.Contains("HandleKey(e.Key, Keyboard.Modifiers)", keyDown, StringComparison.Ordinal);
        Assert.Contains("GhostTextKeys.Decide(key, modifiers, CompletionActive())", keyDown, StringComparison.Ordinal);
        Assert.DoesNotContain("== Key.Tab", keyDown, StringComparison.Ordinal);

        // ⚠ Optional import: a required one that does not resolve fails the whole part's composition, and ghost text,
        // the inline diff preview and the build bootstrap die with it, in silence.
        var listener = ConventionCoverageTests.CodeOnly(Path.Combine(dir, "GhostTextViewListener.cs"));
        Assert.Matches(@"\[Import\(AllowDefault = true\)\]\s*internal IAsyncCompletionBroker\? CompletionBroker", listener);
        Assert.Contains("new GhostTextController(textView, CompletionBroker)", listener, StringComparison.Ordinal);
    }

    /// <summary>The path Tab and Escape really take: an editor command handler, ahead of the completion list's.</summary>
    [Fact]
    public void TabAndEscape_ReachTheGhostText_ThroughTheEditorCommandChain()
    {
        // Read as source: the editor's assemblies are not loadable in the test process.
        var dir    = Path.Combine(ConversationPersistenceSilenceTests.RepoRoot(), "Inferpal.InProc", "GhostText");
        var source = ConventionCoverageTests.CodeOnly(Path.Combine(dir, "GhostTextCommandHandler.cs"));
        Assert.Contains("[Export(typeof(ICommandHandler))]", source, StringComparison.Ordinal);
        Assert.Contains("[Order(Before = PredefinedCompletionNames.CompletionCommandHandler)]", source, StringComparison.Ordinal);
        Assert.Contains("ICommandHandler<TabKeyCommandArgs>", source, StringComparison.Ordinal);
        Assert.Contains("ICommandHandler<EscapeKeyCommandArgs>", source, StringComparison.Ordinal);

        // One decision for both paths: the handler hands the key to the controller, never decides itself.
        Assert.Contains("controller.HandleKey(key, Keyboard.Modifiers)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GhostTextKeys.Decide", source, StringComparison.Ordinal);

        var controller = ConventionCoverageTests.CodeOnly(Path.Combine(dir, "GhostTextController.cs"));
        Assert.Contains("view.Properties[typeof(GhostTextController)] = this;", controller, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(' ',  true,  false, true)]    // after "return ", "= ": the positions a suggestion is worth most at
    [InlineData('.',  true,  false, true)]    // after "user." with the list closed (a comment, a string, a number)
    [InlineData('(',  true,  false, true)]
    [InlineData('x',  true,  false, true)]
    [InlineData('.',  true,  true,  false)]   // the list is open: Tab is its key
    [InlineData('x',  true,  true,  false)]
    [InlineData('.',  false, false, false)]   // no broker: the character that opens the list is the only sign of it
    [InlineData(' ',  false, false, false)]
    [InlineData('x',  false, false, true)]
    public void ASuggestionIsAsked_UnlessTheCompletionListOwnsTheCaret(
        char previous, bool brokerKnown, bool completionActive, bool expected) =>
        Assert.Equal(expected, GhostTextTrigger.Asks(previous, brokerKnown, completionActive));

    [Fact]
    public void TheStartOfTheDocument_AsksToo() => Assert.True(GhostTextTrigger.Asks(null, false, false));

    [Fact]
    public void TheController_AsksThroughTheRule_NeverThroughItsOwnCharacterList()
    {
        // ⚠ The controller refused after any of `. ( [ < " ' ,` or a space, on top of the open-list check: no
        // suggestion after "return ", "= ", "user." or at the start of an indented line — where VS Code suggests.
        var dir        = Path.Combine(ConversationPersistenceSilenceTests.RepoRoot(), "Inferpal.InProc", "GhostText");
        var controller = ConventionCoverageTests.CodeOnly(Path.Combine(dir, "GhostTextController.cs"));

        Assert.Contains("GhostTextTrigger.Asks(cursor > 0 ? snapshot[cursor - 1] : null, _completion is not null, CompletionActive())",
                        controller, StringComparison.Ordinal);
        Assert.DoesNotContain("IntelliSenseTrigger", controller, StringComparison.Ordinal);
    }
}
