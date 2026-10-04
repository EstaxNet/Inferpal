using System.Globalization;
using System.Runtime.Serialization;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Services.Presentation;
using Microsoft.VisualStudio.Extensibility.UI;

namespace Inferpal.ToolWindow;

/// <summary>
/// The seven pages of the settings window — those of the Core <see cref="SettingsSchema"/>, which the
/// VS Code panel renders too — with their side navigation and search, the count of unsaved changes,
/// and Cancel.
/// </summary>
internal partial class InferpalSettingsData
{
    /// <summary>The page keys, in the order of the navigation: the schema's.</summary>
    private static readonly string[] PageKeys = [.. SettingsSchema.Tabs.Select(t => t.Key)];

    private string _page = PageKeys[0];
    /// <summary>Per page, its displayed text in lower case: what the search matches.</summary>
    private Dictionary<string, string> _pageText = [];

    // ── Navigation ─────────────────────────────────────────────────────────────
    private bool _pageServerVisible = true, _pageAgentVisible, _pageContextVisible, _pageSearchVisible,
                 _pageAutocompleteVisible, _pageToolsVisible, _pageAppearanceVisible;
    private bool _navServerVisible = true, _navAgentVisible = true, _navContextVisible = true, _navSearchVisible = true,
                 _navAutocompleteVisible = true, _navToolsVisible = true, _navAppearanceVisible = true;
    private string _navServerBg = "#00000000", _navAgentBg = "#00000000", _navContextBg = "#00000000",
                   _navSearchBg = "#00000000", _navAutocompleteBg = "#00000000", _navToolsBg = "#00000000",
                   _navAppearanceBg = "#00000000";
    private string _navPaneBg = "#14808080", _navLineBrush = "#33808080", _subtleForeground = "#A3A3AD";
    private string _searchText = string.Empty;
    private bool   _searchNoMatch;
    private string _unsavedText = string.Empty;
    private bool   _saveEnabled;
    private string _approvalRulesSummary = string.Empty;

    [DataMember] public bool PageServerVisible       { get => _pageServerVisible;       set => SetProperty(ref _pageServerVisible,       value); }
    [DataMember] public bool PageAgentVisible        { get => _pageAgentVisible;        set => SetProperty(ref _pageAgentVisible,        value); }
    [DataMember] public bool PageContextVisible      { get => _pageContextVisible;      set => SetProperty(ref _pageContextVisible,      value); }
    [DataMember] public bool PageSearchVisible       { get => _pageSearchVisible;       set => SetProperty(ref _pageSearchVisible,       value); }
    [DataMember] public bool PageAutocompleteVisible { get => _pageAutocompleteVisible; set => SetProperty(ref _pageAutocompleteVisible, value); }
    [DataMember] public bool PageToolsVisible        { get => _pageToolsVisible;        set => SetProperty(ref _pageToolsVisible,        value); }
    [DataMember] public bool PageAppearanceVisible   { get => _pageAppearanceVisible;   set => SetProperty(ref _pageAppearanceVisible,   value); }

    [DataMember] public bool NavServerVisible        { get => _navServerVisible;        set => SetProperty(ref _navServerVisible,        value); }
    [DataMember] public bool NavAgentVisible         { get => _navAgentVisible;         set => SetProperty(ref _navAgentVisible,         value); }
    [DataMember] public bool NavContextVisible       { get => _navContextVisible;       set => SetProperty(ref _navContextVisible,       value); }
    [DataMember] public bool NavSearchVisible        { get => _navSearchVisible;        set => SetProperty(ref _navSearchVisible,        value); }
    [DataMember] public bool NavAutocompleteVisible  { get => _navAutocompleteVisible;  set => SetProperty(ref _navAutocompleteVisible,  value); }
    [DataMember] public bool NavToolsVisible         { get => _navToolsVisible;         set => SetProperty(ref _navToolsVisible,         value); }
    [DataMember] public bool NavAppearanceVisible    { get => _navAppearanceVisible;    set => SetProperty(ref _navAppearanceVisible,    value); }

