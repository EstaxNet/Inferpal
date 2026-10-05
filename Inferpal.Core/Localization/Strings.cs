using System.Globalization;
using System.Resources;

namespace Inferpal.Localization;

internal static class Strings
{
    // Explicit resource name: the assembly is Inferpal.Core but RootNamespace stays "Inferpal",
    // so the embedded .resx keeps its historical "Inferpal.Localization.Strings" name.
    private static readonly ResourceManager _rm =
        new("Inferpal.Localization.Strings", typeof(Strings).Assembly);

    internal static CultureInfo? OverrideCulture { get; private set; }

    internal static void ApplyLanguage(string? code)
    {
        try
        {
            OverrideCulture = string.IsNullOrEmpty(code) ? null : CultureInfo.GetCultureInfo(code);
        }
        catch { OverrideCulture = null; }
    }

    /// <summary>The interface culture: the language chosen in the settings, else the thread's UI culture.</summary>
    internal static CultureInfo UiCulture => OverrideCulture ?? CultureInfo.CurrentUICulture;

    private static string Get(string key)
    {
        var culture = UiCulture;
        try { return _rm.GetString(key, culture) ?? key; }
        catch { return key; }
    }

    /// <summary>
    /// A resource by its name, for the names a declarative description carries as data (the settings
    /// schema's labels, hints and titles). Everything else reads its typed accessor.
    /// </summary>
    internal static string ByName(string key) => Get(key);

    // ── UI Labels ──────────────────────────────────────────────────────────────
    public static string LabelLanguage        => Get(nameof(LabelLanguage));
    public static string HintLanguage         => Get(nameof(HintLanguage));
    /// <summary>The automatic language entry, naming the language it resolves to — {0}=that language's own name.</summary>
    public static string LangAuto(string language) => string.Format(Get(nameof(LangAuto)), language);
    public static string LabelProvider        => Get(nameof(LabelProvider));
    public static string HintProvider         => Get(nameof(HintProvider));
    public static string LabelApiKey          => Get(nameof(LabelApiKey));
    public static string HintApiKey           => Get(nameof(HintApiKey));
    public static string LabelUrl             => Get(nameof(LabelUrl));
    public static string HintUrl              => Get(nameof(HintUrl));
    public static string LabelChatModel       => Get(nameof(LabelChatModel));
    public static string HintChatModel        => Get(nameof(HintChatModel));
    public static string BtnTest              => Get(nameof(BtnTest));
    public static string BtnSave              => Get(nameof(BtnSave));
    // ── Settings panel: pages, sections, chrome ─────────────────────────────────
    public static string SettingsPageServer => Get(nameof(SettingsPageServer));
    public static string SettingsPageServerDesc => Get(nameof(SettingsPageServerDesc));
    public static string SettingsPageAgent => Get(nameof(SettingsPageAgent));
    public static string SettingsPageAgentDesc => Get(nameof(SettingsPageAgentDesc));
    public static string SettingsPageContext => Get(nameof(SettingsPageContext));
    public static string SettingsPageContextDesc => Get(nameof(SettingsPageContextDesc));
    public static string SettingsPageSearch => Get(nameof(SettingsPageSearch));
    public static string SettingsPageSearchDesc => Get(nameof(SettingsPageSearchDesc));
    public static string SettingsPageAutocomplete => Get(nameof(SettingsPageAutocomplete));
    public static string SettingsPageAutocompleteDesc => Get(nameof(SettingsPageAutocompleteDesc));
    public static string SettingsPageTools => Get(nameof(SettingsPageTools));
    public static string SettingsPageToolsDesc => Get(nameof(SettingsPageToolsDesc));
    public static string SettingsPageAppearance => Get(nameof(SettingsPageAppearance));
    public static string SettingsPageAppearanceDesc => Get(nameof(SettingsPageAppearanceDesc));
    public static string SettingsSearchPlaceholder => Get(nameof(SettingsSearchPlaceholder));
    public static string SettingsSearchNoMatch => Get(nameof(SettingsSearchNoMatch));
    /// <summary>How many fields differ from the saved configuration — {0}=count.</summary>
    public static string SettingsUnsavedChanges(int count) => string.Format(Get(nameof(SettingsUnsavedChanges)), count);
    /// <summary>The raw template of <see cref="SettingsUnsavedChanges"/>, filled by the VS Code panel.</summary>
    public static string SettingsUnsavedChangesTemplate => Get(nameof(SettingsUnsavedChanges));
    public static string SettingsNoUnsavedChanges => Get(nameof(SettingsNoUnsavedChanges));
    public static string SettingsCancel => Get(nameof(SettingsCancel));
    public static string SettingsShowAdvanced => Get(nameof(SettingsShowAdvanced));
    /// <summary>The approval rules in effect, counted — {0}=count.</summary>
    public static string SettingsApprovalRulesCount(int count) => string.Format(Get(nameof(SettingsApprovalRulesCount)), count);
    /// <summary>The raw template of <see cref="SettingsApprovalRulesCount"/>, filled by the VS Code panel.</summary>
    public static string SettingsApprovalRulesCountTemplate => Get(nameof(SettingsApprovalRulesCount));
    /// <summary>Where the rules in effect come from — {0}=this machine's, {1}=the team's.</summary>
    public static string SettingsApprovalRulesFrom(int machine, int team) =>
        string.Format(Get(nameof(SettingsApprovalRulesFrom)), machine, team);
    public static string SettingsEditRules => Get(nameof(SettingsEditRules));
    public static string SettingsEditAsJson => Get(nameof(SettingsEditAsJson));
    public static string SettingsEditAsText => Get(nameof(SettingsEditAsText));
    public static string SettingsEditAsList => Get(nameof(SettingsEditAsList));

    // ── Settings: MCP server cards ───────────────────────────────────────────
    public static string McpCardConnected(int tools) => string.Format(Get(nameof(McpCardConnected)), tools);
    public static string McpCardSignIn => Get(nameof(McpCardSignIn));
    public static string McpCardDidNotStart => Get(nameof(McpCardDidNotStart));
    public static string McpCardNotStarted => Get(nameof(McpCardNotStarted));
    public static string McpCardOff => Get(nameof(McpCardOff));
    public static string McpCardRetry => Get(nameof(McpCardRetry));
    public static string McpCardSignInButton => Get(nameof(McpCardSignInButton));
    public static string McpCardsSummary(int count) => string.Format(Get(nameof(McpCardsSummary)), count);
    public static string McpCardsSummaryAttention(int count, int attention) =>
        string.Format(Get(nameof(McpCardsSummaryAttention)), count, attention);

    // ── Settings: approval rules table ───────────────────────────────────────
    public static string RuleAllow => Get(nameof(RuleAllow));
    public static string RuleDeny => Get(nameof(RuleDeny));
    public static string RuleFromTeam => Get(nameof(RuleFromTeam));
    public static string RuleFromMachine => Get(nameof(RuleFromMachine));
    public static string RuleIgnoredTeamAllow => Get(nameof(RuleIgnoredTeamAllow));
    public static string RuleUnreadable => Get(nameof(RuleUnreadable));
    public static string RulesColEffect => Get(nameof(RulesColEffect));
    public static string RulesColTool => Get(nameof(RulesColTool));
    public static string RulesColPattern => Get(nameof(RulesColPattern));
    public static string RulesColFrom => Get(nameof(RulesColFrom));
    public static string RulesAddRule => Get(nameof(RulesAddRule));
    public static string RulesTeamUnusable => Get(nameof(RulesTeamUnusable));
    public static string RulesEmpty => Get(nameof(RulesEmpty));
    public static string RulesPatternPlaceholder => Get(nameof(RulesPatternPlaceholder));
    public static string RulesNewInvalid => Get(nameof(RulesNewInvalid));
    public static string ListLineNotRead => Get(nameof(ListLineNotRead));
    public static string PinnedOverCap => Get(nameof(PinnedOverCap));

    // ── Chat panel: run summary, approval card, header, composer, welcome ────────
    public static string RunSteps1 => Get(nameof(RunSteps1));
    public static string RunSteps(int count) => string.Format(Get(nameof(RunSteps)), count);
    public static string RunRead1 => Get(nameof(RunRead1));
    public static string RunRead(int count) => string.Format(Get(nameof(RunRead)), count);
    public static string RunSearched1 => Get(nameof(RunSearched1));
    public static string RunSearched(int count) => string.Format(Get(nameof(RunSearched)), count);
    public static string RunEdited1 => Get(nameof(RunEdited1));
    public static string RunEdited(int count) => string.Format(Get(nameof(RunEdited)), count);
    public static string RunCommand1 => Get(nameof(RunCommand1));
    public static string RunCommands(int count) => string.Format(Get(nameof(RunCommands)), count);
    public static string RunBuildPassed => Get(nameof(RunBuildPassed));
    public static string RunBuildFailed => Get(nameof(RunBuildFailed));
    public static string RunTestsPassed => Get(nameof(RunTestsPassed));
    public static string RunTestsFailed => Get(nameof(RunTestsFailed));
    public static string TurnSeconds(int seconds) => string.Format(Get(nameof(TurnSeconds)), seconds);
    public static string TurnMinutes(int minutes, string seconds) => string.Format(Get(nameof(TurnMinutes)), minutes, seconds);
    public static string ApprovalCreateFile => Get(nameof(ApprovalCreateFile));
    public static string ApprovalChangeFile => Get(nameof(ApprovalChangeFile));
    public static string ApprovalDeleteFile => Get(nameof(ApprovalDeleteFile));
    public static string ApprovalRunCommand => Get(nameof(ApprovalRunCommand));
    public static string ApprovalReadPage => Get(nameof(ApprovalReadPage));
    public static string ApprovalSearchWeb => Get(nameof(ApprovalSearchWeb));
    public static string ApprovalUseTool(string tool) => string.Format(Get(nameof(ApprovalUseTool)), tool);
    public static string ApprovalLineCount(int count) => string.Format(Get(nameof(ApprovalLineCount)), count);
    public static string ApprovalMoreLines(int count) => string.Format(Get(nameof(ApprovalMoreLines)), count);
    public static string ApprovalAlwaysTooltip(string tool) => string.Format(Get(nameof(ApprovalAlwaysTooltip)), tool);
    public static string ApprovalOpenDiff => Get(nameof(ApprovalOpenDiff));
    public static string ChatNewConversation => Get(nameof(ChatNewConversation));
    public static string ChatConversations => Get(nameof(ChatConversations));
    public static string ChatMore => Get(nameof(ChatMore));
    public static string ChatMenuSearch => Get(nameof(ChatMenuSearch));
    public static string ChatMenuExport => Get(nameof(ChatMenuExport));
    public static string ChatMenuXray => Get(nameof(ChatMenuXray));
    public static string ChatMenuSettings => Get(nameof(ChatMenuSettings));
    public static string ChatMenuStepMode => Get(nameof(ChatMenuStepMode));
    public static string ChatNoModelListed => Get(nameof(ChatNoModelListed));
    public static string ChatModelButton(string model) => string.Format(Get(nameof(ChatModelButton)), model);
    public static string ModeChat => Get(nameof(ModeChat));
    public static string ModeAgent => Get(nameof(ModeAgent));
    public static string ModePlan => Get(nameof(ModePlan));
    public static string ModeChatTip => Get(nameof(ModeChatTip));
    public static string ModeAgentTip => Get(nameof(ModeAgentTip));
    public static string ModePlanTip => Get(nameof(ModePlanTip));
    public static string ComposerPlaceholder => Get(nameof(ComposerPlaceholder));
    public static string ChatAttach => Get(nameof(ChatAttach));
    public static string ContextRingTip(int percent, string window) => string.Format(Get(nameof(ContextRingTip)), percent, window);
    public static string TurnWorking(int step) => string.Format(Get(nameof(TurnWorking)), step);
    public static string TurnWaiting => Get(nameof(TurnWaiting));
    public static string RunUndo => Get(nameof(RunUndo));
    public static string WelcomeTitle => Get(nameof(WelcomeTitle));
    public static string WelcomeLine(string model, string server) => string.Format(Get(nameof(WelcomeLine)), model, server);
    public static string WelcomeLocal => Get(nameof(WelcomeLocal));
    public static string WelcomeForFile(string file) => string.Format(Get(nameof(WelcomeForFile)), file);
    public static string WelcomeExplainFile => Get(nameof(WelcomeExplainFile));
    public static string WelcomeExplainFileDesc => Get(nameof(WelcomeExplainFileDesc));
    public static string WelcomeTestsFile => Get(nameof(WelcomeTestsFile));
    public static string WelcomeTestsFileDesc => Get(nameof(WelcomeTestsFileDesc));
    public static string WelcomeUsagesFile => Get(nameof(WelcomeUsagesFile));
    public static string WelcomeUsagesFileDesc => Get(nameof(WelcomeUsagesFileDesc));
    public static string WelcomeUsagesPrompt(string file) => string.Format(Get(nameof(WelcomeUsagesPrompt)), file);
    public static string WelcomeBuildFailed(int errors) => string.Format(Get(nameof(WelcomeBuildFailed)), errors);
    public static string WelcomeBuildFailedUncounted => Get(nameof(WelcomeBuildFailedUncounted));
    public static string WelcomeFixThem => Get(nameof(WelcomeFixThem));
    public static string WelcomeHintAttach => Get(nameof(WelcomeHintAttach));
    public static string WelcomeHintCommands => Get(nameof(WelcomeHintCommands));
    public static string WelcomeHintNewLine => Get(nameof(WelcomeHintNewLine));

    // ── Settings: what an empty model choice means, and the model a page uses ─
    public static string SettingsSameAsChat => Get(nameof(SettingsSameAsChat));
    public static string SettingsSameAsCodeActions => Get(nameof(SettingsSameAsCodeActions));
    public static string SettingsAutomaticBest => Get(nameof(SettingsAutomaticBest));
    public static string SettingsModelUsed(string model) => string.Format(Get(nameof(SettingsModelUsed)), model);
    public static string SettingsChangeInServer => Get(nameof(SettingsChangeInServer));
    public static string SlashColCommand => Get(nameof(SlashColCommand));
    public static string SlashColSends => Get(nameof(SlashColSends));
    public static string ToolColName => Get(nameof(ToolColName));
    public static string ToolColRuns => Get(nameof(ToolColRuns));
    public static string SettingsSectionAsYouType => Get(nameof(SettingsSectionAsYouType));
    public static string SettingsSectionDocs => Get(nameof(SettingsSectionDocs));
    public static string SettingsSectionDocsDesc => Get(nameof(SettingsSectionDocsDesc));
    public static string SettingsDocsNote => Get(nameof(SettingsDocsNote));
    public static string SettingsSectionThisConversation => Get(nameof(SettingsSectionThisConversation));
    public static string SettingsEditFile => Get(nameof(SettingsEditFile));
    public static string SettingsOpenFile => Get(nameof(SettingsOpenFile));

