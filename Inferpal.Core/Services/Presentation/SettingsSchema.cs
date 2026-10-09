using Inferpal.Localization;

namespace Inferpal.Services.Presentation;

/// <summary>What kind of control edits a setting.</summary>
internal enum SettingKind { Text, Password, Bool, Int, Float, Model, Select, TextArea }

/// <summary>An option of a <see cref="SettingKind.Select"/> field.</summary>
/// <param name="Text">Verbatim text - a product name (Ollama, LM Studio) or a language name, which
/// are the same in every language. Also the fallback of <paramref name="Localized"/>.</param>
/// <param name="Localized">Resource lookup for options whose text is <b>ordinary prose</b>, resolved
/// at render time so a language change takes effect.</param>
/// <remarks>
/// ⚠ "Option texts are product names, never localized" holds for two thirds of the uses and fails
/// for the third: "Fast", "Default", "High Accuracy" are adjectives, and they come out in English
/// in a panel whose every other word is translated. The distinction lives in the TYPE rather than
/// in a comment, so neither case can be forgotten.
///
/// And it resolves <b>at display time</b>, never while building the schema:
/// <see cref="SettingsSchema.Tabs"/> is a property initialised once, so a text resolved in there
/// would freeze the language of the first access.
/// </remarks>
internal sealed record SettingOption(string Value, string Text, Func<string>? Localized = null, Func<string>? Describe = null)
{
    /// <summary>Display text in the language in force <i>now</i>.</summary>
    internal string Display => Localized?.Invoke() ?? Text;

    /// <summary>The sentence under the option's name when it is drawn as a card; empty when it has none.</summary>
    internal string Description => Describe?.Invoke() ?? string.Empty;
}

/// <summary>
/// One editable setting. <paramref name="Key"/> is the camelCase key in the config JSON,
/// <paramref name="Label"/>/<paramref name="Hint"/> are <b>resource names</b> resolved against the
/// shared .resx (so both editors show the same wording in all 10 languages).
/// </summary>
/// <param name="Unit">Resource name of the suffix after a compact numeric field (<c>UnitSeconds</c>, <c>UnitGigabytes</c>, …) — resolved like Label and Hint.</param>
/// <param name="Button">Companion button: <c>test</c> or <c>refreshModels</c>.</param>
/// <param name="Inverted">A check box that shows the opposite of the stored value: the key says what is
/// turned OFF (<c>securityAlertsDisabled</c>), the box says what happens (ask before acting).</param>
/// <param name="OpensFold">A value other than the factory one opens the page with its advanced sections
/// shown: a setting in effect is never hidden behind the fold.</param>
/// <param name="HintNotOllama">Resource name of the hint shown instead of <paramref name="Hint"/> when the
/// selected server is not Ollama.</param>
/// <param name="Editor">The structured editor of a list setting — <c>pinnedFiles</c>, <c>mcpServers</c>,
/// <c>approvalRules</c> or <c>nameValue</c> — drawn over its text, which stays what is saved and stays
/// reachable ("Edit as text"): a line the editor cannot read is never lost.</param>
/// <param name="ZeroIsEmpty">A numeric box whose 0 means "not set" (the GPU's memory, detected when empty): it shows
/// empty, as its hint says, and empty saves 0.</param>
/// <param name="EmptyChoice">Resource name of what an optional model list shows for its empty value — the model
/// that does the work then ("Same as chat"): an empty entry says nothing, and the hint had to explain it.</param>
/// <param name="Columns">Resource names of the column headers of a <c>nameValue</c> table (name, then value).</param>
/// <param name="Min">Smallest value a numeric box accepts.</param>
/// <param name="Max">Largest value a numeric box accepts.</param>
/// <remarks>
/// ⚠ The bounds are declared HERE, once, and both panels read them: each panel used to hold its own — the Visual
/// Studio window clamped in silence (50 results saved as 20, the box still showing 50, "1 unsaved change" for ever),
/// the VS Code panel not at all (50 saved). A value outside them is applied no more than a typo is
/// (<see cref="SettingsFallback.WasIgnored"/>): the field is named and its saved value kept.
/// </remarks>
internal sealed record SettingField(
    string  Key,
    SettingKind Kind,
    string  Label,
    string? Hint    = null,
    string? Unit    = null,
    string? Button  = null,
    IReadOnlyList<SettingOption>? Options = null,
    bool    Inverted = false,
    bool    OpensFold = false,
    string? HintNotOllama = null,
    string? Editor = null,
    bool    ZeroIsEmpty = false,
    string? EmptyChoice = null,
    IReadOnlyList<string>? Columns = null,
    int?    Min = null,
    int?    Max = null)
{
    /// <summary>Is <paramref name="value"/> within the box's bounds?</summary>
    public bool Accepts(int value) => (Min is null || value >= Min) && (Max is null || value <= Max);

    /// <summary>Is <paramref name="value"/>, a decimal box's, within the bounds?</summary>
    public bool Accepts(double value) =>
        double.IsFinite(value) && (Min is null || value >= Min) && (Max is null || value <= Max);
}