    [DataMember] public string NavServerBg           { get => _navServerBg;             set => SetProperty(ref _navServerBg,             value); }
    [DataMember] public string NavAgentBg            { get => _navAgentBg;              set => SetProperty(ref _navAgentBg,              value); }
    [DataMember] public string NavContextBg          { get => _navContextBg;            set => SetProperty(ref _navContextBg,            value); }
    [DataMember] public string NavSearchBg           { get => _navSearchBg;             set => SetProperty(ref _navSearchBg,             value); }
    [DataMember] public string NavAutocompleteBg     { get => _navAutocompleteBg;       set => SetProperty(ref _navAutocompleteBg,       value); }
    [DataMember] public string NavToolsBg            { get => _navToolsBg;              set => SetProperty(ref _navToolsBg,              value); }
    [DataMember] public string NavAppearanceBg       { get => _navAppearanceBg;         set => SetProperty(ref _navAppearanceBg,         value); }
    [DataMember] public string NavPaneBg             { get => _navPaneBg;               set => SetProperty(ref _navPaneBg,               value); }
    [DataMember] public string NavLineBrush          { get => _navLineBrush;            set => SetProperty(ref _navLineBrush,            value); }
    [DataMember] public string SubtleForeground      { get => _subtleForeground;        set => SetProperty(ref _subtleForeground,        value); }

    [DataMember] public AsyncCommand SelectPageServerCommand       { get; }
    [DataMember] public AsyncCommand SelectPageAgentCommand        { get; }
    [DataMember] public AsyncCommand SelectPageContextCommand      { get; }
    [DataMember] public AsyncCommand SelectPageSearchCommand       { get; }
    [DataMember] public AsyncCommand SelectPageAutocompleteCommand { get; }
    [DataMember] public AsyncCommand SelectPageToolsCommand        { get; }
    [DataMember] public AsyncCommand SelectPageAppearanceCommand   { get; }
    [DataMember] public AsyncCommand EditRulesCommand              { get; }
    [DataMember] public AsyncCommand ToggleTimeLimitsCommand       { get; }
    [DataMember] public AsyncCommand CancelCommand                 { get; }

    /// <summary>The search box: the navigation keeps the pages whose text mentions it.</summary>
    [DataMember] public string SearchText
    {
        get => _searchText;
        set { if (SetProperty(ref _searchText, value)) ApplySearch(); }
    }
    [DataMember] public bool   SearchNoMatch        { get => _searchNoMatch;        set => SetProperty(ref _searchNoMatch,        value); }
    [DataMember] public string UnsavedText          { get => _unsavedText;          set => SetProperty(ref _unsavedText,          value); }
    [DataMember] public bool   SaveEnabled          { get => _saveEnabled;          set => SetProperty(ref _saveEnabled,          value); }
    [DataMember] public string ApprovalRulesSummary { get => _approvalRulesSummary; set => SetProperty(ref _approvalRulesSummary, value); }

    // ── Page, section and field texts the old window did not have ──────────────
    private string _labelPageServer = "", _labelPageAgent = "", _labelPageContext = "", _labelPageSearch = "",
                   _labelPageAutocomplete = "", _labelPageTools = "", _labelPageAppearance = "",
                   _currentPageTitle = "", _currentPageDesc = "";
    private string _labelSectionModelPerTask = "", _descSectionModelPerTask = "", _labelSectionSampling = "",
                   _labelSectionGpu = "", _labelSectionHowItWorks = "", _labelSectionApprovals = "",
                   _labelSectionInstructions = "", _labelSectionTimeLimits = "", _noteTimeLimits = "",
                   _labelSectionLongConversations = "", _labelSectionAlwaysInPrompt = "", _descSectionAlwaysInPrompt = "",
                   _labelSectionYourCode = "", _labelSectionEditWithAi = "", _descSectionEditWithAi = "",
                   _labelSectionRules = "", _descSectionRules = "", _labelSectionSlash = "", _descSectionSlash = "",
                   _labelSectionAgentTools = "", _descSectionAgentTools = "", _labelSectionAgentRuns = "";
    private string _labelShowAdvanced = "", _labelUseRecommendedSampling = "", _hintUseRecommendedSampling = "",
                   _labelAskBeforeActions = "", _hintAskBeforeActions = "", _labelInlineDiffPreview = "",
                   _hintInlineDiffPreview = "", _labelEditRules = "", _searchPlaceholder = "", _labelSearchNoMatch = "",
                   _btnCancel = "", _unitRangeZeroToOne = "";

