using System.Text.Json;
using System.Text.RegularExpressions;

namespace Inferpal.Services.Execution;

/// <summary>
/// Thrown by the approval service when a tool call is blocked by a <c>deny</c> rule or the built-in
/// hard denylist (as opposed to being declined by the user at the prompt). Tools let it propagate;
/// <see cref="ToolRegistry.ExecuteAsync"/> turns it into a distinct tool-result string so the model
/// sees "blocked by policy" rather than the generic "cancelled by user" and stops retrying.
/// </summary>
internal sealed class PermissionDeniedException(string message) : Exception(message);

/// <summary>The outcome of evaluating a tool call against the permission rules.</summary>
internal enum PermissionDecision
{
    /// <summary>No rule matched — fall back to the interactive approval prompt (and session/YOLO logic).</summary>
    Prompt,
    /// <summary>An allow rule matched — auto-approve without prompting.</summary>
    Allow,
    /// <summary>A deny rule (or the built-in hard denylist) matched — never run, even under YOLO.</summary>
    Deny,
}

/// <summary>
/// A single user-defined permission rule: an allow/deny decision scoped to a tool name (or
/// <c>*</c> for any) and matched against the action's <em>subject</em> (a shell command, a file
/// path, a URL…) by a regular expression.
/// </summary>
internal sealed class PermissionRule
{
    public PermissionDecision Decision { get; }
    /// <summary>Tool this rule applies to, or <c>"*"</c> for any tool.</summary>
    public string Tool { get; }
    public Regex Pattern { get; }

    public PermissionRule(PermissionDecision decision, string tool, Regex pattern)
    {
        Decision = decision;
        Tool     = tool;
        Pattern  = pattern;
    }

    /// <summary>True when this rule applies to <paramref name="toolName"/> and matches <paramref name="subject"/>.</summary>
    public bool Matches(string toolName, string subject) =>
        (Tool == "*" || string.Equals(Tool, toolName, StringComparison.OrdinalIgnoreCase))
        && Pattern.IsMatch(subject);
}

/// <summary>
/// Pure, testable permission engine for tool approval. Classifies a tool call into
/// <see cref="PermissionDecision.Allow"/> / <see cref="PermissionDecision.Deny"/> /
/// <see cref="PermissionDecision.Prompt"/> from a list of user rules plus a built-in,
/// non-bypassable denylist of catastrophic shell commands.
/// </summary>
/// <remarks>
/// <para>
/// Rule DSL (one per line, used both by the per-machine config field and the workspace
/// <c>.inferpal/permissions.json</c> overlay):
/// </para>
/// <code>
/// allow run_command ^\s*(dotnet|git|npm|pnpm|yarn|cargo|go)\b   # auto-approve common dev commands
/// deny  run_command (Remove-Item|rm\s+-rf)                       # but always prompt-block these
/// allow write_file  \.(cs|ts|js|py)$                             # auto-approve edits to source files
/// deny  *           \.env$                                       # never touch secrets, any tool
/// # lines starting with '#' are comments
/// </code>
/// <para>
/// Evaluation: the built-in hard denylist wins first (cannot be overridden, not even by
/// <c>SecurityAlertsDisabled</c>), then user rules in order — <em>first match wins</em> —, then
/// <see cref="PermissionDecision.Prompt"/> when nothing matched. Subjects are matched
/// case-insensitively.
/// </para>
/// </remarks>
internal sealed class PermissionPolicy
{
    private readonly IReadOnlyList<PermissionRule> _rules;

    public PermissionPolicy(IReadOnlyList<PermissionRule> rules) => _rules = rules ?? [];

    /// <summary>An empty policy — every call falls through to <see cref="PermissionDecision.Prompt"/>.</summary>
    public static PermissionPolicy Empty { get; } = new([]);

