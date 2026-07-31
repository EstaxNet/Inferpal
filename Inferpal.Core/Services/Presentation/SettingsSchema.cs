namespace Inferpal.Services.Presentation;

/// <summary>What kind of control edits a setting.</summary>
internal enum SettingKind { Text, Password, Bool, Int, Float, Model, Select, TextArea }

/// <summary>An option of a <see cref="SettingKind.Select"/> field. Option texts are product names
/// or fixed technical labels — never localized.</summary>
internal sealed record SettingOption(string Value, string Text);

/// <summary>
/// One editable setting. <paramref name="Key"/> is the camelCase key in the config JSON,
/// <paramref name="Label"/>/<paramref name="Hint"/> are <b>resource names</b> resolved against the
/// shared .resx (so both editors show the same wording in all 10 languages).
/// </summary>
/// <param name="Unit">Literal suffix after a compact numeric field (<c>s</c>, <c>GB</c>, …).</param>
/// <param name="Gate">Reveal group: <c>roles</c> (distinct model per role) or <c>advanced</c>.</param>
/// <param name="Button">Companion button: <c>test</c> or <c>refreshModels</c>.</param>
internal sealed record SettingField(
    string  Key,
    SettingKind Kind,
    string  Label,
    string? Hint    = null,
    string? Unit    = null,
    string? Gate    = null,
    string? Button  = null,
    IReadOnlyList<SettingOption>? Options = null);

/// <summary>A group of fields under a title, with an optional reveal toggle.</summary>
internal sealed record SettingSection(
    string Title,
    IReadOnlyList<SettingField> Fields,
    string? ToggleGate  = null,
    string? ToggleLabel = null,
    string? ToggleHint  = null);

/// <summary>One tab of the settings window.</summary>
internal sealed record SettingTab(string Key, string Title, IReadOnlyList<SettingSection> Sections);

/// <summary>
/// The declarative description of the settings surface — which keys are editable, in which tab and
/// section, with which control and which label resource.
/// </summary>
/// <remarks>
/// It lives in the Core so the two front-ends stop describing the same form twice: the labels were
/// already shared (served from the .resx), the field list was not, and that gap is what let eight
/// settings strings ship untranslated. The VS Code panel renders whatever this declares; the
/// Visual Studio window still binds its own properties (Remote UI needs primitives), but it now
/// has a single source of truth to be checked against.
/// </remarks>
internal static class SettingsSchema
{
    private static readonly SettingOption[] FimModes =
    [
        new("Fast",         "Fast (128 tok · 300 ms)"),
        new("Default",      "Default (256 tok · 600 ms)"),
        new("HighAccuracy", "High Accuracy (512 tok · 1 s)"),
    ];

    private static readonly SettingOption[] Providers =
    [
        new("ollama",            "Ollama"),
        new("lmstudio",          "LM Studio"),
        new("openai-compatible", "OpenAI-compatible"),
    ];

    /// <summary>Label the adapter resolves itself (not a .resx resource).</summary>
    public const string LocalLabelInlineDiff = "__inlineDiffPreview";
    /// <inheritdoc cref="LocalLabelInlineDiff"/>
    public const string LocalLabelTabTools   = "__tabTools";

