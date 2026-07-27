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

    protected override async Task<ApprovalDecision> PromptUserAsync(string message, Services.CodeActions.DiffInfo? diff, CancellationToken ct)
    {
        try
        {
            // No rich diff surface over the wire yet: flatten the structured change to the
            // prefixed text the VS Code approval card already renders.
            if (diff is not null)
            {
                var diffText = Services.CodeActions.DiffComputer.ComputeText(diff.OldText, diff.NewText);
                if (diffText is not null) message += "\n\n" + diffText;
            }

            var answer = await _rpc.InvokeWithParameterObjectAsync<int>(
                "approval/request", new { message }, ct);
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
