using System.Globalization;
using System.Resources;

namespace Inferpal.Localization;

internal static class Strings
{
    private static readonly ResourceManager _rm =
        new(typeof(Strings).Assembly.GetName().Name + ".Localization.Strings", typeof(Strings).Assembly);

    internal static CultureInfo? OverrideCulture { get; private set; }

    internal static void ApplyLanguage(string? code)
    {
        try
        {
            OverrideCulture = string.IsNullOrEmpty(code) ? null : CultureInfo.GetCultureInfo(code);
        }
        catch { OverrideCulture = null; }
    }

    private static string Get(string key)
    {
        var culture = OverrideCulture ?? CultureInfo.CurrentUICulture;
        try { return _rm.GetString(key, culture) ?? key; }
        catch { return key; }
    }

    // ── UI Labels ──────────────────────────────────────────────────────────────
    public static string LabelLanguage        => Get(nameof(LabelLanguage));
    public static string HintLanguage         => Get(nameof(HintLanguage));
    public static string LangAuto             => Get(nameof(LangAuto));
    public static string LabelProvider        => Get(nameof(LabelProvider));
    public static string HintProvider         => Get(nameof(HintProvider));
    public static string LabelApiKey          => Get(nameof(LabelApiKey));
    public static string HintApiKey           => Get(nameof(HintApiKey));
    public static string LabelUrl             => Get(nameof(LabelUrl));
    public static string HintUrl              => Get(nameof(HintUrl));
    public static string LabelChatModel       => Get(nameof(LabelChatModel));
    public static string HintChatModel        => Get(nameof(HintChatModel));
    public static string LabelSession         => Get(nameof(LabelSession));
    public static string BtnTest              => Get(nameof(BtnTest));
    public static string BtnClear             => Get(nameof(BtnClear));
    public static string BtnLoadSession       => Get(nameof(BtnLoadSession));
    public static string BtnSave              => Get(nameof(BtnSave));
    public static string BtnCancel            => Get(nameof(BtnCancel));
    public static string BtnSend              => Get(nameof(BtnSend));
    public static string TooltipRefreshModels => Get(nameof(TooltipRefreshModels));
    public static string TooltipExport        => Get(nameof(TooltipExport));
    public static string TooltipClear         => Get(nameof(TooltipClear));
    public static string TooltipCopy              => Get(nameof(TooltipCopy));
    public static string BtnFixWithAi             => Get(nameof(BtnFixWithAi));
    public static string BtnRestoreAll            => Get(nameof(BtnRestoreAll));
    public static string BtnRegenerate            => Get(nameof(BtnRegenerate));
    public static string BtnResume                => Get(nameof(BtnResume));
    public static string MultiFileRecapTitle(int count, string files) =>
        string.Format(Get(nameof(MultiFileRecapTitle)), count, files);
    public static string BtnDismiss               => Get(nameof(BtnDismiss));

    public static string PromptFixErrors(string errors) =>
        string.Format(Get(nameof(PromptFixErrors)), errors);

    // ── Editor context menu prompts ────────────────────────────────────────────
    // Legacy two-arg overloads kept for reference; all active call sites use the
    // attachment-based single-arg variants below.
    public static string PromptExplainSelection(string fileName, string codeBlock) =>
        string.Format(Get(nameof(PromptExplainSelection)), fileName, codeBlock);
    public static string PromptRefactorSelection(string fileName, string codeBlock) =>
        string.Format(Get(nameof(PromptRefactorSelection)), fileName, codeBlock);
    public static string PromptAddTestsSelection(string fileName, string codeBlock) =>
        string.Format(Get(nameof(PromptAddTestsSelection)), fileName, codeBlock);
    public static string PromptFixSelection(string fileName, string codeBlock) =>
        string.Format(Get(nameof(PromptFixSelection)), fileName, codeBlock);
    public static string PromptAddDocstringSelection(string fileName, string codeBlock) =>
        string.Format(Get(nameof(PromptAddDocstringSelection)), fileName, codeBlock);
    public static string PromptReviewSelection(string fileName, string codeBlock) =>
        string.Format(Get(nameof(PromptReviewSelection)), fileName, codeBlock);