    // ── Settings: the index card (Code search) ───────────────────────────────
    public static string IndexCardReady => Get(nameof(IndexCardReady));
    public static string IndexCardIndexing => Get(nameof(IndexCardIndexing));
    public static string IndexCardNotBuilt => Get(nameof(IndexCardNotBuilt));
    public static string IndexCardStopped => Get(nameof(IndexCardStopped));
    public static string IndexCardFailed => Get(nameof(IndexCardFailed));
    public static string IndexCardNoWorkspace => Get(nameof(IndexCardNoWorkspace));
    public static string IndexCardNoWorkspaceDetail => Get(nameof(IndexCardNoWorkspaceDetail));
    public static string IndexCardFilesUpdated(string files, string at) => string.Format(Get(nameof(IndexCardFilesUpdated)), files, at);
    public static string IndexCardProgress(string done, string total) => string.Format(Get(nameof(IndexCardProgress)), done, total);
    public static string IndexCardNotBuiltDetail => Get(nameof(IndexCardNotBuiltDetail));
    public static string IndexCardStoppedDetail => Get(nameof(IndexCardStoppedDetail));
    public static string IndexCardFailedDetail(string cause) => string.Format(Get(nameof(IndexCardFailedDetail)), cause);
    public static string IndexCardRebuild => Get(nameof(IndexCardRebuild));
    public static string IndexCardBuild => Get(nameof(IndexCardBuild));
    public static string IndexCardOversize(int count, int kilobytes) => string.Format(Get(nameof(IndexCardOversize)), count, kilobytes);
    public static string IndexCardShowThem => Get(nameof(IndexCardShowThem));
    public static string IndexCardHideThem => Get(nameof(IndexCardHideThem));
    public static string IndexCardMoreFiles(int count) => string.Format(Get(nameof(IndexCardMoreFiles)), count);
    public static string IndexCardHoles(string holes, string total) => string.Format(Get(nameof(IndexCardHoles)), holes, total);
    public static string IndexCardModelSemantic(string model) => string.Format(Get(nameof(IndexCardModelSemantic)), model);
    public static string IndexCardModelKeywords => Get(nameof(IndexCardModelKeywords));
    public static string IndexCardModelOff => Get(nameof(IndexCardModelOff));
    public static string IndexCardEmbeddingDown => Get(nameof(IndexCardEmbeddingDown));
    public static string IndexExclusionsTitle => Get(nameof(IndexExclusionsTitle));
    public static string IndexExclusionsFrom => Get(nameof(IndexExclusionsFrom));
    public static string IndexExclusionsHowTo => Get(nameof(IndexExclusionsHowTo));

    // ── Settings: @Docs sites ────────────────────────────────────────────────
    public static string DocsSiteIndexed(int pages) => string.Format(Get(nameof(DocsSiteIndexed)), pages);
    public static string DocsSiteHoles(int pages, int holes) => string.Format(Get(nameof(DocsSiteHoles)), pages, holes);
    public static string DocsSiteHolesNote(int holes) => string.Format(Get(nameof(DocsSiteHolesNote)), holes);
    public static string DocsSiteIndexing => Get(nameof(DocsSiteIndexing));
    public static string DocsReindex => Get(nameof(DocsReindex));
    public static string DocsAddSite => Get(nameof(DocsAddSite));
    public static string DocsAddUrlLabel => Get(nameof(DocsAddUrlLabel));
    public static string DocsAddButton => Get(nameof(DocsAddButton));
    public static string DocsRemoveSite(string title) => string.Format(Get(nameof(DocsRemoveSite)), title);
    public static string DocsNoSitesYet => Get(nameof(DocsNoSitesYet));

    // ── Settings: this conversation, pinned files, the project's files ───────
    public static string ContextUsageTokens(string used, string window) => string.Format(Get(nameof(ContextUsageTokens)), used, window);
    public static string ContextUsageOpenXray => Get(nameof(ContextUsageOpenXray));
    public static string ContextUsageInstructions => Get(nameof(ContextUsageInstructions));
    public static string ContextUsageTools => Get(nameof(ContextUsageTools));
    public static string ContextUsageConversation => Get(nameof(ContextUsageConversation));
    public static string ContextUsageChangeWindow => Get(nameof(ContextUsageChangeWindow));
    public static string ContextUsageNoChat => Get(nameof(ContextUsageNoChat));
    public static string PinnedFilesCount(int count, int cap) => string.Format(Get(nameof(PinnedFilesCount)), count, cap);
    public static string PinnedFileTokens(string tokens) => string.Format(Get(nameof(PinnedFileTokens)), tokens);
    public static string PinnedFileMissing => Get(nameof(PinnedFileMissing));
    public static string ProjectFilesTitle => Get(nameof(ProjectFilesTitle));
    public static string ProjectFileContext => Get(nameof(ProjectFileContext));
    public static string ProjectFileMemory => Get(nameof(ProjectFileMemory));
    public static string ProjectFileNotes => Get(nameof(ProjectFileNotes));
    public static string ProjectFileRules(int count) => string.Format(Get(nameof(ProjectFileRules)), count);
    public static string ProjectFileNotYet => Get(nameof(ProjectFileNotYet));

    public static string SettingsSectionModelPerTask => Get(nameof(SettingsSectionModelPerTask));
    public static string SettingsSectionModelPerTaskDesc => Get(nameof(SettingsSectionModelPerTaskDesc));
    public static string SettingsSectionSampling => Get(nameof(SettingsSectionSampling));
    public static string SettingsSectionGpu => Get(nameof(SettingsSectionGpu));
    public static string SettingsSectionHowItWorks => Get(nameof(SettingsSectionHowItWorks));
    public static string SettingsSectionApprovals => Get(nameof(SettingsSectionApprovals));
    public static string SettingsSectionInstructions => Get(nameof(SettingsSectionInstructions));
    public static string SettingsSectionTimeLimits => Get(nameof(SettingsSectionTimeLimits));
    public static string SettingsTimeLimitsNote => Get(nameof(SettingsTimeLimitsNote));
    public static string SettingsSectionLongConversations => Get(nameof(SettingsSectionLongConversations));
    public static string SettingsSectionAlwaysInPrompt => Get(nameof(SettingsSectionAlwaysInPrompt));
    public static string SettingsSectionAlwaysInPromptDesc => Get(nameof(SettingsSectionAlwaysInPromptDesc));
    public static string SettingsSectionYourCode => Get(nameof(SettingsSectionYourCode));
    public static string SettingsSectionEditWithAi => Get(nameof(SettingsSectionEditWithAi));
    public static string SettingsSectionEditWithAiDesc => Get(nameof(SettingsSectionEditWithAiDesc));
    public static string SettingsSectionRules => Get(nameof(SettingsSectionRules));
    public static string SettingsSectionRulesDesc => Get(nameof(SettingsSectionRulesDesc));
    public static string SettingsSectionSlash => Get(nameof(SettingsSectionSlash));
    public static string SettingsSectionSlashDesc => Get(nameof(SettingsSectionSlashDesc));
    public static string SettingsSectionAgentTools => Get(nameof(SettingsSectionAgentTools));
    public static string SettingsSectionAgentToolsDesc => Get(nameof(SettingsSectionAgentToolsDesc));
    public static string SettingsSectionAgentRuns => Get(nameof(SettingsSectionAgentRuns));
    public static string LabelUseRecommendedSampling => Get(nameof(LabelUseRecommendedSampling));
    public static string HintUseRecommendedSampling => Get(nameof(HintUseRecommendedSampling));
    public static string LabelAskBeforeActions => Get(nameof(LabelAskBeforeActions));
    public static string HintAskBeforeActions => Get(nameof(HintAskBeforeActions));
    public static string LabelInlineDiffPreview => Get(nameof(LabelInlineDiffPreview));
    public static string HintInlineDiffPreview => Get(nameof(HintInlineDiffPreview));

    /// <summary>What the save could not read, named — {0}=count, {1}=field labels.</summary>
    public static string SettingsFieldsIgnored(int count, string labels) =>
        string.Format(Get(nameof(SettingsFieldsIgnored)), count, labels);

    /// <summary>
    /// The raw template of <see cref="SettingsFieldsIgnored"/>, for the front-end that substitutes
    /// on its own: the VS Code panel receives it over <c>settings/strings</c> and fills it inside
    /// the webview, where it knows the fields. The sentence stays the same on both sides.
    /// </summary>
    public static string SettingsFieldsIgnoredTemplate => Get(nameof(SettingsFieldsIgnored));

    // ⚠ A DIFFERENT fact from SettingsFieldsIgnored: the field itself IS saved — it is some of its
    // lines that are inert. Confusing the two would tell the user they lost what they typed while
    // it is right there. Said AT SAVE TIME, because nobody opens /diagnostics after writing a rule
    // they believe they just put in place.
    public static string SettingsPermissionRulesIgnored(int count) =>
        string.Format(Get(nameof(SettingsPermissionRulesIgnored)), count);

    /// <inheritdoc cref="SettingsFieldsIgnoredTemplate"/>
    public static string SettingsPermissionRulesIgnoredTemplate => Get(nameof(SettingsPermissionRulesIgnored));
    public static string BtnCancel            => Get(nameof(BtnCancel));
    public static string BtnSend              => Get(nameof(BtnSend));
    public static string TooltipRefreshModels => Get(nameof(TooltipRefreshModels));
    public static string TooltipCopy              => Get(nameof(TooltipCopy));
    public static string LabelCopyCode            => Get(nameof(LabelCopyCode));
    public static string BtnFixWithAi             => Get(nameof(BtnFixWithAi));
    public static string BtnRegenerate            => Get(nameof(BtnRegenerate));
    public static string BtnResume                => Get(nameof(BtnResume));

    public static string PromptFixErrors(string errors) =>
        string.Format(Get(nameof(PromptFixErrors)), errors);

    // ── Editor context menu prompts ────────────────────────────────────────────
    // Legacy two-arg overloads kept for reference; all active call sites use the
    // attachment-based single-arg variants below.

    // ── Code-action prompts — code delivered as AttachmentItem ─────────────────
    // {1} (code block) is intentionally omitted: the file is attached separately so
    // it appears as a chip in the chat and is formatted uniformly by SendCoreAsync.
    public static string PromptExplain(string fileName) =>
        string.Format(Get("PromptExplainSelection"), fileName, string.Empty).TrimEnd();
    public static string PromptReview(string fileName) =>
        string.Format(Get("PromptReviewSelection"), fileName, string.Empty).TrimEnd();

    // ── /test — tests generated into a separate file ───────────────────────────
    public static string TestsGenerated(string fileName) =>
        string.Format(Get(nameof(TestsGenerated)), fileName);
    public static string TestsExtended(string fileName) =>
        string.Format(Get(nameof(TestsExtended)), fileName);
    public static string TestsGenerateFailed => Get(nameof(TestsGenerateFailed));
    public static string TestsFileUnreadable(string fileName) =>
        string.Format(Get(nameof(TestsFileUnreadable)), fileName);

    // ── Code actions: "nothing to do" verdicts (the code is already good) ───────
    public static string RefactorNoChange => Get(nameof(RefactorNoChange));
    public static string FixNoChange      => Get(nameof(FixNoChange));
    public static string DocNoChange      => Get(nameof(DocNoChange));
    public static string InlineEditNoChange => Get(nameof(InlineEditNoChange));
    public static string TestsNoChange    => Get(nameof(TestsNoChange));
    /// <summary>Shown when an in-place code action fails (model/network error, empty reply).</summary>
    public static string CodeActionFailed => Get(nameof(CodeActionFailed));
    public static string CodeActionDocumentChanged => Get(nameof(CodeActionDocumentChanged));

    public static string LabelCommandTimeout      => Get(nameof(LabelCommandTimeout));
    public static string HintCommandTimeout       => Get(nameof(HintCommandTimeout));
    public static string LabelToolBubblesExpanded      => Get(nameof(LabelToolBubblesExpanded));
    public static string HintToolBubblesExpanded       => Get(nameof(HintToolBubblesExpanded));
    public static string LabelDensity                  => Get(nameof(LabelDensity));
    public static string HintDensity                   => Get(nameof(HintDensity));
    public static string DensityComfortable            => Get(nameof(DensityComfortable));
    public static string DensityCompact                => Get(nameof(DensityCompact));
    public static string SettingsSectionTheme          => Get(nameof(SettingsSectionTheme));
    public static string SettingsThemeNote             => Get(nameof(SettingsThemeNote));
    public static string ThemeLight                    => Get(nameof(ThemeLight));
    public static string ThemeDark                     => Get(nameof(ThemeDark));
    public static string ThemeHighContrast             => Get(nameof(ThemeHighContrast));
    public static string ThemeInUse(string theme)      => string.Format(Get(nameof(ThemeInUse)), theme);
    public static string LabelContextWindowSize      => Get(nameof(LabelContextWindowSize));
    public static string HintContextWindowSize       => Get(nameof(HintContextWindowSize));
    public static string HintContextWindowSizeClientTrim => Get(nameof(HintContextWindowSizeClientTrim));
    public static string LabelContextWindowKeepTurns => Get(nameof(LabelContextWindowKeepTurns));
    public static string HintContextWindowKeepTurns  => Get(nameof(HintContextWindowKeepTurns));
    public static string LabelVramBudget             => Get(nameof(LabelVramBudget));
    public static string HintVramBudget              => Get(nameof(HintVramBudget));
    public static string LabelCustomSystemPrompt     => Get(nameof(LabelCustomSystemPrompt));
    public static string HintCustomSystemPrompt      => Get(nameof(HintCustomSystemPrompt));
    public static string LabelPinnedContextFiles     => Get(nameof(LabelPinnedContextFiles));
    public static string HintPinnedContextFiles      => Get(nameof(HintPinnedContextFiles));
    public static string LabelPromptTemplates        => Get(nameof(LabelPromptTemplates));
    public static string HintPromptTemplates         => Get(nameof(HintPromptTemplates));
    public static string LabelCustomTools            => Get(nameof(LabelCustomTools));
    public static string HintCustomTools             => Get(nameof(HintCustomTools));
    public static string LabelPermissionRules        => Get(nameof(LabelPermissionRules));
    public static string HintPermissionRules         => Get(nameof(HintPermissionRules));
    // ── Settings — editable lists (pinned files / slash commands / custom tools) ──
    public static string HintRowEdit                 => Get(nameof(HintRowEdit));
    public static string HintRowDelete               => Get(nameof(HintRowDelete));
    public static string BtnRowImport                => Get(nameof(BtnRowImport));
    public static string RowEditTitle(string name)   => string.Format(Get(nameof(RowEditTitle)), name);
    public static string PinnedAddFile               => Get(nameof(PinnedAddFile));
    public static string PinnedAddTitle              => Get(nameof(PinnedAddTitle));
    public static string LabelPinnedPath             => Get(nameof(LabelPinnedPath));
    public static string PinnedBrowse                => Get(nameof(PinnedBrowse));
    public static string PinnedPickerTitle           => Get(nameof(PinnedPickerTitle));
    public static string PinnedValidationPath        => Get(nameof(PinnedValidationPath));
    public static string PinnedValidationDuplicate   => Get(nameof(PinnedValidationDuplicate));
    public static string SlashAddCmd                 => Get(nameof(SlashAddCmd));
    public static string SlashAddTitle               => Get(nameof(SlashAddTitle));
    public static string LabelSlashName              => Get(nameof(LabelSlashName));
    public static string LabelSlashText              => Get(nameof(LabelSlashText));
    public static string SlashValidationNameText     => Get(nameof(SlashValidationNameText));
    public static string SlashValidationDuplicate    => Get(nameof(SlashValidationDuplicate));
    public static string ToolAddTool                 => Get(nameof(ToolAddTool));
    public static string ToolAddTitle                => Get(nameof(ToolAddTitle));
    public static string LabelToolName               => Get(nameof(LabelToolName));
    public static string LabelToolCommand            => Get(nameof(LabelToolCommand));
    public static string ToolValidationNameCommand   => Get(nameof(ToolValidationNameCommand));
    public static string ToolValidationDuplicate     => Get(nameof(ToolValidationDuplicate));
    public static string LabelPersonaAutoSwitch      => Get(nameof(LabelPersonaAutoSwitch));
    public static string HintPersonaAutoSwitch       => Get(nameof(HintPersonaAutoSwitch));
    public static string LabelOodaTurnThreshold      => Get(nameof(LabelOodaTurnThreshold));
    public static string HintOodaTurnThreshold       => Get(nameof(HintOodaTurnThreshold));
    public static string LabelCompactionEnabled      => Get(nameof(LabelCompactionEnabled));
    public static string HintCompactionEnabled       => Get(nameof(HintCompactionEnabled));
    public static string LabelCompactionTimeout      => Get(nameof(LabelCompactionTimeout));
    public static string HintCompactionTimeout       => Get(nameof(HintCompactionTimeout));
    public static string LabelKvCacheAnchor          => Get(nameof(LabelKvCacheAnchor));
    public static string HintKvCacheAnchor           => Get(nameof(HintKvCacheAnchor));
    public static string SectionConnection              => Get(nameof(SectionConnection));
    public static string LabelInlineCompletionMode           => Get(nameof(LabelInlineCompletionMode));
    // The three speed cards: a name, translated like every other word of the panel, and a description that
    // carries the preset's delay — the promise the card makes. SettingsSchemaDriftTests checks the delay is in the
    // description, language by language.
    public static string FimModeFast                         => Get(nameof(FimModeFast));
    public static string FimModeDefault                      => Get(nameof(FimModeDefault));
    public static string FimModeHighAccuracy                 => Get(nameof(FimModeHighAccuracy));
    public static string FimModeFastDesc                     => Get(nameof(FimModeFastDesc));
    public static string FimModeDefaultDesc                  => Get(nameof(FimModeDefaultDesc));
    public static string FimModeHighAccuracyDesc             => Get(nameof(FimModeHighAccuracyDesc));
    public static string LabelInlineCompletionEnabled        => Get(nameof(LabelInlineCompletionEnabled));
    public static string HintInlineCompletionEnabled         => Get(nameof(HintInlineCompletionEnabled));
    public static string LabelInlineCompletionModel          => Get(nameof(LabelInlineCompletionModel));
    public static string HintInlineCompletionModel           => Get(nameof(HintInlineCompletionModel));
    public static string LabelCodeActionsModel               => Get(nameof(LabelCodeActionsModel));
    public static string HintCodeActionsModel                => Get(nameof(HintCodeActionsModel));
    public static string LabelInlineEditModel                => Get(nameof(LabelInlineEditModel));
    public static string HintInlineEditModel                 => Get(nameof(HintInlineEditModel));
    public static string LabelAgentModel                     => Get(nameof(LabelAgentModel));
    public static string HintAgentModel                      => Get(nameof(HintAgentModel));
    public static string LabelUtilityModel                   => Get(nameof(LabelUtilityModel));
    public static string HintUtilityModel                    => Get(nameof(HintUtilityModel));
    public static string LabelModelRouterAuto                => Get(nameof(LabelModelRouterAuto));
    public static string HintModelRouterAuto                 => Get(nameof(HintModelRouterAuto));
    public static string WelcomeCardProject => Get(nameof(WelcomeCardProject));
    public static string WelcomeProjectPrompt => Get(nameof(WelcomeProjectPrompt));
    public static string WelcomeCardChanges => Get(nameof(WelcomeCardChanges));
    public static string WelcomeChangesPrompt => Get(nameof(WelcomeChangesPrompt));
    public static string WelcomeOpenFileHint => Get(nameof(WelcomeOpenFileHint));
    public static string WelcomeCardHelp                     => Get(nameof(WelcomeCardHelp));
    public static string BuildBannerDismiss                  => Get(nameof(BuildBannerDismiss));
    public static string InlineEditDlgTitle                  => Get(nameof(InlineEditDlgTitle));
    public static string InlineEditDlgHeader                 => Get(nameof(InlineEditDlgHeader));
    public static string InlineEditDlgHint                   => Get(nameof(InlineEditDlgHint));
    public static string InlineEditWorking                   => Get(nameof(InlineEditWorking));
    public static string BtnApply                            => Get(nameof(BtnApply));

