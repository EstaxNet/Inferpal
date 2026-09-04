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
    /// `settings/schema` — the declarative description of the settings form (tabs, sections,
    /// fields, control kinds, label resource names) straight from the Core. The webview renders
    /// whatever this returns, so adding a setting no longer means editing a TypeScript table too.
    /// Labels are resource *names*, resolved by the adapter against `settings/strings`.
    /// </summary>
    [JsonRpcMethod("settings/schema")]
    public SettingsSchemaDto SettingsSchemaModel() => new(
        SettingsSchema.Tabs.Select(t => new SettingsTabDto(
            t.Key,
            t.Title,
            t.Sections.Select(sec => new SettingsSectionDto(
                sec.Title,
                sec.Fields.Select(ToFieldDto).ToList(),
                sec.ToggleGate, sec.ToggleLabel, sec.ToggleHint)).ToList())).ToList(),
        SettingsSchema.HeaderFields.Select(ToFieldDto).ToList());

    private static SettingsFieldDto ToFieldDto(SettingField f) => new(
        f.Key,
        f.Kind.ToString().ToLowerInvariant(),
        f.Label,
        f.Hint,
        f.Unit,
        f.Gate,
        f.Button,
        f.Options?.Select(o => new SettingsOptionDto(o.Value, o.Text)).ToList());

    [JsonRpcMethod("settings/strings")]
    public Dictionary<string, string> SettingsStrings() => new()
    {
        // ── Save status ──────────────────────────────────────────────────────
        // ⚠ Served here, and not from the adapter's own strings, so that the sentence naming the
        // ignored fields is THE SAME as the Visual Studio window's: it quotes labels that already
        // come from here. Two translations of one sentence would drift.
        // The panel substitutes {0}=count and {1}=labels.
        [nameof(Strings.SettingsFieldsIgnored)]   = Strings.SettingsFieldsIgnoredTemplate,

        // ── Sections ─────────────────────────────────────────────────────────
        [nameof(Strings.SectionConnection)]        = Strings.SectionConnection,
        [nameof(Strings.SectionBehavior)]          = Strings.SectionBehavior,
        [nameof(Strings.SectionInlineCompletions)] = Strings.SectionInlineCompletions,
        [nameof(Strings.SectionPersona)]           = Strings.SectionPersona,
        [nameof(Strings.SectionRag)]               = Strings.SectionRag,
        [nameof(Strings.SectionContext)]           = Strings.SectionContext,
        [nameof(Strings.SectionMcp)]               = Strings.SectionMcp,
        [nameof(Strings.SectionCommandsTools)]     = Strings.SectionCommandsTools,

        // ── Interface / connection ───────────────────────────────────────────
        [nameof(Strings.LabelLanguage)]            = Strings.LabelLanguage,
        [nameof(Strings.HintLanguage)]             = Strings.HintLanguage,
        [nameof(Strings.LabelProvider)]            = Strings.LabelProvider,
        [nameof(Strings.HintProvider)]             = Strings.HintProvider,
        [nameof(Strings.LabelUrl)]                 = Strings.LabelUrl,
        [nameof(Strings.HintUrl)]                  = Strings.HintUrl,
        [nameof(Strings.BtnTest)]                  = Strings.BtnTest,
        [nameof(Strings.LabelApiKey)]              = Strings.LabelApiKey,
        [nameof(Strings.HintApiKey)]               = Strings.HintApiKey,
        [nameof(Strings.LabelChatModel)]           = Strings.LabelChatModel,
        [nameof(Strings.HintChatModel)]            = Strings.HintChatModel,
        [nameof(Strings.LabelModelRolesAdvanced)]  = Strings.LabelModelRolesAdvanced,
        [nameof(Strings.HintModelRolesAdvanced)]   = Strings.HintModelRolesAdvanced,
        [nameof(Strings.LabelAgentModel)]          = Strings.LabelAgentModel,
        [nameof(Strings.HintAgentModel)]           = Strings.HintAgentModel,
        [nameof(Strings.LabelCodeActionsModel)]    = Strings.LabelCodeActionsModel,
        [nameof(Strings.HintCodeActionsModel)]     = Strings.HintCodeActionsModel,
        [nameof(Strings.LabelInlineCompletionModel)] = Strings.LabelInlineCompletionModel,
        [nameof(Strings.HintInlineCompletionModel)]  = Strings.HintInlineCompletionModel,
        [nameof(Strings.LabelInlineEditModel)]     = Strings.LabelInlineEditModel,
        [nameof(Strings.HintInlineEditModel)]      = Strings.HintInlineEditModel,
        [nameof(Strings.LabelUtilityModel)]        = Strings.LabelUtilityModel,
        [nameof(Strings.HintUtilityModel)]         = Strings.HintUtilityModel,
        [nameof(Strings.LabelModelRouterAuto)]     = Strings.LabelModelRouterAuto,
        [nameof(Strings.HintModelRouterAuto)]      = Strings.HintModelRouterAuto,
        [nameof(Strings.LabelRagEmbeddingModel)]   = Strings.LabelRagEmbeddingModel,
        [nameof(Strings.HintRagEmbeddingModel)]    = Strings.HintRagEmbeddingModel,

        // ── Behavior ─────────────────────────────────────────────────────────
        [nameof(Strings.LabelAdvancedBehavior)]    = Strings.LabelAdvancedBehavior,
        [nameof(Strings.LabelCommandTimeout)]      = Strings.LabelCommandTimeout,
        [nameof(Strings.HintCommandTimeout)]       = Strings.HintCommandTimeout,
        [nameof(Strings.LabelTaskTimeoutQuick)]    = Strings.LabelTaskTimeoutQuick,
        [nameof(Strings.HintTaskTimeoutQuick)]     = Strings.HintTaskTimeoutQuick,
        [nameof(Strings.LabelTaskTimeoutNormal)]   = Strings.LabelTaskTimeoutNormal,
        [nameof(Strings.HintTaskTimeoutNormal)]    = Strings.HintTaskTimeoutNormal,
        [nameof(Strings.LabelTaskTimeoutDeep)]     = Strings.LabelTaskTimeoutDeep,
        [nameof(Strings.HintTaskTimeoutDeep)]      = Strings.HintTaskTimeoutDeep,
        [nameof(Strings.LabelAgentMaxIterations)]  = Strings.LabelAgentMaxIterations,
        [nameof(Strings.HintAgentMaxIterations)]   = Strings.HintAgentMaxIterations,
        [nameof(Strings.LabelModelAutoUnload)]     = Strings.LabelModelAutoUnload,
        [nameof(Strings.HintModelAutoUnload)]      = Strings.HintModelAutoUnload,
        [nameof(Strings.LabelModelIdleTimeout)]    = Strings.LabelModelIdleTimeout,
        [nameof(Strings.HintModelIdleTimeout)]     = Strings.HintModelIdleTimeout,
        [nameof(Strings.LabelToolBubblesExpanded)] = Strings.LabelToolBubblesExpanded,
        [nameof(Strings.HintToolBubblesExpanded)]  = Strings.HintToolBubblesExpanded,
        [nameof(Strings.LabelSecurityAlertsDisabled)] = Strings.LabelSecurityAlertsDisabled,
        [nameof(Strings.HintSecurityAlertsDisabled)]  = Strings.HintSecurityAlertsDisabled,
        [nameof(Strings.LabelPermissionRules)]     = Strings.LabelPermissionRules,
        [nameof(Strings.HintPermissionRules)]      = Strings.HintPermissionRules,
        [nameof(Strings.LabelSmartFixEnabled)]     = Strings.LabelSmartFixEnabled,
        [nameof(Strings.HintSmartFixEnabled)]      = Strings.HintSmartFixEnabled,
        [nameof(Strings.LabelAgentModeEnabled)]    = Strings.LabelAgentModeEnabled,
        [nameof(Strings.HintAgentModeEnabled)]     = Strings.HintAgentModeEnabled,
        [nameof(Strings.LabelInlineCompletionEnabled)] = Strings.LabelInlineCompletionEnabled,
        [nameof(Strings.HintInlineCompletionEnabled)]  = Strings.HintInlineCompletionEnabled,
        [nameof(Strings.LabelInlineCompletionMode)]    = Strings.LabelInlineCompletionMode,
        [nameof(Strings.HintInlineCompletionMode)]     = Strings.HintInlineCompletionMode,
        [nameof(Strings.LabelPersonaAutoSwitch)]   = Strings.LabelPersonaAutoSwitch,
        [nameof(Strings.HintPersonaAutoSwitch)]    = Strings.HintPersonaAutoSwitch,
        [nameof(Strings.LabelCustomSystemPrompt)]  = Strings.LabelCustomSystemPrompt,
        [nameof(Strings.HintCustomSystemPrompt)]   = Strings.HintCustomSystemPrompt,

        // ── Context (RAG + memory) ───────────────────────────────────────────
        [nameof(Strings.LabelRagEnabled)]          = Strings.LabelRagEnabled,
        [nameof(Strings.HintRagEnabled)]           = Strings.HintRagEnabled,
        [nameof(Strings.LabelRagAutoContext)]      = Strings.LabelRagAutoContext,
        [nameof(Strings.HintRagAutoContext)]       = Strings.HintRagAutoContext,
        [nameof(Strings.LabelRagTopK)]             = Strings.LabelRagTopK,
        [nameof(Strings.HintRagTopK)]              = Strings.HintRagTopK,
        [nameof(Strings.LabelRagSimilarityThreshold)] = Strings.LabelRagSimilarityThreshold,
        [nameof(Strings.HintRagSimilarityThreshold)]  = Strings.HintRagSimilarityThreshold,
        [nameof(Strings.LabelLspEnabled)]          = Strings.LabelLspEnabled,
        [nameof(Strings.HintLspEnabled)]           = Strings.HintLspEnabled,
        [nameof(Strings.LabelVramBudget)]          = Strings.LabelVramBudget,
        [nameof(Strings.HintVramBudget)]           = Strings.HintVramBudget,
        [nameof(Strings.LabelContextWindowSize)]   = Strings.LabelContextWindowSize,
        [nameof(Strings.HintContextWindowSize)]    = Strings.HintContextWindowSize,
        [nameof(Strings.LabelContextWindowKeepTurns)] = Strings.LabelContextWindowKeepTurns,
        [nameof(Strings.HintContextWindowKeepTurns)]  = Strings.HintContextWindowKeepTurns,
        [nameof(Strings.LabelCompactionEnabled)]   = Strings.LabelCompactionEnabled,
        [nameof(Strings.HintCompactionEnabled)]    = Strings.HintCompactionEnabled,
        [nameof(Strings.LabelCompactionTimeout)]   = Strings.LabelCompactionTimeout,
        [nameof(Strings.HintCompactionTimeout)]    = Strings.HintCompactionTimeout,
        [nameof(Strings.LabelKvCacheAnchor)]       = Strings.LabelKvCacheAnchor,
        [nameof(Strings.HintKvCacheAnchor)]        = Strings.HintKvCacheAnchor,
        [nameof(Strings.LabelOodaTurnThreshold)]   = Strings.LabelOodaTurnThreshold,
        [nameof(Strings.HintOodaTurnThreshold)]    = Strings.HintOodaTurnThreshold,
        [nameof(Strings.LabelPinnedContextFiles)]  = Strings.LabelPinnedContextFiles,
        [nameof(Strings.HintPinnedContextFiles)]   = Strings.HintPinnedContextFiles,

        // ── Tools ────────────────────────────────────────────────────────────
        [nameof(Strings.LabelMcpEnabled)]          = Strings.LabelMcpEnabled,
        [nameof(Strings.HintMcpEnabled)]           = Strings.HintMcpEnabled,
        [nameof(Strings.LabelMcpServers)]          = Strings.LabelMcpServers,
        [nameof(Strings.HintMcpServers)]           = Strings.HintMcpServers,
        [nameof(Strings.LabelPromptTemplates)]     = Strings.LabelPromptTemplates,
        [nameof(Strings.HintPromptTemplates)]      = Strings.HintPromptTemplates,
        [nameof(Strings.LabelCustomTools)]         = Strings.LabelCustomTools,
        [nameof(Strings.HintCustomTools)]          = Strings.HintCustomTools,
    };
}
