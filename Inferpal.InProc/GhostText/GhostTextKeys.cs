using System.Windows.Input;

namespace Inferpal.GhostText;

/// <summary>What a key press does to a pending ghost-text suggestion.</summary>
internal enum GhostKeyAction
{
    /// <summary>Not ours: the key goes on to the editor untouched.</summary>
    None,
    /// <summary>Insert the suggestion; the key is consumed.</summary>
    Accept,
    /// <summary>Hide the suggestion; the key is consumed.</summary>
    Dismiss,
    /// <summary>Hide the suggestion and let the key through to the completion list that owns it.</summary>
    LeaveToCompletion,
}

/// <summary>
/// The keys of a ghost-text suggestion: plain Tab accepts, plain Escape dismisses — unless the editor's completion
/// list is open.
/// </summary>
/// <remarks>
/// ⚠ The handler runs on PreviewKeyDown, BEFORE Visual Studio's own commanding. In C# the completion list opens as an
/// identifier is typed, and Tab is the key that commits its selection: consumed here, Tab inserted the suggestion and
/// the list item was never committed; Escape hid the suggestion and left the list open. And Shift+Tab (outdent)
/// inserted the suggestion. The VS Code provider already steps aside while its suggest widget is open.
/// </remarks>
internal static class GhostTextKeys
{
    internal static GhostKeyAction Decide(Key key, ModifierKeys modifiers, bool completionActive)
    {
        if (key is not (Key.Tab or Key.Escape) || modifiers != ModifierKeys.None) return GhostKeyAction.None;
        if (completionActive) return GhostKeyAction.LeaveToCompletion;
        return key == Key.Tab ? GhostKeyAction.Accept : GhostKeyAction.Dismiss;
    }
}
