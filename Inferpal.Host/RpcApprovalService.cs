using Inferpal.Config;
using Inferpal.Services;
using StreamJsonRpc;

namespace Inferpal.Host;

/// <summary>
/// Approval prompt over reverse JSON-RPC: the whole pipeline (pattern permission rules,
/// hard denylist, session grants) lives in <see cref="ApprovalServiceBase"/>; this class
/// only forwards the user-facing question to the editor adapter (`approval/request`),
/// which renders the approval card and answers 0 = deny, 1 = once, 2 = always.
/// </summary>
internal sealed class RpcApprovalService : ApprovalServiceBase
{
    private readonly JsonRpc _rpc;

    public RpcApprovalService(InferpalConfig config, Func<string?> rootDir, JsonRpc rpc)
        : base(config, rootDir) => _rpc = rpc;

    /// <summary>The card the chat draws (title, subject, the start of the change) goes with the one-sentence prompt.</summary>
    protected override async Task<ApprovalDecision> PromptUserAsync(Services.Presentation.ApprovalPrompt prompt, CancellationToken ct)
    {
        var card = Services.Presentation.ApprovalCard.Build(prompt, RootDir);
        return await AskAsync(Flatten(prompt.Message, prompt.Diff), card, ct);
    }

    protected override Task<ApprovalDecision> PromptUserAsync(string message, Services.CodeActions.DiffInfo? diff, CancellationToken ct) =>
        AskAsync(Flatten(message, diff), card: null, ct);

    /// <summary>The prompt with its change as prefixed text — what "Open diff" opens, and the fallback dialog shows.</summary>
    private static string Flatten(string message, Services.CodeActions.DiffInfo? diff)
    {
        if (diff is null) return message;
        var diffText = Services.CodeActions.DiffComputer.ComputeText(diff.OldText, diff.NewText);
        return diffText is null ? message : message + "\n\n" + diffText;
    }

    private async Task<ApprovalDecision> AskAsync(string message, Services.Presentation.ApprovalCardModel? card, CancellationToken ct)
    {
        try
        {
            var answer = await _rpc.InvokeWithParameterObjectAsync<int>(
                "approval/request", new { message, card }, ct);
            return answer switch
            {
                1 => ApprovalDecision.Once,
                2 => ApprovalDecision.Always,
                _ => ApprovalDecision.Deny,
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Fail closed: an unreachable adapter must never auto-approve a destructive tool.
            Diagnostics.Swallow("RpcApprovalService.Prompt", ex);
            return ApprovalDecision.Deny;
        }
    }
}
