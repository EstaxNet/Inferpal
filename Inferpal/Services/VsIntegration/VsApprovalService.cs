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
/// During a chat turn the prompt is a card in the conversation (<see cref="VsContextHolder.InlineApproval"/>), whose
/// "Open diff" opens the dialog; outside a turn, file mutations (structured diff available) get a modal dialog with
/// the colored diff viewer, and everything else keeps the lightweight three-choice VS prompt.
/// </summary>
internal class VsApprovalService : ApprovalServiceBase
{
    private readonly VisualStudioExtensibility _vs;
    private readonly VsContextHolder _context;

    public VsApprovalService(VisualStudioExtensibility vs, InferpalConfig config, ProjectIndexService index, VsContextHolder context)
        : base(config, () => index.RootDir)
    {
        _vs      = vs;
        _context = context;
        // What an answer wrote to the settings is said in the conversation the card was in.
        Notice   = text => context.Notice?.Invoke(text);
    }

    protected override async Task<ApprovalDecision> PromptUserAsync(ApprovalPrompt prompt, CancellationToken ct)
    {
        if (_context.InlineApproval is { } inline)
        {
            try
            {
                var card = ApprovalCard.Build(prompt, RootDir);
                // The dialog the card's "Open diff" shows closes when the card is answered from the chat.
                Func<CancellationToken, Task<ApprovalDecision?>>? openDiff = prompt.Diff is { } d
                    ? async answered =>
                    {
                        using var both = CancellationTokenSource.CreateLinkedTokenSource(ct, answered);
                        try { return await ShowDiffDialogAsync(prompt.Message, d, card, both.Token); }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
                    }
                    : null;
                if (await inline(card, openDiff, ct) is { } answer)
                    return answer;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // The card could not be shown: the dialog asks instead — never an approval nobody is asked.
                Diagnostics.Swallow("VsApprovalService.InlineCard", ex);
            }
        }
        // Outside a turn the dialog asks — offering the card's narrow rule too, as VS Code's dialog does.
        return await AskInDialogAsync(prompt.Message, prompt.Diff, ApprovalCard.Build(prompt, RootDir), ct);
    }

    protected override Task<ApprovalDecision> PromptUserAsync(string message, DiffInfo? diff, CancellationToken ct) =>
        AskInDialogAsync(message, diff, card: null, ct);

    /// <summary>A server's form is a card of a running turn, never a dialog: outside a turn, no form is declared.</summary>
    public override bool CanAskForInput => _context.InlineInput is not null && _context.TurnRuns?.Invoke() == true;

    protected override async Task<InputReply?> PromptInputAsync(InputCardModel card, CancellationToken ct)
    {
        if (_context.InlineInput is not { } inline) return null;
        try { return await inline(card, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Not drawn: the server is told the person did not answer, as VS Code's host tells it.
            Diagnostics.Swallow("VsApprovalService.InputCard", ex);
            return null;
        }
    }

    protected override void ShowInputReceipt(InputReceipt receipt) => _context.InputReceipt?.Invoke(receipt);

    /// <param name="card">The card of the call, for the narrow rule it offers; <c>null</c> for a prompt without one.</param>
    private async Task<ApprovalDecision> AskInDialogAsync(string message, DiffInfo? diff, ApprovalCardModel? card, CancellationToken ct)
    {
        if (diff is not null)
        {
            try
            {
                // Dismissed (Esc / close box) → fail closed.
                return await ShowDiffDialogAsync(message, diff, card, ct) ?? ApprovalDecision.Deny;
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

        return await ShowClassicPromptAsync(message, card, ct);
    }

    /// <summary>Modal dialog with the colored diff viewer and the three in-content choices.
    /// Built-in dialog buttons are hidden; a content button completes <c>Decision</c> and the
    /// linked token then closes the dialog. <c>null</c> when the dialog was dismissed: the prompt denies (fail closed),
    /// a card opened from the chat keeps waiting.</summary>
    private async Task<ApprovalDecision?> ShowDiffDialogAsync(string message, DiffInfo diff, ApprovalCardModel? card,
                                                              CancellationToken ct)
    {
        var data = new ApprovalDialogData(message, diff, card?.RuleLabel ?? string.Empty, card?.RuleTooltip ?? string.Empty);
        using var control = new ApprovalDialogControl(data);

        using var close = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var dialogTask = _vs.Shell().ShowDialogAsync(
            control, Strings.ApprovalDialogTitle,
            new DialogOption(DialogButton.None, DialogResult.None), close.Token);

        var first = await Task.WhenAny(data.Decision, dialogTask);
        if (first == dialogTask)
        {
            await dialogTask;                      // surface faults; normal completion = dismissed
            return null;
        }

        var decision = await data.Decision;
        await close.CancelAsync();                 // programmatic close, per the SDK contract
        try { await dialogTask; } catch (OperationCanceledException) { }
        return decision;
    }

    private async Task<ApprovalDecision> ShowClassicPromptAsync(string message, ApprovalCardModel? card, CancellationToken ct)
    {
        // Three choices: "Allow once" (default, preserves the old Enter=approve behaviour),
        // "Always allow this tool" (remembers for the session), and "Cancel" — and the narrow rule when the card offers one.
        var choices = new ChoiceResultCollection<ApprovalDecision>();
        choices.Add(Strings.ApprovalAllowOnce,   ApprovalDecision.Once);
        choices.Add(Strings.ApprovalAlwaysAllow, ApprovalDecision.Always);
        if (card is { RuleLabel.Length: > 0 }) choices.Add(card.RuleLabel, ApprovalDecision.Rule);
        choices.Add(Strings.ApprovalDeny,        ApprovalDecision.Deny);

        // Default = "Allow once"; dismissing the prompt (Esc/close) denies.
        var options = new PromptOptions<ApprovalDecision>(choices, defaultChoiceIndex: 0, dismissedReturns: ApprovalDecision.Deny);

        return await _vs.Shell().ShowPromptAsync(message, options, ct);
    }
}