    // ── Settings — RAG section ───────────────────────────────────────────────────
    public static string LabelRagEnabled        => Get(nameof(LabelRagEnabled));
    public static string HintRagEnabled         => Get(nameof(HintRagEnabled));
    public static string LabelRagAutoContext    => Get(nameof(LabelRagAutoContext));
    public static string HintRagAutoContext     => Get(nameof(HintRagAutoContext));
    public static string LabelRagEmbeddingModel => Get(nameof(LabelRagEmbeddingModel));
    public static string HintRagEmbeddingModel  => Get(nameof(HintRagEmbeddingModel));
    public static string LabelRagTopK                  => Get(nameof(LabelRagTopK));
    /// <summary>Units of the numeric settings fields: resource names of the schema, served to both front-ends.</summary>
    public static string UnitSeconds                   => Get(nameof(UnitSeconds));
    /// <inheritdoc cref="UnitSeconds"/>
    public static string UnitMinutes                   => Get(nameof(UnitMinutes));
    /// <inheritdoc cref="UnitSeconds"/>
    public static string UnitGigabytes                 => Get(nameof(UnitGigabytes));
    /// <inheritdoc cref="UnitSeconds"/>
    public static string UnitTokens                    => Get(nameof(UnitTokens));
    /// <inheritdoc cref="UnitSeconds"/>
    public static string UnitTurns                     => Get(nameof(UnitTurns));
    /// <inheritdoc cref="UnitSeconds"/>
    public static string UnitMessages                  => Get(nameof(UnitMessages));
    /// <inheritdoc cref="UnitSeconds"/>
    public static string UnitChunks                    => Get(nameof(UnitChunks));
    /// <inheritdoc cref="UnitSeconds"/>
    public static string UnitIterations                => Get(nameof(UnitIterations));
    /// <inheritdoc cref="UnitSeconds"/>
    public static string UnitRangeZeroToOne            => Get(nameof(UnitRangeZeroToOne));
    public static string HintRagTopK                   => Get(nameof(HintRagTopK));
    public static string LabelRagSimilarityThreshold   => Get(nameof(LabelRagSimilarityThreshold));
    public static string HintRagSimilarityThreshold    => Get(nameof(HintRagSimilarityThreshold));
    public static string LabelLspEnabled        => Get(nameof(LabelLspEnabled));
    public static string HintLspEnabled         => Get(nameof(HintLspEnabled));
    public static string SectionMcp             => Get(nameof(SectionMcp));
    public static string LabelMcpEnabled        => Get(nameof(LabelMcpEnabled));
    public static string HintMcpEnabled         => Get(nameof(HintMcpEnabled));
    public static string LabelMcpServers        => Get(nameof(LabelMcpServers));
    public static string HintMcpServers         => Get(nameof(HintMcpServers));
    public static string McpCancelled           => Get(nameof(McpCancelled));
    public static string McpAddServer                => Get(nameof(McpAddServer));
    public static string McpAddTitle                 => Get(nameof(McpAddTitle));
    public static string McpEditTitle(string name)   => string.Format(Get(nameof(McpEditTitle)), name);
    public static string LabelMcpName                => Get(nameof(LabelMcpName));
    public static string LabelMcpCommand             => Get(nameof(LabelMcpCommand));
    public static string LabelMcpArgs                => Get(nameof(LabelMcpArgs));
    public static string LabelMcpEnv                 => Get(nameof(LabelMcpEnv));
    public static string LabelMcpHttpServer          => Get(nameof(LabelMcpHttpServer));
    public static string LabelMcpUrl                 => Get(nameof(LabelMcpUrl));
    public static string LabelMcpHeaders             => Get(nameof(LabelMcpHeaders));
    public static string BtnMcpSaveServer            => Get(nameof(BtnMcpSaveServer));
    public static string BtnMcpCancelServer          => Get(nameof(BtnMcpCancelServer));
    public static string McpImportJson               => Get(nameof(McpImportJson));
    public static string McpJsonNotEditableAsList    => Get(nameof(McpJsonNotEditableAsList));
    public static string McpValidationNameCommand    => Get(nameof(McpValidationNameCommand));
    public static string McpValidationNameUrl        => Get(nameof(McpValidationNameUrl));
    public static string McpValidationDuplicate      => Get(nameof(McpValidationDuplicate));
    public static string HintMcpEditServer           => Get(nameof(HintMcpEditServer));
    public static string HintMcpDeleteServer         => Get(nameof(HintMcpDeleteServer));
    // List view-mode toggle, empty-state titles, and the relocated "Commands & tools" section header.
    public static string McpEmptyTitle               => Get(nameof(McpEmptyTitle));
    public static string PinnedEmptyTitle            => Get(nameof(PinnedEmptyTitle));
    public static string SlashEmptyTitle             => Get(nameof(SlashEmptyTitle));
    public static string ToolEmptyTitle              => Get(nameof(ToolEmptyTitle));
    public static string DocsListHeader              => Get(nameof(DocsListHeader));
    public static string DocsNoSites                 => Get(nameof(DocsNoSites));
    public static string DocsSourcesUnreadable(string detail) =>
        string.Format(Get(nameof(DocsSourcesUnreadable)), detail);
    public static string DocsUnknownId(string id)    => string.Format(Get(nameof(DocsUnknownId)), id);
    public static string DocsUsage                   => Get(nameof(DocsUsage));
    public static string DocsAdded(string title)     => string.Format(Get(nameof(DocsAdded)), title);
    public static string DocsRemoved(string id)      => string.Format(Get(nameof(DocsRemoved)), id);
    public static string DocsReindexing(string label) => string.Format(Get(nameof(DocsReindexing)), label);
    public static string DocsNotReady(string status) => string.Format(Get(nameof(DocsNotReady)), status);
    public static string DocsNoResults(string query) => string.Format(Get(nameof(DocsNoResults)), query);
    public static string StatusOodaSummarizing       => Get(nameof(StatusOodaSummarizing));
    public static string StatusCompacting            => Get(nameof(StatusCompacting));
    public static string OodaSummarizePrompt         => Get(nameof(OodaSummarizePrompt));

    public static string CompactionSummarizePrompt(string conversationText) =>
        string.Format(Get(nameof(CompactionSummarizePrompt)), conversationText);

    public static string MsgContextCompacted(int removed, int keepTurns) =>
        string.Format(Get(nameof(MsgContextCompacted)), removed, keepTurns);

    public static string MsgKvCacheAnchorNote(int count) =>
        string.Format(Get(nameof(MsgKvCacheAnchorNote)), count);

    /// <summary>Names the setting by its current label and its page, both passed in (a copy drifts).</summary>
    public static string MsgContextCompactionFallback =>
        string.Format(Get(nameof(MsgContextCompactionFallback)), LabelCompactionTimeout, SettingsPageContext);
    public static string MsgContextCompactionFailed(string cause) =>
        string.Format(Get(nameof(MsgContextCompactionFailed)), cause);
    public static string MsgContextCompactionEmpty => Get(nameof(MsgContextCompactionEmpty));
    public static string MsgContextSummaryTooLong  => Get(nameof(MsgContextSummaryTooLong));
    public static string MsgContextCompactionOnlyReasoning => Get(nameof(MsgContextCompactionOnlyReasoning));
    public static string MsgContextCompactionRepeating => Get(nameof(MsgContextCompactionRepeating));
    public static string MsgContextSummaryCut => Get(nameof(MsgContextSummaryCut));
    public static string MsgContextSummaryPartial(int omitted, int total) =>
        string.Format(Get(nameof(MsgContextSummaryPartial)), omitted, total);
    public static string MsgOodaRecap(int turn, string summary) =>
        string.Format(Get(nameof(MsgOodaRecap)), turn) + "\n\n" + summary;

    public static string MsgContextTruncated(int removed, int keepTurns) =>
        string.Format(Get(nameof(MsgContextTruncated)), removed, keepTurns);
    public static string TooltipSessionPicker  => Get(nameof(TooltipSessionPicker));
    public static string TooltipLoadSession    => Get(nameof(TooltipLoadSession));
    public static string TooltipDeleteSession  => Get(nameof(TooltipDeleteSession));
    public static string DeleteSessionConfirm(string name) => string.Format(Get(nameof(DeleteSessionConfirm)), name);
    public static string TooltipAttachFile       => Get(nameof(TooltipAttachFile));
    public static string TooltipAttachSelection  => Get(nameof(TooltipAttachSelection));
    public static string TooltipBrowseFile       => Get(nameof(TooltipBrowseFile));
    public static string TooltipPinFile          => Get(nameof(TooltipPinFile));
    public static string MenuAttachFile          => Get(nameof(MenuAttachFile));
    public static string MenuAttachSelection     => Get(nameof(MenuAttachSelection));
    public static string MenuBrowseFile          => Get(nameof(MenuBrowseFile));
    public static string MenuPinFile             => Get(nameof(MenuPinFile));
    public static string TooltipPinChip          => Get(nameof(TooltipPinChip));
    public static string TooltipCloseSearch        => Get(nameof(TooltipCloseSearch));
    public static string TooltipSaveSnippet        => Get(nameof(TooltipSaveSnippet));

    // ── Settings — VRAM / Model lifetime ──────────────────────────────────────
    public static string LabelModelAutoUnload     => Get(nameof(LabelModelAutoUnload));
    public static string HintModelAutoUnload      => Get(nameof(HintModelAutoUnload));
    public static string LabelModelIdleTimeout    => Get(nameof(LabelModelIdleTimeout));
    public static string HintModelIdleTimeout     => Get(nameof(HintModelIdleTimeout));

    // ── Settings — Agent Mode ──────────────────────────────────────────────────
    public static string LabelAgentModeEnabled    => Get(nameof(LabelAgentModeEnabled));
    public static string HintAgentModeEnabled     => Get(nameof(HintAgentModeEnabled));
    public static string LabelAgentMaxIterations  => Get(nameof(LabelAgentMaxIterations));
    public static string HintAgentMaxIterations   => Get(nameof(HintAgentMaxIterations));
    public static string LabelTaskTimeoutQuick    => Get(nameof(LabelTaskTimeoutQuick));
    public static string HintTaskTimeoutQuick     => Get(nameof(HintTaskTimeoutQuick));
    public static string LabelTaskTimeoutNormal   => Get(nameof(LabelTaskTimeoutNormal));
    public static string HintTaskTimeoutNormal    => Get(nameof(HintTaskTimeoutNormal));

    // ── ViewModel ──────────────────────────────────────────────────────────────
    public static string StatusConnecting      => Get(nameof(StatusConnecting));
    public static string StatusConnected       => Get(nameof(StatusConnected));
    public static string StatusUnreachable     => Get(nameof(StatusUnreachable));
    public static string StatusRefused(string refusal) => string.Format(Get(nameof(StatusRefused)), refusal);
    public static string StatusThinking        => Get(nameof(StatusThinking));
    public static string StatusAgentPlanning   => Get(nameof(StatusAgentPlanning));
    public static string StatusAgentObserving  => Get(nameof(StatusAgentObserving));
    public static string StatusAgentSynthesizing => Get(nameof(StatusAgentSynthesizing));

    public static string StatusCallingTool(string toolName) =>
        string.Format(Get(nameof(StatusCallingTool)), toolName);

