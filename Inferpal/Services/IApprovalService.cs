namespace Inferpal.Services;

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
    Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct, string? subject = null);
}
