using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Inferpal.Commands;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services;
using Inferpal.Services.Docs;
using Inferpal.Services.Rag;
using Inferpal.Services.Tools;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Editor;
using Microsoft.VisualStudio.Extensibility.Shell;
using Microsoft.VisualStudio.Extensibility.Settings;
using Microsoft.VisualStudio.Extensibility.UI;
using Microsoft.VisualStudio.Threading;

namespace Inferpal.ToolWindow;

internal partial class InferpalToolWindowData
{
    #region Construction & propriétés liées

    public InferpalToolWindowData(IInferenceProvider client, ToolRegistry tools, InferpalConfig config,
        VisualStudioExtensibility extensibility, VsContextHolder contextHolder,
        ProjectIndexService indexService, ModelLifetimeService lifetimeService,
        VsBuildMonitor buildMonitor, DocsIndexService docsIndex)
    {
        _client          = client;
        _orchestrator    = new AgentOrchestrator(client, config);
        _tools           = tools;
        _config          = config;
        _agentMode       = config.AgentModeEnabled;   // main-window switch mirrors the persisted setting
        _vs              = extensibility;
        _contextHolder   = contextHolder;
        _indexService    = indexService;
        _docsIndex       = docsIndex;
        _lifetimeService = lifetimeService;
        _buildMonitor    = buildMonitor;
        _buildMonitor.BuildFailed += OnVsBuildFailed;
        _lifetimeService.ModelsRefreshed += OnModelsRefreshed;
        _config.AgentModeEnabledChanged += OnAgentModeConfigChanged;   // live-sync with the Settings checkbox
        _config.LanguageChanged         += OnLanguageChanged;          // re-localize labels when the Settings language changes
        _baseSystemPrompt = BuildSystemPrompt();
        _history          = [new("system", _baseSystemPrompt)];
        LoadPromptHistory();

        SendCommand               = new AsyncCommand(SendAsync);
        CancelCommand             = new AsyncCommand(CancelAsync);
        ExportCommand             = new AsyncCommand(ExportAsync);
        ClearCommand              = new AsyncCommand(ClearAsync);
        LoadSessionCommand        = new AsyncCommand(LoadSessionAsync);
        DeleteSessionCommand      = new AsyncCommand(DeleteSessionAsync) { CanExecute = false };
        ToggleSessionPanelCommand = new AsyncCommand(ToggleSessionPanelAsync);
        ScrollToBottomCommand     = new AsyncCommand(OnScrollToBottomAsync);
        AttachFileCommand         = new AsyncCommand(AttachFileAsync);
        AttachSelectionCommand    = new AsyncCommand(AttachSelectionAsync);
        BrowseFileCommand         = new AsyncCommand(BrowseFileAsync);
        PinFileCommand            = new AsyncCommand(PinFileAsync);
        ToggleAttachMenuCommand   = new AsyncCommand((_, _) => { IsAttachMenuOpen = !IsAttachMenuOpen; return Task.CompletedTask; });
        ToggleSearchCommand        = new AsyncCommand(ToggleSearchAsync);
        ClearSearchCommand         = new AsyncCommand(ClearSearchAsync);
        ToggleStepModeCommand      = new AsyncCommand((_, _) => ToggleStepModeAsync());
        TogglePlanModeCommand      = new AsyncCommand((_, _) => TogglePlanModeAsync());
        ToggleAgentModeCommand     = new AsyncCommand((_, _) => ToggleAgentModeAsync());
        RunSuggestionCommand       = new AsyncCommand(RunSuggestionAsync);
        HistoryUpCommand          = new AsyncCommand(HistoryUpAsync)   { CanExecute = false };
        HistoryDownCommand        = new AsyncCommand(HistoryDownAsync) { CanExecute = false };
        RetryConnectionCommand    = new AsyncCommand(RetryConnectionAsync);
        DismissBuildBannerCommand = new AsyncCommand(DismissBuildBannerAsync);
        FixBuildBannerCommand     = new AsyncCommand(FixBuildBannerAsync);

        ApplyThemeColors(VsThemeDetector.OsDarkMode());
        LoadPinnedFilesFromConfig();
        _ = InitThemeAsync(extensibility);

        _contextHolder.PendingPromptAvailable += OnPendingPromptAvailable;
        _contextHolder.ActiveFileChanged      += OnActiveFileChanged;
        ConsumePendingPrompt();

        Messages.Add(_anchor0);
        Messages.Add(_anchor1);
        // Welcome screen shows whenever the conversation holds nothing but the two scroll anchors.
        Messages.CollectionChanged += (_, _) => ShowWelcome = Messages.Count <= 2;
        _ = LoadSessionAsync(null, CancellationToken.None);
        // RefreshSessionsList is called from LoadSessionAsync on the context

        _ = StartHeartbeatAsync();
        _ = StartRagIndexingAsync();
        _ = _docsIndex.LoadAsync(CancellationToken.None);
        _ = StartFirstRunDiscoveryAsync();
        _ = _buildMonitor.InitializeAsync();
    }