    // ── Agent Mode ─────────────────────────────────────────────────────────────
    /// <summary>Fallback plan goal when JSON parsing fails.</summary>
    public static string AgentPlanFallbackGoal => Get(nameof(AgentPlanFallbackGoal));
    /// <summary>Single-step description for the fallback plan.</summary>
    public static string AgentPlanFallbackStep => Get(nameof(AgentPlanFallbackStep));
    /// <summary>Label shown above the live plan bubble.</summary>
    public static string AgentPlanLabel => Get(nameof(AgentPlanLabel));

    public static string MsgCancelled          => Get(nameof(MsgCancelled));
    public static string MsgTruncated          => Get(nameof(MsgTruncated));
    public static string DefaultSessionSnippet => Get(nameof(DefaultSessionSnippet));
    public static string MsgIterationLimit(int limit) => string.Format(Get(nameof(MsgIterationLimit)), limit);
    // ⚠ How a run ENDED, when it is not because the model was done. The answer stays — that is the
    // original arbitration, "do not alarm when real work was done" — but it stops passing for a task
    // carried to its end. Both facts lived in OrchestratorResult all along and were read by NOBODY.
    public static string AgentEndedAtIterationLimit => Get(nameof(AgentEndedAtIterationLimit));
    public static string AgentEndedOnRepeat         => Get(nameof(AgentEndedOnRepeat));
    public static string AnswerCutAtLimit           => Get(nameof(AnswerCutAtLimit));
    public static string AgentEditsNotApplied       => Get(nameof(AgentEditsNotApplied));
    public static string AgentLastCheckFailed       => Get(nameof(AgentLastCheckFailed));
    public static string AnswerStoppedRepeating     => Get(nameof(AnswerStoppedRepeating));
    public static string CheckReviewCut             => Get(nameof(CheckReviewCut));
    public static string CommitProposalCut          => Get(nameof(CommitProposalCut));
    public static string ArenaAnswerCut             => Get(nameof(ArenaAnswerCut));
    /// <summary>Under an /arena answer the model never wrote: its reasoning is not shown in its place — a draft would
    /// compete with a written answer in a blind vote, and the model's name stays hidden until the vote.</summary>
    public static string ArenaAnswerOnlyReasoning   => Get(nameof(ArenaAnswerOnlyReasoning));
    public static string MsgLoopDetected      => Get(nameof(MsgLoopDetected));
    public static string MsgCircuitOpen       => Get(nameof(MsgCircuitOpen));
    public static string TokenUsage(string last, string session) =>
        string.Format(Get(nameof(TokenUsage)), last, session);

    public static string MsgAgentDone(string toolSummary) =>
        string.Format(Get(nameof(MsgAgentDone)), toolSummary);

    public static string MsgAgentToolsCalled(string toolSummary) =>
        string.Format(Get(nameof(MsgAgentToolsCalled)), toolSummary);

    public static string MsgNoUrl => Get(nameof(MsgNoUrl));

    /// <summary>
    /// A turn that produced nothing, told by <b>what was observed</b> — {0} = requested model,
    /// {1} = server.
    /// </summary>
    /// <remarks>
    /// ⚠ Replaces <see cref="MsgEmptyResponse"/> on the paths that know the model and the server.
    /// The old text asserted "the configured model may not support text generation — try the
    /// default chat model": a cause the code cannot know, shown at the exact place a user looks to
    /// find out why nothing works. Reported from a machine on the network; measuring the server it
    /// accused showed it serving that very model perfectly, tools and streaming included. The
    /// message therefore sent the user to change a model that was not the cause.
    /// </remarks>
    public static string MsgEmptyResponseFrom(string model, string server) =>
        string.Format(Get(nameof(MsgEmptyResponseFrom)), model, server);
    public static string MsgOnlyReasoningFrom(string model) => string.Format(Get(nameof(MsgOnlyReasoningFrom)), model);

    /// <summary>An in-place rewrite that stopped at the model's length limit: nothing was applied.</summary>
    public static string CodeActionReplyCut => Get(nameof(CodeActionReplyCut));

    /// <summary>A /onboard context draft that stopped at the model's length limit: nothing was written.</summary>
    public static string OnboardContextCut => Get(nameof(OnboardContextCut));

    public static string MsgError(string message) =>
        string.Format(Get(nameof(MsgError)), message);

    public static string MsgTimeout(string url) =>
        string.Format(Get(nameof(MsgTimeout)), url);

    public static string MsgUnreachable(string url) =>
        string.Format(Get(nameof(MsgUnreachable)), url);

    /// <summary>The stream broke after the server had answered (crash, out of memory, restart): reached, so not
    /// "cannot reach… check the URL".</summary>
    public static string MsgStreamDropped(string url) => string.Format(Get(nameof(MsgStreamDropped)), url);

    /// <summary>The server answered the check with a refusal (<paramref name="refusal"/>): running, so not "cannot reach".</summary>
    public static string MsgBackendRefused(string url, string refusal) =>
        string.Format(Get(nameof(MsgBackendRefused)), url, refusal);

    public static string MsgServerError(string url, string detail) =>
        string.Format(Get(nameof(MsgServerError)), url, detail);

    public static string MsgContextOverflow(string detail, string breakdown) =>
        string.Format(Get(nameof(MsgContextOverflow)), detail, breakdown);

    public static string MsgContextWontFit(int estimateTokens, int loadedContext, string breakdown) =>
        string.Format(Get(nameof(MsgContextWontFit)), estimateTokens, loadedContext, breakdown);

    // One line per part of an oversized request (RequestSize.Breakdown): its size and what shrinks it.
    public static string ContextPartTools(int tokens) =>
        string.Format(Get(nameof(ContextPartTools)), tokens);

    public static string ContextPartSystem(int tokens) =>
        string.Format(Get(nameof(ContextPartSystem)), tokens);

    public static string ContextPartEarlier(int tokens) =>
        string.Format(Get(nameof(ContextPartEarlier)), tokens);

    public static string ContextPartLast(int tokens) =>
        string.Format(Get(nameof(ContextPartLast)), tokens);

    /// <summary>
    /// The backend is not usable — one reader for the heartbeat, the send pre-flight and the regenerate guard of both
    /// front-ends. ⚠ A server that ANSWERED the check with a refusal (<paramref name="refusal"/>: 401 for a wrong key,
    /// 404 for a URL at the wrong path) is running: "cannot reach… start it… check the firewall" sent its user to four
    /// remedies for a server that was up.
    /// </summary>
    public static string MsgConnectionLost(string url, string backend, string? refusal) =>
        refusal is null ? MsgConnectionGuardFailed(url, backend) : MsgConnectionRefused(url, backend, refusal);

    internal static string MsgConnectionGuardFailed(string url, string backend) =>
        string.Format(Get(nameof(MsgConnectionGuardFailed)), url, backend);

    internal static string MsgConnectionRefused(string url, string backend, string refusal) =>
        string.Format(Get(nameof(MsgConnectionRefused)), url, backend, refusal);

    /// <summary>⚠ Name the CONFIGURED backend, like <see cref="MsgConnectionGuardFailed"/>: "Reconnected to Ollama"
    /// was said to every LM Studio user.</summary>
    public static string MsgHeartbeatRestored(string backend)   => string.Format(Get(nameof(MsgHeartbeatRestored)), backend);
    public static string TooltipRetryConnection(string backend) => string.Format(Get(nameof(TooltipRetryConnection)), backend);

    public static string MsgToolOutput(string input, string output) =>
        string.Format(Get(nameof(MsgToolOutput)), input, output);

    // ── Approval ───────────────────────────────────────────────────────────────
    public static string ApprovalMessage(string toolName, string details) =>
        string.Format(Get(nameof(ApprovalMessage)), toolName, details);

    public static string ApprovalAllowOnce   => Get(nameof(ApprovalAllowOnce));
    public static string ApprovalAlwaysAllow => Get(nameof(ApprovalAlwaysAllow));
    public static string ApprovalDeny        => Get(nameof(ApprovalDeny));

    /// <summary>Title of the modal approval dialog showing the colored diff preview.</summary>
    public static string ApprovalDialogTitle => Get(nameof(ApprovalDialogTitle));

    /// <summary>Action blocked by a user-defined <c>deny</c> permission rule.</summary>
    public static string PermissionBlockedRule(string subject) =>
        string.Format(Get(nameof(PermissionBlockedRule)), subject);

    /// <summary>Action blocked by the built-in catastrophic-command hard denylist.</summary>
    public static string PermissionBlockedHard(string subject) =>
        string.Format(Get(nameof(PermissionBlockedHard)), subject);

    // ── Tool messages ──────────────────────────────────────────────────────────
    public static string ToolPathRequired  => Get(nameof(ToolPathRequired));
    public static string NoResults         => Get(nameof(NoResults));
    public static string WebSearchRefused  => Get(nameof(WebSearchRefused));
    public static string WriteCancelled    => Get(nameof(WriteCancelled));
    public static string DeleteCancelled   => Get(nameof(DeleteCancelled));
    public static string RunCancelled      => Get(nameof(RunCancelled));
    public static string DebugStartCancelled => Get(nameof(DebugStartCancelled));
    public static string DiagNoProject     => Get(nameof(DiagNoProject));
    public static string DiagFromEditor    => Get(nameof(DiagFromEditor));
    public static string CodeExcerptLabel(string label, int shown, int total) =>
        string.Format(Get(nameof(CodeExcerptLabel)), label, shown, total);
    public static string ActiveDocNoContext => Get(nameof(ActiveDocNoContext));
    public static string EditNotApplied(string path) =>
        string.Format(Get(nameof(EditNotApplied)), path);
    public static string ActiveDocNoFocus => Get(nameof(ActiveDocNoFocus));
    public static string ActiveDocNoFile   => Get(nameof(ActiveDocNoFile));

    public static string ToolFileNotFound(string path) =>
        string.Format(Get(nameof(ToolFileNotFound)), path);

    public static string SlashToolNoOutput => Get(nameof(SlashToolNoOutput));
    public static string MentionFolderIsFile(string name) =>
        string.Format(Get(nameof(MentionFolderIsFile)), name);
    public static string DirNotFound(string path) =>
        string.Format(Get(nameof(DirNotFound)), path);

    public static string ToolPathInvalid(string path, string reason) =>
        string.Format(Get(nameof(ToolPathInvalid)), path, reason);

    public static string WriteOverwrite(string path, int chars) =>
        string.Format(Get(nameof(WriteOverwrite)), path, chars);

    public static string WriteCreate(string path, int chars) =>
        string.Format(Get(nameof(WriteCreate)), path, chars);

    public static string WriteOk(string path, int chars) =>
        string.Format(Get(nameof(WriteOk)), path, chars);

    public static string DeleteConfirm(string path) =>
        string.Format(Get(nameof(DeleteConfirm)), path);

    public static string DeleteOk(string path) =>
        string.Format(Get(nameof(DeleteOk)), path);

    public static string DiffConfirm(string path) =>
        string.Format(Get(nameof(DiffConfirm)), path);

    public static string DebugStartConfirm(string workspace) =>
        string.Format(Get(nameof(DebugStartConfirm)), workspace);

    // ── /debug ───────────────────────────────────────────────
    public static string DebugUnavailable         => Get(nameof(DebugUnavailable));
    public static string DebugStopped             => Get(nameof(DebugStopped));
    public static string DebugUsage               => Get(nameof(DebugUsage));
    public static string DebugStatusHeader        => Get(nameof(DebugStatusHeader));
    public static string DebugStatusNotPaused     => Get(nameof(DebugStatusNotPaused));
    public static string DebugStatusNoBreakpoints => Get(nameof(DebugStatusNoBreakpoints));
    public static string DebugStatusBreakpoints   => Get(nameof(DebugStatusBreakpoints));

    public static string DebugStatusPaused(string location) =>
        string.Format(Get(nameof(DebugStatusPaused)), location);

    public static string DiffCancelled => Get(nameof(DiffCancelled));

    public static string DiffOldNotFound(string path) =>
        string.Format(Get(nameof(DiffOldNotFound)), path);

    public static string DiffAmbiguous(int count, string path) =>
        string.Format(Get(nameof(DiffAmbiguous)), count, path);

    public static string DiffOk(string path) =>
        string.Format(Get(nameof(DiffOk)), path);

    // The three lines the diff renderer writes ITSELF (as opposed to the file content, which it
    // only copies). They reach the user in a DiffLineModel with the "…" prefix, at the approval
    // prompt and in the chat bubble — interface text, in a product that speaks ten languages, so
    // they belong here and not in the renderer.
    public static string DiffTooLarge(int oldLines, int newLines) =>
        string.Format(Get(nameof(DiffTooLarge)), oldLines, newLines);
    public static string DiffUnchangedLines(int lines) =>
        string.Format(Get(nameof(DiffUnchangedLines)), lines);
    public static string DiffMoreLines(int lines) =>
        string.Format(Get(nameof(DiffMoreLines)), lines);

    public static string ApplyEditsEmpty => Get(nameof(ApplyEditsEmpty));
    public static string ApplyEditsConfirm(int files) =>
        string.Format(Get(nameof(ApplyEditsConfirm)), files);
    public static string ApplyEditsAborted(string detail) =>
        string.Format(Get(nameof(ApplyEditsAborted)), detail);
    public static string ApplyEditsOk(int edits, int files) =>
        string.Format(Get(nameof(ApplyEditsOk)), edits, files);

    public static string HistoryNote(string snapPath) =>
        string.Format(Get(nameof(HistoryNote)), snapPath);

    public static string RestoreOk(string path, string snapPath) =>
        string.Format(Get(nameof(RestoreOk)), path, snapPath);

    public static string RestoreNotFound(string path) =>
        string.Format(Get(nameof(RestoreNotFound)), path);

    public static string DiagBuildOk(string filename) =>
        string.Format(Get(nameof(DiagBuildOk)), filename);

    public static string DiagBuildFailed(int exitCode, string output) =>
        string.Format(Get(nameof(DiagBuildFailed)), exitCode, output);

    public static string DiagBuildStopped(int seconds) =>
        string.Format(Get(nameof(DiagBuildStopped)), seconds);

    public static string DiagSummary(int errors, int warnings, string filename) =>
        string.Format(Get(nameof(DiagSummary)), errors, warnings, filename);

    public static string ActiveDocResult(string path, string content) =>
        string.Format(Get(nameof(ActiveDocResult)), path, content);

    // ── Attachment / Browse ────────────────────────────────────────────────────
    public static string AttachError(string msg)              => string.Format(Get(nameof(AttachError)),              msg);
    /// <summary>What went with the question, named under the bubble: "📎 Attached: Foo.cs · Selection (Bar.cs)".</summary>
    public static string MsgAttachedRecap(string labels)      => string.Format(Get(nameof(MsgAttachedRecap)),          labels);
    public static string AttachReadError(string msg)          => string.Format(Get(nameof(AttachReadError)),          msg);
    public static string AttachSelectionError(string msg)     => string.Format(Get(nameof(AttachSelectionError)),     msg);
    public static string AttachSelectionReadError(string msg) => string.Format(Get(nameof(AttachSelectionReadError)), msg);
    public static string AttachFileTooLarge                   => Get(nameof(AttachFileTooLarge));
    public static string AttachNoActiveFile                   => Get(nameof(AttachNoActiveFile));