    // ── Code-action prompts — code delivered as AttachmentItem ─────────────────
    // {1} (code block) is intentionally omitted: the file is attached separately so
    // it appears as a chip in the chat and is formatted uniformly by SendCoreAsync.
    public static string PromptExplain(string fileName) =>
        string.Format(Get(nameof(PromptExplainSelection)), fileName, string.Empty).TrimEnd();
    public static string PromptFix(string fileName) =>
        string.Format(Get(nameof(PromptFixSelection)), fileName, string.Empty).TrimEnd();
    public static string PromptReview(string fileName) =>
        string.Format(Get(nameof(PromptReviewSelection)), fileName, string.Empty).TrimEnd();
    public static string PromptRefactor(string fileName) =>
        string.Format(Get(nameof(PromptRefactorSelection)), fileName, string.Empty).TrimEnd();
    public static string PromptAddTests(string fileName) =>
        string.Format(Get(nameof(PromptAddTestsSelection)), fileName, string.Empty).TrimEnd();
    public static string PromptAddDocstring(string fileName) =>
        string.Format(Get(nameof(PromptAddDocstringSelection)), fileName, string.Empty).TrimEnd();
    /// <summary>
    /// Builds the enhanced /doc prompt, injecting the semantic context block
    /// (namespace, type hierarchy, overrides, interface contracts) as <paramref name="contextBlock"/>.
    /// </summary>
    public static string PromptAddDocstringEnhanced(string fileName, string contextBlock) =>
        string.Format(Get(nameof(PromptAddDocstringEnhanced)), fileName, contextBlock).TrimEnd();

    // ── /test — tests generated into a separate file ───────────────────────────
    public static string TestsGenerated(string fileName) =>
        string.Format(Get(nameof(TestsGenerated)), fileName);
    public static string TestsExtended(string fileName) =>
        string.Format(Get(nameof(TestsExtended)), fileName);
    public static string TestsGenerateFailed => Get(nameof(TestsGenerateFailed));

    // ── Code actions: "nothing to do" verdicts (the code is already good) ───────
    public static string RefactorNoChange => Get(nameof(RefactorNoChange));
    public static string FixNoChange      => Get(nameof(FixNoChange));
    public static string DocNoChange      => Get(nameof(DocNoChange));
    public static string TestsNoChange    => Get(nameof(TestsNoChange));

