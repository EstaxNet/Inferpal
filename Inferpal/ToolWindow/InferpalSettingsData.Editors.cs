using System.Collections.ObjectModel;
using System.Runtime.Serialization;
using Inferpal.Localization;
using Inferpal.Services.Commands;
using Inferpal.Services.Presentation;
using Microsoft.VisualStudio.Extensibility.UI;

namespace Inferpal.ToolWindow;

/// <summary>
/// The structured editors of the Tools page that the old window did not have: the approval rules table (every
/// rule written, team file and this machine, in evaluation order) and the texts of the MCP server cards. Built
/// by the same Core presenters as the VS Code panel, so both editors say the same thing.
/// </summary>
internal partial class InferpalSettingsData
{
    /// <summary>The workspace root, for the team file <c>.inferpal/permissions.json</c>; null without a workspace.</summary>
    private Func<string?> _workspaceRoot = () => null;

    /// <summary>The team file as read when the window opened (or was cancelled): the table is rebuilt on every
    /// keystroke in the rules text, the file is not re-read for each.</summary>
    private string? _teamRules;

    private bool   _rulesTeamUnusable, _rulesEmpty = true, _rulesTableVisible, _rulesTextExpanded, _newRuleIsAllow;
    private string _rulesViewLabel = "", _newRuleTool = "", _newRulePattern = "", _newRuleError = "";
    private string _labelRulesColEffect = "", _labelRulesColTool = "", _labelRulesColPattern = "", _labelRulesColFrom = "",
                   _rulesTeamUnusableText = "", _rulesEmptyText = "",
                   _labelRulesAdd = "", _labelRuleAllow = "", _labelRuleDeny = "",
                   _labelMcpRetry = "", _labelMcpSignIn = "";

    [DataMember] public ObservableCollection<ApprovalRuleItem> ApprovalRules { get; } = [];
    [DataMember] public bool   RulesTeamUnusable   { get => _rulesTeamUnusable;   set => SetProperty(ref _rulesTeamUnusable,   value); }
    [DataMember] public bool   RulesEmpty          { get => _rulesEmpty;          set => SetProperty(ref _rulesEmpty,          value); }
    [DataMember] public bool   RulesTableVisible   { get => _rulesTableVisible;   set => SetProperty(ref _rulesTableVisible,   value); }
    /// <summary>The raw text of the rules shown instead of the table ("Edit as text").</summary>
    [DataMember] public bool   RulesTextExpanded   { get => _rulesTextExpanded;   set => SetProperty(ref _rulesTextExpanded,   value); }
    [DataMember] public string RulesViewLabel      { get => _rulesViewLabel;      set => SetProperty(ref _rulesViewLabel,      value); }
    [DataMember] public bool   NewRuleIsAllow      { get => _newRuleIsAllow;      set => SetProperty(ref _newRuleIsAllow,      value); }
    [DataMember] public string NewRuleTool         { get => _newRuleTool;         set => SetProperty(ref _newRuleTool,         value); }
    [DataMember] public string NewRulePattern      { get => _newRulePattern;      set => SetProperty(ref _newRulePattern,      value); }
    [DataMember] public string NewRuleError        { get => _newRuleError;        set => SetProperty(ref _newRuleError,        value); }

    [DataMember] public string LabelRulesColEffect  { get => _labelRulesColEffect;  set => SetProperty(ref _labelRulesColEffect,  value); }
    [DataMember] public string LabelRulesColTool    { get => _labelRulesColTool;    set => SetProperty(ref _labelRulesColTool,    value); }
    [DataMember] public string LabelRulesColPattern { get => _labelRulesColPattern; set => SetProperty(ref _labelRulesColPattern, value); }
    [DataMember] public string LabelRulesColFrom    { get => _labelRulesColFrom;    set => SetProperty(ref _labelRulesColFrom,    value); }
    [DataMember] public string RulesTeamUnusableText { get => _rulesTeamUnusableText; set => SetProperty(ref _rulesTeamUnusableText, value); }
    [DataMember] public string RulesEmptyText       { get => _rulesEmptyText;       set => SetProperty(ref _rulesEmptyText,       value); }
    [DataMember] public string LabelRulesAdd        { get => _labelRulesAdd;        set => SetProperty(ref _labelRulesAdd,        value); }
    [DataMember] public string LabelRuleAllow       { get => _labelRuleAllow;       set => SetProperty(ref _labelRuleAllow,       value); }
    [DataMember] public string LabelRuleDeny        { get => _labelRuleDeny;        set => SetProperty(ref _labelRuleDeny,        value); }
    [DataMember] public string LabelMcpRetry        { get => _labelMcpRetry;        set => SetProperty(ref _labelMcpRetry,        value); }
    [DataMember] public string LabelMcpSignIn       { get => _labelMcpSignIn;       set => SetProperty(ref _labelMcpSignIn,       value); }

    [DataMember] public AsyncCommand ToggleRulesTextCommand { get; private set; } = null!;
    [DataMember] public AsyncCommand SetRuleAllowCommand    { get; private set; } = null!;
    [DataMember] public AsyncCommand SetRuleDenyCommand     { get; private set; } = null!;
    [DataMember] public AsyncCommand AddRuleCommand         { get; private set; } = null!;