    // ── @mention context providers ──────────────────────────────────────────────
    public static string MentionFileDesc      => Get(nameof(MentionFileDesc));
    public static string MentionCodeDesc      => Get(nameof(MentionCodeDesc));
    public static string MentionCodeHint      => Get(nameof(MentionCodeHint));
    public static string MentionFolderDesc    => Get(nameof(MentionFolderDesc));
    public static string MentionClipboardDesc => Get(nameof(MentionClipboardDesc));
    public static string MentionTreeDesc      => Get(nameof(MentionTreeDesc));
    public static string MentionDiffDesc      => Get(nameof(MentionDiffDesc));
    public static string MentionProblemsDesc  => Get(nameof(MentionProblemsDesc));
    public static string MentionDebuggerDesc  => Get(nameof(MentionDebuggerDesc));
    public static string MentionDebuggerNone  => Get(nameof(MentionDebuggerNone));
    public static string MentionClipboardEmpty => Get(nameof(MentionClipboardEmpty));
    public static string MentionNothingToAttach(string category) =>
        string.Format(Get(nameof(MentionNothingToAttach)), category);
    public static string BrowseError(string msg)              => string.Format(Get(nameof(BrowseError)),              msg);
    public static string PinLimitReached(int max)            => string.Format(Get(nameof(PinLimitReached)),           max);

    // ── Export ─────────────────────────────────────────────────────────────────
    /// <summary>Label of the "user" bubble. ⚠ It was hardcoded, in FRENCH (<c>Label = "Vous"</c>),
    /// in ChatMessageItem.UserMsg — and it goes into the exported document, so a Japanese reader
    /// got "Vous" heading every one of their turns.</summary>
    public static string ChatRoleYou             => Get(nameof(ChatRoleYou));
    /// <inheritdoc cref="ChatRoleYou"/>
    public static string ChatRoleAssistant       => Get(nameof(ChatRoleAssistant));
    public static string ExportNoMessages        => Get(nameof(ExportNoMessages));
    public static string ExportSuccess(string f) => string.Format(Get(nameof(ExportSuccess)), f);
    public static string ExportFailed(string e)  => string.Format(Get(nameof(ExportFailed)),  e);
    /// <summary>Header of the exported document: same reader as the turn labels.</summary>
    public static string ExportTitle             => Get(nameof(ExportTitle));
    /// <inheritdoc cref="ExportTitle"/>
    public static string ExportStatColumn        => Get(nameof(ExportStatColumn));
    /// <inheritdoc cref="ExportTitle"/>
    public static string ExportValueColumn       => Get(nameof(ExportValueColumn));
    /// <inheritdoc cref="ExportTitle"/>
    public static string ExportModel             => Get(nameof(ExportModel));
    /// <inheritdoc cref="ExportTitle"/>
    public static string ExportTurns             => Get(nameof(ExportTurns));
    /// <inheritdoc cref="ExportTitle"/>
    public static string ExportToolCalls         => Get(nameof(ExportToolCalls));
    /// <inheritdoc cref="ExportTitle"/>
    public static string ExportTokens            => Get(nameof(ExportTokens));
    /// <inheritdoc cref="ExportTitle"/>
    public static string ExportDuration          => Get(nameof(ExportDuration));
    /// <summary>The host's refusal while a turn holds the slot: VS Code shows it verbatim.</summary>
    public static string HostTurnBusy            => Get(nameof(HostTurnBusy));

    // ── Slash commands ─────────────────────────────────────────────────────────
    public static string SlashModelCurrent(string model) => string.Format(Get(nameof(SlashModelCurrent)), model);
    public static string SlashModelChanged(string model) => string.Format(Get(nameof(SlashModelChanged)), model);
    public static string SlashModelNotListed(string model) => string.Format(Get(nameof(SlashModelNotListed)), model);
    public static string PromptsShadowedByBuiltIn(string command) => string.Format(Get(nameof(PromptsShadowedByBuiltIn)), command);
    public static string PromptsShadowedByConfig(string command) => string.Format(Get(nameof(PromptsShadowedByConfig)), command);
    public static string ReplayRunOutOfRange(int index, int count) => string.Format(Get(nameof(ReplayRunOutOfRange)), index, count);
    public static string SlashToolsCurrent(string state) => string.Format(Get(nameof(SlashToolsCurrent)), state);
    public static string SlashToolsChanged(string state) => string.Format(Get(nameof(SlashToolsChanged)), state);
    public static string SlashNoActiveDocument            => Get(nameof(SlashNoActiveDocument));
    public static string SlashUsage(string syntax)        => string.Format(Get(nameof(SlashUsage)),        syntax);
    public static string SlashUsageRestore                => Get(nameof(SlashUsageRestore));
    /// <summary>Header alone: the command list that follows is generated from the Catalog.</summary>
    /// <remarks>
    /// ⚠ Header ONLY. A hand-written list of commands here drifts in ten languages at once, and it
    /// is shown at the worst possible moment — the user has just typed a command the product did
    /// not recognise. The list comes from <c>SlashCommandRouter.Catalog</c>, like <c>/help</c>.
    /// </remarks>
    public static string SlashUnknownCommand(string cmd)  => string.Format(Get(nameof(SlashUnknownCommand)), cmd);

    // `/help` section titles — the help text itself is generated from SlashCommandRouter.Catalog
    // (the hand-written SlashHelpAll had drifted from the shipped commands and was dropped).
    public static string SlashCategoryMeta                => Get(nameof(SlashCategoryMeta));
    public static string SlashCategoryCodeActions         => Get(nameof(SlashCategoryCodeActions));
    public static string SlashCategoryFiles               => Get(nameof(SlashCategoryFiles));
    public static string SlashCategoryShell               => Get(nameof(SlashCategoryShell));
    public static string SlashCategoryWeb                 => Get(nameof(SlashCategoryWeb));
    public static string SlashCategoryGit                 => Get(nameof(SlashCategoryGit));
    public static string SlashCategoryBuild               => Get(nameof(SlashCategoryBuild));
    public static string SlashCategoryKnowledge           => Get(nameof(SlashCategoryKnowledge));
    public static string SlashCategorySessions            => Get(nameof(SlashCategorySessions));
    public static string SlashCategoryModels              => Get(nameof(SlashCategoryModels));
    public static string SlashCategoryAgent               => Get(nameof(SlashCategoryAgent));
    public static string SlashCategoryGovernance          => Get(nameof(SlashCategoryGovernance));
    public static string SlashCategoryTransparency        => Get(nameof(SlashCategoryTransparency));
    public static string SlashHintDocs                    => Get(nameof(SlashHintDocs));
    public static string SlashHeadlessUnavailable         => Get(nameof(SlashHeadlessUnavailable));
    public static string HistoryNoSessions                => Get(nameof(HistoryNoSessions));
    public static string HistoryNoResults(string term)    => string.Format(Get(nameof(HistoryNoResults)),   term);
    public static string HistoryListHeader(int count) => string.Format(Get(nameof(HistoryListHeader)), count);
    public static string HistoryMessageCount(int count) => string.Format(Get(nameof(HistoryMessageCount)), count);
    public static string HistorySearchHint => Get(nameof(HistorySearchHint));
    public static string HistorySearchHeader(string term, int count) => string.Format(Get(nameof(HistorySearchHeader)), term, count);
    public static string AgeMinutesAgo(int n) => string.Format(Get(nameof(AgeMinutesAgo)), n);
    public static string AgeHoursAgo(int n) => string.Format(Get(nameof(AgeHoursAgo)), n);
    public static string AgeDaysAgo(int n) => string.Format(Get(nameof(AgeDaysAgo)), n);
    public static string TemplateListHeader => Get(nameof(TemplateListHeader));
    public static string TemplateLabelCodeReview => Get(nameof(TemplateLabelCodeReview));
    public static string TemplateLabelBugHunt => Get(nameof(TemplateLabelBugHunt));
    public static string TemplateLabelArchitecture => Get(nameof(TemplateLabelArchitecture));
    public static string TemplateLabelRefactoring => Get(nameof(TemplateLabelRefactoring));
    public static string TemplateLabelTests => Get(nameof(TemplateLabelTests));
    public static string TemplateGreetingCodeReview => Get(nameof(TemplateGreetingCodeReview));
    public static string TemplateGreetingBugHunt => Get(nameof(TemplateGreetingBugHunt));
    public static string TemplateGreetingArchitecture => Get(nameof(TemplateGreetingArchitecture));
    public static string TemplateGreetingRefactoring => Get(nameof(TemplateGreetingRefactoring));
    public static string TemplateGreetingTests => Get(nameof(TemplateGreetingTests));

    // ── Project context ────────────────────────────────────────────────────────
    public static string SlashContextNoSln                              => Get(nameof(SlashContextNoSln));
    public static string SlashContextNotFound(string path)             => string.Format(Get(nameof(SlashContextNotFound)), path);
    public static string SlashContextLoaded(string path, int chars, string preview) =>
        string.Format(Get(nameof(SlashContextLoaded)), path, chars, preview);

    // ── Git / Solution tool outputs ────────────────────────────────────────────
    public static string GitNotRepo                        => Get(nameof(GitNotRepo));
    /// <param name="command">The git arguments, without the leading <c>git</c>.</param>
    /// <param name="detail">What git itself said — its own words, never a phrase of ours.</param>
    public static string GitCommandFailed(string command, string detail) =>
        string.Format(Get(nameof(GitCommandFailed)), command, detail);
    /// <summary>
    /// The conversation being left could not be archived. Shown <b>after</b> the transcript was
    /// cleared, which is why it says what was lost rather than only why.
    /// </summary>
    public static string SessionArchiveFailed(string reason) =>
        string.Format(Get(nameof(SessionArchiveFailed)), reason);

    public static string SessionAutoSaveFailed(string reason) =>
        string.Format(Get(nameof(SessionAutoSaveFailed)), reason);
    public static string SolutionNoSln                     => Get(nameof(SolutionNoSln));
    public static string SolutionPathNotFound(string path) => string.Format(Get(nameof(SolutionPathNotFound)), path);

    // ── Editor insertion / replacement ─────────────────────────────────────────
    public static string InsertOk(string path, int chars)  => string.Format(Get(nameof(InsertOk)),  path, chars);
    public static string ReplaceOk(string path, int chars) => string.Format(Get(nameof(ReplaceOk)), path, chars);

    // ── Fix-build loop ─────────────────────────────────────────────────────────
    public static string FixBuildSuccess(int rounds)        => string.Format(Get(nameof(FixBuildSuccess)),    rounds);
    public static string FixBuildGiveUp(int maxRounds)      => string.Format(Get(nameof(FixBuildGiveUp)),     maxRounds);
    public static string FixBuildCouldNotBuild              => Get(nameof(FixBuildCouldNotBuild));
    // ⚠ The two progress labels of the SAME loop, left as literals while its two end messages
    // were localized.
    public static string FixBuildBuildingRound(int round, int total) => string.Format(Get(nameof(FixBuildBuildingRound)), round, total);
    public static string FixBuildFixingRound(int round)     => string.Format(Get(nameof(FixBuildFixingRound)), round);
    public static string BuildFailedProposal(int errorCount) => string.Format(Get(nameof(BuildFailedProposal)), errorCount);

    // ── Smart Fix Protocol ─────────────────────────────────────────────────────
    public static string SmartFixBuildOk                                   => Get(nameof(SmartFixBuildOk));
    public static string SmartFixBuildErrors(int count, string errorLines) => string.Format(Get(nameof(SmartFixBuildErrors)), count, errorLines);
    public static string SmartFixTimeout                                    => Get(nameof(SmartFixTimeout));
    public static string SmartFixBuildFailedNoErrors                        => Get(nameof(SmartFixBuildFailedNoErrors));
    /// <summary>
    /// A multi-file batch spanned more projects than Smart Fix builds in one call. Said rather than
    /// trimmed in silence: the files were written either way.
    /// </summary>
    public static string SmartFixBatchCapped(int checkedCount, int skipped) =>
        string.Format(Get(nameof(SmartFixBatchCapped)), checkedCount, skipped);
    public static string LabelSmartFixEnabled                               => Get(nameof(LabelSmartFixEnabled));
    public static string HintSmartFixEnabled                                => Get(nameof(HintSmartFixEnabled));

    // ── Git commit assistant ───────────────────────────────────────────────────
    public static string CommitNothingToCommit  => Get(nameof(CommitNothingToCommit));
    public static string CommitNothingStaged    => Get(nameof(CommitNothingStaged));
    public static string CommitUntrackedLeftOut(int count, string names) =>
        string.Format(Get(nameof(CommitUntrackedLeftOut)), count, names);
    public static string CommitOnlyUntracked(string names) =>
        string.Format(Get(nameof(CommitOnlyUntracked)), names);
    /// <summary>The proposal describes only what fit in the prompt — said before the message.</summary>
    public static string CommitDiffTruncated(int kept, int total) =>
        string.Format(Get(nameof(CommitDiffTruncated)), kept, total);
    public static string CommitProposingLabel   => Get(nameof(CommitProposingLabel));
    public static string CommitConfirmHint      => Get(nameof(CommitConfirmHint));

    // ── Rules & Checks (.inferpal/rules, .inferpal/checks) ────────────────
    // ⚠ A .inferpal/ file that cannot be READ is not a missing file: the rule stops constraining the
    // model, the check stops being applied, and the list gets shorter without a word. This is the
    // rule PlanStore.List had already written, for itself alone.
    public static string GovernanceFilesUnreadable(int count, string names) =>
        string.Format(Get(nameof(GovernanceFilesUnreadable)), count, names);
    /// <summary>Saved sessions missing from a listing because they could not be read.</summary>
    public static string SessionsUnreadableListed(int count, string names) =>
        string.Format(Get(nameof(SessionsUnreadableListed)), count, names);
    /// <summary>
    /// Saved sessions a search could not open. Deliberately a <b>second</b> sentence rather than a
    /// reuse of the one above: "missing from the list" and "not searched — this is not «absent»"
    /// send the reader to two different conclusions, and it is the second that gets acted on.
    /// </summary>
    public static string SessionsUnreadableSearched(int count, string names) =>
        string.Format(Get(nameof(SessionsUnreadableSearched)), count, names);
    public static string RulesNone               => Get(nameof(RulesNone));
    public static string ChecksNone              => Get(nameof(ChecksNone));
    public static string RulesListHeader         => Get(nameof(RulesListHeader));