    public static string LabelCommandTimeout      => Get(nameof(LabelCommandTimeout));
    public static string HintCommandTimeout       => Get(nameof(HintCommandTimeout));
    public static string LabelToolBubblesExpanded      => Get(nameof(LabelToolBubblesExpanded));
    public static string HintToolBubblesExpanded       => Get(nameof(HintToolBubblesExpanded));
    public static string LabelSecurityAlertsDisabled   => Get(nameof(LabelSecurityAlertsDisabled));
    public static string HintSecurityAlertsDisabled    => Get(nameof(HintSecurityAlertsDisabled));
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
    public static string LabelRowAdvanced            => Get(nameof(LabelRowAdvanced));
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
    public static string SettingsTabConnection          => Get(nameof(SettingsTabConnection));
    public static string SettingsTabBehavior            => Get(nameof(SettingsTabBehavior));
    public static string SettingsTabContext             => Get(nameof(SettingsTabContext));
    public static string SettingsTabTools               => Get(nameof(SettingsTabTools));
    public static string SectionBehavior                => Get(nameof(SectionBehavior));
    public static string SectionContext                 => Get(nameof(SectionContext));
    public static string SectionPersona                 => Get(nameof(SectionPersona));
    public static string SectionInterface               => Get(nameof(SectionInterface));
    public static string SectionInlineCompletions            => Get(nameof(SectionInlineCompletions));
    public static string LabelInlineCompletionMode           => Get(nameof(LabelInlineCompletionMode));
    public static string HintInlineCompletionMode            => Get(nameof(HintInlineCompletionMode));
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
    public static string LabelModelRolesAdvanced             => Get(nameof(LabelModelRolesAdvanced));
    public static string HintModelRolesAdvanced              => Get(nameof(HintModelRolesAdvanced));
    public static string LabelAdvancedBehavior               => Get(nameof(LabelAdvancedBehavior));
    public static string TooltipAgentMode                    => Get(nameof(TooltipAgentMode));
    public static string LabelModeChat                       => Get(nameof(LabelModeChat));
    public static string LabelModeAgent                      => Get(nameof(LabelModeAgent));
    public static string WelcomeSubtitle                     => Get(nameof(WelcomeSubtitle));
    public static string WelcomeCardExplain                  => Get(nameof(WelcomeCardExplain));
    public static string WelcomeCardFix                      => Get(nameof(WelcomeCardFix));
    public static string WelcomeCardTest                     => Get(nameof(WelcomeCardTest));
    public static string WelcomeCardHelp                     => Get(nameof(WelcomeCardHelp));
    public static string BuildBannerTitle                    => Get(nameof(BuildBannerTitle));
    public static string BuildBannerDismiss                  => Get(nameof(BuildBannerDismiss));
    public static string BuildBannerFix                      => Get(nameof(BuildBannerFix));
    public static string InlineEditDlgTitle                  => Get(nameof(InlineEditDlgTitle));
    public static string InlineEditDlgHeader                 => Get(nameof(InlineEditDlgHeader));
    public static string InlineEditDlgHint                   => Get(nameof(InlineEditDlgHint));
    public static string InlineEditWorking                   => Get(nameof(InlineEditWorking));
    public static string BtnApply                            => Get(nameof(BtnApply));

    // ── Settings — RAG section ───────────────────────────────────────────────────
    public static string SectionRag             => Get(nameof(SectionRag));
    public static string LabelRagEnabled        => Get(nameof(LabelRagEnabled));
    public static string HintRagEnabled         => Get(nameof(HintRagEnabled));
    public static string LabelRagAutoContext    => Get(nameof(LabelRagAutoContext));
    public static string HintRagAutoContext     => Get(nameof(HintRagAutoContext));
    public static string LabelRagEmbeddingModel => Get(nameof(LabelRagEmbeddingModel));
    public static string HintRagEmbeddingModel  => Get(nameof(HintRagEmbeddingModel));
    public static string LabelRagTopK                  => Get(nameof(LabelRagTopK));
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
    public static string McpNoServers           => Get(nameof(McpNoServers));
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
    public static string McpAdvancedJson             => Get(nameof(McpAdvancedJson));
    public static string McpImportJson               => Get(nameof(McpImportJson));
    public static string McpServerDisabled           => Get(nameof(McpServerDisabled));
    public static string McpValidationNameCommand    => Get(nameof(McpValidationNameCommand));
    public static string McpValidationNameUrl        => Get(nameof(McpValidationNameUrl));
    public static string BtnMcpAuthorize             => Get(nameof(BtnMcpAuthorize));
    public static string McpAuthRequired             => Get(nameof(McpAuthRequired));
    public static string McpValidationDuplicate      => Get(nameof(McpValidationDuplicate));
    public static string HintMcpEditServer           => Get(nameof(HintMcpEditServer));
    public static string HintMcpDeleteServer         => Get(nameof(HintMcpDeleteServer));
    // List view-mode toggle, empty-state titles, and the relocated "Commands & tools" section header.
    public static string ViewList                    => Get(nameof(ViewList));
    public static string ViewJson                    => Get(nameof(ViewJson));
    public static string ViewText                    => Get(nameof(ViewText));
    public static string McpEmptyTitle               => Get(nameof(McpEmptyTitle));
    public static string PinnedEmptyTitle            => Get(nameof(PinnedEmptyTitle));
    public static string SlashEmptyTitle             => Get(nameof(SlashEmptyTitle));
    public static string ToolEmptyTitle              => Get(nameof(ToolEmptyTitle));
    public static string SectionCommandsTools        => Get(nameof(SectionCommandsTools));
    public static string DocsListHeader              => Get(nameof(DocsListHeader));
    public static string DocsNoSites                 => Get(nameof(DocsNoSites));
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

