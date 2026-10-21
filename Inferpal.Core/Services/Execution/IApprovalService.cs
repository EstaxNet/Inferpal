namespace Inferpal.Services.Execution;

/// <summary>
/// Prompts the user for confirmation before a tool performs a potentially destructive operation.
/// </summary>
/// <remarks>
/// Used by <c>write_file</c> and <c>run_command</c> when
/// <see cref="Inferpal.Config.InferpalConfig.SecurityAlertsDisabled"/> is <c>false</c>.
/// Inject this interface instead of the concrete <c>VsApprovalService</c> to keep tools testable.
/// </remarks>
internal interface IApprovalService
{
    /// <summary>
    /// Shows a confirmation dialog and returns <c>true</c> if the user approved,
    /// <c>false</c> if they cancelled or if security alerts are disabled and auto-approved.
    /// </summary>
    /// <param name="toolName">Tool requesting approval (e.g. <c>"write_file"</c>).</param>
    /// <param name="details">Human-readable summary of the action (path, byte count, command, etc.).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="subject">
    /// Machine-matchable value the permission rules are evaluated against — the raw shell command,
    /// the absolute file path, the URL. When <c>null</c>, <paramref name="details"/> is used (already
    /// the raw value for <c>run_command</c>/<c>fetch_url</c>/<c>web_search</c>); pass it explicitly for
    /// tools whose <paramref name="details"/> is a localized sentence (file writes/deletes).
    /// </param>
    /// <param name="diff">
    /// Structured old→new change for file-mutating tools, so the prompt can render a rich
    /// colored diff. Implementations without a rich surface flatten it to text
    /// (<c>DiffComputer.ComputeText</c>); <c>null</c> for tools with nothing to preview.
    /// </param>
    /// <param name="forcePrompt">
    /// Suppresses every auto-approval path — allow rules, session grants,
    /// <see cref="Inferpal.Config.InferpalConfig.SecurityAlertsDisabled"/> — so the human always
    /// reads this one. For actions whose content was authored by the <b>repository</b> rather than
    /// by the user (a <c>.inferpal/validators.json</c> build command ships with any clone): the
    /// consent given to one's own agent is not consent to a stranger's command.
    /// </param>
    Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct, string? subject = null, Services.CodeActions.DiffInfo? diff = null, bool forcePrompt = false);

    /// <summary>
    /// One approval for a change that spans several files (<c>apply_edits</c>): one prompt for the whole batch, with the
    /// batch's combined <paramref name="details"/>.
    /// </summary>
    /// <param name="files">Each changed file's own old→new text and summary.</param>
    /// <remarks>
    /// ⚠ An implementation that KEEPS changes instead of applying them (the <c>/task</c> recorder) needs each file's own
    /// change: a proposal is applied file by file, from its diff, and a batch recorded without one can never be applied.
    /// </remarks>
    /// <param name="forcePrompt">As for <see cref="RequestApprovalAsync"/>: no rule, no grant approves this batch.</param>
    Task<bool> RequestBatchApprovalAsync(string toolName, string details, IReadOnlyList<FileChange> files,
                                         CancellationToken ct, string? subject = null, bool forcePrompt = false) =>
        RequestApprovalAsync(toolName, details, ct, subject: subject, forcePrompt: forcePrompt);

    /// <summary>
    /// A form an MCP server asks for can be shown to the person now, as a card in the conversation of a running turn.
    /// </summary>
    /// <remarks>
    /// ⚠ Read for each call: the call's request declares the elicitation capability only when this is true, and a
    /// server must not ask what was not declared. False by default — a pipeline that cannot draw the card (a
    /// <c>/task</c>, an Agent Client Protocol client) never lets a server ask.
    /// </remarks>
    bool CanAskForInput => false;

    /// <summary>
    /// The person's rules refuse this call without asking — a <c>deny</c> rule, the built-in denylist. Read before anything
    /// the call would send on its way to the card (the package check), so that nothing leaves for a refused call.
    /// False when the service holds no rules.
    /// </summary>
    bool Refuses(string toolName, string subject) => false;

    /// <summary>
    /// Asks the person to fill a server's form: the card shows again with what to fix until the values pass its schema,
    /// or the person declines or cancels. Never answered by anything but the person — no rule, no grant, no setting.
    /// </summary>
    Task<Mcp.McpInputAnswer> AskForInputAsync(Mcp.McpInputForm form, CancellationToken ct) =>
        Task.FromResult(new Mcp.McpInputAnswer(Mcp.McpInputAction.NotShown));
}

/// <summary>One file of a multi-file change: its old→new text, and the summary a reviewer reads for it.</summary>
internal sealed record FileChange(Services.CodeActions.DiffInfo Diff, string Details);
