using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Services;
using Inferpal.ToolWindow;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.Shell;

namespace Inferpal.Commands;

/// <summary>
/// "Fix with Inferpal" in the Error List's context menu: the lines chosen in the list and the code around each, asked of
/// the chat with its tools. The selection is read by the in-process half (<see cref="ErrorListSelectionSignal"/>): a tool
/// window's selection does not cross to this process. VS Code's quick fix asks the same of the host.
/// </summary>
[VisualStudioContribution]
internal class FixErrorListCommand : Command
{
    /// <summary>The in-process half reads the list on the UI thread, which is free once the menu has closed.</summary>
    private static readonly TimeSpan AnswerBudget = TimeSpan.FromSeconds(5);

    private readonly VsContextHolder _contextHolder;
    private readonly Services.Editor.OpenDocumentOverlay _overlay;

    public FixErrorListCommand(VisualStudioExtensibility extensibility, VsContextHolder contextHolder,
                               Services.Editor.OpenDocumentOverlay overlay)
        : base(extensibility)
    {
        _contextHolder = contextHolder;
        _overlay       = overlay;
    }

    public override CommandConfiguration CommandConfiguration => new("%InferpalFixErrorListCommand%")
    {
        Icon = new(ImageMoniker.KnownValues.EditBug, IconSettings.IconAndText),
    };

    // GroupPlacement.VsctParent parents a group to a VSCT menu; CommandPlacement.VsctParent would need a group ID.
    [VisualStudioContribution]
    internal static CommandGroupConfiguration ErrorListContextGroup { get; } =
        new(GroupPlacement.VsctParent(VsctMenus.ShlMainMenu, VsctMenus.ErrorListContext, 0))
        {
            Children = [GroupChild.Command<FixErrorListCommand>()],
        };

    public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken ct)
    {
        _contextHolder.Context = context;

        var answer = await ErrorListSelectionSignal.AskAsync(AnswerBudget, ct);
        // The package serves the request, not the editor listener: alive alone, the listener does not answer.
        if (ProblemsFix.Refusal(answer, InProcAliveSignal.TryRead() is { HasPackage: true }) is { } refusal)
        {
            await Extensibility.Shell().ShowPromptAsync(refusal, PromptOptions.OK, ct);
            return;
        }

        WorkspaceExclusions.Follow();
        if (ProblemsFix.NothingEditable(answer!.Problems, SolutionExtent.ActiveRoot()) is { } outOfReach)
        {
            await Extensibility.Shell().ShowPromptAsync(outOfReach, PromptOptions.OK, ct);
            return;
        }
        var (question, chip, content) = await ProblemsFix.ForAsync(answer.Problems, _overlay, ct);
        _contextHolder.SetPendingPrompt(question, modelOverride: null, attachLabel: chip, attachContent: content);
        await Extensibility.Shell().ShowToolWindowAsync<InferpalToolWindow>(activate: true, ct);
    }
}