    // ── /permissions ──────────────────────────────────────────
    // The only one of the four committable governance artifacts that RESTRICTS, and the only
    // one with no listing: an unreadable overlay stopped applying every
    // one of its rules and only /diagnostics said so.
    public static string SlashHintPermissions          => Get(nameof(SlashHintPermissions));
    public static string PermissionsHeader             => Get(nameof(PermissionsHeader));
    public static string PermissionsOverlaySection     => Get(nameof(PermissionsOverlaySection));
    public static string PermissionsNoWorkspace        => Get(nameof(PermissionsNoWorkspace));
    public static string PermissionsOverlayAbsent      => Get(nameof(PermissionsOverlayAbsent));
    public static string PermissionsOverlayUnusable    => Get(nameof(PermissionsOverlayUnusable));
    public static string PermissionsOverlayEmpty       => Get(nameof(PermissionsOverlayEmpty));
    public static string PermissionsOverlayMalformed(int count) =>
        string.Format(Get(nameof(PermissionsOverlayMalformed)), count);
    public static string PermissionsOverlayAllowIgnored(int count) =>
        string.Format(Get(nameof(PermissionsOverlayAllowIgnored)), count);
    public static string PermissionsConfigSection      => Get(nameof(PermissionsConfigSection));
    public static string PermissionsConfigEmpty        => Get(nameof(PermissionsConfigEmpty));
    public static string PermissionsConfigDropped(int count) =>
        string.Format(Get(nameof(PermissionsConfigDropped)), count);
    public static string PermissionsDenylistNote       => Get(nameof(PermissionsDenylistNote));
    public static string ChecksListHeader        => Get(nameof(ChecksListHeader));
    public static string CheckNoDiff             => Get(nameof(CheckNoDiff));
    public static string CheckReviewingLabel     => Get(nameof(CheckReviewingLabel));
    public static string CheckReviewSystemPrompt => Get(nameof(CheckReviewSystemPrompt));
    public static string CheckUnknownName(string name) => string.Format(Get(nameof(CheckUnknownName)), name);
    public static string CheckNoFindings         => Get(nameof(CheckNoFindings));
    public static string CheckReviewOnlyReasoning(string model) => string.Format(Get(nameof(CheckReviewOnlyReasoning)), model);
    /// <summary>The verdict below covers only what fit — said above the findings, which it qualifies.</summary>
    public static string CheckDiffTruncated(int kept, int total) =>
        string.Format(Get(nameof(CheckDiffTruncated)), kept, total);
    public static string CheckNewFilesNotReviewed(int count, string names) =>
        string.Format(Get(nameof(CheckNewFilesNotReviewed)), count, names);
    public static string CheckSeverityBlocker    => Get(nameof(CheckSeverityBlocker));
    public static string CheckSeverityWarning    => Get(nameof(CheckSeverityWarning));
    public static string CheckSeverityNit        => Get(nameof(CheckSeverityNit));
    public static string CheckAnchorUnanchored   => Get(nameof(CheckAnchorUnanchored));
    public static string CheckFindingsHeader(int count) => string.Format(Get(nameof(CheckFindingsHeader)), count);
    public static string CheckAnchorAdjusted(int reported) => string.Format(Get(nameof(CheckAnchorAdjusted)), reported);
    public static string RulesScaffolded(string path)  => string.Format(Get(nameof(RulesScaffolded)),  path);
    public static string ChecksScaffolded(string path) => string.Format(Get(nameof(ChecksScaffolded)), path);
    public static string PromptsNone             => Get(nameof(PromptsNone));
    public static string PromptsListHeader       => Get(nameof(PromptsListHeader));
    public static string PromptsScaffolded(string path) => string.Format(Get(nameof(PromptsScaffolded)), path);

    // ── analyze_impact tool ───────────────────────────────────────────────────
    public static string ImpactHeader(string fileName)                          => string.Format(Get(nameof(ImpactHeader)),         fileName);
    public static string ImpactNoPublicApi(string fileName)                     => string.Format(Get(nameof(ImpactNoPublicApi)),    fileName);
    public static string ImpactFooter(int direct, int transitive, int tests, int entries) =>
        string.Format(Get(nameof(ImpactFooter)), direct, transitive, tests, entries);

    // ── trace_dependency tool ──────────────────────────────────────────────────
    public static string TraceDepsHeader(string fileName)                        => string.Format(Get(nameof(TraceDepsHeader)),         fileName);
    public static string TraceDepsNoMethods(string fileName)                     => string.Format(Get(nameof(TraceDepsNoMethods)),       fileName);
    public static string TraceDepsSymbolNotFound(string symbol, string fileName) => string.Format(Get(nameof(TraceDepsSymbolNotFound)), symbol, fileName);
    public static string TraceDepsFooter(int methods, int callees, int resolved) => string.Format(Get(nameof(TraceDepsFooter)),         methods, callees, resolved);

    // ── First-Run Auto-Discovery ───────────────────────────────────────────────
    public static string MsgFirstRunWelcome(string models, string selected) =>
        string.Format(Get(nameof(MsgFirstRunWelcome)), models, selected);
    /// <summary>The default model was never chosen and is not installed: the best installed one is used, and said.</summary>
    public static string MsgModelAdopted(string configured, string adopted) =>
        string.Format(Get(nameof(MsgModelAdopted)), configured, adopted);
    /// <summary>The backend answered with no chat model: named (it is not always Ollama), with the slash command that
    /// downloads one when the backend can (<paramref name="canPull"/>), and no model of our own choosing — the measured
    /// ones live in docs/models.md, where they are kept current.</summary>
    public static string MsgFirstRunNoModels(string backend, bool canPull) =>
        string.Format(Get(nameof(MsgFirstRunNoModels)), backend)
        + (canPull ? "\n\n" + MsgFirstRunPullHint : string.Empty);
    private static string MsgFirstRunPullHint => Get(nameof(MsgFirstRunPullHint));

    /// <summary>The first run could not complete — said in the conversation, which is empty
    /// and waiting, and naming the gesture that re-runs it.</summary>
    public static string FirstRunFailed(string detail) =>
        string.Format(Get(nameof(FirstRunFailed)), detail);
    public static string MsgFirstRunBackendDown(string url, string backend) =>
        string.Format(Get(nameof(MsgFirstRunBackendDown)), url, backend);
    public static string MsgFirstRunVramWarning(string neededGb, string budgetGb) =>
        string.Format(Get(nameof(MsgFirstRunVramWarning)), neededGb, budgetGb);

    // ── Slash-command autocomplete hints ───────────────────────────────────────
    public static string SlashHintExplain   => Get(nameof(SlashHintExplain));
    public static string SlashHintFix       => Get(nameof(SlashHintFix));
    public static string SlashHintReview    => Get(nameof(SlashHintReview));
    public static string SlashHintRefactor  => Get(nameof(SlashHintRefactor));
    public static string SlashHintTest      => Get(nameof(SlashHintTest));
    public static string SlashHintDoc       => Get(nameof(SlashHintDoc));
    public static string SlashHintClear     => Get(nameof(SlashHintClear));
    public static string SlashHintModel     => Get(nameof(SlashHintModel));
    public static string SlashHintTools     => Get(nameof(SlashHintTools));
    public static string SlashHintExport    => Get(nameof(SlashHintExport));
    public static string SlashHintRestore   => Get(nameof(SlashHintRestore));
    public static string SlashHintHelp      => Get(nameof(SlashHintHelp));
    public static string SlashHintRead      => Get(nameof(SlashHintRead));
    public static string SlashHintLs        => Get(nameof(SlashHintLs));
    public static string SlashHintGrep      => Get(nameof(SlashHintGrep));
    public static string SlashHintRun       => Get(nameof(SlashHintRun));
    public static string SlashHintFetch     => Get(nameof(SlashHintFetch));
    public static string SlashHintSearch    => Get(nameof(SlashHintSearch));
    public static string SlashHintSearchCode => Get(nameof(SlashHintSearchCode));
    public static string SlashHintCommit    => Get(nameof(SlashHintCommit));
    public static string SlashHintGit       => Get(nameof(SlashHintGit));
    public static string SlashHintMap       => Get(nameof(SlashHintMap));
    public static string SlashHintSolution  => Get(nameof(SlashHintSolution));
    public static string SlashHintBuild     => Get(nameof(SlashHintBuild));
    public static string SlashHintFixBuild  => Get(nameof(SlashHintFixBuild));
    public static string SlashHintContext   => Get(nameof(SlashHintContext));
    public static string SlashHintMemory    => Get(nameof(SlashHintMemory));
    public static string SlashHintIndex     => Get(nameof(SlashHintIndex));
    public static string SlashHintHistory   => Get(nameof(SlashHintHistory));
    public static string SlashHintTemplate  => Get(nameof(SlashHintTemplate));
    public static string TemplateUnknown(string id) => string.Format(Get(nameof(TemplateUnknown)), id);
    public static string SlashHintDiff      => Get(nameof(SlashHintDiff));
    public static string SlashHintCheck     => Get(nameof(SlashHintCheck));
    public static string SlashHintRules     => Get(nameof(SlashHintRules));
    public static string SlashHintChecks    => Get(nameof(SlashHintChecks));
    public static string SlashHintSnippets  => Get(nameof(SlashHintSnippets));
    public static string SlashHintNote      => Get(nameof(SlashHintNote));
    public static string SlashHintNotes     => Get(nameof(SlashHintNotes));
    public static string SlashHintPhistory  => Get(nameof(SlashHintPhistory));
    public static string SlashHintModels    => Get(nameof(SlashHintModels));
    public static string SlashHintAgentStep => Get(nameof(SlashHintAgentStep));
    public static string SlashHintResume    => Get(nameof(SlashHintResume));
    public static string SlashHintPlan      => Get(nameof(SlashHintPlan));
    public static string SlashHintPrompts   => Get(nameof(SlashHintPrompts));
    public static string SlashHintHardware  => Get(nameof(SlashHintHardware));
    public static string SlashHintSetup     => Get(nameof(SlashHintSetup));

    // ── Backend capability notices (non-Ollama providers) ───────────────────────
    public static string HardwareNoVramBackend               => Get(nameof(HardwareNoVramBackend));
    public static string ModelsBackendUnsupported            => Get(nameof(ModelsBackendUnsupported));

    // ── /snippets, /note(s), /phistory, /models command messages ────────────────
    public static string SnippetsCleared                     => Get(nameof(SnippetsCleared));
    public static string SnippetsNoSuch(int idx)             => string.Format(Get(nameof(SnippetsNoSuch)), idx);
    public static string SnippetsCopied(int idx)             => string.Format(Get(nameof(SnippetsCopied)), idx);
    public static string SnippetsDeleted(int idx)            => string.Format(Get(nameof(SnippetsDeleted)), idx);
    /// <summary>A snippets or arena save failed: nothing changed on disk.</summary>
    public static string SnippetsWriteFailed                 => Get(nameof(SnippetsWriteFailed));
    /// <inheritdoc cref="SnippetsWriteFailed"/>
    public static string ArenaVoteNotSaved                   => Get(nameof(ArenaVoteNotSaved));
    /// <inheritdoc cref="SnippetsWriteFailed"/>
    public static string ArenaPendingNotSaved                => Get(nameof(ArenaPendingNotSaved));
    public static string SnippetsNone                        => Get(nameof(SnippetsNone));
    /// <summary>The snippet file exists and did not open — never "none saved yet".</summary>
    public static string SnippetsUnreadable(string path) => string.Format(Get(nameof(SnippetsUnreadable)), path);

    public static string NoteUsage                           => Get(nameof(NoteUsage));
    public static string NoteSaved(string text)              => string.Format(Get(nameof(NoteSaved)), text);
    public static string NoteCannotHold(string character, string encoding) =>
        string.Format(Get(nameof(NoteCannotHold)), character, encoding);
    public static string NotesCleared                        => Get(nameof(NotesCleared));
    public static string NotesNoneYet                        => Get(nameof(NotesNoneYet));
    public static string NotesEmpty                          => Get(nameof(NotesEmpty));
    public static string NotesHeading                        => Get(nameof(NotesHeading));

    public static string PHistoryNoEntry(string target)      => string.Format(Get(nameof(PHistoryNoEntry)), target);
    public static string PHistoryEmpty                       => Get(nameof(PHistoryEmpty));
    /// <summary>The history file exists and did not open — never "history is empty".</summary>
    public static string PHistoryUnreadable(string path) => string.Format(Get(nameof(PHistoryUnreadable)), path);
    public static string PHistoryNoMatch(string? term)       => string.Format(Get(nameof(PHistoryNoMatch)), term);

    public static string ModelsDeleteUsage                   => Get(nameof(ModelsDeleteUsage));
    public static string ModelsDeleted(string model)         => string.Format(Get(nameof(ModelsDeleted)), model);
    public static string ModelsDeleteFailed(string model)    => string.Format(Get(nameof(ModelsDeleteFailed)), model);
    public static string ModelsDeleteUnsupported => Get(nameof(ModelsDeleteUnsupported));
    public static string ModelsNoneRunning                   => Get(nameof(ModelsNoneRunning));
    public static string ModelsNoneInstalled                 => Get(nameof(ModelsNoneInstalled));
    public static string ModelsPullUsage                     => Get(nameof(ModelsPullUsage));
    public static string ModelsPulling(string model)         => string.Format(Get(nameof(ModelsPulling)), model);
    public static string ModelsPullingStatus(string model, string status) => string.Format(Get(nameof(ModelsPullingStatus)), model, status);
    public static string ModelsPulled(string model)          => string.Format(Get(nameof(ModelsPulled)), model);
    public static string ModelsPullFailed(string model)      => string.Format(Get(nameof(ModelsPullFailed)), model);

    // ── List/table formatters (/snippets, /phistory, /models) ───────────────────
    public static string SnippetsListHeader                  => Get(nameof(SnippetsListHeader));
    public static string SnippetsSavedAt(string date)        => string.Format(Get(nameof(SnippetsSavedAt)), date);
    public static string PHistoryListHeader                  => Get(nameof(PHistoryListHeader));
    public static string PHistoryListHeaderTerm(string term) => string.Format(Get(nameof(PHistoryListHeaderTerm)), term);
    public static string ModelsRunningHeader                 => Get(nameof(ModelsRunningHeader));
    public static string ModelsInstalledHeader               => Get(nameof(ModelsInstalledHeader));
    public static string ModelsTableModel                    => Get(nameof(ModelsTableModel));
    public static string ModelsTableVram                     => Get(nameof(ModelsTableVram));

    // ── /diagnostics command ────────────────────────────────────────────────────
    public static string SlashHintDiagnostics                => Get(nameof(SlashHintDiagnostics));
    public static string DiagnosticsHeader                   => Get(nameof(DiagnosticsHeader));
    public static string DiagnosticsEmpty                    => Get(nameof(DiagnosticsEmpty));
    public static string DiagnosticsShowing(int shown, int total) =>
        string.Format(Get(nameof(DiagnosticsShowing)), shown, total);
    public static string DiagnosticsInProcDead               => Get(nameof(DiagnosticsInProcDead));
    public static string DiagnosticsCleared                  => Get(nameof(DiagnosticsCleared));
    public static string DiagnosticsExported                 => Get(nameof(DiagnosticsExported));
    public static string DiagnosticsFileOn                   => Get(nameof(DiagnosticsFileOn));
    public static string DiagnosticsFileOff                  => Get(nameof(DiagnosticsFileOff));

    // ── /undo-run command ───────────────────────────────────────────────────────
    public static string SlashHintUndoRun                    => Get(nameof(SlashHintUndoRun));
    public static string UndoRunNone                         => Get(nameof(UndoRunNone));
    public static string UndoRunAlreadyUndone                => Get(nameof(UndoRunAlreadyUndone));
    public static string UndoRunWhileBusy                    => Get(nameof(UndoRunWhileBusy));
    public static string UndoRunListHeader(int count)        => string.Format(Get(nameof(UndoRunListHeader)), count);
    public static string UndoRunResult(int restored, int deleted) => string.Format(Get(nameof(UndoRunResult)), restored, deleted);
    public static string UndoRunSavedFirst                    => Get(nameof(UndoRunSavedFirst));

    // ── /replay command ─────────────────────────────────────────────────────────
    public static string SlashHintReplay                     => Get(nameof(SlashHintReplay));
    public static string ReplayNone                          => Get(nameof(ReplayNone));
    public static string ReplayHeader(string time, int tools, int files) => string.Format(Get(nameof(ReplayHeader)), time, tools, files);
    public static string ReplayFileUnprotected              => Get(nameof(ReplayFileUnprotected));
    public static string ReplayFilesHeader                   => Get(nameof(ReplayFilesHeader));