/// <summary>A group of fields under a title (empty = no heading), with an optional description.</summary>
/// <param name="Gate">Reveal group the section belongs to: <c>advanced</c> sections show only when the page's
/// "Show advanced settings" box is checked.</param>
/// <param name="Collapsible">Rendered folded under its title, opened on demand.</param>
/// <param name="Note">Resource name of a sentence shown under the fields.</param>
/// <param name="Grid">Fields laid out in two columns.</param>
/// <param name="Widget">A live block rendered under the fields, from what the product knows now rather than from the
/// configuration: <c>approvalRules</c> (the rules in effect and where they come from, with a link to the page that edits
/// them), <c>indexCard</c> (the code index: state, counts, what it left out, its model), <c>indexExclusions</c> (the
/// project's own exclusions), <c>docsSites</c> (the @Docs sites), <c>contextUsage</c> (how full the conversation's
/// window is, and with what), <c>projectFiles</c> (the project's files the prompt reads), <c>repoInstructions</c> (the
/// instruction files the repository gives coding agents, and what each sends), <c>fimModel</c> and
/// <c>editModel</c> (the model a page's feature uses, set on another page), <c>suggestModels</c> (the best installed
/// models, proposed into the form, never saved) and <c>loadedModels</c> (what the server holds in memory, and the
/// unload buttons).</param>
internal sealed record SettingSection(
    string Title,
    IReadOnlyList<SettingField> Fields,
    string? Description = null,
    string? Gate        = null,
    bool    Collapsible = false,
    string? Note        = null,
    bool    Grid        = false,
    string? Widget      = null);