    internal void ApplyLabels()
    {
        BtnLoadSession       = Strings.BtnLoadSession;
        BtnCancel            = Strings.BtnCancel;
        BtnSend              = Strings.BtnSend;
        TooltipExport        = Strings.TooltipExport;
        TooltipClear         = Strings.TooltipClear;
        TooltipCopy          = Strings.TooltipCopy;
        TooltipSessionPicker = Strings.TooltipSessionPicker;
        TooltipLoadSession       = Strings.TooltipLoadSession;
        TooltipDeleteSession     = Strings.TooltipDeleteSession;
        HintSend                 = Strings.HintSend;
        TooltipAttachFile        = Strings.TooltipAttachFile;
        TooltipAttachSelection   = Strings.TooltipAttachSelection;
        TooltipBrowseFile        = Strings.TooltipBrowseFile;
        TooltipPinFile           = Strings.TooltipPinFile;
        MenuAddContext           = Strings.MenuAddContext;
        MenuAttachFile           = Strings.MenuAttachFile;
        MenuAttachSelection      = Strings.MenuAttachSelection;
        MenuBrowseFile           = Strings.MenuBrowseFile;
        MenuPinFile              = Strings.MenuPinFile;
        TooltipPinChip           = Strings.TooltipPinChip;
        TooltipRetryConnection   = Strings.TooltipRetryConnection;
        TooltipSearchConversation = Strings.TooltipSearchConversation;
        TooltipCloseSearch        = Strings.TooltipCloseSearch;
        TooltipSaveSnippet        = Strings.TooltipSaveSnippet;
        TooltipStepMode           = Strings.TooltipStepMode;
        TooltipPlanMode           = Strings.TooltipPlanMode;
        TooltipAgentMode          = Strings.TooltipAgentMode;
        AgentModeLabel            = _agentMode ? Strings.LabelModeAgent : Strings.LabelModeChat;
        WelcomeSubtitle           = Strings.WelcomeSubtitle;
        WelcomeCardExplain        = Strings.WelcomeCardExplain;
        WelcomeCardFix            = Strings.WelcomeCardFix;
        WelcomeCardTest           = Strings.WelcomeCardTest;
        WelcomeCardHelp           = Strings.WelcomeCardHelp;
        BuildBannerTitle          = Strings.BuildBannerTitle;
        BuildBannerDismiss        = Strings.BuildBannerDismiss;
        BuildBannerFix            = Strings.BuildBannerFix;
        ActiveModelLabel          = _config.DefaultModel;
    }

    /// <summary>Welcome-card handler: drops the card's slash command into the prompt and sends it,
    /// giving a new user one-click entry points instead of a blank screen.</summary>
    private async Task RunSuggestionAsync(object? parameter, CancellationToken ct)
    {
        if (parameter is not string cmd || string.IsNullOrWhiteSpace(cmd)) return;
        await RunOnVMContextAsync(() => Prompt = cmd);
        await SendAsync(null, ct);
    }