    public static string MsgContextCompactionFallback => Get(nameof(MsgContextCompactionFallback));
    public static string MsgOodaRecap(int turn, string summary) =>
        string.Format(Get(nameof(MsgOodaRecap)), turn) + "\n\n" + summary;

    public static string MsgContextTruncated(int removed, int keepTurns) =>
        string.Format(Get(nameof(MsgContextTruncated)), removed, keepTurns);
    public static string TooltipSessionPicker  => Get(nameof(TooltipSessionPicker));
    public static string TooltipLoadSession    => Get(nameof(TooltipLoadSession));
    public static string TooltipDeleteSession  => Get(nameof(TooltipDeleteSession));
    public static string DeleteSessionConfirm(string name) => string.Format(Get(nameof(DeleteSessionConfirm)), name);
    public static string HintSend                => Get(nameof(HintSend));
    public static string TooltipAttachFile       => Get(nameof(TooltipAttachFile));
    public static string TooltipAttachSelection  => Get(nameof(TooltipAttachSelection));
    public static string TooltipBrowseFile       => Get(nameof(TooltipBrowseFile));
    public static string TooltipPinFile          => Get(nameof(TooltipPinFile));
    public static string MenuAddContext          => Get(nameof(MenuAddContext));
    public static string MenuAttachFile          => Get(nameof(MenuAttachFile));
    public static string MenuAttachSelection     => Get(nameof(MenuAttachSelection));
    public static string MenuBrowseFile          => Get(nameof(MenuBrowseFile));
    public static string MenuPinFile             => Get(nameof(MenuPinFile));
    public static string TooltipPinChip          => Get(nameof(TooltipPinChip));
    public static string TooltipSearchConversation => Get(nameof(TooltipSearchConversation));
    public static string TooltipCloseSearch        => Get(nameof(TooltipCloseSearch));
    public static string TooltipSaveSnippet        => Get(nameof(TooltipSaveSnippet));
    public static string TooltipStepMode           => Get(nameof(TooltipStepMode));
    public static string TooltipPlanMode           => Get(nameof(TooltipPlanMode));

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
    public static string LabelTaskTimeoutDeep     => Get(nameof(LabelTaskTimeoutDeep));
    public static string HintTaskTimeoutDeep      => Get(nameof(HintTaskTimeoutDeep));

    // ── ViewModel ──────────────────────────────────────────────────────────────
    public static string SystemPrompt          => Get(nameof(SystemPrompt));
    public static string StatusConnecting      => Get(nameof(StatusConnecting));
    public static string StatusConnected       => Get(nameof(StatusConnected));
    public static string StatusUnreachable     => Get(nameof(StatusUnreachable));
    public static string StatusThinking        => Get(nameof(StatusThinking));
    public static string StatusAgentPlanning   => Get(nameof(StatusAgentPlanning));
    public static string StatusAgentObserving  => Get(nameof(StatusAgentObserving));
    public static string StatusAgentSynthesizing => Get(nameof(StatusAgentSynthesizing));

    public static string StatusCallingTool(string toolName) =>
        string.Format(Get(nameof(StatusCallingTool)), toolName);

    // ── Agent Mode ─────────────────────────────────────────────────────────────
    /// <summary>Prompt injected as a user message to ask the model for a JSON plan.</summary>
    public static string AgentPlanPrompt => Get(nameof(AgentPlanPrompt));
    /// <summary>User message injected after the plan to start execution.</summary>
    public static string AgentExecutePlan => Get(nameof(AgentExecutePlan));
    /// <summary>Fallback plan goal when JSON parsing fails.</summary>
    public static string AgentPlanFallbackGoal => Get(nameof(AgentPlanFallbackGoal));
    /// <summary>Single-step description for the fallback plan.</summary>
    public static string AgentPlanFallbackStep => Get(nameof(AgentPlanFallbackStep));
    /// <summary>Label shown above the live plan bubble.</summary>
    public static string AgentPlanLabel => Get(nameof(AgentPlanLabel));