    [DataMember] public string LabelPageServer       { get => _labelPageServer;       set => SetProperty(ref _labelPageServer,       value); }
    [DataMember] public string LabelPageAgent        { get => _labelPageAgent;        set => SetProperty(ref _labelPageAgent,        value); }
    [DataMember] public string LabelPageContext      { get => _labelPageContext;      set => SetProperty(ref _labelPageContext,      value); }
    [DataMember] public string LabelPageSearch       { get => _labelPageSearch;       set => SetProperty(ref _labelPageSearch,       value); }
    [DataMember] public string LabelPageAutocomplete { get => _labelPageAutocomplete; set => SetProperty(ref _labelPageAutocomplete, value); }
    [DataMember] public string LabelPageTools        { get => _labelPageTools;        set => SetProperty(ref _labelPageTools,        value); }
    [DataMember] public string LabelPageAppearance   { get => _labelPageAppearance;   set => SetProperty(ref _labelPageAppearance,   value); }
    [DataMember] public string CurrentPageTitle      { get => _currentPageTitle;      set => SetProperty(ref _currentPageTitle,      value); }
    [DataMember] public string CurrentPageDesc       { get => _currentPageDesc;       set => SetProperty(ref _currentPageDesc,       value); }

    [DataMember] public string LabelSectionModelPerTask      { get => _labelSectionModelPerTask;      set => SetProperty(ref _labelSectionModelPerTask,      value); }
    [DataMember] public string DescSectionModelPerTask       { get => _descSectionModelPerTask;       set => SetProperty(ref _descSectionModelPerTask,       value); }
    [DataMember] public string LabelSectionSampling          { get => _labelSectionSampling;          set => SetProperty(ref _labelSectionSampling,          value); }
    [DataMember] public string LabelSectionGpu               { get => _labelSectionGpu;               set => SetProperty(ref _labelSectionGpu,               value); }
    [DataMember] public string LabelSectionHowItWorks        { get => _labelSectionHowItWorks;        set => SetProperty(ref _labelSectionHowItWorks,        value); }
    [DataMember] public string LabelSectionApprovals         { get => _labelSectionApprovals;         set => SetProperty(ref _labelSectionApprovals,         value); }
    [DataMember] public string LabelSectionInstructions      { get => _labelSectionInstructions;      set => SetProperty(ref _labelSectionInstructions,      value); }
    [DataMember] public string LabelSectionTimeLimits        { get => _labelSectionTimeLimits;        set => SetProperty(ref _labelSectionTimeLimits,        value); }
    [DataMember] public string NoteTimeLimits                { get => _noteTimeLimits;                set => SetProperty(ref _noteTimeLimits,                value); }
    [DataMember] public string LabelSectionLongConversations { get => _labelSectionLongConversations; set => SetProperty(ref _labelSectionLongConversations, value); }
    [DataMember] public string LabelSectionAlwaysInPrompt    { get => _labelSectionAlwaysInPrompt;    set => SetProperty(ref _labelSectionAlwaysInPrompt,    value); }
    [DataMember] public string DescSectionAlwaysInPrompt     { get => _descSectionAlwaysInPrompt;     set => SetProperty(ref _descSectionAlwaysInPrompt,     value); }
    [DataMember] public string LabelSectionYourCode          { get => _labelSectionYourCode;          set => SetProperty(ref _labelSectionYourCode,          value); }
    [DataMember] public string LabelSectionEditWithAi        { get => _labelSectionEditWithAi;        set => SetProperty(ref _labelSectionEditWithAi,        value); }
    [DataMember] public string DescSectionEditWithAi         { get => _descSectionEditWithAi;         set => SetProperty(ref _descSectionEditWithAi,         value); }
    [DataMember] public string LabelSectionRules             { get => _labelSectionRules;             set => SetProperty(ref _labelSectionRules,             value); }
    [DataMember] public string DescSectionRules              { get => _descSectionRules;              set => SetProperty(ref _descSectionRules,              value); }
    [DataMember] public string LabelSectionSlash             { get => _labelSectionSlash;             set => SetProperty(ref _labelSectionSlash,             value); }
    [DataMember] public string DescSectionSlash              { get => _descSectionSlash;              set => SetProperty(ref _descSectionSlash,              value); }
    [DataMember] public string LabelSectionAgentTools        { get => _labelSectionAgentTools;        set => SetProperty(ref _labelSectionAgentTools,        value); }
    [DataMember] public string DescSectionAgentTools         { get => _descSectionAgentTools;         set => SetProperty(ref _descSectionAgentTools,         value); }
    [DataMember] public string LabelSectionAgentRuns         { get => _labelSectionAgentRuns;         set => SetProperty(ref _labelSectionAgentRuns,         value); }

