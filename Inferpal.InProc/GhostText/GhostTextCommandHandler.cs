using System.ComponentModel.Composition;
using System.Windows.Input;
using Microsoft.VisualStudio.Commanding;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Editor.Commanding.Commands;
using Microsoft.VisualStudio.Utilities;

namespace Inferpal.GhostText;

/// <summary>
/// Tab accepts the ghost-text suggestion, Escape hides it — through the editor's command chain, which is where Visual
/// Studio delivers these two keys: it turns them into commands (insert a tab, cancel the selection) before WPF sees
/// them, so a <c>PreviewKeyDown</c> listener alone never runs. The decision stays <see cref="GhostTextKeys.Decide"/>,
/// by way of <see cref="GhostTextController.HandleKey"/>.
/// </summary>
/// <remarks>
/// Ordered before the completion handler: with the list open, the controller hides the suggestion and answers
/// "not handled", so the list still commits (Tab) or closes (Escape). Not handled is also the answer when nothing is
/// pending, and the key does what it always did.
/// </remarks>
[Export(typeof(ICommandHandler))]
[ContentType("code")]
[TextViewRole(PredefinedTextViewRoles.Editable)]
[Name(nameof(GhostTextCommandHandler))]
[Order(Before = PredefinedCompletionNames.CompletionCommandHandler)]
internal sealed class GhostTextCommandHandler :
    ICommandHandler<TabKeyCommandArgs>,
    ICommandHandler<EscapeKeyCommandArgs>
{
    public string DisplayName => "Inferpal ghost text";

    public CommandState GetCommandState(TabKeyCommandArgs args)    => CommandState.Unspecified;
    public CommandState GetCommandState(EscapeKeyCommandArgs args) => CommandState.Unspecified;

    public bool ExecuteCommand(TabKeyCommandArgs args, CommandExecutionContext executionContext)
        => Handle(args.TextView, Key.Tab);

    public bool ExecuteCommand(EscapeKeyCommandArgs args, CommandExecutionContext executionContext)
        => Handle(args.TextView, Key.Escape);

    private static bool Handle(ITextView view, Key key) =>
        view.Properties.TryGetProperty(typeof(GhostTextController), out GhostTextController controller)
        && controller.HandleKey(key, Keyboard.Modifiers);
}