    // ── Theme colors ───────────────────────────────────────────────────────────
    [DataMember] public string ThemeWindowBg    { get => _themeWindowBg;    set => SetProperty(ref _themeWindowBg,    value); }
    [DataMember] public string ThemeText        { get => _themeText;        set => SetProperty(ref _themeText,        value); }
    [DataMember] public string ThemeSubtleText  { get => _themeSubtleText;  set => SetProperty(ref _themeSubtleText,  value); }
    [DataMember] public string ThemeCodeBg      { get => _themeCodeBg;      set => SetProperty(ref _themeCodeBg,      value); }
    [DataMember] public string ThemeCodeText    { get => _themeCodeText;    set => SetProperty(ref _themeCodeText,    value); }
    [DataMember] public string ThemeCodeBorder  { get => _themeCodeBorder;  set => SetProperty(ref _themeCodeBorder,  value); }
    [DataMember] public string ThemeBorder      { get => _themeBorder;      set => SetProperty(ref _themeBorder,      value); }
    [DataMember] public string ThemeSessionBg   { get => _themeSessionBg;   set => SetProperty(ref _themeSessionBg,   value); }
    [DataMember] public string ThemePanelBg     { get => _themePanelBg;     set => SetProperty(ref _themePanelBg,     value); }
    [DataMember] public string ThemeInputBg     { get => _themeInputBg;     set => SetProperty(ref _themeInputBg,     value); }
    [DataMember] public string ThemeInputBorder { get => _themeInputBorder; set => SetProperty(ref _themeInputBorder, value); }
    /// <summary>Surface shown behind a chrome icon button on hover; theme-aware so it doesn't flash
    /// dark in the light theme. Bound from trigger Setters via <c>ElementName=root</c>.</summary>
    [DataMember] public string ThemeHoverBg     { get => _themeHoverBg;     set => SetProperty(ref _themeHoverBg,     value); }

    // ── Collections ────────────────────────────────────────────────────────────
    [DataMember] public ObservableCollection<ChatMessageItem>  Messages          { get; } = [];
    [DataMember] public ObservableCollection<string>           RecentSessions    { get; } = [];
    [DataMember] public ObservableCollection<AttachmentItem>   Attachments       { get; } = [];
    [DataMember] public ObservableCollection<PinnedFileItem>   PinnedFiles       { get; } = [];
    [DataMember] public ObservableCollection<MentionSuggestion> MentionSuggestions { get; } = [];
    [DataMember] public ObservableCollection<SlashSuggestion>   SlashSuggestions   { get; } = [];

    // ── UI Labels ──────────────────────────────────────────────────────────────
    [DataMember] public string BtnLoadSession       { get => _btnLoadSession;       set => SetProperty(ref _btnLoadSession,       value); }
    [DataMember] public string BtnCancel            { get => _btnCancel;            set => SetProperty(ref _btnCancel,            value); }
    [DataMember] public string BtnSend              { get => _btnSend;              set => SetProperty(ref _btnSend,              value); }
    [DataMember] public string TooltipExport           { get => _tooltipExport;           set => SetProperty(ref _tooltipExport,           value); }
    [DataMember] public string TooltipClear            { get => _tooltipClear;            set => SetProperty(ref _tooltipClear,            value); }
    [DataMember] public string TooltipCopy             { get => _tooltipCopy;             set => SetProperty(ref _tooltipCopy,             value); }
    [DataMember] public string TooltipSessionPicker    { get => _tooltipSessionPicker;    set => SetProperty(ref _tooltipSessionPicker,    value); }
    [DataMember] public string TooltipLoadSession      { get => _tooltipLoadSession;      set => SetProperty(ref _tooltipLoadSession,      value); }
    [DataMember] public string TooltipDeleteSession    { get => _tooltipDeleteSession;    set => SetProperty(ref _tooltipDeleteSession,    value); }
    [DataMember] public string HintSend                { get => _hintSend;                set => SetProperty(ref _hintSend,                value); }
    [DataMember] public string TooltipAttachFile       { get => _tooltipAttachFile;       set => SetProperty(ref _tooltipAttachFile,       value); }
    [DataMember] public string TooltipAttachSelection  { get => _tooltipAttachSelection;  set => SetProperty(ref _tooltipAttachSelection,  value); }
    [DataMember] public string TooltipBrowseFile       { get => _tooltipBrowseFile;       set => SetProperty(ref _tooltipBrowseFile,       value); }
    [DataMember] public string TooltipPinFile          { get => _tooltipPinFile;          set => SetProperty(ref _tooltipPinFile,          value); }
    [DataMember] public string TooltipPinChip          { get => _tooltipPinChip;          set => SetProperty(ref _tooltipPinChip,          value); }
    [DataMember] public string TooltipSearchConversation { get => _tooltipSearchConversation; set => SetProperty(ref _tooltipSearchConversation, value); }
    [DataMember] public string TooltipCloseSearch        { get => _tooltipCloseSearch;        set => SetProperty(ref _tooltipCloseSearch,        value); }
    [DataMember] public string TooltipSaveSnippet        { get => _tooltipSaveSnippet;        set => SetProperty(ref _tooltipSaveSnippet,        value); }
    [DataMember] public string TooltipStepMode           { get => _tooltipStepMode;           set => SetProperty(ref _tooltipStepMode,           value); }
    [DataMember] public string TooltipPlanMode           { get => _tooltipPlanMode;           set => SetProperty(ref _tooltipPlanMode,           value); }
    [DataMember] public string TooltipAgentMode          { get => _tooltipAgentMode;          set => SetProperty(ref _tooltipAgentMode,          value); }