    [DataMember] public string LabelShowAdvanced             { get => _labelShowAdvanced;             set => SetProperty(ref _labelShowAdvanced,             value); }
    [DataMember] public string LabelUseRecommendedSampling   { get => _labelUseRecommendedSampling;   set => SetProperty(ref _labelUseRecommendedSampling,   value); }
    [DataMember] public string HintUseRecommendedSampling    { get => _hintUseRecommendedSampling;    set => SetProperty(ref _hintUseRecommendedSampling,    value); }
    [DataMember] public string LabelAskBeforeActions         { get => _labelAskBeforeActions;         set => SetProperty(ref _labelAskBeforeActions,         value); }
    [DataMember] public string HintAskBeforeActions          { get => _hintAskBeforeActions;          set => SetProperty(ref _hintAskBeforeActions,          value); }
    [DataMember] public string LabelInlineDiffPreview        { get => _labelInlineDiffPreview;        set => SetProperty(ref _labelInlineDiffPreview,        value); }
    [DataMember] public string HintInlineDiffPreview         { get => _hintInlineDiffPreview;         set => SetProperty(ref _hintInlineDiffPreview,         value); }
    [DataMember] public string LabelEditRules                { get => _labelEditRules;                set => SetProperty(ref _labelEditRules,                value); }
    [DataMember] public string SearchPlaceholder             { get => _searchPlaceholder;             set => SetProperty(ref _searchPlaceholder,             value); }
    [DataMember] public string LabelSearchNoMatch            { get => _labelSearchNoMatch;            set => SetProperty(ref _labelSearchNoMatch,            value); }
    [DataMember] public string BtnCancel                     { get => _btnCancel;                     set => SetProperty(ref _btnCancel,                     value); }
    [DataMember] public string UnitRangeZeroToOne            { get => _unitRangeZeroToOne;            set => SetProperty(ref _unitRangeZeroToOne,            value); }

    /// <summary>The texts of the pages, called from <see cref="ApplyLabels"/>.</summary>
    private void ApplyPageLabels()
    {
        LabelPageServer       = Strings.SettingsPageServer;
        LabelPageAgent        = Strings.SettingsPageAgent;
        LabelPageContext      = Strings.SettingsPageContext;
        LabelPageSearch       = Strings.SettingsPageSearch;
        LabelPageAutocomplete = Strings.SettingsPageAutocomplete;
        LabelPageTools        = Strings.SettingsPageTools;
        LabelPageAppearance   = Strings.SettingsPageAppearance;

        LabelSectionModelPerTask      = Strings.SettingsSectionModelPerTask;
        DescSectionModelPerTask       = Strings.SettingsSectionModelPerTaskDesc;
        LabelSectionSampling          = Strings.SettingsSectionSampling;
        LabelSectionGpu               = Strings.SettingsSectionGpu;
        LabelSectionHowItWorks        = Strings.SettingsSectionHowItWorks;
        LabelSectionApprovals         = Strings.SettingsSectionApprovals;
        LabelSectionInstructions      = Strings.SettingsSectionInstructions;
        LabelSectionTimeLimits        = Strings.SettingsSectionTimeLimits;
        NoteTimeLimits                = Strings.SettingsTimeLimitsNote;
        LabelSectionLongConversations = Strings.SettingsSectionLongConversations;
        LabelSectionAlwaysInPrompt    = Strings.SettingsSectionAlwaysInPrompt;
        DescSectionAlwaysInPrompt     = Strings.SettingsSectionAlwaysInPromptDesc;
        LabelSectionYourCode          = Strings.SettingsSectionYourCode;
        LabelSectionEditWithAi        = Strings.SettingsSectionEditWithAi;
        DescSectionEditWithAi         = Strings.SettingsSectionEditWithAiDesc;
        LabelSectionRules             = Strings.SettingsSectionRules;
        DescSectionRules              = Strings.SettingsSectionRulesDesc;
        LabelSectionSlash             = Strings.SettingsSectionSlash;
        DescSectionSlash              = Strings.SettingsSectionSlashDesc;
        LabelSectionAgentTools        = Strings.SettingsSectionAgentTools;
        DescSectionAgentTools         = Strings.SettingsSectionAgentToolsDesc;
        LabelSectionAgentRuns         = Strings.SettingsSectionAgentRuns;

        LabelShowAdvanced             = Strings.SettingsShowAdvanced;
        LabelUseRecommendedSampling   = Strings.LabelUseRecommendedSampling;
        HintUseRecommendedSampling    = Strings.HintUseRecommendedSampling;
        LabelAskBeforeActions         = Strings.LabelAskBeforeActions;
        HintAskBeforeActions          = Strings.HintAskBeforeActions;
        LabelInlineDiffPreview        = Strings.LabelInlineDiffPreview;
        HintInlineDiffPreview         = Strings.HintInlineDiffPreview;
        LabelEditRules                = Strings.SettingsEditRules;
        SearchPlaceholder             = Strings.SettingsSearchPlaceholder;
        LabelSearchNoMatch            = Strings.SettingsSearchNoMatch;
        BtnCancel                     = Strings.SettingsCancel;
        UnitRangeZeroToOne            = Strings.UnitRangeZeroToOne;
        ApplyEditorLabels();

        // What the search matches: everything each page displays, in the language in force.
        _pageText = SettingsSchema.Tabs.ToDictionary(
            t => t.Key,
            t => string.Join(" ", new[] { t.Title, t.Description }
                    .Concat(t.Sections.SelectMany(s => new[] { s.Title, s.Description }))
                    .Concat(t.Sections.SelectMany(s => s.Fields).SelectMany(f => new[] { f.Label, f.Hint }))
                    .Where(n => !string.IsNullOrEmpty(n))
                    .Select(n => Strings.ByName(n!)))
                 .ToLower(Strings.UiCulture),
            StringComparer.Ordinal);

        UpdatePageState();
        UpdateApprovalRulesSummary();
        RefreshUnsaved();
    }