    // Built-in, non-bypassable denylist of catastrophic / unrecoverable shell commands. Kept
    // deliberately narrow so it never blocks ordinary dev work (a recursive delete of bin/ or
    // node_modules does NOT match — only deletes targeting a drive root or the home directory do).
    // Users layer their own, looser deny rules on top via the DSL; these are the floor.
    private static readonly Regex[] HardDeny =
    [
        // rm -rf targeting filesystem root / home / wildcard root, or with --no-preserve-root
        new(@"\brm\s+-[a-zA-Z]*[rR][a-zA-Z]*[fF][a-zA-Z]*\s+(/|~|\$HOME|/\*|\.\s*$)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\brm\b[^|&;]*--no-preserve-root", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        // Remove-Item -Recurse -Force (any order) targeting a bare drive root (C:\, D:/ …)
        new(@"Remove-Item\b(?=[^|&;]*-Rec)(?=[^|&;]*-Force)[^|&;]*\s['""]?[A-Za-z]:[\\/]?['""]?(\s|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        // Disk / volume destruction
        new(@"\b(mkfs(\.\w+)?|Format-Volume|Clear-Disk|diskpart)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bformat\s+[A-Za-z]:", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bdd\b[^|&;]*\bif=", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        // Classic fork bomb
        new(@":\(\)\s*\{\s*:\s*\|\s*:", RegexOptions.Compiled),
    ];

    /// <summary>True when <paramref name="subject"/> hits the built-in non-bypassable denylist.</summary>
    public static bool IsHardDenied(string? subject) =>
        !string.IsNullOrEmpty(subject) && HardDeny.Any(r => r.IsMatch(subject));

    /// <summary>Classifies a tool call. See the type remarks for the evaluation order.</summary>
    public PermissionDecision Evaluate(string toolName, string? subject)
    {
        subject ??= string.Empty;
        if (IsHardDenied(subject)) return PermissionDecision.Deny;

        foreach (var rule in _rules)
            if (rule.Matches(toolName, subject))
                return rule.Decision;   // first match wins

        return PermissionDecision.Prompt;
    }

    // ── Parsing ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Parses a single DSL line (<c>allow|deny &lt;tool|*&gt; &lt;regex&gt;</c>). Returns
    /// <c>null</c> for blank lines, <c>#</c> comments, malformed lines, and invalid regexes
    /// (skipped rather than throwing, so one bad line never disables the whole ruleset).
    /// </summary>
    public static PermissionRule? ParseLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        line = line.Trim();
        if (line.StartsWith('#')) return null;

        // decision <space> tool <space> regex(rest of line)
        var first = line.IndexOf(' ');
        if (first <= 0) return null;
        var decisionTok = line[..first].ToLowerInvariant();
        var decision = decisionTok switch
        {
            "allow" => PermissionDecision.Allow,
            "deny"  => PermissionDecision.Deny,
            _       => (PermissionDecision?)null,
        };
        if (decision is null) return null;

        var rest = line[(first + 1)..].TrimStart();
        var second = rest.IndexOf(' ');
        if (second <= 0) return null;
        var tool    = rest[..second].Trim();
        var pattern = rest[(second + 1)..].Trim();
        if (string.IsNullOrEmpty(tool) || string.IsNullOrEmpty(pattern)) return null;

        try
        {
            var regex = new Regex(pattern, RegexOptions.IgnoreCase);
            return new PermissionRule(decision.Value, tool, regex);
        }
        catch (ArgumentException)
        {
            return null;   // invalid regex — skip this rule
        }
    }

    /// <summary>Parses newline-separated DSL text into rules, preserving order and skipping bad lines.</summary>
    public static IReadOnlyList<PermissionRule> ParseRules(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var rules = new List<PermissionRule>();
        foreach (var line in text.Split('\n'))
        {
            var rule = ParseLine(line);
            if (rule is not null) rules.Add(rule);
        }
        return rules;
    }

    /// <summary>
    /// Parses the workspace <c>.inferpal/permissions.json</c> overlay — a JSON object with a
    /// <c>"rules"</c> array of DSL strings: <c>{ "rules": ["allow run_command ^dotnet", "deny * \\.env$"] }</c>.
    /// Returns an empty list on missing/invalid JSON (never throws).
    /// </summary>
    public static IReadOnlyList<PermissionRule> ParseJsonOverlay(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("rules", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return [];
            var rules = new List<PermissionRule>();
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) continue;
                var rule = ParseLine(item.GetString());
                if (rule is not null) rules.Add(rule);
            }
            return rules;
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