    // ── /xray command ───────────────────────────────────────────────────────────
    public static string SlashHintXray                       => Get(nameof(SlashHintXray));
    public static string SlashHintBench                      => Get(nameof(SlashHintBench));
    public static string BenchTitle                          => Get(nameof(BenchTitle));
    public static string BenchRunning(string model, int index, int total)
        => string.Format(Get(nameof(BenchRunning)), model, index, total);
    public static string BenchNoModels                       => Get(nameof(BenchNoModels));
    public static string BenchNoSaved                        => Get(nameof(BenchNoSaved));
    public static string BenchSavedAt(string when)           => string.Format(Get(nameof(BenchSavedAt)), when);
    public static string BenchColModel                       => Get(nameof(BenchColModel));
    public static string BenchColTtft                        => Get(nameof(BenchColTtft));
    public static string BenchColSpeed                       => Get(nameof(BenchColSpeed));
    public static string BenchColVram                        => Get(nameof(BenchColVram));
    public static string BenchColQuality                     => Get(nameof(BenchColQuality));
    public static string BenchRecoHeader                     => Get(nameof(BenchRecoHeader));
    /// <summary>The installed models /bench left out of an automatic run, named above the table.</summary>
    public static string BenchNotMeasured(int measured, int installed, string names) =>
        string.Format(Get(nameof(BenchNotMeasured)), measured, installed, names);
    public static string BenchRecoAgent                      => Get(nameof(BenchRecoAgent));
    public static string BenchRecoUtility                    => Get(nameof(BenchRecoUtility));
    public static string BenchRecoFim                        => Get(nameof(BenchRecoFim));

    // ── /arena command ──────────────────────────────────────────────────────────
    public static string SlashHintArena                      => Get(nameof(SlashHintArena));
    public static string ArenaUsage                          => Get(nameof(ArenaUsage));
    public static string ArenaNeedTwoModels                  => Get(nameof(ArenaNeedTwoModels));
    public static string ArenaRunning(string label)          => string.Format(Get(nameof(ArenaRunning)), label);
    public static string ArenaTitle                          => Get(nameof(ArenaTitle));
    public static string ArenaAnswerHeader(string label, string seconds)
        => string.Format(Get(nameof(ArenaAnswerHeader)), label, seconds);
    public static string ArenaVotePrompt                     => Get(nameof(ArenaVotePrompt));
    public static string ArenaNoPending                      => Get(nameof(ArenaNoPending));
    public static string ArenaReveal(string modelA, string modelB)
        => string.Format(Get(nameof(ArenaReveal)), modelA, modelB);
    public static string ArenaVoteRecordedWin(string model)  => string.Format(Get(nameof(ArenaVoteRecordedWin)), model);
    public static string ArenaVoteRecordedTie                => Get(nameof(ArenaVoteRecordedTie));
    public static string ArenaStatsTitle                     => Get(nameof(ArenaStatsTitle));
    public static string ArenaColModel                       => Get(nameof(ArenaColModel));
    public static string ArenaColBattles                     => Get(nameof(ArenaColBattles));
    public static string ArenaColWins                        => Get(nameof(ArenaColWins));
    public static string ArenaColTies                        => Get(nameof(ArenaColTies));
    public static string ArenaColWinRate                     => Get(nameof(ArenaColWinRate));
    public static string ArenaNoStats                        => Get(nameof(ArenaNoStats));
    /// <summary>The arena file exists and did not open — never "no vote recorded yet".</summary>
    public static string ArenaUnreadable(string path) => string.Format(Get(nameof(ArenaUnreadable)), path);
    /// <summary>What the vote itself became, said before the cause: nothing was written.</summary>
    public static string ArenaVoteNotRead                    => Get(nameof(ArenaVoteNotRead));
    public static string ArenaFailed(string error)           => string.Format(Get(nameof(ArenaFailed)), error);

    // ── /tdd command ────────────────────────────────────────────────────────────
    public static string SlashHintTdd                        => Get(nameof(SlashHintTdd));
    public static string TddRunningTests(int round, int max) => string.Format(Get(nameof(TddRunningTests)), round, max);
    public static string TddFixing(int round)                => string.Format(Get(nameof(TddFixing)), round);
    public static string TddSuccess(int rounds)              => string.Format(Get(nameof(TddSuccess)), rounds);
    public static string TddGiveUp(int maxRounds)            => string.Format(Get(nameof(TddGiveUp)), maxRounds);
    /// <summary>
    /// The loop stopped because the run executed no test — a third state, between "green" and
    /// "failing", that must not be folded into the second: doing so spends agent rounds patching
    /// code against a run where nothing was executed.
    /// </summary>
    public static string TddNothingRan                       => Get(nameof(TddNothingRan));
    public static string TddStoppedAtBudget                  => Get(nameof(TddStoppedAtBudget));
    public static string TddDebugCaptureApproval(string test) => string.Format(Get(nameof(TddDebugCaptureApproval)), test);
    public static string TddDebugCapturing                   => Get(nameof(TddDebugCapturing));
    public static string TddDebugCaptureFailed               => Get(nameof(TddDebugCaptureFailed));
    public static string TddDebugCaptureUnavailable          => Get(nameof(TddDebugCaptureUnavailable));
    public static string XrayHeader(string tokens)           => string.Format(Get(nameof(XrayHeader)), tokens);
    public static string XrayLabelBase                       => Get(nameof(XrayLabelBase));
    public static string XrayLabelPersona                    => Get(nameof(XrayLabelPersona));
    public static string XrayLabelCustom                     => Get(nameof(XrayLabelCustom));
    public static string XrayLabelTemplate                   => Get(nameof(XrayLabelTemplate));
    public static string XrayLabelRules(string count)        => string.Format(Get(nameof(XrayLabelRules)), count);
    public static string XrayHistory(string tokens)          => string.Format(Get(nameof(XrayHistory)), tokens);
    public static string XrayTools(string tokens)            => string.Format(Get(nameof(XrayTools)), tokens);
    public static string XrayRag(string state)               => string.Format(Get(nameof(XrayRag)), state);
    public static string XrayRagNoIndexing                   => Get(nameof(XrayRagNoIndexing));
    public static string XrayBudget(string used, string limit, string pct) => string.Format(Get(nameof(XrayBudget)), used, limit, pct);
    // Interactive X-Ray panel (V2)
    public static string XrayPanelHint                       => Get(nameof(XrayPanelHint));
    public static string XrayPanelWarning                    => Get(nameof(XrayPanelWarning));
    public static string XrayPanelCopy                       => Get(nameof(XrayPanelCopy));
    public static string TooltipXrayClose                    => Get(nameof(TooltipXrayClose));

    // ── MCP OAuth ──────────────────────────────────────────────────────────────
    public static string McpOAuthUnsupportedPlatform => Get(nameof(McpOAuthUnsupportedPlatform));

    // ── Analysis-tool scan coverage ────────────────────────────────────────────
    public static string ScanPartial(int scanned, int total) =>
        string.Format(Get(nameof(ScanPartial)), scanned, total);
    public static string ScanUnreadable(int count) =>
        string.Format(Get(nameof(ScanUnreadable)), count);
    public static string ScanFolderSkipped(string folder) =>
        string.Format(Get(nameof(ScanFolderSkipped)), folder);

    /// <summary>Files the indexing pass dropped for their size — said by <c>/index</c>, because
    /// the index is persisted and a chunk count reads as complete.</summary>
    public static string IndexFilesTooLarge(int count, int kilobytes) =>
        string.Format(Get(nameof(IndexFilesTooLarge)), count, kilobytes);
    public static string ScanFolderNotFollowed(string folder) =>
        string.Format(Get(nameof(ScanFolderNotFollowed)), folder);

    // ── /branch command (conversation branching) ───────────────────────────────
    public static string SlashHintBranch                     => Get(nameof(SlashHintBranch));
    public static string BranchNoConversation                => Get(nameof(BranchNoConversation));
    public static string BranchHeader(int turns)             => string.Format(Get(nameof(BranchHeader)), turns);
    public static string BranchUsage                         => Get(nameof(BranchUsage));
    public static string BranchTreeHeader                    => Get(nameof(BranchTreeHeader));
    public static string BranchCurrent                       => Get(nameof(BranchCurrent));
    public static string BranchForkedAt(int turn)            => string.Format(Get(nameof(BranchForkedAt)), turn);
    public static string BranchInvalidTurn(int turns)        => string.Format(Get(nameof(BranchInvalidTurn)), turns);
    public static string BranchUnknown(string name)          => string.Format(Get(nameof(BranchUnknown)), name);
    public static string BranchCreated(string branch, int turn, string parent) =>
        string.Format(Get(nameof(BranchCreated)), branch, turn, parent);
    public static string BranchSwitched(string name)         => string.Format(Get(nameof(BranchSwitched)), name);

    // ── /task command (background agent tasks) ─────────────────────────────────
    public static string SlashHintTask                       => Get(nameof(SlashHintTask));
    public static string SlashHintDebug                      => Get(nameof(SlashHintDebug));
    public static string TaskSubmitted(string id, string objective) =>
        string.Format(Get(nameof(TaskSubmitted)), id, objective);
    public static string TaskQueueFull(int max)              => string.Format(Get(nameof(TaskQueueFull)), max);
    public static string TaskUnknown(string id)              => string.Format(Get(nameof(TaskUnknown)), id);
    public static string TaskStopRequested(string id)        => string.Format(Get(nameof(TaskStopRequested)), id);
    public static string TaskAlreadyFinished(string id)      => string.Format(Get(nameof(TaskAlreadyFinished)), id);
    public static string TaskForgot(int count)               => string.Format(Get(nameof(TaskForgot)), count);
    public static string TaskListEmpty                       => Get(nameof(TaskListEmpty));
    public static string TaskListTitle                       => Get(nameof(TaskListTitle));
    public static string TaskListHint                        => Get(nameof(TaskListHint));
    public static string TaskColumnState                     => Get(nameof(TaskColumnState));
    public static string TaskColumnObjective                 => Get(nameof(TaskColumnObjective));
    public static string TaskStateQueued                     => Get(nameof(TaskStateQueued));
    public static string TaskStateQueuedAt(int position)     => string.Format(Get(nameof(TaskStateQueuedAt)), position);
    public static string TaskStateRunning                    => Get(nameof(TaskStateRunning));
    public static string TaskStateSucceeded                  => Get(nameof(TaskStateSucceeded));
    public static string TaskStateFailed                     => Get(nameof(TaskStateFailed));
    public static string TaskStateCancelled                  => Get(nameof(TaskStateCancelled));
    public static string TaskStillRunning                    => Get(nameof(TaskStillRunning));
    public static string TaskStepsTitle                      => Get(nameof(TaskStepsTitle));
    public static string TaskDuration(string duration)       => string.Format(Get(nameof(TaskDuration)), duration);
    public static string TaskFinishedNotice(string id)       => string.Format(Get(nameof(TaskFinishedNotice)), id);
    /// <summary>A background task that FAILED: the failure is named in the bubble, not only in `/task`.</summary>
    public static string TaskFailedNotice(string id, string reason) => string.Format(Get(nameof(TaskFailedNotice)), id, reason);
    /// <summary>A cancelled background task: nothing was produced, and "finished" suggested otherwise.</summary>
    public static string TaskCancelledNotice(string id)      => string.Format(Get(nameof(TaskCancelledNotice)), id);

    // ── Inline diff preview ─────────────────────────────────────────────────────
    public static string CodeActionPreviewShown              => Get(nameof(CodeActionPreviewShown));
    // The preview's three mute outcomes (in-proc). They are composed HERE, host-side, and travel
    // WITH the request: Inferpal.InProc does not reference the Core and therefore has neither
    // Strings nor satellites — giving it its own resources would add ten assemblies to the VSIX for
    // three sentences. See InlineDiffNotices.
    public static string InlineDiffPreviewAbandoned          => Get(nameof(InlineDiffPreviewAbandoned));
    public static string InlineDiffPreviewDrifted            => Get(nameof(InlineDiffPreviewDrifted));
    public static string InlineDiffPreviewApplyFailed        => Get(nameof(InlineDiffPreviewApplyFailed));

    // ── /hardware command ──────────────────────────────────────────────────────
    public static string HardwareUsage                       => Get(nameof(HardwareUsage));
    public static string HardwareBudgetSet(string gb)        => string.Format(Get(nameof(HardwareBudgetSet)), gb);
    public static string HardwareReportHeading               => Get(nameof(HardwareReportHeading));
    public static string HardwareBudgetLine(string gb)       => string.Format(Get(nameof(HardwareBudgetLine)), gb);
    public static string HardwareBudgetNotSet                => Get(nameof(HardwareBudgetNotSet));
    public static string HardwareLoadedLine(string gb, string ofBudget, string headroom) =>
        string.Format(Get(nameof(HardwareLoadedLine)), gb, ofBudget, headroom);
    public static string HardwareOfBudget(string gb)         => string.Format(Get(nameof(HardwareOfBudget)), gb);
    public static string HardwareHeadroom(string gb)         => string.Format(Get(nameof(HardwareHeadroom)), gb);
    public static string HardwareCompute(string kind)        => string.Format(Get(nameof(HardwareCompute)), kind);
    public static string HardwareLoadedNone                  => Get(nameof(HardwareLoadedNone));
    public static string HardwareLoadedUnknown(string url)   => string.Format(Get(nameof(HardwareLoadedUnknown)), url);
    public static string HardwareLoadedRefused(string url, string refusal) =>
        string.Format(Get(nameof(HardwareLoadedRefused)), url, refusal);
    public static string HardwareLoadedUnreported(int count) => string.Format(Get(nameof(HardwareLoadedUnreported)), count);
    public static string HardwareLoadedModelsTable           => Get(nameof(HardwareLoadedModelsTable));
    public static string HardwareInstalledModelsTable        => Get(nameof(HardwareInstalledModelsTable));
    public static string HardwareInstalledNote               => Get(nameof(HardwareInstalledNote));
    public static string HardwareContextHeading              => Get(nameof(HardwareContextHeading));
    public static string HardwareConfiguredCtx(int ctx)      => string.Format(Get(nameof(HardwareConfiguredCtx)), ctx);
    public static string HardwareRecommendedCtx(string model, int recommended, string modelMax) =>
        string.Format(Get(nameof(HardwareRecommendedCtx)), model, recommended, modelMax);
    public static string HardwareModelMax(int max)           => string.Format(Get(nameof(HardwareModelMax)), max);
    public static string HardwareCtxWarn(int configured, int recommended) =>
        string.Format(Get(nameof(HardwareCtxWarn)), configured, recommended);

    public static string HardwareCtxExceedsModel(int configured, int modelMax) =>
        string.Format(Get(nameof(HardwareCtxExceedsModel)), configured, modelMax);

    public static string HardwareCtxNoVramRecommendation(string model, int modelMax) =>
        string.Format(Get(nameof(HardwareCtxNoVramRecommendation)), model, modelMax);

    // ── RAG / semantic search ──────────────────────────────────────────────────
    public static string RagIndexNotReady(string status) =>
        string.Format(Get(nameof(RagIndexNotReady)), status);

