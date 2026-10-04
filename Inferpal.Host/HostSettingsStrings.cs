using Inferpal.Localization;
using Inferpal.Services.Presentation;
using StreamJsonRpc;

namespace Inferpal.Host;

/// <summary>
/// `settings/strings` — the localized labels, hints and section titles of the settings UI,
/// straight from the same .resx resources as the Visual Studio settings window. The VS Code
/// settings webview renders them verbatim, so both editors share the exact same wording in
/// all 10 languages with zero duplication. Keys are the resx resource names.
/// </summary>
internal sealed partial class HostServer
{
    /// <summary>
    /// `settings/schema` — the declarative description of the settings form (pages, sections,
    /// fields, control kinds, label resource names) straight from the Core. The webview renders
    /// whatever this returns, so adding a setting no longer means editing a TypeScript table too.
    /// Labels are resource *names*, resolved by the adapter against `settings/strings`.
    /// </summary>
    [JsonRpcMethod("settings/schema")]
    public SettingsSchemaDto SettingsSchemaModel() => new(
        SettingsSchema.Tabs.Select(t => new SettingsTabDto(
            t.Key,
            t.Title,
            t.Description,
            t.Sections.Select(sec => new SettingsSectionDto(
                sec.Title,
                sec.Fields.Select(ToFieldDto).ToList(),
                sec.Description, sec.Gate, sec.Collapsible, sec.Note, sec.Grid, sec.Widget)).ToList(),
            t.AdvancedToggle)).ToList());

    private static SettingsFieldDto ToFieldDto(SettingField f) => new(
        f.Key,
        f.Kind.ToString().ToLowerInvariant(),
        f.Label,
        f.Hint,
        f.Unit,
        f.Button,
        // .Display, not .Text: prose options (the three FIM modes) resolve HERE, at request time,
        // in the language in force - the schema itself is built once.
        f.Options?.Select(o => new SettingsOptionDto(o.Value, o.Display, o.Description is { Length: > 0 } d ? d : null)).ToList(),
        DefaultFor(f),
        f.Inverted,
        f.OpensFold,
        f.HintNotOllama,
        f.Editor,
        f.ZeroIsEmpty,
        f.EmptyChoice,
        f.Columns?.ToList());

    private static readonly Config.InferpalConfig _factoryDefaults = new();