    // ── Build Failed banner ────────────────────────────────────────────────────
    /// <summary>
    /// <c>true</c> when VS completed a solution build with at least one error.
    /// Drives the visibility of the dedicated red "Build Failed" banner above the input card.
    /// </summary>
    [DataMember] public bool   HasBuildFailedBanner  { get => _hasBuildFailedBanner;  set => SetProperty(ref _hasBuildFailedBanner,  value); }
    /// <summary>First error line truncated to ≤ 120 chars — shown in the banner subtitle.</summary>
    [DataMember] public string BuildFailedFirstError { get => _buildFailedFirstError; set => SetProperty(ref _buildFailedFirstError, value); }

    // ── Bound properties ───────────────────────────────────────────────────────
    [DataMember] public string Prompt
    {
        get => _prompt;
        set
        {
            SetProperty(ref _prompt, value);
            TriggerMentionSearch(value);
            TriggerSlashSuggestions(value);
            TriggerShadowSearch(value);
            if (!_navigatingHistory && _promptHistory.IsNavigating)
                _promptHistory.ResetNavigation(); // user typed while browsing history → leave nav mode
            UpdateHistoryCommandState();
        }
    }
    [DataMember] public string SelectedSession
    {
        get => _selectedSession;
        set
        {
            SetProperty(ref _selectedSession, value);
            DeleteSessionCommand.CanExecute = !string.IsNullOrEmpty(value);
        }
    }
    [DataMember] public string CurrentStep           { get => _currentStep;           set => SetProperty(ref _currentStep,           value); }
    [DataMember] public string TokenInfo             { get => _tokenInfo;             set { SetProperty(ref _tokenInfo, value); HasTokenInfo = !string.IsNullOrEmpty(value); } }
    [DataMember] public bool   HasTokenInfo          { get => _hasTokenInfo;          set => SetProperty(ref _hasTokenInfo,          value); }
    [DataMember] public double ContextFillPercent    { get => _contextFillPercent;    set => SetProperty(ref _contextFillPercent,    value); }
    [DataMember] public bool   HasContextBudget      { get => _hasContextBudget;      set => SetProperty(ref _hasContextBudget,      value); }
    [DataMember] public string ContextBudgetColor    { get => _contextBudgetColor;    set => SetProperty(ref _contextBudgetColor,    value); }
    [DataMember] public string ContextBudgetTooltip  { get => _contextBudgetTooltip;  set => SetProperty(ref _contextBudgetTooltip,  value); }
    [DataMember] public bool   IsLoading               { get => _isLoading;               set => SetProperty(ref _isLoading,               value); }
    [DataMember] public bool   IsSessionPanelOpen      { get => _isSessionPanelOpen;      set => SetProperty(ref _isSessionPanelOpen,      value); }
    [DataMember] public bool   IsSearchOpen            { get => _isSearchOpen;            set => SetProperty(ref _isSearchOpen,            value); }
    [DataMember] public bool   HasSearchQuery          { get => _hasSearchQuery;          set => SetProperty(ref _hasSearchQuery,          value); }
    [DataMember] public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (!SetProperty(ref _searchQuery, value)) return;
            var q = value?.Trim() ?? string.Empty;
            HasSearchQuery = q.Length > 0;
            foreach (var msg in Messages)
            {
                if (msg.Role is "anchor" or "status")
                    continue;
                msg.IsSearchDimmed = q.Length > 0 &&
                    !msg.Content.Contains(q, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
    [DataMember] public bool   HasAttachments          { get => _hasAttachments;          set => SetProperty(ref _hasAttachments,          value); }
    [DataMember] public bool   HasPinnedFiles          { get => _hasPinnedFiles;          set => SetProperty(ref _hasPinnedFiles,          value); }
    [DataMember] public bool   HasMentionSuggestions   { get => _hasMentionSuggestions;   set => SetProperty(ref _hasMentionSuggestions,   value); }
    [DataMember] public bool   HasSlashSuggestions     { get => _hasSlashSuggestions;     set => SetProperty(ref _hasSlashSuggestions,     value); }
    [DataMember] public ChatMessageItem? ScrollTarget  { get => _scrollTarget;            set => SetProperty(ref _scrollTarget,            value); }

    // ── Connection Guard ───────────────────────────────────────────────────────
    [DataMember] public string ConnectionStatusText  { get => _connectionStatusText;  set => SetProperty(ref _connectionStatusText,  value); }
    [DataMember] public string ConnectionStatusColor { get => _connectionStatusColor; set => SetProperty(ref _connectionStatusColor, value); }
    [DataMember] public bool   ShowRetryButton       { get => _showRetryButton;       set => SetProperty(ref _showRetryButton,       value); }
    [DataMember] public string TooltipRetryConnection{ get => _tooltipRetryConnection; set => SetProperty(ref _tooltipRetryConnection, value); }
    /// <summary>
    /// Purple (#7C4DFF) when Ollama is reachable; dark gray (#555555) when offline.
    /// Bound to the send button background so the user gets an immediate visual cue.
    /// </summary>
    [DataMember] public string SendButtonColor { get => _sendButtonColor; set => SetProperty(ref _sendButtonColor, value); }

    // ── VRAM badge ─────────────────────────────────────────────────────────────
    /// <summary>
    /// Short summary of models currently loaded in Ollama VRAM (e.g. "llama3.1 · 4.5 GB").
    /// Empty when no models are loaded or the monitoring service hasn't ticked yet.
    /// </summary>
    [DataMember] public string VramStatus    { get => _vramStatus;    set => SetProperty(ref _vramStatus,    value); }
    /// <summary><c>true</c> when <see cref="VramStatus"/> is non-empty and should be shown.</summary>
    [DataMember] public bool   HasVramStatus { get => _hasVramStatus; set => SetProperty(ref _hasVramStatus, value); }

    // ── Commandes ──────────────────────────────────────────────────────────────
    [DataMember] public AsyncCommand SendCommand               { get; }
    [DataMember] public AsyncCommand CancelCommand             { get; }
    [DataMember] public AsyncCommand ExportCommand             { get; }
    [DataMember] public AsyncCommand ClearCommand              { get; }
    [DataMember] public AsyncCommand LoadSessionCommand        { get; }
    [DataMember] public AsyncCommand DeleteSessionCommand      { get; }
    [DataMember] public AsyncCommand ToggleSessionPanelCommand { get; }
    [DataMember] public AsyncCommand ScrollToBottomCommand     { get; }
    [DataMember] public AsyncCommand AttachFileCommand          { get; }
    [DataMember] public AsyncCommand AttachSelectionCommand     { get; }
    [DataMember] public AsyncCommand BrowseFileCommand          { get; }
    [DataMember] public AsyncCommand PinFileCommand             { get; }
    [DataMember] public AsyncCommand ToggleAttachMenuCommand    { get; }
    [DataMember] public AsyncCommand ToggleSearchCommand        { get; }
    [DataMember] public AsyncCommand ClearSearchCommand         { get; }
    /// <summary>Toolbar toggle for agent step mode (equivalent to <c>/agent-step</c>).</summary>
    [DataMember] public AsyncCommand ToggleStepModeCommand      { get; }
    /// <summary>Toolbar toggle for plan mode (equivalent to <c>/plan</c>).</summary>
    [DataMember] public AsyncCommand TogglePlanModeCommand      { get; }
    /// <summary>Main-window switch between Chat and Agent mode (wired to <see cref="InferpalConfig.AgentModeEnabled"/>).</summary>
    [DataMember] public AsyncCommand ToggleAgentModeCommand     { get; }
    /// <summary>Welcome-screen suggestion card: the CommandParameter (a slash command like <c>/explain</c>)
    /// is dropped into the prompt and sent, so a new user has one-click entry points.</summary>
    [DataMember] public AsyncCommand RunSuggestionCommand       { get; }
    [DataMember] public AsyncCommand HistoryUpCommand          { get; }
    [DataMember] public AsyncCommand HistoryDownCommand        { get; }
    [DataMember] public AsyncCommand RetryConnectionCommand    { get; }
    /// <summary>Closes the "Build Failed" banner without taking any action.</summary>
    [DataMember] public AsyncCommand DismissBuildBannerCommand { get; }
    /// <summary>Closes the banner and pre-fills <c>/fix-build</c> in the prompt.</summary>
    [DataMember] public AsyncCommand FixBuildBannerCommand     { get; }

    #endregion
}
