using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Services.Rag;
using Inferpal.ToolWindow;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Shell;
using Microsoft.VisualStudio.RpcContracts.Notifications;

namespace Inferpal.Services.VsIntegration;

/// <summary>
/// Visual Studio flavor of the approval pipeline: all decision logic lives in
/// <see cref="ApprovalServiceBase"/>; this class only renders the user-facing prompt.
/// File mutations (structured diff available) get a modal dialog with the colored diff
/// viewer; everything else keeps the lightweight three-choice VS prompt.
/// </summary>
internal class VsApprovalService : ApprovalServiceBase
{
    private readonly VisualStudioExtensibility _vs;

    public VsApprovalService(VisualStudioExtensibility vs, InferpalConfig config, ProjectIndexService index)
        : base(config, () => index.RootDir)
        => _vs = vs;

    protected override async Task<ApprovalDecision> PromptUserAsync(string message, DiffInfo? diff, CancellationToken ct)
    {
        if (diff is not null)
        {
            try
            {
                return await ShowDiffDialogAsync(message, diff, ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return ApprovalDecision.Deny;      // dialog dismissed (Esc / close box) → fail closed
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Rich dialog unavailable — degrade to the classic prompt with a textual diff.
                Diagnostics.Swallow("VsApprovalService.DiffDialog", ex);
                var diffText = DiffComputer.ComputeText(diff.OldText, diff.NewText);
                if (diffText is not null) message += "\n\n" + diffText;
            }
        }

        return await ShowClassicPromptAsync(message, ct);
    }

    /// <summary>Modal dialog with the colored diff viewer and the three in-content choices.
    /// Built-in dialog buttons are hidden; a content button completes <c>Decision</c> and the
    /// linked token then closes the dialog. Dismissing the dialog denies (fail closed).</summary>
    private async Task<ApprovalDecision> ShowDiffDialogAsync(string message, DiffInfo diff, CancellationToken ct)
    {
        var data = new ApprovalDialogData(message, diff);
        using var control = new ApprovalDialogControl(data);

        using var close = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var dialogTask = _vs.Shell().ShowDialogAsync(
            control, Strings.ApprovalDialogTitle,
            new DialogOption(DialogButton.None, DialogResult.None), close.Token);

        var first = await Task.WhenAny(data.Decision, dialogTask);
        if (first == dialogTask)
        {
            await dialogTask;                      // surface faults; normal completion = dismissed
            return ApprovalDecision.Deny;
        }

        var decision = await data.Decision;
        await close.CancelAsync();                 // programmatic close, per the SDK contract
        try { await dialogTask; } catch (OperationCanceledException) { }
        return decision;
    }

    private async Task<ApprovalDecision> ShowClassicPromptAsync(string message, CancellationToken ct)
    {
        // Three choices: "Allow once" (default, preserves the old Enter=approve behaviour),
        // "Always allow this tool" (remembers for the session), and "Cancel".
        var choices = new ChoiceResultCollection<ApprovalDecision>();
        choices.Add(Strings.ApprovalAllowOnce,   ApprovalDecision.Once);
        choices.Add(Strings.ApprovalAlwaysAllow, ApprovalDecision.Always);
        choices.Add(Strings.ApprovalDeny,        ApprovalDecision.Deny);

        // Default = "Allow once"; dismissing the prompt (Esc/close) denies.
        var options = new PromptOptions<ApprovalDecision>(choices, defaultChoiceIndex: 0, dismissedReturns: ApprovalDecision.Deny);

        return await _vs.Shell().ShowPromptAsync(message, options, ct);
    }
}