    /// <summary>
    /// The factory value of a numeric box, or <c>null</c> for other fields.
    /// </summary>
    /// <remarks>
    /// Clearing a numeric box restores the default in the Visual Studio window
    /// (<see cref="Services.Presentation.SettingsFallback"/>: "empty" is the existing affordance for
    /// "restore the default") and did <b>nothing</b> in the VS Code panel, which did not know those
    /// defaults. The same gesture, two results - two implementations of one rule, what this
    /// repository calls a programmed divergence. The panel now receives them instead of guessing.
    /// </remarks>
    private static string? DefaultFor(SettingField f)
    {
        // Numeric boxes (clearing one restores it) and the fields that open the advanced fold (the
        // panel compares against it). Booleans travel as "true"/"false".
        if (f.Kind is not (SettingKind.Int or SettingKind.Float) && !f.OpensFold) return null;

        var property = typeof(Config.InferpalConfig).GetProperty(
            f.Key, System.Reflection.BindingFlags.Public
                 | System.Reflection.BindingFlags.Instance
                 | System.Reflection.BindingFlags.IgnoreCase);

        return property?.GetValue(_factoryDefaults) switch
        {
            bool flag    => flag ? "true" : "false",
            { } value    => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture),
            null         => null,
        };
    }

    /// <summary>
    /// Every name the schema displays, resolved in the language in force, plus the panel's own
    /// chrome. Read from the schema rather than listed here: a label added to a page is served the
    /// day it is declared — a second list is the one that forgets it.
    /// </summary>
    [JsonRpcMethod("settings/strings")]
    public Dictionary<string, string> SettingsStrings()
    {
        var strings = SettingsSchema.ResourceNames.ToDictionary(n => n, Strings.ByName, StringComparer.Ordinal);

        // ── Save status ──────────────────────────────────────────────────────
        // ⚠ Served here, and not from the adapter's own strings, so that the sentence naming the
        // ignored fields is THE SAME as the Visual Studio window's: it quotes labels that already
        // come from here. Two translations of one sentence would drift.
        // The panel substitutes {0}=count and {1}=labels.
        strings[nameof(Strings.SettingsFieldsIgnored)]          = Strings.SettingsFieldsIgnoredTemplate;
        strings[nameof(Strings.SettingsPermissionRulesIgnored)] = Strings.SettingsPermissionRulesIgnoredTemplate;

        // ── Panel chrome: navigation, footer, buttons ────────────────────────
        strings[nameof(Strings.SettingsSearchPlaceholder)]  = Strings.SettingsSearchPlaceholder;
        strings[nameof(Strings.SettingsSearchNoMatch)]      = Strings.SettingsSearchNoMatch;
        strings[nameof(Strings.SettingsUnsavedChanges)]     = Strings.SettingsUnsavedChangesTemplate;
        strings[nameof(Strings.SettingsNoUnsavedChanges)]   = Strings.SettingsNoUnsavedChanges;
        strings[nameof(Strings.SettingsCancel)]             = Strings.SettingsCancel;
        strings[nameof(Strings.SettingsApprovalRulesCount)] = Strings.SettingsApprovalRulesCountTemplate;
        strings[nameof(Strings.SettingsEditRules)]          = Strings.SettingsEditRules;
        strings[nameof(Strings.BtnTest)]                    = Strings.BtnTest;
        strings[nameof(Strings.BtnSave)]                    = Strings.BtnSave;
        // "Automatic" names the language it resolves to: the editor's, or English when it is not one of ours.
        strings[nameof(Strings.LangAuto)] = Strings.LangAuto(SettingsSchema.AutoLanguageName(
            _editorLocale is { } locale ? System.Globalization.CultureInfo.GetCultureInfo(locale)
                                        : System.Globalization.CultureInfo.CurrentUICulture));
        strings[nameof(Strings.SettingsApprovalRulesFrom)] = Strings.ByName(nameof(Strings.SettingsApprovalRulesFrom));

        // ── Structured editors of the list settings (cards, table, lists) ────
        // Templates ({0}) are served raw: the panel fills them.
        foreach (var name in EditorStrings)
            strings[name] = Strings.ByName(name);
        return strings;
    }

    /// <summary>The resources the structured editors of <c>webview/settingsEditors.ts</c> display.</summary>
    /// <remarks>A name the panel asks for and this list does not serve shows as a raw key — held by
    /// <c>SettingsStrings_ServeEveryNameThePanelAsksFor</c>, which reads the panel's sources.</remarks>
    private static readonly string[] EditorStrings =
    [
        "SettingsEditAsJson", "SettingsEditAsText", "SettingsEditAsList",
        "HintRowEdit", "HintRowDelete", "RowEditTitle", "ListLineNotRead",
        // MCP server cards and their form
        "McpCardRetry", "McpCardSignInButton", "McpCardOff", "McpCardNotStarted", "McpEmptyTitle",
        "McpAddServer", "McpAddTitle", "McpEditTitle", "LabelMcpName", "LabelMcpCommand", "LabelMcpArgs",
        "LabelMcpEnv", "LabelMcpHttpServer", "LabelMcpUrl", "LabelMcpHeaders", "BtnMcpSaveServer",
        "BtnMcpCancelServer", "McpValidationNameCommand", "McpValidationNameUrl", "McpValidationDuplicate",
        "McpJsonNotEditableAsList", "HintMcpEditServer", "HintMcpDeleteServer",
        // Approval rules table
        "RulesColEffect", "RulesColTool", "RulesColPattern", "RulesColFrom", "RuleAllow", "RuleDeny",
        "RulesAddRule", "RulesTeamUnusable", "RulesEmpty",
        "RulesPatternPlaceholder", "RulesNewInvalid",
        // Slash commands and agent tools
        "SlashAddCmd", "SlashAddTitle", "LabelSlashName", "LabelSlashText", "SlashEmptyTitle",
        "SlashValidationNameText", "SlashValidationDuplicate",
        "ToolAddTool", "ToolAddTitle", "LabelToolName", "LabelToolCommand", "ToolEmptyTitle",
        "ToolValidationNameCommand", "ToolValidationDuplicate",
        // Pinned files
        "PinnedAddFile", "PinnedAddTitle", "LabelPinnedPath", "PinnedBrowse", "PinnedValidationPath",
        "PinnedValidationDuplicate", "PinnedEmptyTitle", "PinnedOverCap", "PinnedFilesCount",
        // Live blocks of the pages (the facts come localized from their own RPCs; these are the panel's words)
        "SettingsModelUsed", "SettingsChangeInServer", "SettingsEditFile", "SettingsOpenFile",
        "IndexCardShowThem", "IndexCardHideThem", "IndexExclusionsTitle", "IndexExclusionsFrom", "IndexExclusionsHowTo",
        "DocsReindex", "DocsAddSite", "DocsAddUrlLabel", "DocsAddButton", "DocsNoSitesYet",
        "ContextUsageOpenXray", "ContextUsageChangeWindow", "ProjectFilesTitle", "ProjectFileNotYet",
        // The theme cards of the Appearance page
        "ThemeLight", "ThemeDark", "ThemeHighContrast", "ThemeInUse",
    ];
}