    // ── Navigation and search ──────────────────────────────────────────────────

    private void SelectPage(string key)
    {
        _page = key;
        UpdatePageState();
        OnPageShown(key);
    }

    /// <summary>Visibility of the pages, highlight of the current one in the navigation (theme-aware), and the
    /// page heading.</summary>
    private void UpdatePageState()
    {
        PageServerVisible       = _page == "server";
        PageAgentVisible        = _page == "agent";
        PageContextVisible      = _page == "context";
        PageSearchVisible       = _page == "search";
        PageAutocompleteVisible = _page == "autocomplete";
        PageToolsVisible        = _page == "tools";
        PageAppearanceVisible   = _page == "appearance";

        var dark = IsDarkTheme;
        var active = dark ? "#2E2B3D" : "#E4DFF7";
        const string none = "#00000000";
        NavServerBg       = PageServerVisible       ? active : none;
        NavAgentBg        = PageAgentVisible        ? active : none;
        NavContextBg      = PageContextVisible      ? active : none;
        NavSearchBg       = PageSearchVisible       ? active : none;
        NavAutocompleteBg = PageAutocompleteVisible ? active : none;
        NavToolsBg        = PageToolsVisible        ? active : none;
        NavAppearanceBg   = PageAppearanceVisible   ? active : none;
        NavPaneBg         = dark ? "#14FFFFFF" : "#0A000000";
        NavLineBrush      = dark ? "#33FFFFFF" : "#22000000";
        SubtleForeground  = dark ? "#A3A3AD" : "#5C5C66";

        var tab = SettingsSchema.Tabs.FirstOrDefault(t => t.Key == _page) ?? SettingsSchema.Tabs[0];
        CurrentPageTitle = Strings.ByName(tab.Title);
        CurrentPageDesc  = Strings.ByName(tab.Description);
    }

    /// <summary>Keeps in the navigation the pages that mention the query, and opens the first of them when the
    /// current one is filtered out.</summary>
    private void ApplySearch()
    {
        var q = (SearchText ?? string.Empty).Trim().ToLower(Strings.UiCulture);
        bool Match(string key) => q.Length == 0 || (_pageText.TryGetValue(key, out var text) && text.Contains(q, StringComparison.Ordinal));

        NavServerVisible       = Match("server");
        NavAgentVisible        = Match("agent");
        NavContextVisible      = Match("context");
        NavSearchVisible       = Match("search");
        NavAutocompleteVisible = Match("autocomplete");
        NavToolsVisible        = Match("tools");
        NavAppearanceVisible   = Match("appearance");

        var first = PageKeys.FirstOrDefault(Match);
        SearchNoMatch = first is null;
        if (first is not null && !Match(_page)) SelectPage(first);
    }

