using System.IO;
using System.Windows.Input;
using Inferpal.GhostText;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ Visual Studio's ghost text handles its keys on PreviewKeyDown, before the editor's own commanding. Tab is also the
/// key that commits the C# completion list, which opens as an identifier is typed: consumed here, it inserted the
/// suggestion and the list item was never committed; Escape hid the suggestion and left the list open; Shift+Tab
/// (outdent) inserted the suggestion. Plain Tab and Escape only, and an open completion list keeps its keys.
/// </summary>
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

        Assert.Contains("GhostTextKeys.Decide(e.Key, Keyboard.Modifiers, CompletionActive())", keyDown, StringComparison.Ordinal);
        Assert.DoesNotContain("e.Key == Key.Tab", keyDown, StringComparison.Ordinal);

        // ⚠ Optional import: a required one that does not resolve fails the whole part's composition, and ghost text,
        // the inline diff preview and the build bootstrap die with it, in silence.
        var listener = ConventionCoverageTests.CodeOnly(Path.Combine(dir, "GhostTextViewListener.cs"));
        Assert.Matches(@"\[Import\(AllowDefault = true\)\]\s*internal IAsyncCompletionBroker\? CompletionBroker", listener);
        Assert.Contains("new GhostTextController(textView, CompletionBroker)", listener, StringComparison.Ordinal);
    }
}