    /// <summary>The <c>/index</c> report, served to both front-ends.</summary>
    public static string IndexTitle                      => Get(nameof(IndexTitle));
    /// <inheritdoc cref="IndexTitle"/>
    public static string IndexNoRoot                     => Get(nameof(IndexNoRoot));
    /// <inheritdoc cref="IndexTitle"/>
    public static string IndexRebuildStarted(string root) => string.Format(Get(nameof(IndexRebuildStarted)), root);
    /// <inheritdoc cref="IndexTitle"/>
    public static string IndexDisabled                   => Get(nameof(IndexDisabled));
    /// <inheritdoc cref="IndexTitle"/>
    public static string IndexEnableHint                 => Get(nameof(IndexEnableHint));
    /// <inheritdoc cref="IndexTitle"/>
    public static string IndexNotStarted                 => Get(nameof(IndexNotStarted));
    /// <inheritdoc cref="IndexTitle"/>
    public static string IndexBuildHint                  => Get(nameof(IndexBuildHint));
    /// <inheritdoc cref="IndexTitle"/>
    public static string IndexForceHint                  => Get(nameof(IndexForceHint));
    /// <inheritdoc cref="IndexTitle"/>
    public static string IndexStatusLine(string status)  => string.Format(Get(nameof(IndexStatusLine)), status);
    /// <inheritdoc cref="IndexTitle"/>
    public static string IndexChunksLine(string chunks)  => string.Format(Get(nameof(IndexChunksLine)), chunks);
    /// <inheritdoc cref="IndexTitle"/>
    public static string IndexRootLine(string root)      => string.Format(Get(nameof(IndexRootLine)), root);
    /// <inheritdoc cref="IndexTitle"/>
    public static string IndexModelLine(string model)    => string.Format(Get(nameof(IndexModelLine)), model);
    /// <inheritdoc cref="IndexTitle"/>
    public static string IndexTopKLine(int topK)         => string.Format(Get(nameof(IndexTopKLine)), topK);

    public static string RagNoResults(string query) =>
        string.Format(Get(nameof(RagNoResults)), query);
    // What is ADDED to a "nothing found" when the semantic half did not run. Without these two
    // sentences an absent capability and an absence of results came out as the same words — and a
    // model reading "nothing found" stops looking. See SearchDegradation.
    public static string SearchKeywordOnlySemanticOff => Get(nameof(SearchKeywordOnlySemanticOff));
    public static string SearchKeywordOnlyNoEmbeddingModel => Get(nameof(SearchKeywordOnlyNoEmbeddingModel));
    public static string SearchKeywordOnlyEmbeddingUnavailable(string model) =>
        string.Format(Get(nameof(SearchKeywordOnlyEmbeddingUnavailable)), model);

    // ── Agent memory ───────────────────────────────────────────────────────────
    public static string UpdateMemoryNoProject => Get(nameof(UpdateMemoryNoProject));
    public static string UpdateMemoryNoContent => Get(nameof(UpdateMemoryNoContent));
    public static string UpdateMemoryOk(string path, int chars)    => string.Format(Get(nameof(UpdateMemoryOk)),    path, chars);
    public static string UpdateMemoryClear(string path)            => string.Format(Get(nameof(UpdateMemoryClear)), path);
    public static string SlashMemoryNotFound(string path)          => string.Format(Get(nameof(SlashMemoryNotFound)), path);
    public static string SlashMemoryLoaded(string path, int chars, string preview) =>
        string.Format(Get(nameof(SlashMemoryLoaded)), path, chars, preview);

    // ── /onboard — committable project profile ───────────────────
    public static string SlashHintOnboard             => Get(nameof(SlashHintOnboard));
    public static string OnboardUsage                 => Get(nameof(OnboardUsage));
    public static string OnboardHeading               => Get(nameof(OnboardHeading));
    public static string OnboardNoProfile(string path) => string.Format(Get(nameof(OnboardNoProfile)), path);
    public static string OnboardProfileUnusable(string path, string detail) =>
        string.Format(Get(nameof(OnboardProfileUnusable)), path, detail);
    public static string OnboardProfileNotAnObject    => Get(nameof(OnboardProfileNotAnObject));
    public static string OnboardAppliedHeading        => Get(nameof(OnboardAppliedHeading));
    public static string OnboardRecommendedHeading    => Get(nameof(OnboardRecommendedHeading));
    public static string OnboardRecommendLine(string key, string proposed, string current) =>
        string.Format(Get(nameof(OnboardRecommendLine)), key, proposed, current);
    public static string OnboardApplyHint             => Get(nameof(OnboardApplyHint));
    public static string OnboardIgnoredHeading        => Get(nameof(OnboardIgnoredHeading));
    public static string OnboardContextPresent(string path) => string.Format(Get(nameof(OnboardContextPresent)), path);
    public static string OnboardContextMissing        => Get(nameof(OnboardContextMissing));
    public static string OnboardNothingToApply        => Get(nameof(OnboardNothingToApply));
    public static string OnboardApplied(string keys)  => string.Format(Get(nameof(OnboardApplied)), keys);
    public static string OnboardContextWindowRefused(string value) => string.Format(Get(nameof(OnboardContextWindowRefused)), value);
    public static string OnboardContextExists(string path) => string.Format(Get(nameof(OnboardContextExists)), path);
    public static string OnboardContextReadingLabel   => Get(nameof(OnboardContextReadingLabel));
    public static string OnboardContextDraftingLabel  => Get(nameof(OnboardContextDraftingLabel));
    public static string OnboardContextEmpty          => Get(nameof(OnboardContextEmpty));
    public static string OnboardContextGenerated(string path, int chars) =>
        string.Format(Get(nameof(OnboardContextGenerated)), path, chars);
    public static string FilePreviousVersionSaved(string path) =>
        string.Format(Get(nameof(FilePreviousVersionSaved)), path);
    public static string FileNotReplacedNoBackup(string path) =>
        string.Format(Get(nameof(FileNotReplacedNoBackup)), path);
    public static string OnboardContextSystemPrompt   => Get(nameof(OnboardContextSystemPrompt));
    public static string OnboardContextUserPrompt(string brief) =>
        string.Format(Get(nameof(OnboardContextUserPrompt)), brief);
    public static string OnboardProfileScaffolded(string path) =>
        string.Format(Get(nameof(OnboardProfileScaffolded)), path);
    public static string OnboardProfileExists(string path) =>
        string.Format(Get(nameof(OnboardProfileExists)), path);

    // ── /plan — plan mode and persistent plans ────────────────────
    public static string PlanUsage           => Get(nameof(PlanUsage));
    public static string PlanModeOn          => Get(nameof(PlanModeOn));
    public static string PlanModeOff         => Get(nameof(PlanModeOff));

    // ⚠ The twin pair of step mode, left as an English literal in the VM AND in the host when §17
    // localized the plan-mode one three lines above.
    public static string StepModeOn          => Get(nameof(StepModeOn));
    public static string StepModeOff         => Get(nameof(StepModeOff));
    public static string AgentPausedForStep  => Get(nameof(AgentPausedForStep));
    public static string NoAgentStepPaused   => Get(nameof(NoAgentStepPaused));

    /// <summary>Titles and filters of the VS window's file dialogs.</summary>
    public static string DialogAttachTitle   => Get(nameof(DialogAttachTitle));
    public static string DialogAllFiles      => Get(nameof(DialogAllFiles));
    public static string DialogExportTitle   => Get(nameof(DialogExportTitle));
    public static string DialogExportFilter  => Get(nameof(DialogExportFilter));

    /// <summary>Three failures that must not pass in silence.</summary>
    public static string SessionLoadFailed(string name)  => string.Format(Get(nameof(SessionLoadFailed)),  name);
    public static string PlanOpenFailed(string path)     => string.Format(Get(nameof(PlanOpenFailed)),     path);
    public static string SettingsSaveFailed(string error) => string.Format(Get(nameof(SettingsSaveFailed)), error);
    public static string PlanListEmpty       => Get(nameof(PlanListEmpty));
    public static string PlanListHeader      => Get(nameof(PlanListHeader));
    public static string PlanNoSteps         => Get(nameof(PlanNoSteps));
    public static string PlanNoActive        => Get(nameof(PlanNoActive));
    public static string PlanNoStepsInFile   => Get(nameof(PlanNoStepsInFile));
    public static string PlanStateDone       => Get(nameof(PlanStateDone));
    public static string PlanStateTodo       => Get(nameof(PlanStateTodo));
    public static string PlanSaved(string name, int steps, string path) =>
        string.Format(Get(nameof(PlanSaved)), name, steps, path);
    public static string PlanNotFound(string name) =>
        string.Format(Get(nameof(PlanNotFound)), name);
    public static string PlanHeader(string title, string name, int done, int total) =>
        string.Format(Get(nameof(PlanHeader)), title, name, done, total);
    public static string PlanNextStep(int number, string text) =>
        string.Format(Get(nameof(PlanNextStep)), number, text);
    public static string PlanComplete(string title) =>
        string.Format(Get(nameof(PlanComplete)), title);
    /// <summary>A plan whose file holds no step at all — which is not the same as a finished one.</summary>
    public static string PlanNoStepsYet(string title) =>
        string.Format(Get(nameof(PlanNoStepsYet)), title);
    public static string PlanStepTicked(int number, string text) =>
        string.Format(Get(nameof(PlanStepTicked)), number, text);
    public static string PlanStepUnticked(int number) =>
        string.Format(Get(nameof(PlanStepUnticked)), number);
    public static string PlanStepUnknown(int number, int total) =>
        string.Format(Get(nameof(PlanStepUnknown)), number, total);
    public static string PlanStepAlready(int number, string state) =>
        string.Format(Get(nameof(PlanStepAlready)), number, state);

    // ── /task proposals — background tasks that propose writes ────
    public static string TaskProposalsHeader(int count) =>
        string.Format(Get(nameof(TaskProposalsHeader)), count);
    public static string TaskProposalItem(int number, string tool, string details) =>
        string.Format(Get(nameof(TaskProposalItem)), number, tool, details);
    public static string TaskProposalsApplyHint(string taskId) =>
        string.Format(Get(nameof(TaskProposalsApplyHint)), taskId);
    public static string TaskSubmittedProposing(string id, string objective) =>
        string.Format(Get(nameof(TaskSubmittedProposing)), id, objective);
    public static string TaskNoProposals(string id) =>
        string.Format(Get(nameof(TaskNoProposals)), id);
    public static string TaskProposalUnknown(int number, int total) =>
        string.Format(Get(nameof(TaskProposalUnknown)), number, total);
    public static string TaskProposalStale(string path) =>
        string.Format(Get(nameof(TaskProposalStale)), path);
    public static string TaskProposalAlreadyApplied(string path) =>
        string.Format(Get(nameof(TaskProposalAlreadyApplied)), path);
    public static string TaskProposalFileMissing(string path) =>
        string.Format(Get(nameof(TaskProposalFileMissing)), path);

    public static string TaskProposalUnreadable(string path) =>
        string.Format(Get(nameof(TaskProposalUnreadable)), path);
    public static string TaskProposalUnusable(string path) =>
        string.Format(Get(nameof(TaskProposalUnusable)), path);
    public static string TaskProposalApplied(string path) =>
        string.Format(Get(nameof(TaskProposalApplied)), path);

    // ── Models page: the suggestion, what is loaded, unloading ─────────────────────────────────────────
    public static string ModelRoleChat => Get(nameof(ModelRoleChat));
    public static string ModelRoleAgent => Get(nameof(ModelRoleAgent));
    public static string ModelRoleCodeActions => Get(nameof(ModelRoleCodeActions));
    public static string ModelRoleInlineEdit => Get(nameof(ModelRoleInlineEdit));
    public static string ModelRoleAutocomplete => Get(nameof(ModelRoleAutocomplete));
    public static string ModelRoleUtility => Get(nameof(ModelRoleUtility));
    public static string ModelRoleCodeSearch => Get(nameof(ModelRoleCodeSearch));
    public static string SuggestReview => Get(nameof(SuggestReview));
    public static string SuggestNoChange => Get(nameof(SuggestNoChange));
    public static string SuggestOnlyEmbedding => Get(nameof(SuggestOnlyEmbedding));
    public static string SuggestNothingListed => Get(nameof(SuggestNothingListed));
    public static string SuggestChatRecommended(string a) =>
        string.Format(Get(nameof(SuggestChatRecommended)), a);
    public static string SuggestChatUsable(string a) =>
        string.Format(Get(nameof(SuggestChatUsable)), a);
    public static string SuggestChatNotRecommended(string a) =>
        string.Format(Get(nameof(SuggestChatNotRecommended)), a);
    public static string SuggestChatUnmeasured(string a) =>
        string.Format(Get(nameof(SuggestChatUnmeasured)), a);
    public static string SuggestInstallRecommended(string a) =>
        string.Format(Get(nameof(SuggestInstallRecommended)), a);
    public static string SuggestEmbeddingNone => Get(nameof(SuggestEmbeddingNone));
    public static string SuggestEmbeddingAuto(string a) =>
        string.Format(Get(nameof(SuggestEmbeddingAuto)), a);
    public static string SuggestEmbeddingReindex => Get(nameof(SuggestEmbeddingReindex));
    public static string SuggestFimMeasured(string a) =>
        string.Format(Get(nameof(SuggestFimMeasured)), a);
    public static string SuggestFimDoesNotFit(string a, string b, string c) =>
        string.Format(Get(nameof(SuggestFimDoesNotFit)), a, b, c);
    public static string SuggestFimChatCompletes => Get(nameof(SuggestFimChatCompletes));
    public static string SuggestFimSameAsChat => Get(nameof(SuggestFimSameAsChat));
    public static string SuggestOverrides(string a) =>
        string.Format(Get(nameof(SuggestOverrides)), a);
    public static string LoadedModelsUnknown => Get(nameof(LoadedModelsUnknown));
    public static string LoadedModelsNone => Get(nameof(LoadedModelsNone));
    public static string LoadedModelsUnreachable => Get(nameof(LoadedModelsUnreachable));
    public static string LoadedModelsCount(int a) =>
        string.Format(Get(nameof(LoadedModelsCount)), a);
    public static string LoadedModelVram(string a) =>
        string.Format(Get(nameof(LoadedModelVram)), a);
    public static string LoadedModelCpu => Get(nameof(LoadedModelCpu));
    public static string LoadedModelDiskSize(string a) =>
        string.Format(Get(nameof(LoadedModelDiskSize)), a);
    public static string LoadedModelContext(string a) =>
        string.Format(Get(nameof(LoadedModelContext)), a);
    public static string LoadedModelStays => Get(nameof(LoadedModelStays));
    public static string LoadedModelUnloadsIn(int a) =>
        string.Format(Get(nameof(LoadedModelUnloadsIn)), a);
    public static string LoadedModelUses(string a) =>
        string.Format(Get(nameof(LoadedModelUses)), a);
    public static string LoadedModelUnused => Get(nameof(LoadedModelUnused));
    public static string UnloadNotSupported => Get(nameof(UnloadNotSupported));
    public static string UnloadWhileAnswering => Get(nameof(UnloadWhileAnswering));
    public static string UnloadNothing => Get(nameof(UnloadNothing));
    public static string UnloadDone(string a) =>
        string.Format(Get(nameof(UnloadDone)), a);
    public static string UnloadKept(string a) =>
        string.Format(Get(nameof(UnloadKept)), a);
    public static string UnloadUnverified(string a) =>
        string.Format(Get(nameof(UnloadUnverified)), a);
    public static string BtnSuggestModels => Get(nameof(BtnSuggestModels));
    public static string HintSuggestModels => Get(nameof(HintSuggestModels));
    public static string SettingsSectionLoadedModels => Get(nameof(SettingsSectionLoadedModels));
    public static string NoteLoadedModels => Get(nameof(NoteLoadedModels));
    public static string BtnUnloadModel => Get(nameof(BtnUnloadModel));
    public static string BtnUnloadAll => Get(nameof(BtnUnloadAll));
    public static string BtnRefreshLoaded => Get(nameof(BtnRefreshLoaded));
}