    /// <summary>Called from the constructor: the root, the team file, the commands of the editors.</summary>
    private void InitEditors(Func<string?>? workspaceRoot)
    {
        _workspaceRoot = workspaceRoot ?? (() => null);
        _teamRules     = ReadTeamRules();
        ToggleRulesTextCommand = new AsyncCommand((_, _) =>
        {
            RulesTextExpanded = !RulesTextExpanded;
            RulesViewLabel = RulesTextExpanded ? Strings.SettingsEditAsList : Strings.SettingsEditAsText;
            return Task.CompletedTask;
        });
        SetRuleAllowCommand = new AsyncCommand((_, _) => { NewRuleIsAllow = true;  return Task.CompletedTask; });
        SetRuleDenyCommand  = new AsyncCommand((_, _) => { NewRuleIsAllow = false; return Task.CompletedTask; });
        AddRuleCommand      = new AsyncCommand((_, _) => { AddRule(); return Task.CompletedTask; });
    }

    /// <summary>The texts of the editors, called from <see cref="ApplyPageLabels"/>.</summary>
    private void ApplyEditorLabels()
    {
        LabelRulesColEffect   = Strings.RulesColEffect;
        LabelRulesColTool     = Strings.RulesColTool;
        LabelRulesColPattern  = Strings.RulesColPattern;
        LabelRulesColFrom     = Strings.RulesColFrom;
        RulesTeamUnusableText = Strings.RulesTeamUnusable;
        RulesEmptyText        = Strings.RulesEmpty;
        LabelRulesAdd         = Strings.RulesAddRule;
        LabelRuleAllow        = Strings.RuleAllow;
        LabelRuleDeny         = Strings.RuleDeny;
        LabelMcpRetry         = Strings.McpCardRetry;
        LabelMcpSignIn        = Strings.McpCardSignInButton;
        RulesViewLabel        = RulesTextExpanded ? Strings.SettingsEditAsList : Strings.SettingsEditAsText;
        // The rows carry translated texts (Allow, This machine…): rebuilt in the new language.
        RebuildRulesTable();
    }

    /// <summary>The team file, read as <c>/permissions</c> reads it.</summary>
    private string? ReadTeamRules() => PermissionsCommandHandler.ReadOverlayFile(_workspaceRoot());

    /// <summary>Rebuilds the table from the rules text the form holds — unsaved edits included.</summary>
    private void RebuildRulesTable()
    {
        var table = ApprovalRuleTable.Build(_teamRules, PermissionRules);
        ApprovalRules.Clear();
        foreach (var row in table.Rows)
            ApprovalRules.Add(new ApprovalRuleItem(row, DeleteRuleLine));
        RulesTeamUnusable = table.TeamUnusable;
        var inForce = table.Rows.Where(r => r.Status == RuleStatus.InForce).ToList();
        var team    = inForce.Count(r => r.Source == RuleSource.Team);
        ApprovalRulesSummary = Strings.SettingsApprovalRulesCount(inForce.Count);
        ApprovalRulesFrom    = Strings.SettingsApprovalRulesFrom(inForce.Count - team, team);
        RulesEmpty        = table.Rows.Count == 0;
        RulesTableVisible = table.Rows.Count > 0;
    }

    /// <summary>Appends the new rule to the machine rules — only if it would be read: a rule that is ignored is a
    /// restriction believed set. Saved with the rest of the form.</summary>
    private void AddRule()
    {
        var line = ApprovalRuleTable.NewRuleLine(NewRuleIsAllow, NewRuleTool, NewRulePattern);
        if (line is null)
        {
            NewRuleError = Strings.RulesNewInvalid;
            return;
        }
        NewRuleError   = string.Empty;
        var text       = (PermissionRules ?? string.Empty).TrimEnd();
        PermissionRules = text.Length == 0 ? line : text + "\n" + line;
        NewRuleTool    = string.Empty;
        NewRulePattern = string.Empty;
    }

    // ── Lines a name=value list cannot read ───────────────────────────────────
    // Kept as written (EditableListText) — and shown: hidden, they read as entries that vanished.
    private string _slashUnreadText = "", _toolUnreadText = "";
    private bool   _hasSlashUnread, _hasToolUnread;

    [DataMember] public string SlashUnreadText { get => _slashUnreadText; set { if (SetProperty(ref _slashUnreadText, value)) HasSlashUnread = value.Length > 0; } }
    [DataMember] public bool   HasSlashUnread  { get => _hasSlashUnread;  private set => SetProperty(ref _hasSlashUnread, value); }
    [DataMember] public string ToolUnreadText  { get => _toolUnreadText;  set { if (SetProperty(ref _toolUnreadText, value)) HasToolUnread = value.Length > 0; } }
    [DataMember] public bool   HasToolUnread   { get => _hasToolUnread;   private set => SetProperty(ref _hasToolUnread, value); }

    /// <summary>The unreadable lines under their note, or empty when every line is an entry.</summary>
    private static string UnreadLines(IReadOnlyList<string> lines) =>
        lines.Count == 0 ? string.Empty : Strings.ListLineNotRead + "\n" + string.Join("\n", lines);

    private void DeleteRuleLine(int index)
    {
        var lines = (PermissionRules ?? string.Empty).Split('\n').ToList();
        if (index < 0 || index >= lines.Count) return;
        lines.RemoveAt(index);
        PermissionRules = string.Join("\n", lines);
    }
}