    /// <summary>OBSERVE injection injected between Act iterations.</summary>
    /// <param name="iteration">1-based current iteration number.</param>
    /// <param name="max">Maximum allowed iterations.</param>
    /// <param name="toolNames">Comma-separated names of tools executed this round.</param>
    /// <param name="remaining">Steps still left in the plan.</param>
    public static string AgentObservePrompt(int iteration, int max, string toolNames, int remaining) =>
        string.Format(Get(nameof(AgentObservePrompt)), iteration, max, toolNames, remaining);
    /// <summary>
    /// OBSERVE injection used instead of <see cref="AgentObservePrompt"/> once every plan step is
    /// done: the generic variant opens with "call the tool for the next step", which pushes the
    /// model into a gratuitous extra tool call (e.g. read_file on the active document) whose
    /// result then displaces the real answer. This variant re-anchors on the user's request and
    /// asks for the final answer instead.
    /// </summary>
    /// <param name="iteration">1-based current iteration number.</param>
    /// <param name="max">Maximum allowed iterations.</param>
    /// <param name="toolNames">Comma-separated names of tools executed this round.</param>
    /// <param name="task">Short quote of the user's current request (see AgentOrchestrator.TaskSnippet).</param>
    public static string AgentObservePromptComplete(int iteration, int max, string toolNames, string task) =>
        string.Format(Get(nameof(AgentObservePromptComplete)), iteration, max, toolNames, task);
    /// <summary>
    /// One-shot nudge injected when the first ACT response contained text but no tool calls.
    /// Forces the model to invoke a tool instead of narrating its intentions.
    /// </summary>
    public static string AgentNudgeToolCall => Get(nameof(AgentNudgeToolCall));
    /// <summary>
    /// Final-synthesis prompt injected (no tools) when the loop ended without a printable answer
    /// but tools ran: asks the model to write the complete answer from the gathered results.
    /// </summary>
    /// <param name="task">Short quote of the user's current request (see AgentOrchestrator.TaskSnippet)
    /// so the model answers the latest question rather than an earlier turn.</param>
    public static string AgentSynthesizePrompt(string task) =>
        string.Format(Get(nameof(AgentSynthesizePrompt)), task);
    public static string MsgCancelled          => Get(nameof(MsgCancelled));
    public static string MsgTruncated          => Get(nameof(MsgTruncated));
    public static string DefaultSessionSnippet => Get(nameof(DefaultSessionSnippet));
    public static string MsgIterationLimit     => Get(nameof(MsgIterationLimit));
    public static string MsgLoopDetected      => Get(nameof(MsgLoopDetected));
    public static string MsgCircuitOpen       => Get(nameof(MsgCircuitOpen));
    public static string TokenUsage(string last, string session) =>
        string.Format(Get(nameof(TokenUsage)), last, session);

    public static string MsgAgentDone(string toolSummary) =>
        string.Format(Get(nameof(MsgAgentDone)), toolSummary);

    public static string MsgNoUrl => Get(nameof(MsgNoUrl));
    public static string MsgEmptyResponse => Get(nameof(MsgEmptyResponse));

    public static string MsgError(string message) =>
        string.Format(Get(nameof(MsgError)), message);

    public static string MsgTimeout(string url) =>
        string.Format(Get(nameof(MsgTimeout)), url);

    public static string MsgUnreachable(string url) =>
        string.Format(Get(nameof(MsgUnreachable)), url);

    public static string MsgServerError(string url, string detail) =>
        string.Format(Get(nameof(MsgServerError)), url, detail);

    public static string MsgContextOverflow(string detail) =>
        string.Format(Get(nameof(MsgContextOverflow)), detail);

    public static string MsgContextWontFit(int estimateTokens, int loadedContext) =>
        string.Format(Get(nameof(MsgContextWontFit)), estimateTokens, loadedContext);

    public static string MsgConnectionGuardFailed(string url) =>
        string.Format(Get(nameof(MsgConnectionGuardFailed)), url);

