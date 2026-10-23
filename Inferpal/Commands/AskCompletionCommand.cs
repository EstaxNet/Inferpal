using Inferpal.Config;
using Inferpal.Localization;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.Editor;

namespace Inferpal.Commands;

/// <summary>
/// Alt+' — asks for an inline completion at the caret, or takes the suggestion shown (a second accept key where an
/// open list holds Tab). The suggestion lives in devenv (the in-process ghost text); this command, out of process, has
/// what decides: the settings read now, the chat's state, the sentences — sent along by <see cref="CompletionAskSignal"/>.
/// </summary>
/// <remarks>⚠ The key is the same in VS Code (<c>package.json</c>) and free in Visual Studio, ReSharper's Visual Studio
/// scheme and VS Code — Alt+\ is Visual Studio's Go to Member and ReSharper's Go to File Member. It can be changed in
/// Tools › Options › Keyboard.</remarks>
[VisualStudioContribution]
internal class AskCompletionCommand : Command
{
    public AskCompletionCommand(VisualStudioExtensibility extensibility) : base(extensibility) { }

    public override CommandConfiguration CommandConfiguration => new("%InferpalAskCompletionCommand%")
    {
        Shortcuts = [new CommandShortcutConfiguration(ModifierKey.LeftAlt, Key.VK_OEM_7)],
    };

    public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken ct)
    {
        string? path;
        try
        {
            // No editor, no suggestion to ask for: the key does nothing, as it did before.
            if (await Extensibility.Editor().GetActiveTextViewAsync(context, ct) is not { } view) return;
            path = view.Document.Uri.LocalPath;
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            Services.Diagnostics.Swallow("AskCompletion.ActiveView", ex);
            path = null;
        }
        var config = InferpalConfig.Load();
        // The chat window declares the workspace's ignore files; it may never have been opened in this session.
        Exclusions.Follow();
        CompletionAskSignal.Write(AskedCompletion.Refusal(config, path), AskedCompletion.Nothing, AskedCompletion.ServerFailed,
                                  Strings.CompletionAskedListHint, Strings.CompletionAskedNoEditor);
    }

    private static readonly Services.Governance.OpenWorkspaceExclusions Exclusions = new(() => InferpalConfig.Load().IgnoreFiles);
}