    /// <summary>"Approval rules: N" on the Agent page — the rules IN FORCE, team file included, counted by the rules
    /// table (<see cref="RebuildRulesTable"/>), which the VS Code panel counts too.</summary>
    private void UpdateApprovalRulesSummary() => RebuildRulesTable();

    // ── Unsaved changes ────────────────────────────────────────────────────────

    /// <summary>The properties whose change can alter what Save writes. The four lists (MCP servers, pinned
    /// files, slash commands, agent tools) save on their own, row by row: they are never "unsaved".</summary>
    private static readonly HashSet<string> FormProperties = new(StringComparer.Ordinal)
    {
        nameof(BaseUrl), nameof(SelectedProvider), nameof(ApiKey), nameof(SelectedModel), nameof(InlineCompletionModel),
        nameof(RagEmbeddingModel), nameof(ContextWindowSizeText), nameof(AgentModel), nameof(CodeActionsModel),
        nameof(InlineEditModel), nameof(UtilityModel), nameof(ModelRouterAuto), nameof(UseRecommendedSampling),
        nameof(VramBudgetText), nameof(ModelAutoUnloadEnabled), nameof(ModelIdleTimeoutText), nameof(AgentModeEnabled),
        nameof(SmartFixEnabled), nameof(AgentMaxIterationsText), nameof(AskBeforeActions), nameof(CustomSystemPrompt),
        nameof(PersonaAutoSwitch), nameof(TimeoutHoursText), nameof(TimeoutMinutesText), nameof(TimeoutSecondsText),
        nameof(QuickTimeoutHoursText), nameof(QuickTimeoutMinutesText), nameof(QuickTimeoutSecondsText),
        nameof(NormalTimeoutHoursText), nameof(NormalTimeoutMinutesText), nameof(NormalTimeoutSecondsText),
        nameof(CompactionEnabled), nameof(ContextWindowKeepTurnsText), nameof(OodaTurnThresholdText),
        nameof(CompactionTimeoutHoursText), nameof(CompactionTimeoutMinutesText), nameof(CompactionTimeoutSecondsText),
        nameof(KvCacheAnchorMessagesText), nameof(RagEnabled), nameof(RagAutoContextEnabled), nameof(LspEnabled),
        nameof(RagTopKText), nameof(RagSimilarityThresholdText), nameof(InlineCompletionEnabled), nameof(SelectedInlineMode),
        nameof(InlineDiffPreviewEnabled), nameof(McpEnabled), nameof(PermissionRules), nameof(SelectedLanguage),
        nameof(ToolBubblesExpanded), nameof(ChatDensity),
    };

    /// <summary>Recounts the fields that differ from the configuration the form was opened (or last saved)
    /// with, and enables Save only when there is something to save.</summary>
    private void RefreshUnsaved()
    {
        var count = CountUnsaved();
        UnsavedText = count == 0 ? Strings.SettingsNoUnsavedChanges : Strings.SettingsUnsavedChanges(count);
        SaveEnabled = count > 0;
    }