    public static string MsgHeartbeatRestored    => Get(nameof(MsgHeartbeatRestored));
    public static string TooltipRetryConnection  => Get(nameof(TooltipRetryConnection));

    public static string MsgToolOutput(string input, string output) =>
        string.Format(Get(nameof(MsgToolOutput)), input, output);

    // ── Approval ───────────────────────────────────────────────────────────────
    public static string ApprovalMessage(string toolName, string details) =>
        string.Format(Get(nameof(ApprovalMessage)), toolName, details);

    public static string ApprovalAllowOnce   => Get(nameof(ApprovalAllowOnce));
    public static string ApprovalAlwaysAllow => Get(nameof(ApprovalAlwaysAllow));
    public static string ApprovalDeny        => Get(nameof(ApprovalDeny));

    /// <summary>Action blocked by a user-defined <c>deny</c> permission rule.</summary>
    public static string PermissionBlockedRule(string subject) =>
        string.Format(Get(nameof(PermissionBlockedRule)), subject);

    /// <summary>Action blocked by the built-in, non-bypassable catastrophic-command denylist.</summary>
    public static string PermissionBlockedHard(string subject) =>
        string.Format(Get(nameof(PermissionBlockedHard)), subject);

    // ── Tool messages ──────────────────────────────────────────────────────────
    public static string ToolPathRequired  => Get(nameof(ToolPathRequired));
    public static string NoResults         => Get(nameof(NoResults));
    public static string WriteCancelled    => Get(nameof(WriteCancelled));
    public static string DeleteCancelled   => Get(nameof(DeleteCancelled));
    public static string RunCancelled      => Get(nameof(RunCancelled));
    public static string DiagNoProject     => Get(nameof(DiagNoProject));
    public static string ActiveDocNoContext => Get(nameof(ActiveDocNoContext));
    public static string ActiveDocNoFile   => Get(nameof(ActiveDocNoFile));

    public static string ToolFileNotFound(string path) =>
        string.Format(Get(nameof(ToolFileNotFound)), path);

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

    public static string DiffCancelled => Get(nameof(DiffCancelled));

    public static string DiffOldNotFound(string path) =>
        string.Format(Get(nameof(DiffOldNotFound)), path);

    public static string DiffAmbiguous(int count, string path) =>
        string.Format(Get(nameof(DiffAmbiguous)), count, path);

    public static string DiffOk(string path) =>
        string.Format(Get(nameof(DiffOk)), path);

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

    public static string DiagSummary(int errors, int warnings, string filename) =>
        string.Format(Get(nameof(DiagSummary)), errors, warnings, filename);

    public static string ActiveDocResult(string path, string content) =>
        string.Format(Get(nameof(ActiveDocResult)), path, content);

    // ── Attachment / Browse ────────────────────────────────────────────────────
    public static string AttachError(string msg)              => string.Format(Get(nameof(AttachError)),              msg);
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
    public static string BrowseError(string msg)              => string.Format(Get(nameof(BrowseError)),              msg);
    public static string PinLimitReached(int max)            => string.Format(Get(nameof(PinLimitReached)),           max);

    // ── Export ─────────────────────────────────────────────────────────────────
    public static string ExportNoMessages        => Get(nameof(ExportNoMessages));
    public static string ExportSuccess(string f) => string.Format(Get(nameof(ExportSuccess)), f);
    public static string ExportFailed(string e)  => string.Format(Get(nameof(ExportFailed)),  e);

    // ── Slash commands ─────────────────────────────────────────────────────────
    public static string SlashModelCurrent(string model) => string.Format(Get(nameof(SlashModelCurrent)), model);
    public static string SlashModelChanged(string model) => string.Format(Get(nameof(SlashModelChanged)), model);
    public static string SlashToolsCurrent(string state) => string.Format(Get(nameof(SlashToolsCurrent)), state);
    public static string SlashToolsChanged(string state) => string.Format(Get(nameof(SlashToolsChanged)), state);
    public static string SlashNoActiveDocument            => Get(nameof(SlashNoActiveDocument));
    public static string SlashUsage(string syntax)        => string.Format(Get(nameof(SlashUsage)),        syntax);
    public static string SlashUsageRestore                => Get(nameof(SlashUsageRestore));
    public static string SlashHelp(string unknownCmd)     => string.Format(Get(nameof(SlashHelp)),         unknownCmd);
    public static string SlashHelpAll                     => Get(nameof(SlashHelpAll));
    public static string HistoryNoSessions                => Get(nameof(HistoryNoSessions));
    public static string HistoryNoResults(string term)    => string.Format(Get(nameof(HistoryNoResults)),   term);

