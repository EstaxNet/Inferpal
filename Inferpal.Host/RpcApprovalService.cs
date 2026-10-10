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
internal sealed class RpcApprovalService : ApprovalServiceBase, IApprovalService
{
    private readonly JsonRpc _rpc;
    private readonly bool    _structured;

    /// <summary>The files of the multi-file change being approved in this flow (<see cref="RequestBatchApprovalAsync"/>).</summary>
    private static readonly AsyncLocal<IReadOnlyList<FileChange>?> Batch = new();

    /// <param name="structured">The request also carries what an adapter draws on its own: the id of the tool call it
    /// belongs to (<see cref="ToolEventRegistry.CurrentCallId"/>), the tool, and each file's change as old and new
    /// text (<see cref="InitializeParams.ToolEvents"/>).</param>
    public RpcApprovalService(InferpalConfig config, Func<string?> rootDir, JsonRpc rpc, bool structured = false)
        : base(config, rootDir)
    {
        _rpc        = rpc;
        _structured = structured;
    }

    /// <summary>
    /// A multi-file change asks once, as before — and, for an adapter that draws each file's change, keeps the files at
    /// hand for the prompt below: the base question carries the summary only.
    /// </summary>
    public async Task<bool> RequestBatchApprovalAsync(string toolName, string details, IReadOnlyList<FileChange> files,
                                                      CancellationToken ct, string? subject = null)
    {
        var previous = Batch.Value;
        Batch.Value = files;
        try { return await RequestApprovalAsync(toolName, details, ct, subject: subject); }
        finally { Batch.Value = previous; }
    }

    /// <summary>The card the chat draws (title, subject, the start of the change) goes with the one-sentence prompt.</summary>
    protected override async Task<ApprovalDecision> PromptUserAsync(Services.Presentation.ApprovalPrompt prompt, CancellationToken ct)
    {
        var card = Services.Presentation.ApprovalCard.Build(prompt, RootDir);
        return await AskAsync(Flatten(prompt.Message, prompt.Diff), card, _structured ? prompt : null, ct);
    }

    protected override Task<ApprovalDecision> PromptUserAsync(string message, Services.CodeActions.DiffInfo? diff, CancellationToken ct) =>
        AskAsync(Flatten(message, diff), card: null, prompt: null, ct);

    /// <summary>The prompt with its change as prefixed text — what "Open diff" opens, and the fallback dialog shows.</summary>
    private static string Flatten(string message, Services.CodeActions.DiffInfo? diff)
    {
        if (diff is null) return message;
        var diffText = Services.CodeActions.DiffComputer.ComputeText(diff.OldText, diff.NewText);
        return diffText is null ? message : message + "\n\n" + diffText;
    }

    /// <summary>Each file's change, its path absolute: the batch's files, else the single diff under the prompt's subject.</summary>
    private List<object> Diffs(Services.Presentation.ApprovalPrompt prompt)
    {
        var changes = Batch.Value is { Count: > 0 } files
            ? files.Select(f => f.Diff)
            : prompt.Diff is { } one ? [one] : [];
        var list = new List<object>();
        foreach (var d in changes)
        {
            var path = string.IsNullOrWhiteSpace(d.FilePath) ? prompt.Subject : d.FilePath;
            if (string.IsNullOrWhiteSpace(path)) continue;
            list.Add(new { path = Absolute(path), oldText = d.OldText, newText = d.NewText });
        }
        return list;
    }

    private string Absolute(string path)
    {
        try
        {
            var root = RootDir;
            return Path.IsPathRooted(path) || string.IsNullOrEmpty(root) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(root, path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;   // shown as written: a path the file system refuses is still what the tool was given
        }
    }

    private async Task<ApprovalDecision> AskAsync(string message, Services.Presentation.ApprovalCardModel? card,
                                                  Services.Presentation.ApprovalPrompt? prompt, CancellationToken ct)
    {
        try
        {
            var answer = prompt is null
                ? await _rpc.InvokeWithParameterObjectAsync<int>("approval/request", new { message, card }, ct)
                : await _rpc.InvokeWithParameterObjectAsync<int>("approval/request", new
                  {
                      message,
                      card,
                      callId  = ToolEventRegistry.CurrentCallId,
                      tool    = prompt.ToolName,
                      details = prompt.Details,
                      diffs   = Diffs(prompt),
                  }, ct);
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