    private int CountUnsaved()
    {
        InferpalConfig saved;
        try { saved = JsonSerializer.Deserialize<InferpalConfig>(_opened.ToJsonString()) ?? new InferpalConfig(); }
        catch (JsonException) { return 0; }

        static string N(int v) => v.ToString(CultureInfo.InvariantCulture);
        string Opt(string? selected, string value) => (selected ?? value).Trim();

        var providerCode = ProviderOptions.FirstOrDefault(p => p.Name == SelectedProvider).Code ?? saved.Provider;
        var languageCode = SelectedLanguage is null || SelectedLanguage == AvailableLanguages.FirstOrDefault()
            ? (SelectedLanguage is null ? saved.Language : string.Empty)
            : LanguageOptions.FirstOrDefault(l => l.Name == SelectedLanguage).Code ?? saved.Language;
        var modeIndex = SelectedInlineMode is null ? -1 : AvailableInlineModes.IndexOf(SelectedInlineMode);
        var modeCode = modeIndex >= 0 && modeIndex < InlineModeOptions.Length ? InlineModeOptions[modeIndex].Code : saved.InlineCompletionMode;
        var vramSaved = saved.VramBudgetGb > 0 ? saved.VramBudgetGb.ToString("0.#", CultureInfo.CurrentCulture) : string.Empty;

        (string Form, string Saved)[] fields =
        [
            (BaseUrl.Trim(), saved.BaseUrl),
            (providerCode, saved.Provider),
            (ApiKey.Trim(), saved.ApiKey),
            (Opt(SelectedModel, saved.DefaultModel), saved.DefaultModel),
            (Opt(InlineCompletionModel, saved.InlineCompletionModel), saved.InlineCompletionModel),
            (Opt(RagEmbeddingModel, saved.RagEmbeddingModel), saved.RagEmbeddingModel),
            (Opt(AgentModel, saved.AgentModel), saved.AgentModel),
            (Opt(CodeActionsModel, saved.CodeActionsModel), saved.CodeActionsModel),
            (Opt(InlineEditModel, saved.InlineEditModel), saved.InlineEditModel),
            (Opt(UtilityModel, saved.UtilityModel), saved.UtilityModel),
            (ContextWindowSizeText.Trim(), N(saved.ContextWindowSize)),
            (ModelRouterAuto.ToString(), saved.ModelRouterAuto.ToString()),
            (UseRecommendedSampling.ToString(), saved.UseRecommendedSampling.ToString()),
            (VramBudgetText.Trim(), vramSaved),
            (ModelAutoUnloadEnabled.ToString(), saved.ModelAutoUnloadEnabled.ToString()),
            (ModelIdleTimeoutText.Trim(), N(saved.ModelIdleTimeoutMinutes)),
            (AgentModeEnabled.ToString(), saved.AgentModeEnabled.ToString()),
            (SmartFixEnabled.ToString(), saved.SmartFixEnabled.ToString()),
            (AgentMaxIterationsText.Trim(), N(saved.AgentMaxIterations)),
            ((!AskBeforeActions).ToString(), saved.SecurityAlertsDisabled.ToString()),
            (CustomSystemPrompt ?? string.Empty, saved.CustomSystemPrompt),
            (PersonaAutoSwitch.ToString(), saved.PersonaAutoSwitch.ToString()),
            (N(CombineDuration(TimeoutHoursText, TimeoutMinutesText, TimeoutSecondsText)), N(saved.CommandTimeoutSeconds)),
            (N(CombineDuration(QuickTimeoutHoursText, QuickTimeoutMinutesText, QuickTimeoutSecondsText)), N(saved.QuickTimeoutSeconds)),
            (N(CombineDuration(NormalTimeoutHoursText, NormalTimeoutMinutesText, NormalTimeoutSecondsText)), N(saved.NormalTimeoutSeconds)),
            (CompactionEnabled.ToString(), saved.CompactionEnabled.ToString()),
            (ContextWindowKeepTurnsText.Trim(), N(saved.ContextWindowKeepTurns)),
            (OodaTurnThresholdText.Trim(), N(saved.OodaTurnThreshold)),
            (N(CombineDuration(CompactionTimeoutHoursText, CompactionTimeoutMinutesText, CompactionTimeoutSecondsText)), N(saved.CompactionTimeoutSeconds)),
            (KvCacheAnchorMessagesText.Trim(), N(saved.KvCacheAnchorMessages)),
            (RagEnabled.ToString(), saved.RagEnabled.ToString()),
            (RagAutoContextEnabled.ToString(), saved.RagAutoContextEnabled.ToString()),
            (LspEnabled.ToString(), saved.LspEnabled.ToString()),
            (RagTopKText.Trim(), N(saved.RagTopK)),
            (RagSimilarityThresholdText.Trim(), saved.RagSimilarityThreshold.ToString("F2", CultureInfo.InvariantCulture)),
            (InlineCompletionEnabled.ToString(), saved.InlineCompletionEnabled.ToString()),
            (modeCode, saved.InlineCompletionMode),
            (InlineDiffPreviewEnabled.ToString(), saved.InlineDiffPreviewEnabled.ToString()),
            (McpEnabled.ToString(), saved.McpEnabled.ToString()),
            ((PermissionRules ?? string.Empty).Trim(), saved.PermissionRules.Trim()),
            (languageCode, saved.Language),
            (ToolBubblesExpanded.ToString(), saved.ToolBubblesExpanded.ToString()),
            (ChatDensity, saved.IsCompactChat ? "compact" : "comfortable"),
        ];
        return fields.Count(f => !string.Equals(f.Form, f.Saved.Trim(), StringComparison.Ordinal));
    }