    // ── Project context ────────────────────────────────────────────────────────
    public static string SlashContextNoSln                              => Get(nameof(SlashContextNoSln));
    public static string SlashContextNotFound(string path)             => string.Format(Get(nameof(SlashContextNotFound)), path);
    public static string SlashContextLoaded(string path, int chars, string preview) =>
        string.Format(Get(nameof(SlashContextLoaded)), path, chars, preview);

    // ── Git / Solution tool outputs ────────────────────────────────────────────
    public static string GitNotRepo                        => Get(nameof(GitNotRepo));
    public static string SolutionNoSln                     => Get(nameof(SolutionNoSln));
    public static string SolutionPathNotFound(string path) => string.Format(Get(nameof(SolutionPathNotFound)), path);

    // ── Editor insertion / replacement ─────────────────────────────────────────
    public static string InsertOk(string path, int chars)  => string.Format(Get(nameof(InsertOk)),  path, chars);
    public static string ReplaceOk(string path, int chars) => string.Format(Get(nameof(ReplaceOk)), path, chars);

    // ── Fix-build loop ─────────────────────────────────────────────────────────
    public static string FixBuildSuccess(int rounds)        => string.Format(Get(nameof(FixBuildSuccess)),    rounds);
    public static string FixBuildGiveUp(int maxRounds)      => string.Format(Get(nameof(FixBuildGiveUp)),     maxRounds);
    public static string BuildFailedProposal(int errorCount) => string.Format(Get(nameof(BuildFailedProposal)), errorCount);

    // ── Smart Fix Protocol ─────────────────────────────────────────────────────
    public static string SmartFixBuildOk                                   => Get(nameof(SmartFixBuildOk));
    public static string SmartFixBuildErrors(int count, string errorLines) => string.Format(Get(nameof(SmartFixBuildErrors)), count, errorLines);
    public static string SmartFixTimeout                                    => Get(nameof(SmartFixTimeout));
    public static string LabelSmartFixEnabled                               => Get(nameof(LabelSmartFixEnabled));
    public static string HintSmartFixEnabled                                => Get(nameof(HintSmartFixEnabled));

    // ── Git commit assistant ───────────────────────────────────────────────────
    public static string CommitNothingToCommit  => Get(nameof(CommitNothingToCommit));
    public static string CommitNothingStaged    => Get(nameof(CommitNothingStaged));
    public static string CommitProposingLabel   => Get(nameof(CommitProposingLabel));
    public static string CommitConfirmHint      => Get(nameof(CommitConfirmHint));

    // ── Rules & Checks (.inferpal/rules, .inferpal/checks) ────────────────
    public static string RulesNone               => Get(nameof(RulesNone));
    public static string ChecksNone              => Get(nameof(ChecksNone));
    public static string RulesListHeader         => Get(nameof(RulesListHeader));
    public static string ChecksListHeader        => Get(nameof(ChecksListHeader));
    public static string CheckNoDiff             => Get(nameof(CheckNoDiff));
    public static string CheckReviewingLabel     => Get(nameof(CheckReviewingLabel));
    public static string CheckReviewSystemPrompt => Get(nameof(CheckReviewSystemPrompt));
    public static string CheckUnknownName(string name) => string.Format(Get(nameof(CheckUnknownName)), name);
    public static string RulesScaffolded(string path)  => string.Format(Get(nameof(RulesScaffolded)),  path);
    public static string ChecksScaffolded(string path) => string.Format(Get(nameof(ChecksScaffolded)), path);
    public static string PromptsNone             => Get(nameof(PromptsNone));
    public static string PromptsListHeader       => Get(nameof(PromptsListHeader));
    public static string PromptsScaffolded(string path) => string.Format(Get(nameof(PromptsScaffolded)), path);

