using Inferpal.Localization;
using Inferpal.Services.Execution;

namespace Inferpal.Services.Presentation;

/// <summary>Where an approval rule is written.</summary>
internal enum RuleSource
{
    /// <summary><c>.inferpal/permissions.json</c>: ships with the repository, deny-only.</summary>
    Team,
    /// <summary>The per-machine <c>permissionRules</c> setting.</summary>
    Machine,
}

/// <summary>Whether a written rule takes part in the decision.</summary>
internal enum RuleStatus
{
    InForce,
    /// <summary>An <c>allow</c> in the team file: set aside by the overlay's contract, not a defect.</summary>
    IgnoredAllow,
    /// <summary>A malformed line or an invalid pattern: a restriction the user believes is set.</summary>
    Unreadable,
}

/// <summary>One row of the approval rules table.</summary>
/// <param name="Allow"><c>true</c> allow, <c>false</c> deny, <c>null</c> when the line could not be read.</param>
/// <param name="MachineLine">Index of the line in the machine setting (split on <c>\n</c>), for deletion;
/// <c>-1</c> for a team rule, which the settings window does not edit.</param>
internal sealed record ApprovalRuleRow(
    RuleSource Source, bool? Allow, string Tool, string Pattern, RuleStatus Status,
    string EffectText, string FromText, string? NoteText, int MachineLine);

/// <param name="TeamUnusable">The team file exists and none of it applies (invalid JSON, no <c>rules</c>).</param>
internal sealed record ApprovalRuleTableModel(IReadOnlyList<ApprovalRuleRow> Rows, bool TeamUnusable);

/// <summary>
/// The approval rules as the settings window shows them: every rule WRITTEN, team file and machine
/// setting, in the order they are evaluated (<see cref="PermissionPolicy.Compose"/>: team first, first
/// match wins), each saying whether it takes part.
/// </summary>
/// <remarks>
/// ⚠ A table of the rules in force only would hide the two cases the user needs to see: an <c>allow</c>
/// written in the team file (never applied, by contract) and a line that does not parse (a restriction
/// believed set). Both stay in the table, named. Records nothing to <c>/diagnostics</c>: the table is
/// redrawn on every edit, and the reading that records is <c>/permissions</c>'s.
/// </remarks>
internal static class ApprovalRuleTable
{
    public static ApprovalRuleTableModel Build(string? overlayJson, string? machineRules)
    {
        var rows = new List<ApprovalRuleRow>();

        var team = PermissionPolicy.OverlayEntries(overlayJson, out var unusable, out _);
        foreach (var entry in team)
        {
            if (entry.Rule is { } rule)
                rows.Add(Row(RuleSource.Team, rule.Decision == PermissionDecision.Allow, rule.Tool, rule.Pattern.ToString(),
                             rule.Decision == PermissionDecision.Allow ? RuleStatus.IgnoredAllow : RuleStatus.InForce, -1));
            else if (!IsBlankOrComment(entry.Text) || !entry.IsString)
                rows.Add(Unreadable(RuleSource.Team, entry.Text, -1));
        }

        var lines = (machineRules ?? string.Empty).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (IsBlankOrComment(line)) continue;
            rows.Add(PermissionPolicy.ParseLine(line) is { } rule
                ? Row(RuleSource.Machine, rule.Decision == PermissionDecision.Allow, rule.Tool, rule.Pattern.ToString(),
                      RuleStatus.InForce, i)
                : Unreadable(RuleSource.Machine, line, i));
        }

        return new ApprovalRuleTableModel(rows, unusable);
    }

    /// <summary>The line the settings window appends for a new rule — the format <see cref="PermissionPolicy.ParseLine"/>
    /// reads. <c>null</c> when it would not parse, so a rule is never added that would be ignored.</summary>
    public static string? NewRuleLine(bool allow, string? tool, string? pattern)
    {
        var line = $"{(allow ? "allow" : "deny")} {(string.IsNullOrWhiteSpace(tool) ? "*" : tool.Trim())} {pattern?.Trim()}";
        return PermissionPolicy.ParseLine(line) is null ? null : line;
    }

    private static bool IsBlankOrComment(string line) =>
        string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#');

    private static ApprovalRuleRow Row(RuleSource source, bool allow, string tool, string pattern, RuleStatus status, int line) =>
        new(source, allow, tool, pattern, status,
            allow ? Strings.RuleAllow : Strings.RuleDeny,
            From(source),
            status == RuleStatus.IgnoredAllow ? Strings.RuleIgnoredTeamAllow : null,
            line);

    private static ApprovalRuleRow Unreadable(RuleSource source, string text, int line) =>
        new(source, null, string.Empty, text, RuleStatus.Unreadable,
            string.Empty, From(source), Strings.RuleUnreadable, line);

    private static string From(RuleSource source) =>
        source == RuleSource.Team ? Strings.RuleFromTeam : Strings.RuleFromMachine;
}