    public static IReadOnlyList<SettingTab> Tabs { get; } =
    [
        new("connection", "SectionConnection",
        [
            new("SectionConnection",
            [
                new("provider",     SettingKind.Select, "LabelProvider",   "HintProvider",   Options: Providers),
                new("baseUrl",      SettingKind.Text,   "LabelUrl",        "HintUrl",        Button: "test"),
                new("apiKey",       SettingKind.Password, "LabelApiKey",   "HintApiKey"),
                new("defaultModel", SettingKind.Model,  "LabelChatModel",  "HintChatModel",  Button: "refreshModels"),
            ]),
            new("",
            [
                new("agentModel",             SettingKind.Model, "LabelAgentModel",             "HintAgentModel",             Gate: "roles"),
                new("codeActionsModel",       SettingKind.Model, "LabelCodeActionsModel",       "HintCodeActionsModel",       Gate: "roles"),
                new("inlineCompletionModel",  SettingKind.Model, "LabelInlineCompletionModel",  "HintInlineCompletionModel",  Gate: "roles"),
                new("inlineEditModel",        SettingKind.Model, "LabelInlineEditModel",        "HintInlineEditModel",        Gate: "roles"),
                new("utilityModel",           SettingKind.Model, "LabelUtilityModel",           "HintUtilityModel",           Gate: "roles"),
                new("modelRouterAuto",        SettingKind.Bool,  "LabelModelRouterAuto",        "HintModelRouterAuto",        Gate: "roles"),
                new("ragEmbeddingModel",      SettingKind.Model, "LabelRagEmbeddingModel",      "HintRagEmbeddingModel"),
            ],
            ToggleGate: "roles", ToggleLabel: "LabelModelRolesAdvanced", ToggleHint: "HintModelRolesAdvanced"),
        ]),

        new("behavior", "SectionBehavior",
        [
            new("SectionBehavior",
            [
                new("commandTimeoutSeconds",   SettingKind.Int,      "LabelCommandTimeout",         "HintCommandTimeout",         Unit: "s",   Gate: "advanced"),
                new("quickTimeoutSeconds",     SettingKind.Int,      "LabelTaskTimeoutQuick",       "HintTaskTimeoutQuick",       Unit: "s",   Gate: "advanced"),
                new("normalTimeoutSeconds",    SettingKind.Int,      "LabelTaskTimeoutNormal",      "HintTaskTimeoutNormal",      Unit: "s",   Gate: "advanced"),
                new("deepTimeoutSeconds",      SettingKind.Int,      "LabelTaskTimeoutDeep",        "HintTaskTimeoutDeep",        Unit: "s",   Gate: "advanced"),
                new("agentMaxIterations",      SettingKind.Int,      "LabelAgentMaxIterations",     "HintAgentMaxIterations",     Gate: "advanced"),
                new("modelAutoUnloadEnabled",  SettingKind.Bool,     "LabelModelAutoUnload",        "HintModelAutoUnload",        Gate: "advanced"),
                new("modelIdleTimeoutMinutes", SettingKind.Int,      "LabelModelIdleTimeout",       "HintModelIdleTimeout",       Unit: "min", Gate: "advanced"),
                new("toolBubblesExpanded",     SettingKind.Bool,     "LabelToolBubblesExpanded",    "HintToolBubblesExpanded"),
                new("securityAlertsDisabled",  SettingKind.Bool,     "LabelSecurityAlertsDisabled", "HintSecurityAlertsDisabled"),
                new("permissionRules",         SettingKind.TextArea, "LabelPermissionRules",        "HintPermissionRules"),
                new("smartFixEnabled",         SettingKind.Bool,     "LabelSmartFixEnabled",        "HintSmartFixEnabled"),
                new("agentModeEnabled",        SettingKind.Bool,     "LabelAgentModeEnabled",       "HintAgentModeEnabled"),
            ],
            ToggleGate: "advanced", ToggleLabel: "LabelAdvancedBehavior"),

            new("SectionInlineCompletions",
            [
                new("inlineCompletionEnabled", SettingKind.Bool,   "LabelInlineCompletionEnabled", "HintInlineCompletionEnabled"),
                new("inlineCompletionMode",    SettingKind.Select, "LabelInlineCompletionMode",    "HintInlineCompletionMode", Options: FimModes),
            ]),

            new("SectionPersona",
            [
                new("personaAutoSwitch",    SettingKind.Bool,     "LabelPersonaAutoSwitch",   "HintPersonaAutoSwitch"),
                new("customSystemPrompt",   SettingKind.TextArea, "LabelCustomSystemPrompt",  "HintCustomSystemPrompt"),
            ]),
        ]),

        new("context", "SectionContext",
        [
            new("SectionRag",
            [
                new("ragEnabled",             SettingKind.Bool,  "LabelRagEnabled",             "HintRagEnabled"),
                new("ragAutoContextEnabled",  SettingKind.Bool,  "LabelRagAutoContext",         "HintRagAutoContext"),
                new("ragTopK",                SettingKind.Int,   "LabelRagTopK",                "HintRagTopK",                Unit: "chunks"),
                new("ragSimilarityThreshold", SettingKind.Float, "LabelRagSimilarityThreshold", "HintRagSimilarityThreshold", Unit: "0–1"),
                new("lspEnabled",             SettingKind.Bool,  "LabelLspEnabled",             "HintLspEnabled"),
            ]),

            new("SectionContext",
            [
                new("vramBudgetGb",             SettingKind.Float,    "LabelVramBudget",            "HintVramBudget",            Unit: "GB"),
                new("contextWindowSize",        SettingKind.Int,      "LabelContextWindowSize",     "HintContextWindowSize",     Unit: "tokens"),
                new("contextWindowKeepTurns",   SettingKind.Int,      "LabelContextWindowKeepTurns","HintContextWindowKeepTurns",Unit: "turns"),
                new("compactionEnabled",        SettingKind.Bool,     "LabelCompactionEnabled",     "HintCompactionEnabled"),
                new("compactionTimeoutSeconds", SettingKind.Int,      "LabelCompactionTimeout",     "HintCompactionTimeout",     Unit: "s"),
                new("kvCacheAnchorMessages",    SettingKind.Int,      "LabelKvCacheAnchor",         "HintKvCacheAnchor",         Unit: "msg"),
                new("oodaTurnThreshold",        SettingKind.Int,      "LabelOodaTurnThreshold",     "HintOodaTurnThreshold",     Unit: "turns"),
                new("inlineDiffPreviewEnabled", SettingKind.Bool,     LocalLabelInlineDiff),
                new("pinnedContextFiles",       SettingKind.TextArea, "LabelPinnedContextFiles",    "HintPinnedContextFiles"),
            ]),
        ]),

        new("tools", LocalLabelTabTools,
        [
            new("SectionMcp",
            [
                new("mcpEnabled",     SettingKind.Bool,     "LabelMcpEnabled", "HintMcpEnabled"),
                new("mcpServersJson", SettingKind.TextArea, "LabelMcpServers", "HintMcpServers"),
            ]),

            new("SectionCommandsTools",
            [
                new("promptTemplates", SettingKind.TextArea, "LabelPromptTemplates", "HintPromptTemplates"),
                new("customTools",     SettingKind.TextArea, "LabelCustomTools",     "HintCustomTools"),
            ]),
        ]),
    ];

    /// <summary>UI languages. Names are deliberately never localized (a French speaker looking for
    /// their language looks for "Français", not for its German name).</summary>
    private static readonly SettingOption[] Languages =
    [
        new("",      "Auto"),
        new("en",    "English"),  new("fr",    "Français"),
        new("de",    "Deutsch"),  new("es",    "Español"),
        new("it",    "Italiano"), new("ru",    "Русский"),
        new("ja",    "日本語"),    new("ko",    "한국어"),
        new("pl",    "Polski"),   new("zh-CN", "中文(简体)"),
    ];

    /// <summary>
    /// Fields rendered outside the tabs — today only the language selector, which sits in the
    /// header because switching it re-renders every label around it.
    /// </summary>
    public static IReadOnlyList<SettingField> HeaderFields { get; } =
    [
        new("language", SettingKind.Select, "LabelLanguage", Options: Languages),
    ];

    /// <summary>Every editable field, flattened — for validation and for the adapters. Header
    /// fields come last, matching the order the panel saves them in.</summary>
    public static IEnumerable<SettingField> AllFields =>
        Tabs.SelectMany(t => t.Sections).SelectMany(s => s.Fields).Concat(HeaderFields);
}