    // ── analyze_impact tool ───────────────────────────────────────────────────
    public static string ImpactHeader(string fileName)                          => string.Format(Get(nameof(ImpactHeader)),         fileName);
    public static string ImpactNoPublicApi(string fileName)                     => string.Format(Get(nameof(ImpactNoPublicApi)),    fileName);
    public static string ImpactSymbolNotFound(string symbol, string fileName)   => string.Format(Get(nameof(ImpactSymbolNotFound)), symbol, fileName);
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
    public static string MsgFirstRunNoModels => Get(nameof(MsgFirstRunNoModels));
    public static string MsgFirstRunBackendDown(string url) =>
        string.Format(Get(nameof(MsgFirstRunBackendDown)), url);
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
    public static string SnippetsNone                        => Get(nameof(SnippetsNone));

    public static string NoteUsage                           => Get(nameof(NoteUsage));
    public static string NoteSaved(string text)              => string.Format(Get(nameof(NoteSaved)), text);
    public static string NotesCleared                        => Get(nameof(NotesCleared));
    public static string NotesNoneYet                        => Get(nameof(NotesNoneYet));
    public static string NotesEmpty                          => Get(nameof(NotesEmpty));
    public static string NotesHeading                        => Get(nameof(NotesHeading));

    public static string PHistoryNoEntry(int idx)            => string.Format(Get(nameof(PHistoryNoEntry)), idx);
    public static string PHistoryEmpty                       => Get(nameof(PHistoryEmpty));
    public static string PHistoryNoMatch(string? term)       => string.Format(Get(nameof(PHistoryNoMatch)), term);

    public static string ModelsDeleteUsage                   => Get(nameof(ModelsDeleteUsage));
    public static string ModelsDeleted(string model)         => string.Format(Get(nameof(ModelsDeleted)), model);
    public static string ModelsDeleteFailed(string model)    => string.Format(Get(nameof(ModelsDeleteFailed)), model);
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
    public static string DiagnosticsCleared                  => Get(nameof(DiagnosticsCleared));
    public static string DiagnosticsFileOn                   => Get(nameof(DiagnosticsFileOn));
    public static string DiagnosticsFileOff                  => Get(nameof(DiagnosticsFileOff));

    // ── /undo-run command ───────────────────────────────────────────────────────
    public static string SlashHintUndoRun                    => Get(nameof(SlashHintUndoRun));
    public static string UndoRunNone                         => Get(nameof(UndoRunNone));
    public static string UndoRunListHeader(int count)        => string.Format(Get(nameof(UndoRunListHeader)), count);
    public static string UndoRunResult(int restored, int deleted) => string.Format(Get(nameof(UndoRunResult)), restored, deleted);

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

    // ── RAG / semantic search ──────────────────────────────────────────────────
    public static string RagIndexNotReady(string status) =>
        string.Format(Get(nameof(RagIndexNotReady)), status);

    public static string RagNoResults(string query) =>
        string.Format(Get(nameof(RagNoResults)), query);

    // ── Agent memory ───────────────────────────────────────────────────────────
    public static string UpdateMemoryNoProject => Get(nameof(UpdateMemoryNoProject));
    public static string UpdateMemoryNoContent => Get(nameof(UpdateMemoryNoContent));
    public static string UpdateMemoryOk(string path, int chars)    => string.Format(Get(nameof(UpdateMemoryOk)),    path, chars);
    public static string UpdateMemoryClear(string path)            => string.Format(Get(nameof(UpdateMemoryClear)), path);
    public static string SlashMemoryNotFound(string path)          => string.Format(Get(nameof(SlashMemoryNotFound)), path);
    public static string SlashMemoryLoaded(string path, int chars, string preview) =>
        string.Format(Get(nameof(SlashMemoryLoaded)), path, chars, preview);
}
