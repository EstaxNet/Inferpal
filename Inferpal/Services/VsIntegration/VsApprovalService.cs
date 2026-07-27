using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Services.Rag;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Shell;

namespace Inferpal.Services.VsIntegration;

/// <summary>
/// Visual Studio flavor of the approval pipeline: all decision logic lives in
/// <see cref="ApprovalServiceBase"/>; this class only renders the three-choice VS prompt.
/// </summary>
internal class VsApprovalService : ApprovalServiceBase
{
    private readonly VisualStudioExtensibility _vs;

    public VsApprovalService(VisualStudioExtensibility vs, InferpalConfig config, ProjectIndexService index)
        : base(config, () => index.RootDir)
        => _vs = vs;

    protected override async Task<ApprovalDecision> PromptUserAsync(string message, CancellationToken ct)
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
