using Inferpal.Config;
using Inferpal.Services;
using StreamJsonRpc;

namespace Inferpal.Host;

/// <summary>
/// Approval prompt over reverse JSON-RPC: the whole pipeline (pattern permission rules,
/// hard denylist, session grants) lives in <see cref="ApprovalServiceBase"/>; this class
/// only forwards the user-facing question to the editor adapter (`approval/request`),
/// which renders the approval card and answers 0 = deny, 1 = once, 2 = always, 3 = the narrow rule the card offers
/// (any other number denies, so an adapter that predates an answer fails closed).
/// </summary>
internal sealed class RpcApprovalService : ApprovalServiceBase, IApprovalService
{
    private readonly JsonRpc _rpc;
    private readonly bool    _structured;
    private readonly bool    _inputCards;

    /// <summary>The files of the multi-file change being approved in this flow (<see cref="RequestBatchApprovalAsync"/>).</summary>
    private static readonly AsyncLocal<IReadOnlyList<FileChange>?> Batch = new();

    /// <param name="structured">The request also carries what an adapter draws on its own: the id of the tool call it
    /// belongs to (<see cref="ToolEventRegistry.CurrentCallId"/>), the tool, and each file's change as old and new
    /// text (<see cref="InitializeParams.ToolEvents"/>).</param>
    /// <param name="inputCards">The adapter draws the card an MCP server's form is filled on (<c>input/form</c>,
    /// <see cref="InitializeParams.InputCards"/>).</param>
    public RpcApprovalService(InferpalConfig config, Func<string?> rootDir, JsonRpc rpc, bool structured = false,
                              bool inputCards = false)
        : base(config, rootDir)
    {
        _rpc        = rpc;
        _structured = structured;
        _inputCards = inputCards;
    }

    /// <summary>A chat turn runs: the only time the adapter draws a card. Set by the host.</summary>
    internal Func<bool>? TurnRuns { get; set; }

    /// <summary>The adapter draws input cards, and a turn is running to draw this one in.</summary>
    public override bool CanAskForInput => _inputCards && TurnRuns?.Invoke() == true;

    /// <summary>
    /// The card, asked of the adapter (<c>input/form</c>): it answers what the person did and the values they left, or
    /// <c>null</c> when it could not draw the card.
    /// </summary>
    protected override async Task<Services.Presentation.InputReply?> PromptInputAsync(Services.Presentation.InputCardModel card, CancellationToken ct)
    {
        try
        {
            var answer = await _rpc.InvokeWithParameterObjectAsync<InputFormAnswer?>("input/form", new { card }, ct);
            // ⚠ An adapter that retires the card on the cancellation still answers the request (null): the Stop is the
            // run's, and the card says it went unanswered, never that it could not be drawn.
            ct.ThrowIfCancellationRequested();
            return answer is null ? null : answer.ToReply();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Not shown: the server is told the person did not answer, never that they sent nothing.
            Diagnostics.Swallow("RpcApprovalService.InputForm", ex);
            return null;
        }
    }

    /// <summary>The answered card's line, for the adapter to draw where the card was and to keep in the conversation.</summary>
    protected override void ShowInputReceipt(Services.Presentation.InputReceipt receipt)
    {
        var send = _rpc.NotifyWithParameterObjectAsync("chat/inputReceipt", new
        {
            id     = receipt.CardId,
            title  = receipt.Title,
            detail = receipt.Detail,
            body   = receipt.Body,
            saved  = receipt.Saved,
        });
        // A connection gone meanwhile has nobody left to show it to: traced, never left unobserved.
        _ = send.ContinueWith(t => Diagnostics.Swallow("RpcApprovalService.InputReceipt", t.Exception!),
                              CancellationToken.None,
                              TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                              TaskScheduler.Default);
    }

    /// <summary>
    /// A multi-file change asks once, as before — and, for an adapter that draws each file's change, keeps the files at
    /// hand for the prompt below: the base question carries the summary only.
    /// </summary>
    public async Task<bool> RequestBatchApprovalAsync(string toolName, string details, IReadOnlyList<FileChange> files,
                                                      CancellationToken ct, string? subject = null, bool forcePrompt = false)
    {
        var previous = Batch.Value;
        Batch.Value = files;
        try { return await RequestApprovalAsync(toolName, details, ct, subject: subject, forcePrompt: forcePrompt); }
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

    /// <summary>What the adapter answers to <c>input/form</c>.</summary>
    /// <param name="Action"><c>accept</c>, <c>decline</c>, <c>cancel</c> or <c>setAside</c> (a note took the card away);
    /// anything else is read as <c>cancel</c>.</param>
    /// <param name="Values">By field key: a string (text, number, the one choice), a boolean (a box), or an array of
    /// strings (the choices ticked).</param>
    internal sealed record InputFormAnswer(string? Action, Dictionary<string, System.Text.Json.JsonElement>? Values)
    {
        public Services.Presentation.InputReply ToReply()
        {
            var action = Action switch
            {
                "accept"   => Services.Mcp.McpInputAction.Accept,
                "decline"  => Services.Mcp.McpInputAction.Decline,
                "setAside" => Services.Mcp.McpInputAction.SetAside,
                _          => Services.Mcp.McpInputAction.Cancel,
            };
            var values = new Dictionary<string, Services.Mcp.McpInputValue>(StringComparer.Ordinal);
            foreach (var (key, v) in Values ?? [])
            {
                values[key] = v.ValueKind switch
                {
                    System.Text.Json.JsonValueKind.String => new Services.Mcp.McpInputValue(Text: v.GetString()),
                    System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False => new Services.Mcp.McpInputValue(Flag: v.GetBoolean()),
                    System.Text.Json.JsonValueKind.Array => new Services.Mcp.McpInputValue(Picks:
                        [.. v.EnumerateArray().Where(e => e.ValueKind == System.Text.Json.JsonValueKind.String).Select(e => e.GetString()!)]),
                    System.Text.Json.JsonValueKind.Number => new Services.Mcp.McpInputValue(Text: v.GetRawText()),
                    _ => new Services.Mcp.McpInputValue(),
                };
            }
            return new Services.Presentation.InputReply(action, values);
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
                3 => ApprovalDecision.Rule,
                4 => ApprovalDecision.Withdrawn,
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