/// <summary>One page of the settings window, reached from the side navigation.</summary>
/// <param name="AdvancedToggle">Resource name of the page's "Show advanced settings" box, when some of its
/// sections are gated <c>advanced</c>.</param>
internal sealed record SettingTab(
    string Key,
    string Title,
    string Description,
    IReadOnlyList<SettingSection> Sections,
    string? AdvancedToggle = null);

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
    /// <summary>The three inline-completion presets. Public: the Visual Studio window kept its own
    /// copy, which is exactly what this schema exists to remove.
    /// <c>IReadOnlyList</c> and not an array: a public <c>static readonly T[]</c> protects the
    /// reference, never its contents - any caller could write into it. This is the single source of
    /// those lists, it must not be modifiable from outside.</summary>
    public static readonly IReadOnlyList<SettingOption> FimModes =
    [
        new("Fast",         "Fast",     () => Strings.FimModeFast,         () => Strings.FimModeFastDesc),
        new("Default",      "Balanced", () => Strings.FimModeDefault,      () => Strings.FimModeDefaultDesc),
        new("HighAccuracy", "Accurate", () => Strings.FimModeHighAccuracy, () => Strings.FimModeHighAccuracyDesc),
    ];

    /// <summary>Backend names - never translated. Public for the same reason as
    /// <see cref="FimModes"/>: the VS window copy had already DRIFTED (it said
    /// "OpenAI-compatible (generic)", the schema "OpenAI-compatible") and nobody had seen it.</summary>
    public static readonly IReadOnlyList<SettingOption> Providers =
    [
        new("ollama",            "Ollama"),
        new("lmstudio",          "LM Studio"),
        new("openai-compatible", "OpenAI-compatible (generic)"),
    ];

    // ⚠ Above Tabs: static initializers run in textual order. Declared below it, the list is still
    // null when the language field captures it - a language box with no option, and no error.
    /// <summary>UI languages. Names are deliberately never localized (a French speaker looking for
    /// their language looks for "Français", not for its German name).</summary>
    public static readonly IReadOnlyList<SettingOption> Languages =
    [
        new("",      "Auto"),
        new("en",    "English"),  new("fr",    "Français"),
        new("de",    "Deutsch"),  new("es",    "Español"),
        new("it",    "Italiano"), new("ru",    "Русский"),
        new("ja",    "日本語"),    new("ko",    "한국어"),
        new("pl",    "Polski"),   new("zh-CN", "中文(简体)"),
    ];

    /// <summary>
    /// The name of the language "automatic" resolves to for an editor in <paramref name="culture"/>: the first culture
    /// of its parent chain that is one of ours, English otherwise — the resource manager's own fallback, so the name
    /// is the language the panel actually speaks (<c>fr-CA</c> → Français, <c>zh-TW</c> → English).
    /// </summary>
    public static string AutoLanguageName(System.Globalization.CultureInfo culture)
    {
        for (var c = culture; !string.IsNullOrEmpty(c.Name); c = c.Parent)
            if (Languages.FirstOrDefault(l => l.Value.Length > 0
                                            && string.Equals(l.Value, c.Name, StringComparison.OrdinalIgnoreCase)) is { } match)
                return match.Text;
        return "English";
    }

    /// <summary>The gate of the sections a page's "Show advanced settings" box reveals.</summary>
    public const string AdvancedGate = "advanced";

    /// <summary>How much space the chat leaves around its turns: two options, drawn as a segmented switch.
    /// ⚠ Above <see cref="Tabs"/>: a static initialiser reads the fields declared before it, and one declared after is still
    /// null there, and the switch shows no option.</summary>
    public static readonly IReadOnlyList<SettingOption> Densities =
    [
        new("comfortable", "Comfortable", () => Strings.DensityComfortable),
        new("compact",     "Compact",     () => Strings.DensityCompact),
    ];

    /// <summary>
    /// The seven pages, in the order of the side navigation. Labels and hints say what a setting
    /// DOES, in the user's words; the configuration key keeps its technical name.
    /// </summary>
    /// <remarks>
    /// ⚠ A setting that nothing reads does not get a box: <c>deepTimeoutSeconds</c> stays in the
    /// configuration file and out of the form, since no request is ever made with that budget.
    /// </remarks>
    public static IReadOnlyList<SettingTab> Tabs { get; } =
    [
        new("server", "SettingsPageServer", "SettingsPageServerDesc",
        [
            new("",
            [
                new("baseUrl",               SettingKind.Text,  "LabelUrl",                   "HintUrl",                   Button: "test"),
            ]),
            new("",
            [
                new("defaultModel",          SettingKind.Model, "LabelChatModel",             "HintChatModel",             Button: "refreshModels"),
                new("inlineCompletionModel", SettingKind.Model, "LabelInlineCompletionModel", "HintInlineCompletionModel",
                    EmptyChoice: "SettingsSameAsChat"),
                new("ragEmbeddingModel",     SettingKind.Model, "LabelRagEmbeddingModel",     "HintRagEmbeddingModel",
                    EmptyChoice: "SettingsAutomaticBest"),
                new("contextWindowSize",     SettingKind.Int,   "LabelContextWindowSize",     "HintContextWindowSize",     Unit: "UnitTokens",
                    HintNotOllama: "HintContextWindowSizeClientTrim", Min: 0),
            ],
            Grid: true, Widget: "suggestModels"),
            new("SettingsSectionLoadedModels", [], Note: "NoteLoadedModels", Widget: "loadedModels"),
            new("SettingsSectionModelPerTask",
            [
                new("agentModel",       SettingKind.Model, "LabelAgentModel",       "HintAgentModel",       OpensFold: true,
                    EmptyChoice: "SettingsSameAsChat"),
                new("codeActionsModel", SettingKind.Model, "LabelCodeActionsModel", "HintCodeActionsModel", OpensFold: true,
                    EmptyChoice: "SettingsSameAsChat"),
                new("inlineEditModel",  SettingKind.Model, "LabelInlineEditModel",  "HintInlineEditModel",  OpensFold: true,
                    EmptyChoice: "SettingsSameAsCodeActions"),
                new("utilityModel",     SettingKind.Model, "LabelUtilityModel",     "HintUtilityModel",     OpensFold: true,
                    EmptyChoice: "SettingsSameAsChat"),
                new("modelRouterAuto",  SettingKind.Bool,  "LabelModelRouterAuto",  "HintModelRouterAuto",  OpensFold: true),
            ],
            Description: "SettingsSectionModelPerTaskDesc", Gate: AdvancedGate, Grid: true),
            new("SettingsSectionSampling",
            [
                new("useRecommendedSampling", SettingKind.Bool, "LabelUseRecommendedSampling", "HintUseRecommendedSampling", OpensFold: true),
            ],
            Gate: AdvancedGate),
            new("SettingsSectionGpu",
            [
                new("vramBudgetGb",            SettingKind.Float, "LabelVramBudget",       "HintVramBudget",       Unit: "UnitGigabytes",
                    ZeroIsEmpty: true),
                new("modelAutoUnloadEnabled",  SettingKind.Bool,  "LabelModelAutoUnload",  "HintModelAutoUnload"),
                new("modelIdleTimeoutMinutes", SettingKind.Int,   "LabelModelIdleTimeout", "HintModelIdleTimeout", Unit: "UnitMinutes",
                    Min: 1),
            ],
            Gate: AdvancedGate),
            new("SectionConnection",
            [
                new("provider", SettingKind.Select,   "LabelProvider", "HintProvider", Options: Providers),
                new("apiKey",   SettingKind.Password, "LabelApiKey",   "HintApiKey"),
            ],
            Gate: AdvancedGate, Grid: true),
        ],
        AdvancedToggle: "SettingsShowAdvanced"),

        new("agent", "SettingsPageAgent", "SettingsPageAgentDesc",
        [
            new("SettingsSectionHowItWorks",
            [
                new("agentModeEnabled",   SettingKind.Bool, "LabelAgentModeEnabled",   "HintAgentModeEnabled"),
                new("smartFixEnabled",    SettingKind.Bool, "LabelSmartFixEnabled",    "HintSmartFixEnabled"),
                new("agentMaxIterations", SettingKind.Int,  "LabelAgentMaxIterations", "HintAgentMaxIterations", Unit: "UnitIterations",
                    Min: 0),
            ]),
            new("SettingsSectionApprovals",
            [
                new("securityAlertsDisabled", SettingKind.Bool, "LabelAskBeforeActions", "HintAskBeforeActions", Inverted: true),
            ],
            Widget: "approvalRules"),
            new("SettingsSectionInstructions",
            [
                new("customSystemPrompt", SettingKind.TextArea, "LabelCustomSystemPrompt", "HintCustomSystemPrompt"),
                new("personaAutoSwitch",  SettingKind.Bool,     "LabelPersonaAutoSwitch",  "HintPersonaAutoSwitch"),
            ]),
            new("SettingsSectionTimeLimits",
            [
                // ⚠ 0 stops every command the instant it starts, and a negative deadline throws; a task deadline
                // under 10 s is raised to 10 by the client that applies it.
                new("commandTimeoutSeconds", SettingKind.Int, "LabelCommandTimeout",    "HintCommandTimeout",    Unit: "UnitSeconds",
                    Min: 1),
                new("quickTimeoutSeconds",   SettingKind.Int, "LabelTaskTimeoutQuick",  "HintTaskTimeoutQuick",  Unit: "UnitSeconds",
                    Min: 10),
                new("normalTimeoutSeconds",  SettingKind.Int, "LabelTaskTimeoutNormal", "HintTaskTimeoutNormal", Unit: "UnitSeconds",
                    Min: 10),
            ],
            Collapsible: true, Note: "SettingsTimeLimitsNote"),
        ]),

        new("context", "SettingsPageContext", "SettingsPageContextDesc",
        [
            new("SettingsSectionThisConversation", [], Widget: "contextUsage"),
            new("SettingsSectionLongConversations",
            [
                new("compactionEnabled",        SettingKind.Bool, "LabelCompactionEnabled",      "HintCompactionEnabled"),
            ]),
            new("",
            [
                new("contextWindowKeepTurns",   SettingKind.Int,  "LabelContextWindowKeepTurns", "HintContextWindowKeepTurns", Unit: "UnitTurns",
                    Min: 1),
                new("oodaTurnThreshold",        SettingKind.Int,  "LabelOodaTurnThreshold",      "HintOodaTurnThreshold",      Unit: "UnitTurns",
                    Min: 0),
                new("compactionTimeoutSeconds", SettingKind.Int,  "LabelCompactionTimeout",      "HintCompactionTimeout",      Unit: "UnitSeconds",
                    Min: 10, Max: 300),
                new("kvCacheAnchorMessages",    SettingKind.Int,  "LabelKvCacheAnchor",          "HintKvCacheAnchor",          Unit: "UnitMessages",
                    Min: 0, Max: 20),
            ],
            Grid: true),
            new("SettingsSectionAlwaysInPrompt",
            [
                new("pinnedContextFiles", SettingKind.TextArea, "LabelPinnedContextFiles", "HintPinnedContextFiles", Editor: "pinnedFiles"),
            ],
            Description: "SettingsSectionAlwaysInPromptDesc", Widget: "projectFiles"),
            new("SettingsSectionRepoInstructions",
            [
                new("repoInstructionFamilies", SettingKind.Text, "LabelRepoInstructionFamilies", "HintRepoInstructionFamilies"),
                new("skillsAutoMode", SettingKind.Bool, "LabelSkillsAutoMode", "HintSkillsAutoMode"),
            ],
            Description: "SettingsSectionRepoInstructionsDesc", Widget: "repoInstructions"),
        ]),

        new("search", "SettingsPageSearch", "SettingsPageSearchDesc",
        [
            new("", [], Widget: "indexCard"),
            new("SettingsSectionYourCode",
            [
                new("ragEnabled",             SettingKind.Bool,  "LabelRagEnabled",             "HintRagEnabled"),
                new("ragAutoContextEnabled",  SettingKind.Bool,  "LabelRagAutoContext",         "HintRagAutoContext"),
                new("lspEnabled",             SettingKind.Bool,  "LabelLspEnabled",             "HintLspEnabled"),
            ]),
            new("",
            [
                new("ragTopK",                SettingKind.Int,   "LabelRagTopK",                "HintRagTopK",                Unit: "UnitChunks",
                    Min: 1, Max: 20),
                // ⚠ A cosine similarity is at most 1: above it no semantic hit ever passes, and search falls back to
                // keywords without a word.
                new("ragSimilarityThreshold", SettingKind.Float, "LabelRagSimilarityThreshold", "HintRagSimilarityThreshold", Unit: "UnitRangeZeroToOne",
                    Min: 0, Max: 1),
            ],
            Grid: true, Widget: "indexExclusions"),
            new("SettingsSectionDocs", [], Description: "SettingsSectionDocsDesc", Note: "SettingsDocsNote", Widget: "docsSites"),
        ]),

        new("autocomplete", "SettingsPageAutocomplete", "SettingsPageAutocompleteDesc",
        [
            new("SettingsSectionAsYouType",
            [
                new("inlineCompletionEnabled", SettingKind.Bool,   "LabelInlineCompletionEnabled", "HintInlineCompletionEnabled"),
                new("inlineCompletionMode",    SettingKind.Select, "LabelInlineCompletionMode",    Options: FimModes, Editor: "cards"),
            ],
            Widget: "fimModel"),
            new("SettingsSectionEditWithAi",
            [
                new("inlineDiffPreviewEnabled", SettingKind.Bool, "LabelInlineDiffPreview", "HintInlineDiffPreview"),
            ],
            Description: "SettingsSectionEditWithAiDesc", Widget: "editModel"),
        ]),

        new("tools", "SettingsPageTools", "SettingsPageToolsDesc",
        [
            new("SectionMcp",
            [
                new("mcpEnabled",     SettingKind.Bool,     "LabelMcpEnabled", "HintMcpEnabled"),
                new("mcpServersJson", SettingKind.TextArea, "LabelMcpServers", "HintMcpServers", Editor: "mcpServers"),
            ]),
            new("SettingsSectionRules",
            [
                new("permissionRules", SettingKind.TextArea, "LabelPermissionRules", "HintPermissionRules", Editor: "approvalRules"),
            ],
            Description: "SettingsSectionRulesDesc"),
            new("SettingsSectionSlash",
            [
                new("promptTemplates", SettingKind.TextArea, "LabelPromptTemplates", "HintPromptTemplates", Editor: "nameValue",
                    Columns: ["SlashColCommand", "SlashColSends"]),
            ],
            Description: "SettingsSectionSlashDesc"),
            new("SettingsSectionAgentTools",
            [
                new("customTools", SettingKind.TextArea, "LabelCustomTools", "HintCustomTools", Editor: "nameValue",
                    Columns: ["ToolColName", "ToolColRuns"]),
            ],
            Description: "SettingsSectionAgentToolsDesc"),
        ]),

        new("appearance", "SettingsPageAppearance", "SettingsPageAppearanceDesc",
        [
            new("",
            [
                new("language", SettingKind.Select, "LabelLanguage", "HintLanguage", Options: Languages),
            ]),
            // The editor's theme, shown, never set here: the panel follows it, high contrast included.
            new("SettingsSectionTheme", [], Note: "SettingsThemeNote", Widget: "themeCards"),
            new("",
            [
                new("chatDensity", SettingKind.Select, "LabelDensity", "HintDensity", Options: Densities, Editor: "segmented"),
            ]),
            new("SettingsSectionAgentRuns",
            [
                new("toolBubblesExpanded", SettingKind.Bool, "LabelToolBubblesExpanded", "HintToolBubblesExpanded"),
            ]),
        ]),
    ];

    /// <summary>Every editable field, flattened — for validation and for the adapters.</summary>
    public static IEnumerable<SettingField> AllFields =>
        Tabs.SelectMany(t => t.Sections).SelectMany(s => s.Fields);

    /// <summary>The field of a configuration key (its JSON name).</summary>
    public static SettingField Field(string key) => AllFields.First(f => f.Key == key);

    /// <summary>
    /// Every resource name the form displays — page titles and descriptions, section titles,
    /// descriptions and notes, field labels, hints and units. What `settings/strings` serves and
    /// what the Visual Studio window resolves.
    /// </summary>
    public static IEnumerable<string> ResourceNames =>
        Tabs.SelectMany(t => new[] { t.Title, t.Description, t.AdvancedToggle }
                .Concat(t.Sections.SelectMany(s => new[] { s.Title, s.Description, s.Note }))
                .Concat(t.Sections.SelectMany(s => s.Fields).SelectMany(f =>
                    new[] { f.Label, f.Hint, f.Unit, f.HintNotOllama, f.EmptyChoice }.Concat(f.Columns ?? []))))
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .Distinct(StringComparer.Ordinal);
}
