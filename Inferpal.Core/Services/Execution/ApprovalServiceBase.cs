using System.Collections.Concurrent;
using System.IO;
using Inferpal.Config;
using Inferpal.Localization;

namespace Inferpal.Services.Execution;

/// <summary>The three outcomes a user can pick in the approval prompt.</summary>
internal enum ApprovalDecision { Deny = 0, Once = 1, Always = 2 }

/// <summary>
/// Editor-agnostic approval pipeline: pattern permission rules (per-machine config +
/// workspace <c>.inferpal/permissions.json</c> overlay, first match wins), the hard denylist,
/// the session-scoped "always allow" grants and the compiled-policy cache.
/// Concrete services only supply the user-facing prompt (<see cref="PromptUserAsync"/>).
/// </summary>
internal abstract class ApprovalServiceBase : IApprovalService
{
    private readonly InferpalConfig _config;
    private readonly Func<string?>  _rootDir;

    /// <summary>
    /// Tool names the user chose to "always allow" via the approval prompt.
    /// Scoped to the current editor session on purpose: MCP and shell tools run arbitrary
    /// external code, so the trust grant must NOT survive a restart (never persisted to config).
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _sessionAllowed = new(StringComparer.Ordinal);

    // Cached compiled policy, rebuilt only when the config rule text or the workspace overlay file
    // changes. Regex compilation is non-trivial and RequestApprovalAsync runs on every tool call
    // (including the auto-approved ones inside an agent loop), so we must not reparse each time.
    private PermissionPolicy _policy = PermissionPolicy.Empty;
    private string?          _cachedConfigRules;
    private string?          _cachedOverlayPath;
    private DateTime         _cachedOverlayStamp;
    private readonly object  _policyLock = new();

    /// <param name="rootDir">Workspace root provider (null/empty = no workspace overlay).</param>
    protected ApprovalServiceBase(InferpalConfig config, Func<string?> rootDir)
    {
        _config  = config;
        _rootDir = rootDir;
    }

    /// <summary>Shows the editor-specific approval prompt and returns the user's decision.</summary>
    /// <param name="diff">Structured change to preview; renderers without a rich surface flatten
    /// it via <c>DiffComputer.ComputeText</c>, rich ones show the colored diff viewer.</param>
    protected abstract Task<ApprovalDecision> PromptUserAsync(string message, Services.CodeActions.DiffInfo? diff, CancellationToken ct);

    public async Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct, string? subject = null, Services.CodeActions.DiffInfo? diff = null, bool forcePrompt = false)
    {
        // The value the rules match against: the explicit subject (raw path for file tools) when
        // provided, otherwise the details (already the raw command/url/query for the other tools).
        var matchOn = subject ?? details;

        var decision = GetPolicy().Evaluate(toolName, matchOn);
        if (decision == PermissionDecision.Deny)
        {
            // Always enforced — even under SecurityAlertsDisabled. Recorded (visible via /diagnostics).
            // Thrown rather than returned false so the tool result reads "blocked by policy" instead of
            // the generic "cancelled by user": ToolRegistry turns this into a distinct string for the
            // model (uniform across every tool, including MCP and custom shell, with no per-tool change).
            Diagnostics.Record("Permission", $"Blocked {toolName}: {matchOn}");
            throw new PermissionDeniedException(
                PermissionPolicy.IsHardDenied(matchOn)
                    ? Strings.PermissionBlockedHard(matchOn)
                    : Strings.PermissionBlockedRule(matchOn));
        }
        // Indirect execution (iex, -EncodedCommand, FromBase64String…) defeats text matching:
        // the rules engine cannot know what actually runs. No auto-approval path may apply —
        // not an allow rule, not SecurityAlertsDisabled, not a session grant. The call is NOT
        // blocked: it falls through to the prompt, where the human reads the raw text.
        // `forcePrompt` joins it for the same reason from the other end: the text is perfectly
        // readable, but the repository wrote it, so no consent the user gave their own agent applies.
        var opaque = PermissionPolicy.IsOpaqueExecution(matchOn) || forcePrompt;
        if (opaque && (decision == PermissionDecision.Allow
                       || _config.SecurityAlertsDisabled
                       || _sessionAllowed.ContainsKey(toolName)))
            Diagnostics.Record("Permission",
                $"Force-prompt ({(forcePrompt ? "repository-authored" : "opaque execution")}) {toolName}: {matchOn}");

        if (!opaque)
        {
            if (decision == PermissionDecision.Allow)  return true;   // rule auto-approved

            // decision == Prompt — fall through to the global YOLO switch, then the session grant.
            if (_config.SecurityAlertsDisabled)        return true;
            if (_sessionAllowed.ContainsKey(toolName)) return true;
        }

        var message = Strings.ApprovalMessage(toolName, details);

        var promptDecision = await PromptUserAsync(message, diff, ct);
        if (promptDecision == ApprovalDecision.Always)
            _sessionAllowed[toolName] = 0;

        return promptDecision != ApprovalDecision.Deny;
    }

    /// <summary>
    /// Returns the current compiled policy, rebuilding it only when the per-machine config rules or
    /// the workspace <c>.inferpal/permissions.json</c> overlay have changed since the last build.
    /// Overlay rules (deny-only) are evaluated before config rules — see
    /// <see cref="PermissionPolicy.Compose"/> for why the order is load-bearing.
    /// </summary>
    private PermissionPolicy GetPolicy()
    {
        var configRules = _config.PermissionRules ?? string.Empty;
        var overlayPath = OverlayPath();
        var overlayStamp = OverlayStamp(overlayPath);

        lock (_policyLock)
        {
            if (configRules == _cachedConfigRules
                && overlayPath == _cachedOverlayPath
                && overlayStamp == _cachedOverlayStamp)
                return _policy;

            IReadOnlyList<PermissionRule> overlayRules = [];
            if (overlayPath is not null && overlayStamp != default)
            {
                try { overlayRules = PermissionPolicy.ParseJsonOverlay(File.ReadAllText(overlayPath)); }
                catch (Exception ex) { Diagnostics.Swallow("PermissionOverlayRead", ex); }
            }

            _policy             = new PermissionPolicy(
                PermissionPolicy.Compose(PermissionPolicy.ParseRules(configRules), overlayRules));
            _cachedConfigRules  = configRules;
            _cachedOverlayPath  = overlayPath;
            _cachedOverlayStamp = overlayStamp;
            return _policy;
        }
    }

    private string? OverlayPath()
    {
        var root = _rootDir();
        return string.IsNullOrEmpty(root) ? null : Path.Combine(root, ".inferpal", "permissions.json");
    }

    // File's last-write time, or default(DateTime) when absent — also the cache key, so deleting
    // the overlay invalidates the cached policy. A stat failure is traced: treating the overlay
    // as absent silently drops workspace deny rules, which must at least show up in /diagnostics.
    private static DateTime OverlayStamp(string? path)
    {
        try { return path is not null && File.Exists(path) ? File.GetLastWriteTimeUtc(path) : default; }
        catch (Exception ex) { Diagnostics.Swallow("PermissionOverlayStamp", ex); return default; }
    }
}