    // ── Cancel ─────────────────────────────────────────────────────────────────

    /// <summary>Puts every box back to what is saved. Nothing is written.</summary>
    private void ReloadFromConfig()
    {
        var c = _config;
        _opened = c.SnapshotNow();
        _teamRules = ReadTeamRules();   // the team file may have changed since the window opened

        BaseUrl = c.BaseUrl;
        SelectedProvider = ProviderOptions.FirstOrDefault(p => p.Code == c.Provider).Name is { Length: > 0 } pn ? pn : ProviderOptions[0].Name;
        ApiKey = c.ApiKey;
        SelectedModel = c.DefaultModel;
        InlineCompletionModel = c.InlineCompletionModel;
        RagEmbeddingModel = c.RagEmbeddingModel;
        AgentModel = c.AgentModel;
        CodeActionsModel = c.CodeActionsModel;
        InlineEditModel = c.InlineEditModel;
        UtilityModel = c.UtilityModel;
        ModelRouterAuto = c.ModelRouterAuto;
        UseRecommendedSampling = c.UseRecommendedSampling;
        ContextWindowSizeText = N(c.ContextWindowSize);
        VramBudgetText = c.VramBudgetGb > 0 ? c.VramBudgetGb.ToString("0.#", CultureInfo.CurrentCulture) : string.Empty;
        ModelAutoUnloadEnabled = c.ModelAutoUnloadEnabled;
        ModelIdleTimeoutText = N(c.ModelIdleTimeoutMinutes);
        AgentModeEnabled = c.AgentModeEnabled;
        SmartFixEnabled = c.SmartFixEnabled;
        AgentMaxIterationsText = N(c.AgentMaxIterations);
        AskBeforeActions = !c.SecurityAlertsDisabled;
        CustomSystemPrompt = c.CustomSystemPrompt;
        PersonaAutoSwitch = c.PersonaAutoSwitch;
        (TimeoutHoursText, TimeoutMinutesText, TimeoutSecondsText) = SplitDuration(c.CommandTimeoutSeconds);
        (QuickTimeoutHoursText, QuickTimeoutMinutesText, QuickTimeoutSecondsText) = SplitDuration(c.QuickTimeoutSeconds);
        (NormalTimeoutHoursText, NormalTimeoutMinutesText, NormalTimeoutSecondsText) = SplitDuration(c.NormalTimeoutSeconds);
        CompactionEnabled = c.CompactionEnabled;
        ContextWindowKeepTurnsText = N(c.ContextWindowKeepTurns);
        OodaTurnThresholdText = N(c.OodaTurnThreshold);
        (CompactionTimeoutHoursText, CompactionTimeoutMinutesText, CompactionTimeoutSecondsText) = SplitDuration(c.CompactionTimeoutSeconds);
        KvCacheAnchorMessagesText = N(c.KvCacheAnchorMessages);
        RagEnabled = c.RagEnabled;
        RagAutoContextEnabled = c.RagAutoContextEnabled;
        LspEnabled = c.LspEnabled;
        RagTopKText = N(c.RagTopK);
        RagSimilarityThresholdText = c.RagSimilarityThreshold.ToString("F2", CultureInfo.InvariantCulture);
        InlineCompletionEnabled = c.InlineCompletionEnabled;
        InlineDiffPreviewEnabled = c.InlineDiffPreviewEnabled;
        McpEnabled = c.McpEnabled;
        PermissionRules = c.PermissionRules;
        ToolBubblesExpanded = c.ToolBubblesExpanded;
        ChatDensity = c.IsCompactChat ? "compact" : "comfortable";
        ShowAdvanced = ModelRoleSettings.OpensAdvanced(c);

        SaveStatus = string.Empty;
        ApplyLabels();   // the language and the inline mode, re-selected from the configuration

        static string N(int v) => v.ToString(CultureInfo.InvariantCulture);
    }
}
