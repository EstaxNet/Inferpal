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

[DataContract]
internal class InferpalToolWindowData : NotifyPropertyChangedObject
{
    #region État & champs

    private readonly IInferenceProvider       _client;
    private readonly AgentOrchestrator        _orchestrator;
    private readonly ToolRegistry             _tools;
    private readonly InferpalConfig        _config;
    private readonly ConversationStore        _store = new();
    private readonly VisualStudioExtensibility _vs;
    private readonly VsContextHolder           _contextHolder;
    private readonly ProjectIndexService       _indexService;
    private readonly DocsIndexService          _docsIndex;
    private readonly ModelLifetimeService      _lifetimeService;
    private readonly VsBuildMonitor            _buildMonitor;

    private static readonly SettingIdentifier<string> ColorThemeId = "environment.visualExperience.colorTheme";

    private const int MaxCodeChars = 8_000;

    private IDisposable? _themeSubscription;
    private bool _isDark = true;

    // ── Connection Guard / Heartbeat ───────────────────────────────────────────
    private CancellationTokenSource _heartbeatCts        = new();
    private string _connectionStatusText  = "● …";
    private string _connectionStatusColor = "#A0A0A0";
    private bool   _showRetryButton       = false;
    private string _tooltipRetryConnection = string.Empty;
    // Volatile: written on VM context, read from SendAsync thread-pool path (pre-flight check).
    private volatile bool   _isBackendReachable = true; // optimistic start
    private string _sendButtonColor = "#7C4DFF";

    // ── VRAM badge ─────────────────────────────────────────────────────────────
    private string _vramStatus    = string.Empty;
    private bool   _hasVramStatus = false;

    private string _themeWindowBg    = "#1E1E1E";
    private string _themeText        = "#D4D4D4";
    private string _themeSubtleText  = "#A0A0A0";
    private string _themeCodeBg      = "#161616";
    private string _themeCodeText    = "#CE9178";
    private string _themeCodeBorder  = "#333333";
    private string _themeBorder      = "#3F3F46";
    private string _themeSessionBg   = "#1E1E28";
    private string _themePanelBg     = "#2D2D30";
    private string _themeInputBg     = "#2A2A32";
    private string _themeInputBorder = "#5A5A72";
    private string _themeHoverBg      = "#3F3F46";

    // Two invisible anchors always kept at the end of Messages.
    // ScrollToBottom() alternates between them so SelectedItem always changes,
    // forcing VS Remote UI to send the update and ListBoxItem.OnSelected to fire.
    private readonly ChatMessageItem _anchor0 = ChatMessageItem.Anchor();
    private readonly ChatMessageItem _anchor1 = ChatMessageItem.Anchor();
    private bool _scrollToggle;

    private List<ChatMessageDto>     _history = [];
    private CancellationTokenSource? _currentCts;
    private ChatMessageItem?         _lastRegenerableMsg;
    private CancellationTokenSource? _mentionCts;
    private CancellationTokenSource? _shadowSearchCts;
    private bool _toolsEnabled = true;

    private string           _prompt                = string.Empty;
    private string           _selectedSession       = string.Empty;

    // ── Prompt history ─────────────────────────────────────────────────────────
    private const  int            PromptHistoryMax  = 50;
    private readonly PromptHistoryNavigator _promptHistory = new(PromptHistoryMax);

    private static readonly string _promptHistoryFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Inferpal", "prompt_history.json");
    private bool   _navigatingHistory = false;
    private string           _currentStep           = string.Empty;
    private string           _tokenInfo             = string.Empty;
    private bool             _isLoading;
    private bool             _hasTokenInfo;
    private bool             _isSessionPanelOpen;
    private bool             _isSearchOpen;
    private string           _activeTemplateSuffix     = string.Empty;
    private bool             _workspaceContextInjected;
    private DateTime?        _sessionStartTime;
    private bool             _agentStepMode;
    /// <summary>Bindable mirror of <see cref="_agentStepMode"/> for the toolbar toggle button's active state.</summary>
    [DataMember] public bool IsStepMode { get => _agentStepMode; set => SetProperty(ref _agentStepMode, value); }
    private TaskCompletionSource<bool>? _stepResume;
    private bool             _planMode;
    /// <summary>Bindable mirror of <see cref="_planMode"/> for the toolbar toggle button's active state.</summary>
    [DataMember] public bool IsPlanMode { get => _planMode; set => SetProperty(ref _planMode, value); }
    private bool             _agentMode;
    /// <summary>Bindable mirror of <see cref="InferpalConfig.AgentModeEnabled"/> for the main-window
    /// chat/agent switch. Toggling persists the config (see <see cref="ToggleAgentModeAsync"/>).</summary>
    [DataMember] public bool IsAgentMode { get => _agentMode; set => SetProperty(ref _agentMode, value); }
    private string           _agentModeLabel = string.Empty;
    /// <summary>"💬 Chat" / "🤖 Agent" — the switch's current-mode caption (recomputed on toggle and language change).</summary>
    [DataMember] public string AgentModeLabel { get => _agentModeLabel; set => SetProperty(ref _agentModeLabel, value); }
    private bool             _attachMenuOpen;
    /// <summary><c>true</c> while the single "＋ add context" toolbar menu is expanded — replaces the old row of
    /// four separate attach glyphs (attach file / selection / browse / pin) with one labelled menu.</summary>
    [DataMember] public bool IsAttachMenuOpen { get => _attachMenuOpen; set => SetProperty(ref _attachMenuOpen, value); }

    // ── Welcome / empty state + active-model indicator ─────────────────────────
    private bool   _showWelcome = true;
    /// <summary><c>true</c> while the conversation is empty (only the two scroll anchors) — drives the
    /// welcome screen so the first thing a user sees is what they can do, not a blank pane.</summary>
    [DataMember] public bool   ShowWelcome { get => _showWelcome; set => SetProperty(ref _showWelcome, value); }
    private string _activeModelLabel = string.Empty;
    /// <summary>The chat model currently in use (<see cref="InferpalConfig.DefaultModel"/>), surfaced in
    /// the header + welcome screen so the active model is never ambiguous (the VRAM badge is separate).</summary>
    [DataMember] public string ActiveModelLabel { get => _activeModelLabel; set => SetProperty(ref _activeModelLabel, value); }
    private string _welcomeSubtitle    = string.Empty;
    [DataMember] public string WelcomeSubtitle    { get => _welcomeSubtitle;    set => SetProperty(ref _welcomeSubtitle,    value); }
    private string _welcomeCardExplain = string.Empty;
    [DataMember] public string WelcomeCardExplain { get => _welcomeCardExplain; set => SetProperty(ref _welcomeCardExplain, value); }
    private string _welcomeCardFix     = string.Empty;
    [DataMember] public string WelcomeCardFix     { get => _welcomeCardFix;     set => SetProperty(ref _welcomeCardFix,     value); }
    private string _welcomeCardTest    = string.Empty;
    [DataMember] public string WelcomeCardTest    { get => _welcomeCardTest;    set => SetProperty(ref _welcomeCardTest,    value); }
    private string _welcomeCardHelp    = string.Empty;
    [DataMember] public string WelcomeCardHelp    { get => _welcomeCardHelp;    set => SetProperty(ref _welcomeCardHelp,    value); }
    private string _buildBannerTitle   = string.Empty;
    [DataMember] public string BuildBannerTitle   { get => _buildBannerTitle;   set => SetProperty(ref _buildBannerTitle,   value); }
    private string _buildBannerDismiss = string.Empty;
    [DataMember] public string BuildBannerDismiss { get => _buildBannerDismiss; set => SetProperty(ref _buildBannerDismiss, value); }
    private string _buildBannerFix     = string.Empty;
    [DataMember] public string BuildBannerFix     { get => _buildBannerFix;     set => SetProperty(ref _buildBannerFix,     value); }
    private string _menuAddContext      = string.Empty;
    [DataMember] public string MenuAddContext      { get => _menuAddContext;      set => SetProperty(ref _menuAddContext,      value); }
    private string _menuAttachFile      = string.Empty;
    [DataMember] public string MenuAttachFile      { get => _menuAttachFile;      set => SetProperty(ref _menuAttachFile,      value); }
    private string _menuAttachSelection = string.Empty;
    [DataMember] public string MenuAttachSelection { get => _menuAttachSelection; set => SetProperty(ref _menuAttachSelection, value); }
    private string _menuBrowseFile      = string.Empty;
    [DataMember] public string MenuBrowseFile      { get => _menuBrowseFile;      set => SetProperty(ref _menuBrowseFile,      value); }
    private string _menuPinFile         = string.Empty;
    [DataMember] public string MenuPinFile         { get => _menuPinFile;         set => SetProperty(ref _menuPinFile,         value); }
    private bool             _hasSearchQuery;
    private string           _searchQuery = string.Empty;
    private bool             _hasAttachments;
    private bool             _hasPinnedFiles;
    private bool             _hasMentionSuggestions;
    private bool             _hasSlashSuggestions;
    private ChatMessageItem? _scrollTarget;
    private int              _sessionTokens;
    private int              _lastPromptTokens;
    private int              _conversationTurnCount;
    private double           _contextFillPercent;
    private bool             _hasContextBudget;
    private string           _contextBudgetColor   = "#606060";
    private string           _contextBudgetTooltip = string.Empty;
    private string           _baseSystemPrompt = string.Empty;
    // Path of the file currently active in the editor — drives glob-scoped project rules
    // (see RulesService) in BuildSystemPrompt. Updated by OnActiveFileChanged.
    private string?          _activeFilePath;
    private string           _oodaSummary      = string.Empty;

    // Label backing fields
    private string _btnLoadSession          = string.Empty;
    private string _btnCancel               = string.Empty;
    private string _btnSend                 = string.Empty;
    private string _tooltipExport           = string.Empty;
    private string _tooltipClear            = string.Empty;
    private string _tooltipCopy             = string.Empty;
    private string _tooltipSessionPicker    = string.Empty;
    private string _tooltipLoadSession      = string.Empty;
    private string _tooltipDeleteSession    = string.Empty;
    private string _hintSend                = string.Empty;
    private string _tooltipAttachFile       = string.Empty;
    private string _tooltipAttachSelection  = string.Empty;
    private string _tooltipBrowseFile       = string.Empty;
    private string _tooltipPinFile          = string.Empty;
    private string _tooltipPinChip          = string.Empty;
    private string _tooltipSearchConversation = string.Empty;
    private string _tooltipCloseSearch        = string.Empty;
    private string _tooltipSaveSnippet        = string.Empty;
    private string _tooltipStepMode           = string.Empty;
    private string _tooltipPlanMode           = string.Empty;
    private string _tooltipAgentMode          = string.Empty;

    // ── Build Failed banner ────────────────────────────────────────────────────
    // Shown when VS finishes a solution build with at least one compilation error.
    // Dismissed when the user clicks "Dismiss" or "Fix with AI".
    private bool   _hasBuildFailedBanner  = false;
    private string _buildFailedFirstError = string.Empty;
    /// <summary>Full error lines stored for the "Fix with AI" flow.</summary>
    private string _buildFailedErrorLines = string.Empty;

    // sticky:true sets SynchronizationContext.Current = this context while callbacks run,
    // so PropertyChanged / CollectionChanged events are raised with the context as Current.
    // VS Remote UI validates that all ViewModel mutations come from this context.
    internal NonConcurrentSynchronizationContext SynchronizationContext { get; } =
        new NonConcurrentSynchronizationContext(sticky: true);

    #endregion

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

    #region Helpers UI : budget, défilement, contexte VM, thème

    // ── Context budget ─────────────────────────────────────────────────────────

    /// <summary>
    /// Recomputes the context-window fill indicator from <see cref="_lastPromptTokens"/>
    /// and <see cref="InferpalConfig.ContextWindowSize"/>. Must be called on the VM thread.
    /// </summary>
    private void UpdateContextBudget()
    {
        var budget = Services.Presentation.ContextBudgetGauge.Compute(_lastPromptTokens, _config.ContextWindowSize);
        if (budget is null)
        {
            HasContextBudget = false;
            return;
        }

        ContextFillPercent   = budget.FillPercent;
        ContextBudgetColor   = budget.Color;
        ContextBudgetTooltip = budget.Tooltip;
        HasContextBudget     = true;
    }

    // ── Scroll helpers ─────────────────────────────────────────────────────────

    // Alternates between two invisible anchor items so SelectedItem always changes.
    // VS Remote UI batches PropertyChanged; the null→item trick collapses to one value.
    // With two anchors each call sends a genuinely different object.
    // ⚠ Selection alone NEVER scrolls a WPF ListBox (no BringIntoView code path exists in
    // Selector/ListBox/ListBoxItem — verified against dotnet/wpf sources). The actual
    // scrolling happens in-process: ChatAutoScroller (GhostText) class-handles
    // ListBoxItem.Selected on the tagged chat list and calls BringIntoView, and follows
    // content growth during streaming. This method is therefore only the cross-process
    // "scroll to bottom" signal; without the in-proc side it is a no-op.
    private void ScrollToBottom()
    {
        if (Messages.Count < 2) return;
        _scrollToggle = !_scrollToggle;
        var target = _scrollToggle ? _anchor0 : _anchor1;
        Post(() => ScrollTarget = target);
    }

    // ── VM context helpers ─────────────────────────────────────────────────────

    // Fire-and-forget: used for onToken/onStep callbacks during streaming.
    private void Post(Action action) =>
        SynchronizationContext.Post(_ =>
        {
            try { action(); }
            catch { }
        }, null);

    // Awaitable: ensures the action runs with SynchronizationContext.Current = our context.
    // Because NonConcurrentSynchronizationContext is FIFO, awaiting this after RunAgentAsync
    // guarantees all prior Post() calls (onToken etc.) have already completed.
    private Task RunOnVMContextAsync(Action action)
    {
        var tcs = new TaskCompletionSource();
        SynchronizationContext.Post(_ =>
        {
            try   { action(); tcs.SetResult(); }
            catch (Exception ex) { tcs.SetException(ex); }
        }, null);
        return tcs.Task;
    }

    // ── Theme ──────────────────────────────────────────────────────────────────

    private async Task InitThemeAsync(VisualStudioExtensibility extensibility)
    {
        try
        {
            _themeSubscription = await extensibility.Settings().SubscribeAsync(
                ColorThemeId,
                CancellationToken.None,
                value => Post(() =>
                    ApplyThemeColors(VsThemeDetector.IsDark(value.ValueOrDefault(string.Empty)))));
        }
        catch { }
    }

    private void ApplyThemeColors(bool isDark)
    {
        _isDark          = isDark;
        var p            = ThemePalette.For(isDark);
        ThemeWindowBg    = p.WindowBg;
        ThemeText        = p.Text;
        ThemeSubtleText  = p.SubtleText;
        ThemeCodeBg      = p.CodeBg;
        ThemeCodeText    = p.CodeText;
        ThemeCodeBorder  = p.CodeBorder;
        ThemeBorder      = p.Border;
        ThemeSessionBg   = p.SessionBg;
        ThemePanelBg     = p.PanelBg;
        ThemeInputBg     = p.InputBg;
        ThemeInputBorder = p.InputBorder;
        ThemeHoverBg     = p.HoverBg;
        UpdateMessageBubbles();
    }

    private void ApplyItemTheme(ChatMessageItem item)
    {
        var p = ThemePalette.For(_isDark);

        item.BubbleBackground = p.BubbleBackground(item.Role);
        item.ThemeText        = p.Text;
        item.ThemeSubtleText  = p.BubbleSubtleText;
        item.ThemeToolText    = p.BubbleToolText;
        item.ThemeCodeText    = p.CodeText;
        item.ThemeCodeBg      = p.CodeBg;
        item.ThemeCodeBorder  = p.CodeBorder;

        foreach (var b in item.Blocks)
        {
            b.ThemeText       = item.ThemeText;
            b.ThemeCodeText   = item.ThemeCodeText;
            b.ThemeCodeBg     = item.ThemeCodeBg;
            b.ThemeCodeBorder = item.ThemeCodeBorder;
            foreach (var run in b.Inlines)
                run.Foreground = run.IsCode ? item.ThemeCodeText : item.ThemeText;
        }
    }

    private void UpdateMessageBubbles()
    {
        foreach (var m in Messages)
            ApplyItemTheme(m);
    }

    #endregion

    #region Envoi & tour de chat

    // ── Handlers ───────────────────────────────────────────────────────────────

    private async Task SendAsync(object? _, CancellationToken ct)
    {
        // The send button doubles as a stop button: while a request is running it cancels
        // the in-flight request instead of starting a new one (the XAML shows a red square).
        if (IsLoading)
        {
            if (_currentCts is { } cts)
                await cts.CancelAsync();
            return;
        }

        // If the @-mention popup is open, Enter accepts the first suggestion instead of
        // sending. Otherwise a typed mention like "@code Foo" / "@file Bar" would be sent
        // verbatim as chat text — the popup item is only reachable by mouse click, so the
        // feature looked dead from the keyboard. Mirrors standard autocomplete behaviour.
        if (HasMentionSuggestions)
        {
            MentionSuggestion? first = null;
            await RunOnVMContextAsync(() => first = MentionSuggestions.FirstOrDefault());
            if (first is not null)
            {
                await first.OnSelect(ct);
                return;
            }
        }

        string               userText    = string.Empty;
        List<AttachmentItem> attachments = [];

        await RunOnVMContextAsync(() =>
        {
            userText    = Prompt.Trim();
            attachments = [..Attachments];
        });
        if (string.IsNullOrEmpty(userText)) return;

        if (userText.StartsWith('/'))
        {
            await RunOnVMContextAsync(() => Prompt = string.Empty);
            await HandleSlashCommandAsync(userText, ct);
            return;
        }

        await SendCoreAsync(userText, oneTimeModel: null, attachments, ct, clearPrompt: true);
    }

    /// <param name="clearPrompt">
    /// <c>true</c> for normal chat sends (clears the input box and attachments).
    /// <c>false</c> for code-action auto-sends (preserves the user's current draft).
    /// </param>
    private async Task SendCoreAsync(
        string               userText,
        string?              oneTimeModel,
        List<AttachmentItem> attachments,
        CancellationToken    ct,
        bool                 clearPrompt = true)
    {
        // ── Pre-flight: heartbeat says Ollama is offline → fail immediately ────────
        // This avoids the 30-minute HTTP timeout; the prompt stays in the input box
        // so the user can retry once Ollama is back up.
        if (!_isBackendReachable)
        {
            await RunOnVMContextAsync(() =>
            {
                var url = _config.BaseUrl;
                var msg = ChatMessageItem.AssistantMsg(Strings.MsgConnectionGuardFailed(url));
                ApplyItemTheme(msg);
                Messages.Insert(Messages.Count - 2, msg);
                ScrollToBottom();
            });
            return;
        }

        string                   historyText  = string.Empty;
        CancellationTokenSource? localCts     = null;
        var                      sendStart    = DateTime.UtcNow;

        try
        {
            // Build context-enriched history message (not shown in chat bubble)
            historyText = Services.Agent.ChatTurnPolicy.BuildHistoryText(userText, attachments);

            _sessionStartTime ??= DateTime.Now;

            // First-turn workspace context: silently prepend solution + open editors
            if (!_workspaceContextInjected && !userText.StartsWith('/'))
            {
                _workspaceContextInjected = true;
                var workspaceCtx = await BuildWorkspaceContextAsync(ct);
                if (!string.IsNullOrEmpty(workspaceCtx))
                    historyText = workspaceCtx + "\n\n" + historyText;
            }

            // Auto-context: retrieve and inject the most relevant indexed chunks for this turn,
            // skipping anything already attached. Guarantees per-turn RAG context even when the
            // debounced auto-attach chips did not fire (e.g. the user typed fast and sent).
            var autoCtx = await BuildAutoContextAsync(userText, attachments, ct);
            if (!string.IsNullOrEmpty(autoCtx))
                historyText = autoCtx + "\n\n" + historyText;

            await RunOnVMContextAsync(() =>
            {
                _config.Save();
                if (_promptHistory.Append(userText)) // also resets navigation state
                    SavePromptHistory();
                if (clearPrompt)
                {
                    Prompt         = string.Empty;
                    Attachments.Clear();
                    HasAttachments = false;
                }
                IsLoading      = true;
                var userItem   = ChatMessageItem.UserMsg(userText);
                ApplyItemTheme(userItem);
                Messages.Insert(Messages.Count - 2, userItem);
                ScrollToBottom();
                _history.Add(new ChatMessageDto("user", historyText));
                localCts    = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _currentCts = localCts;
                // Shadow search is no longer needed — agent is running
                _shadowSearchCts?.Cancel();
                _shadowSearchCts?.Dispose();
                _shadowSearchCts = null;
            });

            if (localCts is null) return;
        }
        catch (Exception ex)
        {
            var msg = ex.Message;
            await RunOnVMContextAsync(() =>
                Messages.Insert(Messages.Count - 2, ChatMessageItem.AssistantMsg(Strings.MsgError(msg))));
            return;
        }

        // Declared before the try block so it is accessible in the catch/finally handlers.
        // Needed to clean up an orphaned streaming bubble when cancellation or an exception
        // interrupts the stream before the normal RunOnVMContextAsync finalisation runs.
        ChatMessageItem? streamingMsg  = null;
        // Live-status bubble: inserted on first onStep callback and updated in place while the
        // agent works.  Removed before the final response is inserted so it never persists.
        ChatMessageItem? statusBubble  = null;

        // Creates the live-status bubble on first use, then updates it in place.
        void UpsertStatusBubble(string text) => Post(() =>
        {
            CurrentStep = text;
            if (statusBubble is null)
            {
                statusBubble = ChatMessageItem.StatusMsg(text);
                ApplyItemTheme(statusBubble);
                Messages.Insert(Messages.Count - 2, statusBubble);
                ScrollToBottom();
            }
            else
            {
                statusBubble.Content = text;
            }
        });

        // Removes the live-status bubble so it never persists in the conversation or on disk.
        // Must run on the VM context (called from within RunOnVMContextAsync below).
        void RemoveStatusBubble()
        {
            if (statusBubble is null) return;
            var sidx = Messages.IndexOf(statusBubble);
            if (sidx >= 0) Messages.RemoveAt(sidx);
            statusBubble = null;
        }

        // Background RAG indexing yields to this turn automatically: RunAgentAsync now holds a
        // GpuScheduler chat lease for its whole duration (covers every chat entrypoint, not just
        // this one), so the chat model is never starved by the embedding workload.
        try
        {
            await CompactOrTruncateAsync(localCts!.Token);

            // Agent mode (Plan→Act→Observe) is active only when the user switch is on AND tools are
            // engaged AND no one-time model overrides this turn. Computed here so model selection and
            // the execution path below share one source of truth.
            var useOrchestrator = _config.AgentModeEnabled
                && oneTimeModel is null
                && _toolsEnabled;

            // Model selection: a one-time model (code actions) always wins; otherwise, only the actual
            // agent loop routes to the dedicated AgentModel if set, so a fast cache-friendly model
            // handles multi-turn tool work while a heavier model stays on plain chat. Chat mode keeps
            // DefaultModel even when tools are enabled (the basic tool-calling loop is still "chat").
            var effectiveModel =
                !string.IsNullOrEmpty(oneTimeModel)                          ? oneTimeModel
                : useOrchestrator && !string.IsNullOrEmpty(_config.AgentModel) ? _config.AgentModel
                : _config.DefaultModel;

            using var sink = new ThrottledTokenSink(chunk => Post(() =>
            {
                if (streamingMsg is null)
                {
                    streamingMsg = ChatMessageItem.StreamingMsg(effectiveModel);
                    Messages.Insert(Messages.Count - 2, streamingMsg);
                    ScrollToBottom();
                }
                streamingMsg.Content += chunk;
            }));

            // ── Real-time token meter ────────────────────────────────────────────────
            // The header's context-fill bar and "tokens used" readout used to stay frozen at the
            // previous turn's value until the whole run finished, so a long generation looked
            // stalled. Seed the gauge from the prompt about to be sent, then grow a rough estimate
            // (~4 chars/token, matching EstimateTokens) as answer and reasoning tokens stream in.
            // These are provisional (prefixed "~"); the run's final block snaps both to the real
            // prompt_eval_count + eval_count once the provider reports them.
            var runBasePromptTokens = Services.Agent.AgentOrchestrator.EstimateTokens(_history);
            Post(() => { _lastPromptTokens = runBasePromptTokens; UpdateContextBudget(); });

            var liveGenChars    = 0;
            var lastPostedChars = 0;
            void CountLive(int addedChars)
            {
                liveGenChars += addedChars;
                if (liveGenChars - lastPostedChars < 240) return;   // throttle: ~60 tokens between VM updates
                lastPostedChars = liveGenChars;
                var lastTurn = runBasePromptTokens + liveGenChars / 4;
                Post(() =>
                {
                    _lastPromptTokens = lastTurn;
                    UpdateContextBudget();
                    TokenInfo = Strings.TokenUsage($"~{lastTurn:N0}", $"~{_sessionTokens + lastTurn:N0}");
                });
            }

            // ── Choose execution path: Orchestrated (Plan→Act→Observe) vs basic loop ──
            IToolRegistry effectiveTools = (oneTimeModel is not null || !_toolsEnabled)
                ? EmptyToolRegistry.Instance
                : _tools;

            // Decorators when tools are enabled: plan mode filters to read-only tools
            // first, then step mode wraps whatever remains.
            if (effectiveTools == (IToolRegistry)_tools)
            {
                // Start a change-tracking run so this turn's file writes can be reverted via /undo-run.
                _tools.History.BeginRun();
                if (_planMode)      effectiveTools = new Services.Execution.PlanModeToolRegistry(_tools);
                if (_agentStepMode) effectiveTools = new Services.Execution.StepModeToolRegistry(effectiveTools, PauseForStepAsync);
            }

            // Common result variables filled by whichever path runs.
            string               agentFinalResponse = string.Empty;
            List<ToolExecution>  agentExecutions    = [];
            int                  agentTokensUsed    = 0;

            // Set true once tool bubbles have been streamed live (below), so the final
            // render pass skips re-inserting them and avoids duplicates.
            bool liveToolBubbles = false;

            // Builds the permanent result bubble for a finished tool execution
            // (shared by the live stream below and the final render pass).
            ChatMessageItem BuildToolBubble(ToolExecution exec)
            {
                var preview  = Services.Agent.ChatTurnPolicy.BuildToolPreview(exec.Output);
                var toolItem = ChatMessageItem.ToolMsg(exec.Name, Strings.MsgToolOutput(exec.Input, preview), _config.ToolBubblesExpanded);
                if (exec.Diff is not null)
                    toolItem.InitDiff(exec.Diff);
                if (exec.HasErrors)
                    toolItem.InitFixCallback(exec.Output, rawErrors => Post(() => Prompt = BuildFixPrompt(rawErrors)));
                ApplyItemTheme(toolItem);
                return toolItem;
            }

            // Inserts a tool-result bubble live, above the live status / streaming bubbles
            // so completed tools stack in chronological order while the agent keeps working.
            void StreamToolBubble(ToolExecution exec) => Post(() =>
            {
                liveToolBubbles = true;
                var toolItem = BuildToolBubble(exec);
                int idx = Messages.Count - 2;
                if (statusBubble is not null) { var i = Messages.IndexOf(statusBubble); if (i >= 0) idx = Math.Min(idx, i); }
                if (streamingMsg  is not null) { var i = Messages.IndexOf(streamingMsg);  if (i >= 0) idx = Math.Min(idx, i); }
                if (idx < 0) idx = 0;
                Messages.Insert(idx, toolItem);
                ScrollToBottom();
            });

            // Live reasoning preview (see ThinkingPreview): the most recent slice of a thinking
            // model's chain-of-thought is pushed into the status bubble so a long reasoning phase
            // reads as "the model is working" instead of a blank bubble. This deliberately never
            // touches the streaming/answer bubble, so the empty-bubble guards (see bug history)
            // are unaffected.
            var thinkingPreview = new Services.Presentation.ThinkingPreview();
            void OnThinking(string delta)
            {
                // Reasoning tokens are generated too — count them so the gauge moves during a long
                // thinking phase (reasoning models can spend most of a turn here before any answer).
                CountLive(delta.Length);
                // Plain chat mode: a reasoning model (qwen3.6…) streams its whole chain-of-thought
                // into the reasoning channel, and surfacing that raw text in the status reads as
                // stray "agent" output — it can even be off-topic deliberation, not the answer. Show
                // a generic "thinking" indicator instead (still proves the model is working), but keep
                // the throttle so the RPC rate stays bounded. The orchestrated agent path is left
                // untouched: there the live reasoning tail is genuine step progress.
                if (!useOrchestrator)
                {
                    if (thinkingPreview.Append(delta) is not null)
                        UpsertStatusBubble("\U0001F4AD " + Strings.StatusThinking);
                    return;
                }
                if (thinkingPreview.Append(delta) is { } text)
                    UpsertStatusBubble(text);
            }

            if (useOrchestrator)
            {
                // Orchestrated path: generates an explicit plan, then runs the
                // Plan → Act → Observe loop with live step-status tracking.
                ChatMessageItem?          planBubble  = null;
                Models.AgentPlan? activePlan = null;

                var orchResult = await _orchestrator.RunAsync(
                    model:         effectiveModel,
                    history:       _history,
                    tools:         effectiveTools,
                    onStep:        step  => UpsertStatusBubble("⏳ " + step),
                    onToken:       token => { sink.Append(token); CountLive(token.Length); },
                    onPlanReady:   plan  => Post(() =>
                    {
                        activePlan = plan;
                        planBubble = ChatMessageItem.AgentPlanMsg(plan);
                        ApplyItemTheme(planBubble);
                        // Insert plan bubble just before the scroll anchors.
                        Messages.Insert(Messages.Count - 2, planBubble);
                        ScrollToBottom();
                    }),
                    onStepUpdate:  (_, _) => Post(() =>
                    {
                        // Re-render the plan markdown with updated step emojis.
                        if (planBubble is not null && activePlan is not null)
                            planBubble.RefreshPlan(activePlan);
                    }),
                    onToolExecuted: StreamToolBubble,
                    onStreamReset: () =>
                    {
                        // Drop buffered tokens synchronously FIRST so a pending timer flush cannot
                        // resurrect the bubble after it is removed below (would duplicate the response).
                        sink.Reset();
                        thinkingPreview.Reset(); // stale reasoning from the finished act must not bleed into the next
                        Post(() =>
                        {
                        // Discard the intermediate streaming bubble entirely.
                        // Setting Content = "" was insufficient: some model outputs
                        // (lone "---", partial timer-flush tokens, whitespace runs)
                        // passed HasPrintableText/HasBlocks guards and left a visually
                        // empty bubble visible.  By removing the item from Messages and
                        // nulling streamingMsg, we guarantee the final response bubble
                        // is created fresh from the very first token of the last act —
                        // so it can never contain intermediate tool-calling rationale.
                        if (streamingMsg is not null)
                        {
                            var idx = Messages.IndexOf(streamingMsg);
                            if (idx >= 0) Messages.RemoveAt(idx);
                            streamingMsg = null;
                        }
                        });
                    },
                    ct: localCts.Token,
                    onThinking: OnThinking);

                agentFinalResponse = orchResult.FinalResponse;
                agentExecutions    = orchResult.Executions;
                agentTokensUsed    = orchResult.TokensUsed;
            }
            else
            {
                // Basic path: unchanged 20-turn reactive loop.
                // Code actions use EmptyToolRegistry → Quick timeout; tool-enabled chat → Normal.
                var loopComplexity = effectiveTools == EmptyToolRegistry.Instance
                    ? TaskComplexity.Quick
                    : TaskComplexity.Normal;
                var result = await _client.RunAgentAsync(
                    model:      effectiveModel,
                    history:    _history,
                    tools:      effectiveTools,
                    onStep:     step  => UpsertStatusBubble("⏳ " + step),
                    onToken:    token => { sink.Append(token); CountLive(token.Length); },
                    ct:         localCts.Token,
                    complexity: loopComplexity,
                    onToolExecuted: StreamToolBubble,
                    onThinking: OnThinking);

                agentFinalResponse = result.FinalResponse;
                agentExecutions    = result.Executions;
                agentTokensUsed    = result.TokensUsed;
            }

            sink.Stop();

            // ── Render results — same for both paths ─────────────────────────────
            // RunOnVMContextAsync posts AFTER all onToken posts — FIFO guarantees
            // streamingMsg is fully populated when this action executes.
            await RunOnVMContextAsync(() =>
            {
                // Remove the live-status bubble before inserting the real results so it
                // never ends up persisted in the conversation or saved to disk.
                RemoveStatusBubble();

                var insertIdx = streamingMsg is not null
                    ? Messages.IndexOf(streamingMsg)
                    : Messages.Count - 2;

                // Tool bubbles are normally streamed live (StreamToolBubble); only insert them
                // here when no live streaming happened (e.g. a path that didn't wire the callback).
                if (!liveToolBubbles)
                    foreach (var exec in agentExecutions)
                        Messages.Insert(insertIdx++, BuildToolBubble(exec));

                // Multi-file recap: if ≥2 files were written/patched, add a summary bubble with "Restore All"
                var modifiedPaths = Services.Agent.ChatTurnPolicy.ModifiedFilePaths(agentExecutions);
                if (modifiedPaths.Count >= 2)
                {
                    var fileNames    = modifiedPaths.Select(Path.GetFileName).ToList();
                    var recapContent = Strings.MultiFileRecapTitle(modifiedPaths.Count, string.Join(", ", fileNames));
                    var recapItem    = ChatMessageItem.AssistantMsg(recapContent);
                    var capturedPaths = modifiedPaths;
                    recapItem.InitRestoreCallback(() => Post(() => _ = RestoreAllFilesAsync(capturedPaths)));
                    ApplyItemTheme(recapItem);
                    Messages.Insert(insertIdx++, recapItem);
                }

                ChatMessageItem? lastAssistant = null;

                // A visually empty bubble is discarded so the fallback chain below can
                // show something meaningful instead.
                streamingMsg = FinalizeStreamingBubble(streamingMsg);
                if (streamingMsg is not null)
                    lastAssistant = streamingMsg;

                // Fallback chain when the streamed bubble was absent or visibly empty:
                // stored final response → tool summary → absolute "empty response" fallback.
                switch (Services.Agent.ChatTurnPolicy.DecideFinalAnswer(
                            streamingBubbleVisible: streamingMsg is not null,
                            finalResponse:          agentFinalResponse,
                            executionCount:         agentExecutions.Count))
                {
                    case Services.Agent.FinalAnswerKind.FinalText:
                        var finalMsg = ChatMessageItem.AssistantMsg(
                            Services.Presentation.MarkdownParser.StripThinkTags(agentFinalResponse));
                        ApplyItemTheme(finalMsg);
                        Messages.Insert(Messages.Count - 2, finalMsg);
                        lastAssistant = finalMsg;
                        break;

                    case Services.Agent.FinalAnswerKind.ToolSummary:
                        var summaryMsg = ChatMessageItem.AssistantMsg(
                            Strings.MsgAgentDone(Services.Agent.ChatTurnPolicy.BuildToolSummary(agentExecutions)));
                        ApplyItemTheme(summaryMsg);
                        Messages.Insert(Messages.Count - 2, summaryMsg);
                        lastAssistant = summaryMsg;
                        break;

                    case Services.Agent.FinalAnswerKind.EmptyFallback:
                        var emptyMsg = ChatMessageItem.AssistantMsg(Strings.MsgEmptyResponse);
                        Messages.Insert(Messages.Count - 2, emptyMsg);
                        lastAssistant = emptyMsg;
                        break;

                    // FinalAnswerKind.StreamedAnswer: lastAssistant is already the streamed bubble.
                }

                if (lastAssistant is not null)
                    MarkRegeneratable(lastAssistant);

                ScrollToBottom();
                // Persist a CLEAN durable history: the pre-run history (which already ends with
                // the user's message) plus the one assistant answer shown in the chat. The run's
                // internal transcript (plan prompts, plan JSON, OBSERVE injections, capped tool
                // results, the tool list appended to the system prompt) must NOT leak across
                // turns: it bloats every following prompt and, worse, teaches the model by
                // example to replay its previous final answer verbatim instead of answering the
                // new question (observed with devstral: turn 2 returned turn 1's answer
                // word-for-word). This also matches what a session save/reload would rebuild.
                var persistedAnswer = Services.Agent.ChatTurnPolicy.ChoosePersistedAnswer(
                    lastAssistant?.Content, agentFinalResponse);
                if (persistedAnswer.Length > 0)
                    _history.Add(new ChatMessageDto("assistant", persistedAnswer));
                // The real prompt size of the run's last call reflects the discarded internal
                // transcript, not what the next turn will send — estimate from the durable
                // history instead so CompactOrTruncateAsync and the budget badge stay honest.
                _lastPromptTokens = Services.Agent.AgentOrchestrator.EstimateTokens(_history);
                _sessionTokens   += agentTokensUsed;
                _conversationTurnCount++;
                if (agentTokensUsed > 0)
                    TokenInfo = Strings.TokenUsage($"{agentTokensUsed:N0}", $"{_sessionTokens:N0}");
                UpdateContextBudget();
            });

            if (_config.OodaTurnThreshold > 0 && _conversationTurnCount % _config.OodaTurnThreshold == 0)
                await RunOodaSummaryAsync(localCts.Token);

            _ = AutoSaveAsync();
        }
        catch (OperationCanceledException)
        {
            await RunOnVMContextAsync(() =>
            {
                // Remove the live-status bubble (if any) before showing the "Cancelled" message.
                RemoveStatusBubble();
                // Finalize and discard any empty/invisible streaming bubble that was started
                // before the cancellation arrived (a visible partial response stays).
                streamingMsg = FinalizeStreamingBubble(streamingMsg);
                Messages.Insert(Messages.Count - 2, ChatMessageItem.AssistantMsg(Strings.MsgCancelled));
                ScrollToBottom();
            });
        }
        catch (Exception ex)
        {
            var msg = ex.Message;
            await RunOnVMContextAsync(() =>
            {
                // Remove the live-status bubble (if any) before showing the error message.
                RemoveStatusBubble();
                streamingMsg = FinalizeStreamingBubble(streamingMsg);
                Messages.Insert(Messages.Count - 2, ChatMessageItem.AssistantMsg(Strings.MsgError(msg)));
                ScrollToBottom();
            });
        }
        finally
        {
            // Background RAG indexing resumes automatically when RunAgentAsync releases its
            // GpuScheduler chat lease — no manual gate balancing needed here anymore.
            await RunOnVMContextAsync(() =>
            {
                // Safety-net: remove any leftover status bubble in case the main path
                // or the catch handlers didn't reach the removal code.
                RemoveStatusBubble();
                localCts?.Dispose();
                _currentCts = null;
                IsLoading   = false;
                CurrentStep = string.Empty;
            });

            // Beep when a long agent run completes, so the user can work elsewhere
            if ((DateTime.UtcNow - sendStart).TotalSeconds > 30)
                NotifyAgentComplete();
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool MessageBeep(uint uType);

    private static void NotifyAgentComplete()
    {
        try { MessageBeep(0x00000040); } // MB_ICONINFORMATION — asterisk/ding
        catch { }
    }

    private Task CancelAsync(object? _, CancellationToken ct)
    {
        _currentCts?.Cancel();
        return Task.CompletedTask;
    }

    // ── Connection Guard / Heartbeat ───────────────────────────────────────────

    #endregion

    #region Connexion, heartbeat, sessions & modes

    private async Task RetryConnectionAsync(object? _, CancellationToken ct)
    {
        _client.ResetCircuit();
        await _heartbeatCts.CancelAsync();
        _heartbeatCts = new CancellationTokenSource();
        // Keep _isBackendReachable=false until the next heartbeat tick confirms success —
        // this prevents a race where the user clicks Send during the 2-second check delay.
        await RunOnVMContextAsync(() =>
        {
            ConnectionStatusText  = "● …";
            ConnectionStatusColor = "#A0A0A0";
            ShowRetryButton       = false;
        });
        _ = StartHeartbeatAsync();
    }

    // ── VRAM monitoring ───────────────────────────────────────────────────────

    /// <summary>
    /// Called on a thread-pool thread by <see cref="ModelLifetimeService"/> whenever
    /// the running-model list is refreshed.  Formats a compact VRAM badge string and
    /// marshals the update to the Remote UI synchronization context.
    /// </summary>
    private void OnModelsRefreshed(IReadOnlyList<RunningModelInfo> models)
    {
        var text = Services.Inference.ModelCatalog.FormatVramBadge(models);

        SynchronizationContext.Post(_ =>
        {
            try
            {
                VramStatus    = text;
                HasVramStatus = text.Length > 0;
            }
            catch { }
        }, null);
    }

    private async Task StartHeartbeatAsync()
    {
        var ct = _heartbeatCts.Token;
        try
        {
            // Let the window finish its initial render before the first check.
            await Task.Delay(2_000, ct);

            var presenter = new ConnectionStatusPresenter();

            while (!ct.IsCancellationRequested)
            {
                // Re-point RAG indexing when the user switches to a different solution.
                CheckSolutionSwitch();

                var url = _config.BaseUrl;
                var ok  = await _client.CheckConnectionAsync(url, ct);

                _isBackendReachable = ok; // volatile write — read by SendCoreAsync pre-flight

                var status = presenter.Evaluate(ok);

                await RunOnVMContextAsync(() =>
                {
                    ConnectionStatusText  = status.StatusText;
                    ConnectionStatusColor = status.StatusColor;
                    SendButtonColor       = status.SendButtonColor;
                    ShowRetryButton       = status.ShowRetry;

                    var edgeMessage = status.Transition switch
                    {
                        ConnectionTransition.Restored => Strings.MsgHeartbeatRestored,
                        ConnectionTransition.Lost     => Strings.MsgConnectionGuardFailed(url),
                        _                             => null,
                    };
                    if (edgeMessage is not null)
                    {
                        var msg = ChatMessageItem.AssistantMsg(edgeMessage);
                        ApplyItemTheme(msg);
                        Messages.Insert(Messages.Count - 2, msg);
                        ScrollToBottom();
                    }
                });

                // Connected: poll every 20 s (was 60 s) — fast enough to catch a Ollama crash
                // within one poll cycle without hammering the daemon.
                // Disconnected: poll every 8 s (was 15 s) — show recovery promptly.
                await Task.Delay(ok ? 20_000 : 8_000, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private Task ToggleSessionPanelAsync(object? _, CancellationToken ct) =>
        RunOnVMContextAsync(() => IsSessionPanelOpen = !IsSessionPanelOpen);

    // Triggered by SizeChanged on each message Border — fires AFTER WPF layout so ScrollIntoView
    // sees correct item dimensions. Two separate Posts ensure VS Remote UI sends distinct batches.
    private Task OnScrollToBottomAsync(object? _, CancellationToken ct) =>
        RunOnVMContextAsync(ScrollToBottom);

    private async Task ClearAsync(object? _, CancellationToken ct)
    {
        // Capture snapshot and first user message on the VM context before clearing.
        bool hasMessages = false;
        string firstUserContent = string.Empty;
        List<SavedMessage> snapshot = [];

        await RunOnVMContextAsync(() =>
        {
            hasMessages = Messages.Count > 0;
            if (!hasMessages) return;

            var firstUser    = Messages.FirstOrDefault(m => m.Role == "user");
            firstUserContent = firstUser?.Content ?? string.Empty;
            snapshot         = SessionManager.BuildSnapshot(
                Messages.Select(m => (m.Role, m.Content, m.ToolName, m.Timestamp)));
        });

        // Fire save+title generation in background so the UI clears immediately.
        if (hasMessages)
            _ = SaveNamedSessionAsync(firstUserContent, snapshot);

        await RunOnVMContextAsync(() =>
        {
            Messages.Clear();
            Messages.Add(_anchor0);
            Messages.Add(_anchor1);
            _activeTemplateSuffix     = string.Empty;
            _workspaceContextInjected = false;
            _sessionStartTime         = null;
            _lastRegenerableMsg       = null;
            _baseSystemPrompt         = BuildSystemPrompt();
            _history               = [new("system", _baseSystemPrompt)];
            _oodaSummary           = string.Empty;
            _conversationTurnCount = 0;
            _sessionTokens         = 0;
            _lastPromptTokens      = 0;
            TokenInfo              = string.Empty;
            HasContextBudget       = false;
            ContextFillPercent     = 0;
            ContextBudgetColor     = "#606060";
            HasBuildFailedBanner   = false;   // dismiss banner on /clear
        });
    }

    private async Task LoadSessionAsync(object? _, CancellationToken ct)
    {
        try
        {
            string name = string.Empty;
            await RunOnVMContextAsync(() =>
                name = string.IsNullOrWhiteSpace(SelectedSession) ? "last_session" : SelectedSession);

            var session = await _store.LoadAsync(name, ct);
            if (session is null || session.Messages.Count == 0)
            {
                await RunOnVMContextAsync(() =>
                {
                    if (Messages.Count == 0) { Messages.Add(_anchor0); Messages.Add(_anchor1); }
                    RefreshSessionsList();
                });
                return;
            }

            await RunOnVMContextAsync(() =>
            {
                Messages.Clear();
                _activeTemplateSuffix     = string.Empty;
                _workspaceContextInjected = false;
                _sessionStartTime         = null;
                _lastRegenerableMsg       = null;
                _baseSystemPrompt         = BuildSystemPrompt();
                _history                  = SessionManager.BuildRestoredHistory(_baseSystemPrompt, session.Messages);
                _oodaSummary              = string.Empty;
                _conversationTurnCount    = 0;

                foreach (var m in session.Messages)
                {
                    var item = ChatMessageItem.FromSaved(m.Role, m.Content, m.ToolName ?? string.Empty, _config.ToolBubblesExpanded, m.Timestamp ?? string.Empty);
                    ApplyItemTheme(item);
                    Messages.Add(item);
                }

                Messages.Add(_anchor0);
                Messages.Add(_anchor1);
                IsSessionPanelOpen = false;
                RefreshSessionsList();
                ScrollToBottom();
            });
        }
        catch { }
    }


    /// <summary>Toggles agent step mode (shared by the <c>/agent-step</c> command and the toolbar button).</summary>
    private async Task ToggleStepModeAsync()
    {
        IsStepMode = !IsStepMode;
        await ShowInfoAsync(IsStepMode
            ? "🦶 **Step mode ON** — the agent will pause after each tool call. Use ▶ Resume (or `/resume`) to continue."
            : "Step mode **OFF**.");
    }

    /// <summary>Toggles plan mode (shared by the <c>/plan</c> command and the toolbar button).
    /// Refreshes the system prompt so the plan-mode instructions follow the toggle mid-session.</summary>
    private async Task TogglePlanModeAsync()
    {
        IsPlanMode = !IsPlanMode;
        await RunOnVMContextAsync(() =>
        {
            _baseSystemPrompt = BuildSystemPrompt();
            if (_history.Count > 0 && _history[0].Role == "system")
                _history[0] = new ChatMessageDto("system", _baseSystemPrompt);
        });
        await ShowInfoAsync(IsPlanMode
            ? "📋 **Plan mode ON** — read-only: the agent explores and proposes a plan, but cannot edit files or run commands. Toggle again (or `/plan`) to apply changes."
            : "Plan mode **OFF**.");
    }

    /// <summary>
    /// Main-window switch between Chat and Agent mode. Flips the persisted
    /// <see cref="InferpalConfig.AgentModeEnabled"/> (controls <c>useOrchestrator</c> in
    /// <see cref="SendCoreAsync"/>) and saves immediately so the next turn picks it up.
    /// </summary>
    private async Task ToggleAgentModeAsync()
    {
        IsAgentMode = !IsAgentMode;
        _config.AgentModeEnabled = IsAgentMode;
        _config.Save();
        AgentModeLabel = IsAgentMode ? Strings.LabelModeAgent : Strings.LabelModeChat;
        await ShowInfoAsync(IsAgentMode
            ? $"**{Strings.LabelModeAgent}** — Plan → Act → Observe"
            : $"**{Strings.LabelModeChat}**");
    }

    /// <summary>
    /// Live-sync handler for <see cref="InferpalConfig.AgentModeEnabledChanged"/>: keeps the
    /// toolbar switch aligned when the Settings checkbox flips the value. Marshals to the VM context.
    /// Idempotent for the toolbar's own toggle (the property setters no-op when unchanged).
    /// </summary>
    private void OnAgentModeConfigChanged(bool enabled) => Post(() =>
    {
        IsAgentMode    = enabled;
        AgentModeLabel = enabled ? Strings.LabelModeAgent : Strings.LabelModeChat;
    });

    /// <summary>Releases a step-mode pause, letting the agent proceed to its next action.</summary>
    private void ResumeStep() => _stepResume?.TrySetResult(true);

    private async Task PauseForStepAsync(CancellationToken ct)
    {
        _stepResume = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ChatMessageItem? pauseBubble = null;

        await RunOnVMContextAsync(() =>
        {
            pauseBubble = ChatMessageItem.AssistantMsg(
                "⏸ **Agent paused after tool call.** Click ▶ Resume (or type `/resume`) to continue, or Cancel to abort.");
            pauseBubble.InitResumeCallback(() => Post(() => ResumeStep()));
            ApplyItemTheme(pauseBubble);
            Messages.Insert(Messages.Count - 2, pauseBubble);
            ScrollToBottom();
        });

        try
        {
            await _stepResume.Task.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // Ensure TCS is completed so any racing awaiter unblocks cleanly
            _stepResume.TrySetCanceled(ct);
            throw;
        }
        finally
        {
            _stepResume = null;
            // Remove the pause bubble — it's no longer relevant after resume or cancel
            if (pauseBubble is not null)
            {
                Post(() =>
                {
                    var idx = Messages.IndexOf(pauseBubble);
                    if (idx >= 0) Messages.RemoveAt(idx);
                });
            }
        }
    }

    #endregion

    #region Pièces jointes, pins & contexte workspace

    private async Task<string> BuildWorkspaceContextAsync(CancellationToken ct)
    {
        const int TimeoutMs = 5000;
        var sb = new StringBuilder("## Workspace context (auto-injected on session start)\n\n");

        try
        {
            using var cts1   = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts1.CancelAfter(TimeoutMs);
            var solutionJson = JsonSerializer.Serialize(new { });
            var solutionArgs = JsonDocument.Parse(solutionJson).RootElement.Clone();
            var solutionInfo = await _tools.ExecuteAsync("get_solution_info", solutionArgs, cts1.Token);
            if (!string.IsNullOrWhiteSpace(solutionInfo))
                sb.AppendLine("### Solution\n").AppendLine(solutionInfo).AppendLine();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { } // timeout — skip silently
        catch { }

        try
        {
            using var cts2   = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts2.CancelAfter(TimeoutMs);
            var editorsJson  = JsonSerializer.Serialize(new { });
            var editorsArgs  = JsonDocument.Parse(editorsJson).RootElement.Clone();
            var openEditors  = await _tools.ExecuteAsync("get_open_editors", editorsArgs, cts2.Token);
            if (!string.IsNullOrWhiteSpace(openEditors))
                sb.AppendLine("### Open editors\n").AppendLine(openEditors);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { } // timeout — skip silently
        catch { }

        var result = sb.ToString().TrimEnd();
        return result.Length > "## Workspace context (auto-injected on session start)".Length + 5
            ? result
            : string.Empty;
    }

    /// <summary>
    /// Retrieves the most relevant indexed chunks for <paramref name="userText"/> and formats them
    /// as a budget-capped context block (see <see cref="RagAutoContext"/>), skipping chunks whose
    /// file is already attached. Reuses the pre-warmed shadow result when available (free), else runs
    /// one bounded embed + search. Best-effort: any failure yields no auto-context.
    /// </summary>
    private async Task<string> BuildAutoContextAsync(
        string userText, IReadOnlyList<AttachmentItem> attachments, CancellationToken ct)
    {
        if (!_config.RagAutoContextEnabled || !_config.RagEnabled)            return string.Empty;
        if (_indexService.ChunkCount == 0 || _client.IsEmbeddingCircuitOpen) return string.Empty;

        var trimmed = userText.Trim();
        if (trimmed.Length < 12 || trimmed.StartsWith('/')) return string.Empty;   // same gate as the shadow

        try
        {
            // Warm shadow (pre-computed while typing) → free; otherwise one bounded embed + search.
            var (_, results) = _indexService.TryGetShadow(trimmed);
            if (results is null || results.Count == 0)
            {
                var model = string.IsNullOrEmpty(_config.RagEmbeddingModel) ? "nomic-embed-text" : _config.RagEmbeddingModel;
                var embedding = await _client.GetEmbeddingAsync(trimmed, model, ct);
                if (embedding is null) return string.Empty;
                results = await _indexService.SearchAsync(embedding, trimmed, RagAutoContext.DefaultMaxChunks, ct);
            }
            if (results is null || results.Count == 0) return string.Empty;

            var attached = attachments
                .Where(a => !string.IsNullOrEmpty(a.SourcePath))
                .Select(a => a.SourcePath!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return RagAutoContext.Build(results, attached);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Diagnostics.Swallow("RagAutoContext", ex); return string.Empty; }
    }

    private Task ToggleSearchAsync(object? _, CancellationToken ct)
    {
        Post(() =>
        {
            IsSearchOpen = !IsSearchOpen;
            if (!IsSearchOpen)
            {
                SearchQuery = string.Empty;
            }
        });
        return Task.CompletedTask;
    }

    private Task ClearSearchAsync(object? _, CancellationToken ct)
    {
        Post(() =>
        {
            SearchQuery  = string.Empty;
            IsSearchOpen = false;
        });
        return Task.CompletedTask;
    }

    // ── Attachments ───────────────────────────────────────────────────────────

    private async Task<ITextViewSnapshot?> ResolveActiveViewAsync(CancellationToken ct)
    {
        if (_contextHolder.LatestView is not null)
            return _contextHolder.LatestView;

        if (_contextHolder.Context is not null)
        {
            try { return await _vs.Editor().GetActiveTextViewAsync(_contextHolder.Context, ct); }
            catch { }
        }
        return null;
    }

    private async Task AttachFileAsync(object? _, CancellationToken ct)
    {
        IsAttachMenuOpen = false;
        ITextViewSnapshot? view = null;
        try
        {
            view = await ResolveActiveViewAsync(ct);
        }
        catch (Exception ex)
        {
            var m = ex.Message;
            await RunOnVMContextAsync(() =>
                Messages.Insert(Messages.Count - 2, ChatMessageItem.AssistantMsg(Strings.AttachError(m))));
            return;
        }

        if (view is null)
        {
            await RunOnVMContextAsync(() =>
                Messages.Insert(Messages.Count - 2,
                    ChatMessageItem.AssistantMsg(Strings.AttachNoActiveFile)));
            return;
        }

        try
        {
            var path    = view.Document.Uri.LocalPath;
            var label   = Path.GetFileName(path);
            var content = view.Document.Text.CopyToString();
            await RunOnVMContextAsync(() => AddAttachment(label, content, sourcePath: path));
        }
        catch (Exception ex)
        {
            var m = ex.Message;
            await RunOnVMContextAsync(() =>
                Messages.Insert(Messages.Count - 2, ChatMessageItem.AssistantMsg(Strings.AttachReadError(m))));
        }
    }

    private async Task AttachSelectionAsync(object? _, CancellationToken ct)
    {
        IsAttachMenuOpen = false;
        ITextViewSnapshot? view = null;
        try
        {
            view = await ResolveActiveViewAsync(ct);
        }
        catch (Exception ex)
        {
            var m = ex.Message;
            await RunOnVMContextAsync(() =>
                Messages.Insert(Messages.Count - 2, ChatMessageItem.AssistantMsg(Strings.AttachSelectionError(m))));
            return;
        }

        if (view is null)
        {
            await RunOnVMContextAsync(() =>
                Messages.Insert(Messages.Count - 2,
                    ChatMessageItem.AssistantMsg(Strings.AttachNoActiveFile)));
            return;
        }

        try
        {
            var path     = view.Document.Uri.LocalPath;
            var fileName = Path.GetFileName(path);
            var sel      = view.Selection;
            if (!sel.IsEmpty)
            {
                // Selection snippet has no standalone on-disk path → not pinnable.
                var content = sel.Extent.CopyToString();
                await RunOnVMContextAsync(() => AddAttachment($"Selection ({fileName})", content));
            }
            else
            {
                var content = view.Document.Text.CopyToString();
                await RunOnVMContextAsync(() => AddAttachment(fileName, content, sourcePath: path));
            }
        }
        catch (Exception ex)
        {
            var m = ex.Message;
            await RunOnVMContextAsync(() =>
                Messages.Insert(Messages.Count - 2, ChatMessageItem.AssistantMsg(Strings.AttachSelectionReadError(m))));
        }
    }

    private async Task BrowseFileAsync(object? _, CancellationToken ct)
    {
        IsAttachMenuOpen = false;
        string? filePath = await ShowOpenFileDialogAsync();
        if (filePath is null) return;

        try
        {
            if (new FileInfo(filePath).Length > 512_000)
            {
                await RunOnVMContextAsync(() =>
                    Messages.Insert(Messages.Count - 2,
                        ChatMessageItem.AssistantMsg(Strings.AttachFileTooLarge)));
                return;
            }

            var content = await File.ReadAllTextAsync(filePath, ct);
            var label   = Path.GetFileName(filePath);
            await RunOnVMContextAsync(() => AddAttachment(label, content, sourcePath: filePath));
        }
        catch (Exception ex)
        {
            var m = ex.Message;
            await RunOnVMContextAsync(() =>
                Messages.Insert(Messages.Count - 2,
                    ChatMessageItem.AssistantMsg(Strings.BrowseError(m))));
        }
    }

    private static Task<string?> ShowOpenFileDialogAsync()
    {
        var tcs = new TaskCompletionSource<string?>();
        var thread = new System.Threading.Thread(() =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title  = "Select File to Attach",
                Filter = "All Files (*.*)|*.*"
            };
            tcs.SetResult(dlg.ShowDialog() == true ? dlg.FileName : null);
        });
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    private void AddAttachment(string label, string content, string? sourcePath = null)
    {
        AttachmentItem? item = null;
        Action? onPin = sourcePath is null ? null : () => Post(() =>
        {
            // Promote to a persistent pinned file, then drop the transient chip.
            AddPinnedFile(sourcePath);
            Attachments.Remove(item!);
            HasAttachments = Attachments.Count > 0;
        });
        item = new AttachmentItem(label, content, () => Post(() =>
        {
            Attachments.Remove(item!);
            HasAttachments = Attachments.Count > 0;
        }), sourcePath: sourcePath, onPin: onPin);
        var chip         = ThemePalette.For(_isDark);
        item.Background  = chip.AttachChipBg;
        item.Foreground  = chip.AttachChipText;
        item.BorderColor = chip.AttachChipBorder;
        Attachments.Add(item);
        HasAttachments = true;
    }

    // ── Pinned context files ────────────────────────────────────────────────────
    // Persistent files always injected into the system prompt (see BuildSystemPrompt).
    // Managed directly from the chat input card (gold 📌 chips) instead of the raw
    // path textbox in Settings. Parsing, dedup/cap rules and the '#'-disabled
    // round-trip live in PinnedFilesPolicy; this VM only owns the chips.

    /// <summary>Pins the active editor file as a persistent context file; falls back to a
    /// file-picker when no editor is active. Bound to the 📌 toolbar button.</summary>
    private async Task PinFileAsync(object? _, CancellationToken ct)
    {
        IsAttachMenuOpen = false;
        string? path = null;
        try
        {
            var view = await ResolveActiveViewAsync(ct);
            if (view is not null)
                path = view.Document.Uri.LocalPath;
        }
        catch { /* fall through to picker */ }

        path ??= await ShowOpenFileDialogAsync();
        if (string.IsNullOrEmpty(path)) return;

        await RunOnVMContextAsync(() => AddPinnedFile(path!));
    }

    /// <summary>Rebuilds the pinned-chip strip from <c>config.PinnedContextFiles</c> at startup.</summary>
    private void LoadPinnedFilesFromConfig()
    {
        foreach (var path in PinnedFilesPolicy.ParseActive(_config.PinnedContextFiles))
            CreatePinnedChip(path);
        HasPinnedFiles = PinnedFiles.Count > 0;
    }

    /// <summary>Adds <paramref name="path"/> to the pinned set (deduplicated, capped) and
    /// persists the change to config. No-op when already pinned or the cap is reached.</summary>
    private void AddPinnedFile(string path)
    {
        path = path.Trim();
        var current = PinnedFiles.Select(p => p.Path).ToList();
        switch (PinnedFilesPolicy.Decide(current, path))
        {
            case PinDecision.CapReached:
                Messages.Insert(Messages.Count - 2,
                    ChatMessageItem.AssistantMsg(Strings.PinLimitReached(PinnedFilesPolicy.MaxPinned)));
                return;
            case PinDecision.Duplicate:
            case PinDecision.Invalid:
                return;
        }

        CreatePinnedChip(path);
        HasPinnedFiles = true;
        SavePinnedFiles();
    }

    private void CreatePinnedChip(string path)
    {
        PinnedFileItem? item = null;
        item = new PinnedFileItem(path, Path.GetFileName(path), () => Post(() =>
        {
            PinnedFiles.Remove(item!);
            HasPinnedFiles = PinnedFiles.Count > 0;
            SavePinnedFiles();
        }));
        var chip         = ThemePalette.For(_isDark);
        item.Background  = chip.PinChipBg;
        item.Foreground  = chip.PinChipText;
        item.BorderColor = chip.PinChipBorder;
        PinnedFiles.Add(item);
    }

    private void SavePinnedFiles()
    {
        _config.PinnedContextFiles = PinnedFilesPolicy.Serialize(
            PinnedFiles.Select(p => p.Path), _config.PinnedContextFiles);
        _config.Save();
    }

    #endregion

    #region Commandes slash — routage & handlers

    // ── Slash commands ─────────────────────────────────────────────────────────
    // Parsing, usage validation, and tool-argument building live in SlashCommandRouter
    // (pure, unit-tested); this VM only executes the resulting action with its services.

    private async Task HandleSlashCommandAsync(string prompt, CancellationToken ct)
    {
        switch (SlashCommandRouter.Route(prompt, GetUserTemplates()))
        {
            case SlashInfoAction info:
                await ShowInfoAsync(info.Message);
                break;

            case SlashToolAction tool:
                await InvokeToolAsync(tool.Tool, tool.Args, ct, attachAs: tool.AttachAs);
                break;

            case SlashPromptAction expanded:
                await SendCoreAsync(expanded.Prompt, oneTimeModel: null, attachments: [], ct: ct, clearPrompt: true);
                break;

            case SlashCodeAction code:
                await RunCodeActionCommandAsync(code.Kind, ct);
                break;

            case SlashDelegatedAction delegated:
                await RunDelegatedCommandAsync(delegated.Id, delegated.Parts, ct);
                break;
        }
    }

    /// <summary>
    /// Runs a code-action slash command.
    /// <para>
    /// <c>/refactor</c>, <c>/fix</c> and <c>/doc</c> <b>apply their result directly to the
    /// document</b> (in place, undoable with Ctrl+Z) — see <see cref="RunInPlaceCodeActionAsync"/>.
    /// <c>/test</c> <b>writes its result to a separate test file</b> (created/opened or extended)
    /// — see <see cref="RunGenerateTestsAsync"/>. <c>/explain</c> and <c>/review</c> stay read-only
    /// and stream their answer into the chat, with the code delivered as an
    /// <see cref="AttachmentItem"/> (file chip).
    /// </para>
    /// </summary>
    private async Task RunCodeActionCommandAsync(SlashCodeActionKind kind, CancellationToken ct)
    {
        if (kind is SlashCodeActionKind.Refactor or SlashCodeActionKind.Fix or SlashCodeActionKind.Doc)
        {
            await RunInPlaceCodeActionAsync(kind, ct);
            return;
        }

        if (kind == SlashCodeActionKind.Test)
        {
            await RunGenerateTestsAsync(ct);
            return;
        }

        var (code, fn, label) = await GetActiveCodeAsync(ct);
        if (string.IsNullOrEmpty(code)) { await ShowInfoAsync(Strings.SlashNoActiveDocument); return; }

        var prompt = kind switch
        {
            SlashCodeActionKind.Explain => Strings.PromptExplain(fn),
            _                           => Strings.PromptReview(fn),   // Review
        };
        await SendCodeActionAsync(prompt, [new AttachmentItem(label, code, () => {})], ct);
    }

    /// <summary>
    /// Generates unit tests into a <b>separate test file</b> via the shared
    /// <see cref="TestGenerationEdit"/> pipeline: the conventional test path next to the active
    /// document is created and opened, or — if it already exists — extended in place. Reports the
    /// outcome in the chat.
    /// </summary>
    private async Task RunGenerateTestsAsync(CancellationToken ct)
    {
        var view = await ResolveActiveViewAsync(ct);
        if (view is null) { await ShowInfoAsync(Strings.SlashNoActiveDocument); return; }

        var model  = string.IsNullOrEmpty(_config.CodeActionsModel) ? _config.DefaultModel : _config.CodeActionsModel;
        var result = await TestGenerationEdit.RunAsync(_vs, view, _client, model, ct);

        await ShowInfoAsync(
            result.NoChange ? Strings.TestsNoChange
            : result.Ok     ? (result.Extended ? Strings.TestsExtended(result.TestFileName) : Strings.TestsGenerated(result.TestFileName))
            :                 Strings.TestsGenerateFailed);
    }

    /// <summary>
    /// Applies a transform action (Refactor / Fix / Add-docs) directly to the active document via
    /// the shared <see cref="InPlaceCodeEdit"/> pipeline: selection if any, otherwise the whole
    /// file. For <c>/doc</c>, the document's semantic context (namespace, type hierarchy, overrides,
    /// interface contracts) is appended to the system prompt as reference.
    /// </summary>
    private async Task RunInPlaceCodeActionAsync(SlashCodeActionKind kind, CancellationToken ct)
    {
        var view = await ResolveActiveViewAsync(ct);
        if (view is null) { await ShowInfoAsync(Strings.SlashNoActiveDocument); return; }

        var (system, instruction) = kind switch
        {
            SlashCodeActionKind.Refactor => (InPlaceCodeActionPrompts.RefactorSystem,  InPlaceCodeActionPrompts.RefactorInstruction),
            SlashCodeActionKind.Fix      => (InPlaceCodeActionPrompts.FixSystem,       InPlaceCodeActionPrompts.FixInstruction),
            _                            => (InPlaceCodeActionPrompts.DocstringSystem, InPlaceCodeActionPrompts.DocstringInstruction),
        };

        if (kind == SlashCodeActionKind.Doc)
        {
            var fullPath     = view.Document.Uri.LocalPath;
            var docText      = view.Document.Text.CopyToString();
            var contextBlock = await DocContextExtractor.BuildContextBlockAsync(fullPath, docText, ct);
            if (!string.IsNullOrWhiteSpace(contextBlock))
                system += "\n\nContext (for reference only — do not output it):\n" + contextBlock;
        }

        var model   = string.IsNullOrEmpty(_config.CodeActionsModel) ? _config.DefaultModel : _config.CodeActionsModel;
        var outcome = await InPlaceCodeEdit.RunAsync(_vs, view, _client, model, system, instruction, ct);

        // The model judged the action a no-op (already clear / correct / documented) — tell the user
        // rather than leaving the chat silent, so the absence of an edit doesn't look like a failure.
        if (outcome == InPlaceEditOutcome.NoChangeNeeded)
            await ShowInfoAsync(kind switch
            {
                SlashCodeActionKind.Refactor => Strings.RefactorNoChange,
                SlashCodeActionKind.Fix      => Strings.FixNoChange,
                _                            => Strings.DocNoChange,
            });
    }

    /// <summary>Executes the stateful commands the router hands back to the VM.</summary>
    private async Task RunDelegatedCommandAsync(SlashCommandId id, string[] parts, CancellationToken ct)
    {
        switch (id)
        {
            case SlashCommandId.Clear:
                await ClearAsync(null, ct);
                break;

            case SlashCommandId.TestBuildBanner:
                // Fires a synthetic BuildFailed event to verify the OOP banner pipeline.
                // If the red banner appears, the OOP side is working correctly.
                // If not, the issue is in the in-process MEF component or signal file.
                _buildMonitor.FireTestBuildFailed();
                await ShowInfoAsync("🔴 Test build-failure signal sent. If the red banner does not appear above, the in-process → OOP pipeline is broken (check signal file: %TEMP%\\Inferpal\\build_signal.json).");
                break;

            case SlashCommandId.Model:
                if (parts.Length < 2) { await ShowInfoAsync(Strings.SlashModelCurrent(_config.DefaultModel)); break; }
                _config.DefaultModel = parts[1];
                _config.Save();
                await RunOnVMContextAsync(() => ActiveModelLabel = parts[1]);
                await ShowInfoAsync(Strings.SlashModelChanged(parts[1]));
                break;

            case SlashCommandId.Tools:
                if (parts.Length < 2 || parts[1] is not ("on" or "off")) { await ShowInfoAsync(Strings.SlashToolsCurrent(_toolsEnabled ? "on" : "off")); break; }
                _toolsEnabled = parts[1] == "on";
                await ShowInfoAsync(Strings.SlashToolsChanged(parts[1]));
                break;

            case SlashCommandId.Export:     await ExportConversationAsync(ct);                            break;
            case SlashCommandId.Context:    await HandleContextCommandAsync(ct);                          break;
            case SlashCommandId.Memory:     await HandleMemoryCommandAsync(ct);                           break;
            case SlashCommandId.Index:      await HandleRagIndexCommandAsync(parts, ct);                  break;
            case SlashCommandId.Commit:     await HandleCommitCommandAsync(ct);                           break;
            case SlashCommandId.CommitExec: await HandleCommitExecAsync(string.Join(" ", parts[1..]), ct); break;
            case SlashCommandId.FixBuild:   await HandleFixBuildCommandAsync(parts, ct);                  break;
            case SlashCommandId.History:    await HandleHistoryCommandAsync(parts, ct);                   break;
            case SlashCommandId.UndoRun:    await HandleUndoRunCommandAsync(parts, ct);                   break;
            case SlashCommandId.PHistory:   await HandlePHistoryCommandAsync(parts, ct);                  break;
            case SlashCommandId.Models:     await HandleModelsCommandAsync(parts, ct);                    break;
            case SlashCommandId.Hardware:   await HandleHardwareCommandAsync(parts, ct);                  break;
            case SlashCommandId.Setup:      await HandleSetupCommandAsync(parts, ct);                    break;
            case SlashCommandId.AgentStep:  await ToggleStepModeAsync();                                  break;
            case SlashCommandId.Plan:       await TogglePlanModeAsync();                                  break;
            case SlashCommandId.Prompts:    await HandlePromptsCommandAsync(parts, ct);                   break;

            case SlashCommandId.Resume:
                if (_stepResume is not null)
                    ResumeStep();
                else
                    await ShowInfoAsync("No agent step is currently paused.");
                break;

            case SlashCommandId.Note:       await HandleNoteCommandAsync(parts, ct);     break;
            case SlashCommandId.Notes:      await HandleNotesCommandAsync(parts, ct);    break;
            case SlashCommandId.Snippets:   await HandleSnippetsCommandAsync(parts, ct); break;
            case SlashCommandId.Template:   await HandleTemplateCommandAsync(parts, ct); break;
            case SlashCommandId.Docs:       await HandleDocsCommandAsync(parts, ct);     break;
            case SlashCommandId.Check:      await HandleCheckCommandAsync(parts, ct);    break;
            case SlashCommandId.Rules:      await HandleRulesCommandAsync(parts, ct);    break;
            case SlashCommandId.Checks:     await HandleChecksCommandAsync(parts, ct);   break;
            case SlashCommandId.Diagnostics: await ShowInfoAsync(Services.Commands.DiagnosticsCommandHandler.Handle(parts)); break;
        }
    }

    /// <summary>
    /// Handles <c>/docs add &lt;url&gt; [title] | list | remove &lt;id&gt; | reindex [id]</c> —
    /// manages external documentation sources crawled and embedded for the <c>search_docs</c> tool.
    /// Crawl/embed runs in the background with progress reported as chat bubbles.
    /// </summary>
    private async Task HandleDocsCommandAsync(string[] parts, CancellationToken ct)
    {
        var sub   = parts.Length >= 2 ? parts[1].ToLowerInvariant() : "list";
        var sites = DocSite.Parse(_config.DocSitesJson);

        switch (sub)
        {
            case "add":
            {
                if (parts.Length < 3) { await ShowInfoAsync(Strings.DocsUsage); break; }

                var url = parts[2];
                if (!DocSite.IsValidHttpUrl(url))
                {
                    await ShowInfoAsync(Strings.DocsUsage);
                    break;
                }

                var title = parts.Length > 3 ? string.Join(" ", parts[3..]) : null;
                var site  = DocSite.Create(url, title);

                _config.DocSitesJson = DocSite.Serialize(DocSite.Upsert(sites, site));
                _config.Save();

                await ShowInfoAsync(Strings.DocsAdded(site.Title));

                // Crawl + embed in the background; progress surfaces as info bubbles.
                var progress = new Progress<string>(msg => _ = ShowInfoAsync(msg));
                _ = Task.Run(() => _docsIndex.AddOrReindexAsync(site, progress, CancellationToken.None));
                break;
            }

            case "remove":
            {
                if (parts.Length < 3) { await ShowInfoAsync(Strings.DocsUsage); break; }

                var id      = parts[2].ToLowerInvariant();
                var updated = DocSite.Remove(sites, id);
                if (updated is null) { await ShowInfoAsync(Strings.DocsNoSites); break; }

                _config.DocSitesJson = DocSite.Serialize(updated);
                _config.Save();
                await _docsIndex.RemoveAsync(id, ct);
                await ShowInfoAsync(Strings.DocsRemoved(id));
                break;
            }

            case "reindex":
            {
                var target = parts.Length >= 3
                    ? sites.FirstOrDefault(s => s.Id == parts[2].ToLowerInvariant())
                    : null;
                var toIndex = target is not null ? [target] : sites.ToArray();
                if (toIndex.Length == 0) { await ShowInfoAsync(Strings.DocsNoSites); break; }

                await ShowInfoAsync(Strings.DocsReindexing(target?.Title ?? $"{toIndex.Length}"));
                var progress = new Progress<string>(msg => _ = ShowInfoAsync(msg));
                _ = Task.Run(async () =>
                {
                    foreach (var s in toIndex)
                        await _docsIndex.AddOrReindexAsync(s, progress, CancellationToken.None);
                });
                break;
            }

            case "list":
            default:
            {
                if (sites.Count == 0) { await ShowInfoAsync(Strings.DocsNoSites); break; }

                var stats = _docsIndex.Sites.ToDictionary(
                    s => s.Site.Id, s => (s.PageCount, s.ChunkCount));
                await ShowInfoAsync(DocSite.FormatList(sites, stats));
                break;
            }
        }
    }

    private async Task HandleModelsCommandAsync(string[] parts, CancellationToken ct)
    {
        var sub = parts.Length >= 2 ? parts[1].ToLowerInvariant() : "list";

        // Pull owns a live status bubble (insert → update → remove), so it stays in the VM; the rest
        // (list/delete/running) is pure decision logic in ModelsCommandHandler.
        if (sub == "pull")
        {
            if (!_client.Capabilities.ModelManagement) { await ShowInfoAsync(Strings.ModelsBackendUnsupported); return; }
            await PullModelInteractiveAsync(parts, ct);
            return;
        }

        var result = await Services.Commands.ModelsCommandHandler.HandleAsync(_client, parts, ct);
        await ShowInfoAsync(result.Message);
    }

    // /models pull <name> — downloads a model while showing a live status bubble updated per progress line.
    private async Task PullModelInteractiveAsync(string[] parts, CancellationToken ct)
    {
        if (parts.Length < 3) { await ShowInfoAsync(Strings.ModelsPullUsage); return; }
        var model     = string.Join(" ", parts[2..]);
        var statusBbl = ChatMessageItem.StatusMsg(Strings.ModelsPulling(model));
        await RunOnVMContextAsync(() => { ApplyItemTheme(statusBbl); Messages.Insert(Messages.Count - 2, statusBbl); });

        var ok = await _client.PullModelAsync(model, status =>
            Post(() => statusBbl.Content = Strings.ModelsPullingStatus(model, status)), ct);

        await RunOnVMContextAsync(() =>
        {
            var idx = Messages.IndexOf(statusBbl);
            if (idx >= 0) Messages.RemoveAt(idx);
        });

        await ShowInfoAsync(ok ? Strings.ModelsPulled(model) : Strings.ModelsPullFailed(model));
    }

    private async Task HandleHardwareCommandAsync(string[] parts, CancellationToken ct)
    {
        var result = await Services.Commands.HardwareCommandHandler.HandleAsync(_config, _client, parts, ct);

        // The handler never persists config (so tests don't touch %APPDATA%); apply + save here.
        if (result.SetBudgetGb is { } gb)
        {
            _config.VramBudgetGb = gb;
            _config.Save();
        }

        await ShowInfoAsync(result.Message);
    }

    private async Task HandlePHistoryCommandAsync(string[] parts, CancellationToken ct)
    {
        // Read the history snapshot on the VM context, then let the pure handler decide; the VM applies
        // the single resulting effect (fill the prompt, or show a message).
        List<string> history = [];
        await RunOnVMContextAsync(() => history = [.._promptHistory.Entries]);

        var result = Services.Commands.PHistoryCommandHandler.Handle(history, parts);

        if (result.FillPrompt is { } text)
            await RunOnVMContextAsync(() => Prompt = text);
        else if (result.Message is { } msg)
            await ShowInfoAsync(msg);
    }

    private async Task HandleNoteCommandAsync(string[] parts, CancellationToken ct)
    {
        var result = await Services.Commands.NotesCommandHandler.HandleNoteAsync(
            FindProjectRoot(), parts, DateTime.Now, ct);

        if (result.RefreshSystemPrompt)
            // Refresh system prompt so the new note is visible in the current session.
            await RunOnVMContextAsync(() =>
            {
                _baseSystemPrompt = BuildSystemPrompt();
                if (_history.Count > 0 && _history[0].Role == "system")
                    _history[0] = new ChatMessageDto("system", _baseSystemPrompt);
            });

        await ShowInfoAsync(result.Message);
    }

    private async Task HandleNotesCommandAsync(string[] parts, CancellationToken ct)
    {
        var result = await Services.Commands.NotesCommandHandler.HandleNotesAsync(FindProjectRoot(), parts, ct);
        await ShowInfoAsync(result.Message);
    }

    private async Task HandleSnippetsCommandAsync(string[] parts, CancellationToken ct)
    {
        // Pure decision logic lives in SnippetsCommandHandler (unit-tested); the VM only performs the
        // UI/OS side effects it returns — clipboard copy on an STA thread and the info bubble.
        var result = await Services.Commands.SnippetsCommandHandler.HandleAsync(parts, ct);

        if (result.CopyToClipboard is { } code)
        {
            var thread = new Thread(() =>
            {
                try { System.Windows.Clipboard.SetText(string.IsNullOrEmpty(code) ? " " : code); }
                catch { }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
        }

        await ShowInfoAsync(result.Message);
    }

    private async Task HandleTemplateCommandAsync(string[] parts, CancellationToken ct)
    {
        if (parts.Length < 2)
        {
            await ShowInfoAsync(SessionManager.FormatTemplateList());
            return;
        }

        var id = parts[1].ToLowerInvariant();
        var tmpl = SessionManager.FindTemplate(id);
        if (tmpl is null)
        {
            await ShowInfoAsync($"Unknown template `{id}`. Type `/template` to see the list.");
            return;
        }

        // Clear the conversation and apply the template
        await ClearAsync(null, ct);

        await RunOnVMContextAsync(() =>
        {
            _activeTemplateSuffix = tmpl.SystemSuffix;
            _baseSystemPrompt     = BuildSystemPrompt();
            _history[0]           = new ChatMessageDto("system", _baseSystemPrompt);
        });

        await ShowInfoAsync(tmpl.Greeting);
    }

    // Config templates first so they shadow a prompt file with the same command name
    // (the router resolves with FirstOrDefault; built-ins always win over both).
    private IReadOnlyList<UserSlashTemplate> GetUserTemplates()
    {
        var fromConfig = SlashCommandRouter.ParseUserTemplates(_config.PromptTemplates);
        var fromFiles  = PromptFilesService.Load(PromptsDir());
        return fromFiles.Count == 0
            ? fromConfig
            : fromConfig.Concat(fromFiles).DistinctBy(t => t.Name).ToList();
    }

    private string PromptsDir() => Path.Combine(FindProjectRoot(), ".inferpal", "prompts");

    #endregion

    #region Invocation d'outils & export

    private async Task InvokeToolAsync(string toolName, object argsObj, CancellationToken ct, string? attachAs = null)
    {
        string    result;
        DiffInfo? diff = null;
        try
        {
            var json = JsonSerializer.Serialize(argsObj);
            var args = JsonDocument.Parse(json).RootElement.Clone();
            result   = await _tools.ExecuteAsync(toolName, args, ct);
            diff     = _tools.ConsumeDiff();
        }
        catch (Exception ex)
        {
            result = Strings.MsgError(ex.Message);
        }

        if (attachAs is not null)
        {
            await RunOnVMContextAsync(() => AddAttachment(attachAs, result));
            return;
        }

        var capturedDiff = diff;
        await RunOnVMContextAsync(() =>
        {
            var item = ChatMessageItem.ToolMsg(toolName, result, expanded: true);
            if (capturedDiff is not null)
                item.InitDiff(capturedDiff);
            ApplyItemTheme(item);
            bool buildFailed = toolName == GetDiagnosticsTool.ToolName && GetDiagnosticsTool.OutputHasErrors(result);
            if (buildFailed)
                item.InitFixCallback(result, rawErrors => Post(() => Prompt = BuildFixPrompt(rawErrors)));
            Messages.Insert(Messages.Count - 2, item);

            // When the build fails, insert a visible assistant bubble that proposes to fix it.
            // The button pre-fills "/fix-build" so the user just presses Enter to start the
            // automated fix loop, rather than having to discover the button inside the tool bubble.
            if (buildFailed)
            {
                var errorCount = result
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Count(l => GetDiagnosticsTool.ErrorLineRegex.IsMatch(l));
                var proposal = ChatMessageItem.AssistantMsg(Strings.BuildFailedProposal(errorCount));
                proposal.InitFixCallback(result, _ => Post(() => Prompt = "/fix-build"));
                ApplyItemTheme(proposal);
                Messages.Insert(Messages.Count - 2, proposal);
            }

            ScrollToBottom();
        });
    }

    /// <summary>
    /// Finalizes a streaming bubble (must run on the VM context): stops streaming —
    /// which triggers ParseMarkdown — then either discards the bubble when it is
    /// visually empty (returns <c>null</c>; see <see cref="Services.Agent.ChatTurnPolicy.IsVisiblyEmpty"/>
    /// and the empty-bubble bug history) or themes it and returns it. Replaces the
    /// triple guard that used to be copy-pasted at every stream completion/cancel/error.
    /// </summary>
    private ChatMessageItem? FinalizeStreamingBubble(ChatMessageItem? item)
    {
        if (item is null) return null;
        item.IsStreaming = false;
        if (Services.Agent.ChatTurnPolicy.IsVisiblyEmpty(item.Content))
        {
            var idx = Messages.IndexOf(item);
            if (idx >= 0) Messages.RemoveAt(idx);
            return null;
        }
        ApplyItemTheme(item);
        return item;
    }

    private void MarkRegeneratable(ChatMessageItem msg)
    {
        if (_lastRegenerableMsg is not null)
            _lastRegenerableMsg.IsRegeneratable = false;
        _lastRegenerableMsg = msg;
        msg.InitRegenerateCallback(() => Post(() => _ = RegenerateAsync()));
    }

    private async Task RegenerateAsync()
    {
        if (_currentCts is not null) return;

        // Find the last user message in the visible list (before the two anchors at the end)
        ChatMessageItem? lastUserItem = null;
        var lastUserIdx = -1;
        for (var i = Messages.Count - 3; i >= 0; i--)
        {
            if (Messages[i].Role == "user") { lastUserItem = Messages[i]; lastUserIdx = i; break; }
        }
        if (lastUserItem is null) return;

        var userText = lastUserItem.Content;

        // Remove everything from the user message onward (keep only what came before)
        while (Messages.Count - 2 > lastUserIdx)
            Messages.RemoveAt(Messages.Count - 3);

        // Forget the current regenerate handle — the new response will set a fresh one
        if (_lastRegenerableMsg is not null)
        {
            _lastRegenerableMsg.IsRegeneratable = false;
            _lastRegenerableMsg = null;
        }

        // Roll _history back to just before the last user turn (_history[0] is always the system prompt)
        for (var i = _history.Count - 1; i >= 1; i--)
        {
            if (_history[i].Role == "user") { _history.RemoveRange(i, _history.Count - i); break; }
        }

        await SendCoreAsync(userText, oneTimeModel: null, attachments: [], ct: CancellationToken.None, clearPrompt: false);
    }

    private async Task RestoreAllFilesAsync(List<string> paths)
    {
        foreach (var path in paths)
            await InvokeToolAsync("restore_file", new { path }, CancellationToken.None);
    }

    private Task ShowToolResultAsync(string toolName, string result) =>
        RunOnVMContextAsync(() =>
        {
            var item = ChatMessageItem.ToolMsg(toolName, result, expanded: true);
            ApplyItemTheme(item);
            Messages.Insert(Messages.Count - 2, item);
            ScrollToBottom();
        });

    private Task ShowInfoAsync(string markdown) =>
        RunOnVMContextAsync(() =>
        {
            var item = ChatMessageItem.AssistantMsg(markdown);
            ApplyItemTheme(item);
            Messages.Insert(Messages.Count - 2, item);
        });

    private Task ExportAsync(object? _, CancellationToken ct) => ExportConversationAsync(ct);

    private async Task ExportConversationAsync(CancellationToken ct)
    {
        List<Services.Persistence.ExportMessage> snapshot = [];
        await RunOnVMContextAsync(() =>
        {
            snapshot = Messages
                .Where(m => m.Role is "user" or "assistant" or "tool")
                .Select(m => new Services.Persistence.ExportMessage(m.Role, m.Label, m.Content, m.Timestamp))
                .ToList();
        });

        if (snapshot.Count == 0)
        {
            await ShowInfoAsync(Strings.ExportNoMessages);
            return;
        }

        // Capture session stats on VM context
        int    sessionTokens = 0;
        string modelName     = string.Empty;
        await RunOnVMContextAsync(() =>
        {
            sessionTokens = _sessionTokens;
            modelName     = _config.DefaultModel;
        });

        string? filePath = await ShowSaveFileDialogAsync();
        if (filePath is null) return;

        try
        {
            var isTxt    = Path.GetExtension(filePath).Equals(".txt", StringComparison.OrdinalIgnoreCase);
            var date     = DateTime.Now.ToString("f", CultureInfo.CurrentCulture);
            var duration = _sessionStartTime.HasValue
                ? (DateTime.Now - _sessionStartTime.Value) : (TimeSpan?)null;

            var document = Services.Persistence.ConversationExporter.Build(
                snapshot, isTxt, modelName, sessionTokens, date,
                Services.Persistence.ConversationExporter.FormatDuration(duration));

            await File.WriteAllTextAsync(filePath, document, System.Text.Encoding.UTF8, ct);
            await ShowInfoAsync(Strings.ExportSuccess(Path.GetFileName(filePath)));
        }
        catch (Exception ex)
        {
            var m = ex.Message;
            await ShowInfoAsync(Strings.ExportFailed(m));
        }
    }

    private static Task<string?> ShowSaveFileDialogAsync()
    {
        var tcs    = new TaskCompletionSource<string?>();
        var thread = new System.Threading.Thread(() =>
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title      = "Export Conversation",
                Filter     = "Markdown (*.md)|*.md|Text (*.txt)|*.txt",
                DefaultExt = ".md",
                FileName   = $"conversation_{DateTime.Now:yyyy-MM-dd_HHmm}",
            };
            tcs.SetResult(dlg.ShowDialog() == true ? dlg.FileName : null);
        });
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    // EmptyToolRegistry is now internal and lives in Inferpal.Services (see EmptyToolRegistry.cs).

    #endregion

    #region Mentions & autocomplétion

    // ── Shadow RAG pre-warm ────────────────────────────────────────────────────

    /// <summary>
    /// Debounced (800 ms) background embedding + search triggered while the user types.
    /// Results are cached in <see cref="ProjectIndexService"/> and consumed by
    /// <see cref="SemanticSearchTool"/> to skip the embedding round-trip when the
    /// agent's query matches the typed prompt exactly.
    /// </summary>
    private void TriggerShadowSearch(string prompt)
    {
        _shadowSearchCts?.Cancel();
        _shadowSearchCts?.Dispose();
        _shadowSearchCts = null;

        // Clear stale auto-attach chips immediately when search is reset
        var staleChips = Attachments.Where(a => a.IsAutoAttach).ToList();
        foreach (var chip in staleChips) Attachments.Remove(chip);
        if (staleChips.Count > 0) HasAttachments = Attachments.Count > 0;

        // Guard: RAG must be enabled, index ready, embedding circuit healthy
        if (!_config.RagEnabled
            || _indexService.ChunkCount == 0
            || _client.IsEmbeddingCircuitOpen)
            return;

        // Skip slash commands and very short prompts (not worth embedding)
        var trimmed = prompt.Trim();
        if (trimmed.Length < 12 || trimmed.StartsWith('/')) return;

        var model = string.IsNullOrEmpty(_config.RagEmbeddingModel)
            ? "nomic-embed-text"
            : _config.RagEmbeddingModel;

        var cts = _shadowSearchCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(800, cts.Token);          // debounce — wait for pause in typing
                await _indexService.ShadowPreWarmAsync(trimmed, model, cts.Token);

                // Auto-attach top RAG results as dismissable cyan chips
                var (_, results) = _indexService.TryGetShadow(trimmed);
                if (results?.Count > 0)
                {
                    var topPaths = results
                        .Select(r => r.Chunk.FilePath)
                        .Where(p => !string.IsNullOrEmpty(p))
                        .Distinct()
                        .Take(2)
                        .ToList();
                    Post(() => UpdateAutoRagAttachments(topPaths, results));
                }
            }
            catch (OperationCanceledException) { }         // user kept typing
            catch { }                                      // best-effort
        });
    }

    private void UpdateAutoRagAttachments(
        List<string> paths,
        List<(RagChunk Chunk, float Score)> results)
    {
        // Remove existing auto-attach chips
        var toRemove = Attachments.Where(a => a.IsAutoAttach).ToList();
        foreach (var a in toRemove) Attachments.Remove(a);

        foreach (var path in paths)
        {
            // Skip if user already has this file attached manually
            var fileName = Path.GetFileName(path);
            if (Attachments.Any(a => !a.IsAutoAttach && a.Label == fileName)) continue;

            // Use the best chunk content for the file (most relevant)
            var chunks    = results.Where(r => r.Chunk.FilePath == path).Take(3).ToList();
            var content   = string.Join("\n\n...\n\n", chunks.Select(r => r.Chunk.Content));

            AttachmentItem? item = null;
            item = new AttachmentItem($"🔮 {fileName}", content, () =>
            {
                Post(() =>
                {
                    Attachments.Remove(item!);
                    HasAttachments = Attachments.Count > 0;
                });
            }, isAutoAttach: true, sourcePath: path);   // sourcePath enables auto-context dedup

            Attachments.Add(item);
        }

        HasAttachments = Attachments.Count > 0;
    }

    // ── @mention completion (typed context providers) ──────────────────────────
    // Typing '@' opens a menu of context categories; query-based ones (@file/@code/@folder)
    // drill into a sub-search, instant ones (@clipboard/@tree/@diff/@problems) attach directly.
    // Parsing, category matching, and the filesystem searches live in MentionController
    // (pure, unit-tested); this VM keeps the debounce, popup UI, and attachments.

    private void TriggerMentionSearch(string prompt)
    {
        _mentionCts?.Cancel();
        _mentionCts?.Dispose();
        _mentionCts = null;

        var palette = ThemePalette.For(_isDark);
        var text    = palette.Text;
        var sub     = palette.SuggestionSubtleText;

        switch (MentionController.Parse(prompt))
        {
            // ── 1a. "@code xyz" committed: semantic search is explicit — a single action item. ──
            case MentionCommittedQuery { Category: "code" } code:
            {
                MentionSuggestions.Clear();
                var q = code.Query.Trim();
                if (q.Length > 0)
                    MentionSuggestions.Add(new MentionSuggestion(
                        "🔮 " + q, Strings.MentionCodeDesc, text, sub,
                        ct => SelectCodeMentionAsync(q, ct)));
                else
                    // Just committed "@code " with no query yet — keep the popup open with a
                    // hint instead of leaving the user staring at a silent dead-end.
                    MentionSuggestions.Add(new MentionSuggestion(
                        "🔮 " + Strings.MentionCodeHint, Strings.MentionCodeDesc, text, sub,
                        _ => Task.CompletedTask));
                HasMentionSuggestions = MentionSuggestions.Count > 0;
                return;
            }

            // ── 1b. "@file Foo" / "@folder Bar" committed → debounced filesystem search. ──
            case MentionCommittedQuery committed:
            {
                var ql         = committed.Query.Trim().ToLowerInvariant();
                var openForCat = _contextHolder.GetOpenPaths();
                var isFolder   = committed.Category == "folder";
                var catCts     = _mentionCts = new CancellationTokenSource();
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(120, catCts.Token);
                        var root  = FindProjectRoot(openForCat);
                        var paths = isFolder
                            ? MentionController.FindFolders(root, ql, catCts.Token)
                            : MentionController.FindFiles(root, ql, catCts.Token);
                        if (catCts.IsCancellationRequested) return;
                        Post(() =>
                        {
                            MentionSuggestions.Clear();
                            foreach (var s in BuildPathSuggestions(paths, root, isFolder, text, sub))
                                MentionSuggestions.Add(s);
                            HasMentionSuggestions = MentionSuggestions.Count > 0;
                        });
                    }
                    catch (OperationCanceledException) { }
                }, catCts.Token);
                return;
            }

            // ── 2. Choosing a category (still typing "@xxx", no space yet) ──
            case MentionTypingCategory typing:
            {
                var queryLower = typing.Partial.ToLowerInvariant();

                MentionSuggestions.Clear();
                // Matching categories first (token without the leading '@' starts with what was typed).
                foreach (var category in MentionController.MatchCategories(queryLower))
                {
                    var cat = category;
                    MentionSuggestions.Add(new MentionSuggestion(
                        cat.Token, cat.Desc(), text, sub, ct => SelectCategoryAsync(cat, ct)));
                }

                if (queryLower.Length == 0)
                {
                    // Bare '@' → also list currently open files for quick attach (below the categories).
                    var openPaths = _contextHolder.GetOpenPaths();
                    var root      = FindProjectRoot(openPaths);
                    foreach (var p in openPaths.Take(6).Where(File.Exists))
                    {
                        var path = p;
                        MentionSuggestions.Add(new MentionSuggestion(
                            Path.GetFileName(path), MentionController.RelLabel(path, root), text, sub,
                            ct => SelectFileMentionAsync(path, ct)));
                    }
                    HasMentionSuggestions = MentionSuggestions.Count > 0;
                    return;
                }

                // "@xxx" → keep the category matches and append fuzzy file matches (debounced).
                HasMentionSuggestions = MentionSuggestions.Count > 0;
                var categoryCount  = MentionSuggestions.Count;
                var openFilePaths  = _contextHolder.GetOpenPaths();
                var cts            = _mentionCts = new CancellationTokenSource();
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(120, cts.Token);
                        var root      = FindProjectRoot(openFilePaths);
                        var filePaths = MentionController.FindFiles(root, queryLower, cts.Token);
                        if (cts.IsCancellationRequested) return;
                        Post(() =>
                        {
                            while (MentionSuggestions.Count > categoryCount)
                                MentionSuggestions.RemoveAt(MentionSuggestions.Count - 1);
                            foreach (var s in BuildPathSuggestions(filePaths, root, folders: false, text, sub))
                                MentionSuggestions.Add(s);
                            HasMentionSuggestions = MentionSuggestions.Count > 0;
                        });
                    }
                    catch (OperationCanceledException) { }
                }, cts.Token);
                return;
            }

            default:    // MentionNone — close the popup if it was open.
                if (_hasMentionSuggestions)
                {
                    MentionSuggestions.Clear();
                    HasMentionSuggestions = false;
                }
                return;
        }
    }

    /// <summary>Wraps ranked paths from <see cref="MentionController"/> into popup suggestion items.</summary>
    private List<MentionSuggestion> BuildPathSuggestions(
        IReadOnlyList<string> paths, string rootDir, bool folders, string text, string sub) =>
        paths.Select(p =>
        {
            var path = p;
            return folders
                ? new MentionSuggestion(
                    "📁 " + Path.GetFileName(path), MentionController.RelLabel(path, rootDir), text, sub,
                    ct => SelectFolderMentionAsync(path, ct))
                : new MentionSuggestion(
                    Path.GetFileName(path), MentionController.RelLabel(path, rootDir), text, sub,
                    ct => SelectFileMentionAsync(path, ct));
        }).ToList();

    // ── Category selection ──────────────────────────────────────────────────────

    private async Task SelectCategoryAsync(MentionCategory cat, CancellationToken ct)
    {
        if (cat.QueryBased)
        {
            // Commit "@token " into the prompt; the user then types the provider-specific query.
            await RunOnVMContextAsync(() =>
                Prompt = MentionController.CommitCategory(_prompt, cat.Token));
            return;
        }

        // Instant provider: strip the partial @token, fetch context, attach as a chip.
        await RunOnVMContextAsync(StripMentionToken);
        await AttachInstantAsync(cat.Kind, ct);
    }

    private async Task AttachInstantAsync(MentionKind kind, CancellationToken ct)
    {
        try
        {
            switch (kind)
            {
                case MentionKind.Clipboard:
                {
                    var clip = string.Empty;
                    await RunOnVMContextAsync(() =>
                    {
                        try { clip = System.Windows.Clipboard.GetText() ?? string.Empty; } catch { }
                    });
                    if (string.IsNullOrWhiteSpace(clip))
                    {
                        await NotifyMentionAsync(Strings.MentionClipboardEmpty);
                        return;
                    }
                    await RunOnVMContextAsync(() => AddAttachment("📋 @clipboard", clip));
                    break;
                }
                case MentionKind.Tree:
                {
                    var map = await _tools.ExecuteAsync("generate_project_map", MentionArgs(), ct);
                    await RunOnVMContextAsync(() => AddAttachment("🌲 @tree", map));
                    break;
                }
                case MentionKind.Diff:
                {
                    var diff = await _tools.ExecuteAsync(
                        "get_git_status", MentionArgs(new { include_diff = true }), ct);
                    await RunOnVMContextAsync(() => AddAttachment("📊 @diff", diff));
                    break;
                }
                case MentionKind.Problems:
                {
                    var diags = await _tools.ExecuteAsync("get_diagnostics", MentionArgs(), ct);
                    await RunOnVMContextAsync(() => AddAttachment("⚠ @problems", diags));
                    break;
                }
                case MentionKind.Debugger:
                {
                    // Read the signal directly (not via the tool) so "not paused" can be a
                    // notification instead of a useless attachment chip.
                    var snap = Services.VsIntegration.DebuggerStateSignal.TryRead();
                    if (snap is null)
                    {
                        await NotifyMentionAsync(Strings.MentionDebuggerNone);
                        return;
                    }
                    var state = Services.VsIntegration.DebuggerStateSignal.Format(snap);
                    await RunOnVMContextAsync(() => AddAttachment("🐞 @debugger", state));
                    break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            var m = ex.Message;
            await NotifyMentionAsync(Strings.AttachError(m));
        }
    }

    private static JsonElement MentionArgs(object? o = null) =>
        JsonDocument.Parse(o is null ? "{}" : JsonSerializer.Serialize(o)).RootElement.Clone();

    private Task NotifyMentionAsync(string message) =>
        RunOnVMContextAsync(() =>
            Messages.Insert(Messages.Count - 2, ChatMessageItem.AssistantMsg(message)));

    /// <summary>Removes the trailing @mention token (committed "@file foo" or bare "@foo").</summary>
    private void StripMentionToken() => Prompt = MentionController.StripMentionToken(_prompt);

    private async Task SelectFolderMentionAsync(string folderPath, CancellationToken ct)
    {
        try
        {
            var content = await Task.Run(() => MentionController.BuildFolderContext(folderPath, ct), ct);
            var label   = "📁 " + Path.GetFileName(folderPath);
            await RunOnVMContextAsync(() =>
            {
                StripMentionToken();
                AddAttachment(label, content);
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            var m = ex.Message;
            await NotifyMentionAsync(Strings.AttachError(m));
        }
    }

    private async Task SelectCodeMentionAsync(string query, CancellationToken ct)
    {
        try
        {
            await RunOnVMContextAsync(StripMentionToken);
            var result = await _tools.ExecuteAsync(
                "search_codebase", MentionArgs(new { query }), ct);
            var label = "🔮 " + (query.Length > 40 ? query[..40] + "…" : query);
            await RunOnVMContextAsync(() => AddAttachment(label, result));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            var m = ex.Message;
            await NotifyMentionAsync(Strings.AttachError(m));
        }
    }

    private async Task SelectFileMentionAsync(string filePath, CancellationToken ct)
    {
        try
        {
            if (new FileInfo(filePath).Length > 512_000)
            {
                await NotifyMentionAsync(Strings.AttachFileTooLarge);
                return;
            }

            var content = await File.ReadAllTextAsync(filePath, ct);
            var label   = Path.GetFileName(filePath);

            await RunOnVMContextAsync(() =>
            {
                // Setting Prompt triggers TriggerMentionSearch → clears suggestions automatically.
                StripMentionToken();
                AddAttachment(label, content, sourcePath: filePath);
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            var m = ex.Message;
            await NotifyMentionAsync(Strings.AttachError(m));
        }
    }

    // ── Slash-command autocomplete ─────────────────────────────────────────────

    private void TriggerSlashSuggestions(string text)
    {
        // Matching (built-ins + user templates, '/prefix' with no space yet) lives in the router.
        var matches = SlashCommandRouter.MatchCommands(text, GetUserTemplates());
        if (matches.Count > 0 || (text.StartsWith('/') && !text.Contains(' ')))
        {
            var palette   = ThemePalette.For(_isDark);
            var textColor = palette.Text;
            var subColor  = palette.SuggestionSubtleText;
            SlashSuggestions.Clear();
            foreach (var (cmd, hint) in matches)
                SlashSuggestions.Add(new SlashSuggestion(cmd, hint, textColor, subColor, SelectSlashSuggestionAsync));
            HasSlashSuggestions = SlashSuggestions.Count > 0;
        }
        else if (_hasSlashSuggestions)
        {
            SlashSuggestions.Clear();
            HasSlashSuggestions = false;
        }
    }

    private Task SelectSlashSuggestionAsync(string command, CancellationToken ct)
    {
        Post(() =>
        {
            SlashSuggestions.Clear();
            HasSlashSuggestions = false;
            Prompt = command + " ";
        });
        return Task.CompletedTask;
    }

    #endregion

    #region Code actions & bannière de build

    // ── Code-action helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Returns the raw source code (no fences) of the active selection or full document,
    /// together with a display label suitable for use as an <see cref="AttachmentItem"/> chip.
    /// </summary>
    private async Task<(string RawCode, string FileName, string Label)> GetActiveCodeAsync(CancellationToken ct)
    {
        var view = await ResolveActiveViewAsync(ct);
        if (view is null) return (string.Empty, string.Empty, string.Empty);

        var fileName = Path.GetFileName(view.Document.Uri.LocalPath);
        var sel      = view.Selection;
        var rawCode  = !sel.IsEmpty
            ? sel.Extent.CopyToString()
            : view.Document.Text.CopyToString();
        var label    = !sel.IsEmpty
            ? $"Selection ({fileName})"
            : fileName;

        if (rawCode.Length > MaxCodeChars)
            rawCode = rawCode[..MaxCodeChars] + "\n…(truncated)";

        return (rawCode, fileName, label);
    }

    private static string DetectLanguage(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".cs"     => "csharp",
            ".ts"     => "typescript",
            ".tsx"    => "typescript",
            ".js"     => "javascript",
            ".jsx"    => "javascript",
            ".py"     => "python",
            ".go"     => "go",
            ".java"   => "java",
            ".cpp"    => "cpp",
            ".c"      => "c",
            ".h"      => "cpp",
            ".hpp"    => "cpp",
            ".rs"     => "rust",
            ".fs"     => "fsharp",
            ".rb"     => "ruby",
            ".php"    => "php",
            ".swift"  => "swift",
            ".kt"     => "kotlin",
            ".razor"  => "razor",
            ".vue"    => "vue",
            _         => string.Empty,
        };

    /// <summary>
    /// Sends a code-action prompt with tools disabled (read-only response).
    /// Always uses the CodeActionsModel (or DefaultModel as fallback) as oneTimeModel,
    /// which triggers <see cref="EmptyToolRegistry"/> in <see cref="SendCoreAsync"/>.
    /// The file code is passed via <paramref name="attachments"/> (chip in the chat UI)
    /// rather than embedded in the prompt text.
    /// </summary>
    private async Task SendCodeActionAsync(string prompt, List<AttachmentItem> attachments, CancellationToken ct)
    {
        var model = string.IsNullOrEmpty(_config.CodeActionsModel)
            ? _config.DefaultModel
            : _config.CodeActionsModel;
        await SendCoreAsync(prompt, oneTimeModel: model, attachments: attachments, ct: ct, clearPrompt: true);
    }

    // ── Diagnostics fix ────────────────────────────────────────────────────────

    // Prompt formatting and diagnostic parsing live in FixPromptBuilder (unit-tested);
    // the VM only supplies the file reader.
    private static string BuildFixPrompt(string rawErrors) =>
        Services.CodeActions.FixPromptBuilder.Build(rawErrors, path =>
        {
            if (!File.Exists(path)) return null;
            try { return File.ReadAllText(path, System.Text.Encoding.UTF8); }
            catch { return null; }
        });

    // ── VS Build failure detection ────────────────────────────────────────────

    /// <summary>
    /// Called on a background thread by <see cref="VsBuildMonitor"/> when Visual Studio
    /// completes a solution build with at least one compilation error.
    /// Shows the dedicated "Build Failed" banner above the input card (LocalPilot-style),
    /// populated with the first error line.  The full error list is stored so that
    /// "Fix with AI" can pre-fill the prompt.
    /// </summary>
    private void OnVsBuildFailed(int errorCount, string errorLines)
    {
        // Show the banner regardless of Ollama connectivity.
        // If offline, clicking "Fix with AI" will surface the offline error naturally.

        // Extract first line for display; truncate if it is too wide for the banner.
        var firstError = Services.CodeActions.FixPromptBuilder.FirstErrorLine(errorLines);

        _buildFailedErrorLines = errorLines;

        Post(() =>
        {
            BuildFailedFirstError = firstError;
            HasBuildFailedBanner  = true;
        });
    }

    /// <summary>Closes the "Build Failed" banner without triggering a fix.</summary>
    private Task DismissBuildBannerAsync(object? _, CancellationToken ct) =>
        RunOnVMContextAsync(() => HasBuildFailedBanner = false);

    /// <summary>
    /// Closes the banner and pre-fills <c>/fix-build</c> in the prompt so the user
    /// only needs to press Enter to start the automated build-fix loop.
    /// </summary>
    private Task FixBuildBannerAsync(object? _, CancellationToken ct) =>
        RunOnVMContextAsync(() =>
        {
            HasBuildFailedBanner = false;
            Prompt = "/fix-build";
        });

    #endregion

    #region Indexation RAG, premier lancement, OODA & compaction

    // ── RAG indexing ──────────────────────────────────────────────────────────

    /// <summary>
    /// Waits for VS to finish initializing, then triggers background RAG indexing.
    /// Retries up to 5 times (separated by 5 s each) until the solution root can be located.
    /// </summary>
    private async Task StartRagIndexingAsync()
    {
        try
        {
            // Give VS time to fully initialize before the first attempt
            await Task.Delay(4_000).ConfigureAwait(false);

            if (!_config.RagEnabled) return;

            for (int attempt = 0; attempt < 5; attempt++)
            {
                if (attempt > 0)
                    await Task.Delay(5_000).ConfigureAwait(false);

                // Use FindReliableProjectRoot (signal → open files → CWD-anchored .sln) for a
                // *reliable* root.
                // FindProjectRoot() always returns something (falls back to CWD), so
                // the guard `!string.IsNullOrEmpty(root)` was always true and RAG could
                // start against the wrong directory.  We only start once we have a real
                // .sln-anchored root; if none is found after 5 attempts we give up.
                var root = FindReliableProjectRoot();
                if (root is not null)
                {
                    _indexService.StartIndexing(root);
                    return;
                }
            }
        }
        catch { }
    }

    /// <summary>
    /// Detects when the active VS solution (reported by the in-process package via
    /// <see cref="ActiveSolutionSignal"/>) differs from the directory RAG is currently indexing,
    /// and re-points indexing at the new solution root. No-ops when RAG is disabled, when no
    /// solution is reported yet, or when the root is unchanged. Called on each heartbeat tick.
    /// </summary>
    private void CheckSolutionSwitch()
    {
        if (!_config.RagEnabled) return;

        // Only handle genuine *switches*: the initial index is driven by StartRagIndexingAsync.
        // RootDir is set synchronously by StartIndexing, so this is empty only before the first pass.
        if (string.IsNullOrEmpty(_indexService.RootDir)) return;

        var activeDir = ActiveSolutionSignal.TryReadSolutionDir();
        if (string.IsNullOrEmpty(activeDir)) return;

        if (string.Equals(activeDir, _indexService.RootDir, StringComparison.OrdinalIgnoreCase))
            return;

        // A different solution is open than the one indexed — re-index the new root.
        _indexService.StartIndexing(activeDir!);
    }

    // ── First-Run Auto-Discovery ───────────────────────────────────────────────
    // Model classification and choice live in ModelCatalog (unit-tested, shared with
    // the settings VM's embedding dropdown).

    /// <summary>
    /// On first launch: pings Ollama, discovers models, picks the best one,
    /// and posts a welcome bubble. Clears <see cref="InferpalConfig.IsFirstRun"/> permanently.
    /// </summary>
    private async Task StartFirstRunDiscoveryAsync()
    {
        try
        {
            if (!_config.IsFirstRun) return;

            // Let the heartbeat complete its first check (2 s delay + RTT) before we act.
            await Task.Delay(3_500).ConfigureAwait(false);

            if (!_config.IsFirstRun) return; // guard against re-entry

            // First-run posts its bubbles directly (no user turn exists yet to attach a reply to).
            await RunSetupDiscoveryAsync(FirstRunPresentAsync).ConfigureAwait(false);
        }
        catch { }
    }

    /// <summary>Presenter used by the automatic first-run path: inserts a themed assistant bubble.</summary>
    private Task FirstRunPresentAsync(string text) => RunOnVMContextAsync(() =>
    {
        var msg = ChatMessageItem.AssistantMsg(text);
        ApplyItemTheme(msg);
        Messages.Insert(Messages.Count - 2, msg);
        ScrollToBottom();
    });

    /// <summary>
    /// Core setup discovery, shared by the automatic first-run and the manual <c>/setup</c> command:
    /// auto-detects the backend, checks connectivity, discovers and auto-selects chat + embedding
    /// models, seeds the VRAM budget, and reports the result via <paramref name="present"/>. Always
    /// clears <see cref="InferpalConfig.IsFirstRun"/>. Re-runnable on demand (no IsFirstRun guard here).
    /// </summary>
    private async Task RunSetupDiscoveryAsync(Func<string, Task> present)
    {
        var url = _config.BaseUrl;

        // ── 0. Backend auto-detection ─────────────────────────────────────────
        // Probe the URL to pick the right backend (Ollama / LM Studio / OpenAI-compatible) without
        // the user choosing manually. When it differs from the configured one, persist it and use a
        // matching client for discovery; the active singleton is rebuilt on the next VS reload.
        var detected = await Services.Inference.ProviderProbe.DetectAsync(url, _config.ApiKey, CancellationToken.None)
            .ConfigureAwait(false);
        if (detected is not null && !string.Equals(detected, _config.Provider, StringComparison.OrdinalIgnoreCase))
        {
            _config.Provider = detected;
            _config.Save();
        }
        var client = detected is not null ? Services.Inference.InferenceProviderFactory.Create(_config) : _client;

        // ── 1. Connectivity check ─────────────────────────────────────────────
        // A successful probe already proves reachability; otherwise fall back to the client's own check.
        var reachable = detected is not null
            || await client.CheckConnectionAsync(url, CancellationToken.None).ConfigureAwait(false);

        if (!reachable)
        {
            _config.IsFirstRun = false;
            _config.Save();
            await present(Strings.MsgFirstRunBackendDown(url)).ConfigureAwait(false);
            return;
        }

        // ── 2. Model discovery ────────────────────────────────────────────────
        var allModels  = await client.ListModelsAsync(CancellationToken.None)
            .ConfigureAwait(false);

        var chatModels = allModels.Where(m => !Services.Inference.ModelCatalog.IsEmbeddingModel(m)).ToList();
        var embModels  = allModels.Where(m =>  Services.Inference.ModelCatalog.IsEmbeddingModel(m)).ToList();

        if (chatModels.Count == 0)
        {
            _config.IsFirstRun = false;
            _config.Save();
            await present(Strings.MsgFirstRunNoModels).ConfigureAwait(false);
            return;
        }

        // ── 3. Auto-configure ─────────────────────────────────────────────────
        var best = Services.Inference.ModelCatalog.PickBestChatModel(chatModels);
        _config.DefaultModel = best;
        await RunOnVMContextAsync(() => ActiveModelLabel = best);

        // Auto-pick first embedding model only when nothing is configured yet.
        if (string.IsNullOrEmpty(_config.RagEmbeddingModel) && embModels.Count > 0)
            _config.RagEmbeddingModel = embModels[0];

        _config.IsFirstRun = false;
        _config.Save();

        // ── 4. Hardware fit-check ─────────────────────────────────────────────
        // Auto-seed the VRAM budget if Ollama is local, then warn when the auto-picked
        // chat + embedding set is estimated to overflow it. Silent when the budget is
        // unknown (remote host) or the models comfortably fit.
        await Services.Hardware.HardwareProfile.EnsureBudgetAsync(_config, CancellationToken.None)
            .ConfigureAwait(false);

        var vramWarning = await BuildFirstRunVramWarningAsync(best).ConfigureAwait(false);

        var countLabel = chatModels.Count == 1 ? "1 model" : $"{chatModels.Count} models";
        var text = Strings.MsgFirstRunWelcome(countLabel, best);
        if (vramWarning is not null) text += "\n\n" + vramWarning;
        await present(text).ConfigureAwait(false);
    }

    /// <summary>Handles <c>/setup</c>: re-runs the first-run discovery on demand (re-detect backend, re-pick models).</summary>
    private async Task HandleSetupCommandAsync(string[] parts, CancellationToken ct)
        => await RunSetupDiscoveryAsync(ShowInfoAsync).ConfigureAwait(false);

    /// <summary>
    /// Builds the first-run VRAM overflow warning, or <c>null</c> when the budget is unknown or
    /// the auto-picked chat + embedding models comfortably fit. Estimates footprint from the
    /// models' on-disk size (<c>/api/tags</c>).
    /// </summary>
    private async Task<string?> BuildFirstRunVramWarningAsync(string chatModel)
    {
        if (_config.VramBudgetGb <= 0) return null;

        var installed = await _client.ListInstalledModelsAsync(CancellationToken.None).ConfigureAwait(false);
        if (installed.Count == 0) return null;

        var sizeByName = installed.ToDictionary(m => m.Name, m => m.SizeBytes);

        // The set that will be resident together: chat + embedding (FIM defaults to the chat model).
        var names = new List<string> { chatModel };
        if (!string.IsNullOrEmpty(_config.RagEmbeddingModel)) names.Add(_config.RagEmbeddingModel);

        var sizes = names.Distinct()
            .Select(n => sizeByName.TryGetValue(n, out var s) ? s : 0L)
            .ToList();

        if (Services.Inference.ModelCatalog.TrioFitsBudget(_config.VramBudgetGb, sizes, out var neededGb))
            return null;

        return Strings.MsgFirstRunVramWarning(
            neededGb.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture),
            _config.VramBudgetGb.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture));
    }

    /// <summary>Handles the <c>/index</c> slash command (show status or trigger a rebuild).</summary>
    private async Task HandleRagIndexCommandAsync(string[] parts, CancellationToken ct)
    {
        var rebuild = parts.Length >= 2 &&
                      parts[1].Equals("rebuild", StringComparison.OrdinalIgnoreCase);

        if (rebuild)
        {
            var root = FindProjectRoot();
            if (string.IsNullOrEmpty(root))
            {
                await ShowInfoAsync("⚠ Cannot locate solution root — open a file first.");
                return;
            }
            _indexService.StartIndexing(root);
            await ShowInfoAsync($"🔄 RAG re-indexing started: `{root}`");
            return;
        }

        // ── Show current status ───────────────────────────────────────────────
        var model  = string.IsNullOrEmpty(_config.RagEmbeddingModel) ? "nomic-embed-text" : _config.RagEmbeddingModel;
        var sb     = new StringBuilder();

        sb.AppendLine("**RAG Index**");
        sb.AppendLine();

        if (!_config.RagEnabled)
        {
            sb.AppendLine("Status: **disabled** (`ragEnabled = false` in settings)");
            sb.AppendLine();
            sb.AppendLine("Enable it to get semantic cross-file search via `search_codebase`.");
        }
        else if (_indexService.ChunkCount == 0 && !_indexService.IsIndexing)
        {
            sb.AppendLine($"Status: {(_indexService.Status is { Length: > 0 } s ? s : "not started")}");
            sb.AppendLine();
            sb.AppendLine("Use `/index rebuild` to build the index manually.");
        }
        else
        {
            sb.AppendLine($"Status : {_indexService.Status}");
            sb.AppendLine($"Chunks : {_indexService.ChunkCount:N0}");
            sb.AppendLine($"Root   : `{_indexService.RootDir}`");
            sb.AppendLine($"Model  : `{model}`");
            sb.AppendLine($"Top-K  : {_config.RagTopK}");
            sb.AppendLine();
            sb.AppendLine("Use `/index rebuild` to force a full re-index.");
        }

        await ShowInfoAsync(sb.ToString().TrimEnd());
    }

    // ── Project context ────────────────────────────────────────────────────────

    // ── OODA Turn Loop ─────────────────────────────────────────────────────────

    private async Task RunOodaSummaryAsync(CancellationToken ct)
    {
        Post(() => CurrentStep = Strings.StatusOodaSummarizing);

        var summarizeHistory = new List<ChatMessageDto>(_history)
        {
            new("user", Strings.OodaSummarizePrompt)
        };

        try
        {
            var result = await _client.RunAgentAsync(
                model:   _config.DefaultModel,
                history: summarizeHistory,
                tools:   EmptyToolRegistry.Instance,
                onStep:  _ => { },
                onToken: null,
                ct:      ct);

            var summary = result.FinalResponse?.Trim();
            if (!string.IsNullOrEmpty(summary))
            {
                await RunOnVMContextAsync(() =>
                {
                    _oodaSummary  = summary;
                    _history[0]   = new ChatMessageDto("system",
                        _baseSystemPrompt + "\n\n## Session Summary\n\n" + _oodaSummary);

                    var recapItem = ChatMessageItem.ToolMsg(
                        "ooda_recap",
                        Strings.MsgOodaRecap(_conversationTurnCount, summary),
                        _config.ToolBubblesExpanded);
                    ApplyItemTheme(recapItem);
                    Messages.Insert(Messages.Count - 2, recapItem);
                    ScrollToBottom();
                });
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    // ── Context window management ──────────────────────────────────────────────

    // Range computation, the summarize request, and the history rewrites live in
    // HistoryCompaction (unit-tested); the VM keeps the LLM call, its timeout fuse,
    // and the chat notices.
    private async Task CompactOrTruncateAsync(CancellationToken ct)
    {
        var plan = Services.Agent.HistoryCompaction.Decide(
            _history,
            _config.ContextWindowSize,
            _lastPromptTokens,
            _config.ContextWindowKeepTurns,
            _config.KvCacheAnchorMessages,
            _config.CompactionEnabled);
        if (plan.Action == Services.Agent.CompactionAction.None) return;

        // ── Path A: hard truncation (compaction disabled or nothing to compact) ──
        if (plan.Action == Services.Agent.CompactionAction.Truncate)
        {
            await RunOnVMContextAsync(() =>
            {
                Services.Agent.HistoryCompaction.ApplyTruncation(_history, plan);
                _lastPromptTokens = 0;
                var warn = ChatMessageItem.AssistantMsg(Strings.MsgContextTruncated(plan.Count, plan.KeepTurns));
                ApplyItemTheme(warn);
                Messages.Insert(Messages.Count - 2, warn);
                ScrollToBottom();
            });
            return;
        }

        // ── Path B: smart compaction ───────────────────────────────────────────
        Post(() => CurrentStep = Strings.StatusCompacting);

        var toCompact        = Services.Agent.HistoryCompaction.SliceToCompact(_history, plan);
        var summarizeHistory = Services.Agent.HistoryCompaction.BuildSummarizeRequest(_history, toCompact);

        string? summary = null;
        try
        {
            var timeoutSec = Math.Max(10, _config.CompactionTimeoutSeconds);
            using var compactCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            compactCts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));

            var result = await _client.RunAgentAsync(
                model:   _config.DefaultModel,
                history: summarizeHistory,
                tools:   EmptyToolRegistry.Instance,
                onStep:  _ => { },
                onToken: null,
                ct:      compactCts.Token);

            summary = result.FinalResponse?.Trim();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        catch { }

        // ── Path B1: safety fuse fired — inline truncation, single message ─────
        if (string.IsNullOrEmpty(summary))
        {
            await RunOnVMContextAsync(() =>
            {
                Services.Agent.HistoryCompaction.ApplyTruncation(_history, plan);
                _lastPromptTokens = 0;
                var fallback = ChatMessageItem.AssistantMsg(Strings.MsgContextCompactionFallback);
                ApplyItemTheme(fallback);
                Messages.Insert(Messages.Count - 2, fallback);
                ScrollToBottom();
            });
            return;
        }

        // ── Path B2: compaction succeeded — replace compacted range with summary ──
        await RunOnVMContextAsync(() =>
        {
            Services.Agent.HistoryCompaction.ApplySummary(_history, plan, summary);
            _lastPromptTokens = 0;

            var note = plan.KvAnchor > 0 ? Strings.MsgKvCacheAnchorNote(plan.KvAnchor) : string.Empty;
            var compactBubble = ChatMessageItem.ToolMsg(
                "context_compact",
                Strings.MsgContextCompacted(plan.Count, plan.KeepTurns) + note,
                _config.ToolBubblesExpanded);
            ApplyItemTheme(compactBubble);
            Messages.Insert(Messages.Count - 2, compactBubble);
            ScrollToBottom();
        });
    }

    // Prompt layering itself lives in SystemPromptBuilder (unit-tested); the VM only
    // gathers its inputs: project root, active-file relative path, template suffix.
    private string BuildSystemPrompt(string? language = null)
    {
        var dir = FindProjectRoot();
        var prompt = new SystemPromptBuilder(_config).Build(
            Strings.SystemPrompt,
            language,
            _activeTemplateSuffix,
            dir,
            ActiveFileRelativeTo(dir));
        return _planMode ? prompt + PlanModeToolRegistry.SystemPromptSuffix : prompt;
    }

    // Active editor file path relative to the project root (forward slashes), or null when no file
    // is active or it lives outside the root. Used to scope project rules by glob.
    private string? ActiveFileRelativeTo(string root)
    {
        var path = _activeFilePath;
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root)) return null;
        try
        {
            var rel = Path.GetRelativePath(root, path);
            if (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel)) return null;
            return rel.Replace('\\', '/');
        }
        catch { return null; }
    }

    // Returns a .sln-anchored root, or null if none can be found yet.
    // Unlike FindProjectRoot(), never falls back to CWD — callers that need a reliable
    // root (e.g. RAG indexing) should use this and retry later if it returns null.
    private string? FindReliableProjectRoot() =>
        _rootLocator.LocateReliable(
            _contextHolder.GetOpenPaths(),
            ActiveSolutionSignal.TryReadSolutionDir(),
            Directory.GetCurrentDirectory());

    // Resolution order and walk limits live in ProjectRootLocator (unit-tested); the VM
    // only supplies the open editor paths, the authoritative signal, and the CWD.
    private static readonly Services.VsIntegration.ProjectRootLocator _rootLocator = new();

    private string FindProjectRoot(IReadOnlyList<string>? openPaths = null) =>
        _rootLocator.Locate(
            openPaths ?? _contextHolder.GetOpenPaths(),
            ActiveSolutionSignal.TryReadSolutionDir(),
            Directory.GetCurrentDirectory());

    #endregion

    #region Historique de prompt & commandes (contexte, fix-build, git, rules)

    // ── Prompt history navigation ──────────────────────────────────────────────

    private void UpdateHistoryCommandState()
    {
        HistoryUpCommand.CanExecute   = _promptHistory.CanUp;
        HistoryDownCommand.CanExecute = _promptHistory.CanDown;
    }

    private void LoadPromptHistory()
    {
        try
        {
            if (!File.Exists(_promptHistoryFile)) return;
            var json = File.ReadAllText(_promptHistoryFile, System.Text.Encoding.UTF8);
            _promptHistory.Load(JsonSerializer.Deserialize<List<string>>(json) ?? []);
        }
        catch { }
    }

    private void SavePromptHistory()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_promptHistoryFile)!);
            File.WriteAllText(_promptHistoryFile,
                JsonSerializer.Serialize(_promptHistory.Entries),
                System.Text.Encoding.UTF8);
        }
        catch { }
    }

    private Task HistoryUpAsync(object? _, CancellationToken ct)
    {
        if (!_promptHistory.CanUp) return Task.CompletedTask;
        _navigatingHistory = true;
        Prompt = _promptHistory.Up(_prompt); // stashes the live draft on the first step
        _navigatingHistory = false;
        return Task.CompletedTask;
    }

    private Task HistoryDownAsync(object? _, CancellationToken ct)
    {
        if (!_promptHistory.CanDown) return Task.CompletedTask;
        _navigatingHistory = true;
        Prompt = _promptHistory.Down(_prompt); // restores the draft when stepping past the newest entry
        _navigatingHistory = false;
        return Task.CompletedTask;
    }

    private async Task HandleContextCommandAsync(CancellationToken ct)
    {
        var dir = FindProjectRoot();
        if (Directory.GetFiles(dir, "*.sln", SearchOption.TopDirectoryOnly).Length == 0)
        {
            await ShowInfoAsync(Strings.SlashContextNoSln);
            return;
        }

        var path = Path.Combine(dir, ".inferpal", "context.md");
        if (!File.Exists(path))
        {
            await ShowInfoAsync(Strings.SlashContextNotFound(path));
            return;
        }

        try
        {
            var content = await File.ReadAllTextAsync(path, System.Text.Encoding.UTF8, ct);
            var preview = content.Length > 400 ? content[..400] + "…" : content;
            await ShowInfoAsync(Strings.SlashContextLoaded(path, content.Length, preview));
        }
        catch (Exception ex)
        {
            await ShowInfoAsync(Strings.MsgError(ex.Message));
        }
    }

    private async Task HandleHistoryCommandAsync(string[] parts, CancellationToken ct)
    {
        if (parts.Length >= 2)
        {
            // ── Search mode ───────────────────────────────────────────────────
            var term    = string.Join(" ", parts[1..]);
            var matches = await _store.SearchAsync(term, ct);

            if (matches.Count == 0)
            {
                await ShowInfoAsync(Strings.HistoryNoResults(term));
                return;
            }

            await ShowInfoAsync(SessionManager.FormatHistorySearch(term, matches, DateTime.UtcNow));
        }
        else
        {
            // ── List mode ─────────────────────────────────────────────────────
            var sessions = await _store.ListWithPreviewAsync(ct);

            if (sessions.Count == 0)
            {
                await ShowInfoAsync(Strings.HistoryNoSessions);
                return;
            }

            await ShowInfoAsync(SessionManager.FormatHistoryList(sessions, DateTime.UtcNow));
        }
    }

    // /undo-run         → revert every file changed during the most recent agent run
    // /undo-run list    → list the change-tracking runs of this session
    private async Task HandleUndoRunCommandAsync(string[] parts, CancellationToken ct)
    {
        var runs = _tools.History.Runs;

        if (parts.Length >= 2 && parts[1].Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            var withChanges = runs.Where(r => r.FileCount > 0).ToList();
            if (withChanges.Count == 0) { await ShowInfoAsync(Strings.UndoRunNone); return; }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine(Strings.UndoRunListHeader(withChanges.Count));
            foreach (var r in withChanges)
                sb.AppendLine($"- {r.StartedAt:HH:mm:ss} — {r.FileCount} file(s)");
            await ShowInfoAsync(sb.ToString().TrimEnd());
            return;
        }

        var run = runs.FirstOrDefault(r => r.FileCount > 0);
        if (run is null) { await ShowInfoAsync(Strings.UndoRunNone); return; }

        var result = await _tools.History.UndoRunAsync(run, ct);

        var lines = new System.Text.StringBuilder();
        lines.AppendLine(Strings.UndoRunResult(result.Restored.Count, result.Deleted.Count));
        string Root() => FindProjectRoot();
        foreach (var p in result.Restored) lines.AppendLine($"  ↩ {System.IO.Path.GetRelativePath(Root(), p)}");
        foreach (var p in result.Deleted)  lines.AppendLine($"  🗑 {System.IO.Path.GetRelativePath(Root(), p)}");
        foreach (var p in result.Failed)   lines.AppendLine($"  ⚠ {System.IO.Path.GetRelativePath(Root(), p)}");
        await ShowInfoAsync(lines.ToString().TrimEnd());
    }

    private async Task HandleMemoryCommandAsync(CancellationToken ct)
    {
        var dir  = FindProjectRoot();
        var path = Path.Combine(dir, ".inferpal", "memory.md");

        if (!File.Exists(path))
        {
            await ShowInfoAsync(Strings.SlashMemoryNotFound(path));
            return;
        }

        try
        {
            var content = await File.ReadAllTextAsync(path, System.Text.Encoding.UTF8, ct);
            var preview = content.Length > 400 ? content[..400] + "…" : content;
            await ShowInfoAsync(Strings.SlashMemoryLoaded(path, content.Length, preview));
        }
        catch (Exception ex)
        {
            await ShowInfoAsync(Strings.MsgError(ex.Message));
        }
    }

    // ── Fix-build loop ─────────────────────────────────────────────────────────

    private async Task HandleFixBuildCommandAsync(string[] parts, CancellationToken ct)
    {
        const int MaxRounds = 5;

        CancellationTokenSource? localCts = null;
        await RunOnVMContextAsync(() =>
        {
            localCts    = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _currentCts = localCts;
            IsLoading   = true;
        });
        if (localCts is null) return;
        var tok = localCts.Token;

        try
        {
            // Resolve project path once — bypasses CWD bug in GetDiagnosticsTool
            string? slnPath = null;
            if (parts.Length >= 2)
            {
                slnPath = string.Join(" ", parts[1..]);
            }
            else
            {
                var root = FindProjectRoot();
                slnPath = Directory.GetFiles(root, "*.sln", SearchOption.TopDirectoryOnly).FirstOrDefault()
                       ?? Directory.GetFiles(root, "*.csproj", SearchOption.TopDirectoryOnly).FirstOrDefault();
            }

            var diagArgsJson = slnPath is not null
                ? JsonSerializer.Serialize(new { path = slnPath })
                : "{}";

            for (int round = 1; round <= MaxRounds; round++)
            {
                tok.ThrowIfCancellationRequested();
                await RunOnVMContextAsync(() => CurrentStep = $"🔨 Build {round}/{MaxRounds}…");

                // ── Build ──────────────────────────────────────────────────────
                string buildOutput;
                try
                {
                    var argsElem = JsonDocument.Parse(diagArgsJson).RootElement.Clone();
                    buildOutput  = await _tools.ExecuteAsync(GetDiagnosticsTool.ToolName, argsElem, tok);
                }
                catch (Exception ex) { buildOutput = Strings.MsgError(ex.Message); }

                bool hasErrors = GetDiagnosticsTool.OutputHasBuildErrors(buildOutput);

                await RunOnVMContextAsync(() =>
                {
                    var label    = hasErrors ? "❌ get_diagnostics" : "✅ get_diagnostics";
                    var diagItem = ChatMessageItem.ToolMsg(label, buildOutput, expanded: true);
                    ApplyItemTheme(diagItem);
                    if (hasErrors)
                        diagItem.InitFixCallback(buildOutput,
                            rawErrors => Post(() => Prompt = BuildFixPrompt(rawErrors)));
                    Messages.Insert(Messages.Count - 2, diagItem);
                    ScrollToBottom();
                });

                // ── Success ────────────────────────────────────────────────────
                if (!hasErrors)
                {
                    await ShowInfoAsync(Strings.FixBuildSuccess(round));
                    return;
                }

                // ── Give up ────────────────────────────────────────────────────
                if (round == MaxRounds)
                {
                    await ShowInfoAsync(Strings.FixBuildGiveUp(MaxRounds));
                    return;
                }

                // ── Fix iteration ──────────────────────────────────────────────
                tok.ThrowIfCancellationRequested();
                await RunFixIterationAsync(buildOutput, round, tok);
            }
        }
        catch (OperationCanceledException)
        {
            await RunOnVMContextAsync(() =>
            {
                Messages.Insert(Messages.Count - 2, ChatMessageItem.AssistantMsg(Strings.MsgCancelled));
                ScrollToBottom();
            });
        }
        catch (Exception ex)
        {
            var msg = ex.Message;
            await RunOnVMContextAsync(() =>
            {
                Messages.Insert(Messages.Count - 2, ChatMessageItem.AssistantMsg(Strings.MsgError(msg)));
                ScrollToBottom();
            });
        }
        finally
        {
            await RunOnVMContextAsync(() =>
            {
                localCts?.Dispose();
                _currentCts = null;
                IsLoading   = false;
                CurrentStep = string.Empty;
            });
        }
    }

    private async Task RunFixIterationAsync(string buildOutput, int round, CancellationToken ct)
    {
        var fixHistory = new List<ChatMessageDto>
        {
            _history[0],                          // system prompt (context + memory)
            new("user", BuildFixPrompt(buildOutput))
        };

        ChatMessageItem? streamItem = null;

        await RunOnVMContextAsync(() =>
        {
            CurrentStep = $"🔧 Fix {round}…";
            streamItem  = ChatMessageItem.StreamingMsg();
            streamItem.Label = $"🔧 Fix {round}";
            ApplyItemTheme(streamItem);
            Messages.Insert(Messages.Count - 2, streamItem);
            ScrollToBottom();
        });

        AgentResult result;
        try
        {
            using var sink = new ThrottledTokenSink(chunk => Post(() => { if (streamItem is not null) streamItem.Content += chunk; }));
            result = await _client.RunAgentAsync(
                model:   _config.DefaultModel,
                history: fixHistory,
                tools:   (IToolRegistry)_tools,
                onStep:  step  => Post(() => CurrentStep = step),
                onToken: token => sink.Append(token),
                ct:      ct);
            sink.Stop();
        }
        catch
        {
            // streamItem was inserted before streaming started — discard it if empty/invisible
            // so it doesn't leave an orphaned streaming bubble in the chat.
            await RunOnVMContextAsync(() => streamItem = FinalizeStreamingBubble(streamItem));
            throw; // re-throw so RunSmartFixAsync's catch handles user messaging
        }

        await RunOnVMContextAsync(() =>
        {
            var insertIdx = streamItem is not null
                ? Messages.IndexOf(streamItem)
                : Messages.Count - 2;

            foreach (var exec in result.Executions)
            {
                var preview  = exec.Output.Length > 500
                    ? exec.Output[..500] + Strings.MsgTruncated
                    : exec.Output;
                var toolItem = ChatMessageItem.ToolMsg(
                    exec.Name, Strings.MsgToolOutput(exec.Input, preview), _config.ToolBubblesExpanded);
                ApplyItemTheme(toolItem);
                Messages.Insert(insertIdx++, toolItem);
            }

            streamItem = FinalizeStreamingBubble(streamItem);

            if (streamItem is null)
            {
                var visibleFinal = Services.Presentation.MarkdownParser.StripThinkTags(result.FinalResponse);
                if (Services.Presentation.MarkdownParser.HasPrintableText(visibleFinal))
                {
                    var msg = ChatMessageItem.AssistantMsg(visibleFinal);
                    ApplyItemTheme(msg);
                    Messages.Insert(Messages.Count - 2, msg);
                }
            }

            ScrollToBottom();
        });
    }

    // ── Git commit assistant ───────────────────────────────────────────────────

    private async Task HandleCommitCommandAsync(CancellationToken ct)
    {
        var root = FindProjectRoot();

        // Staged diff first; fall back to unstaged if nothing staged
        var (staged, _) = await RunGitAsync("diff --staged", root, ct);
        bool nothingStaged = string.IsNullOrWhiteSpace(staged);

        string diffContext;
        if (nothingStaged)
        {
            var (status, _) = await RunGitAsync("status --short", root, ct);
            if (string.IsNullOrWhiteSpace(status))
            {
                await ShowInfoAsync(Strings.CommitNothingToCommit);
                return;
            }
            var (diff, _) = await RunGitAsync("diff", root, ct);
            diffContext = Services.GitCommitPolicy.BuildUnstagedContext(status, diff);
            await ShowInfoAsync(Strings.CommitNothingStaged);
        }
        else
        {
            diffContext = Services.GitCommitPolicy.BuildStagedContext(staged);
        }

        diffContext = Services.GitCommitPolicy.CapDiff(diffContext);

        // Stream the LLM's proposed commit message
        ChatMessageItem? streamItem = null;
        await RunOnVMContextAsync(() =>
        {
            streamItem       = ChatMessageItem.StreamingMsg();
            streamItem.Label = Strings.CommitProposingLabel;
            ApplyItemTheme(streamItem);
            Messages.Insert(Messages.Count - 2, streamItem);
            IsLoading   = true;
            CurrentStep = Strings.StatusThinking;
            ScrollToBottom();
        });

        try
        {
            var commitHistory = Services.GitCommitPolicy.BuildProposalRequest(diffContext);

            using var sink = new ThrottledTokenSink(chunk => Post(() => { if (streamItem is not null) streamItem.Content += chunk; }));
            var result = await _client.RunAgentAsync(
                model:   _config.DefaultModel,
                history: commitHistory,
                tools:   EmptyToolRegistry.Instance,
                onStep:  _ => { },
                onToken: token => sink.Append(token),
                ct:      ct);
            sink.Stop();

            // Think tags are stripped so reasoning-model output doesn't land in the prompt
            var proposed = Services.GitCommitPolicy.CleanProposal(result.FinalResponse);

            await RunOnVMContextAsync(() =>
            {
                streamItem  = FinalizeStreamingBubble(streamItem);
                IsLoading   = false;
                CurrentStep = string.Empty;

                if (!string.IsNullOrWhiteSpace(proposed))
                {
                    Prompt = $"/commit-exec {proposed}";
                    var hint = ChatMessageItem.AssistantMsg(Strings.CommitConfirmHint);
                    ApplyItemTheme(hint);
                    Messages.Insert(Messages.Count - 2, hint);
                }
                ScrollToBottom();
            });
        }
        catch (OperationCanceledException)
        {
            await RunOnVMContextAsync(() =>
            {
                streamItem  = FinalizeStreamingBubble(streamItem);
                IsLoading   = false;
                CurrentStep = string.Empty;
            });
        }
        catch (Exception ex)
        {
            var msg = ex.Message;
            await RunOnVMContextAsync(() =>
            {
                streamItem  = FinalizeStreamingBubble(streamItem);
                IsLoading   = false;
                CurrentStep = string.Empty;
                Messages.Insert(Messages.Count - 2, ChatMessageItem.AssistantMsg(Strings.MsgError(msg)));
                ScrollToBottom();
            });
        }
    }

    // ── Rules & Checks (.inferpal/rules, .inferpal/checks) ─────────────────

    // AI-reviews the current git diff against .inferpal/checks. /check init scaffolds an example;
    // /check <name> runs a single check. 100% local — no diff leaves the machine.
    private async Task HandleCheckCommandAsync(string[] parts, CancellationToken ct)
    {
        var root = FindProjectRoot();
        var arg  = parts.Length >= 2 ? string.Join(" ", parts[1..]).Trim() : null;

        if (string.Equals(arg, "init", StringComparison.OrdinalIgnoreCase))
        {
            // Single source of truth for the checks scaffold (dir/file/content): reuse the handler.
            var scaffold = Services.Commands.RulesChecksPromptsCommandHandler.Checks(root, parts).Scaffold!;
            await ScaffoldFileAsync(scaffold.Dir, scaffold.FileName, scaffold.Content, Strings.ChecksScaffolded);
            return;
        }

        var checks = ChecksService.Load(Path.Combine(root, ".inferpal", "checks"));
        if (checks.Count == 0) { await ShowInfoAsync(Strings.ChecksNone); return; }

        if (!string.IsNullOrEmpty(arg))
        {
            var one = checks.FirstOrDefault(c => c.Name.Equals(arg, StringComparison.OrdinalIgnoreCase));
            if (one is null) { await ShowInfoAsync(Strings.CheckUnknownName(arg)); return; }
            checks = [one];
        }

        // Current diff: staged first, fall back to unstaged + status (mirror /commit).
        var (staged, _) = await RunGitAsync("diff --staged", root, ct);
        string diff;
        if (string.IsNullOrWhiteSpace(staged))
        {
            var (unstaged, _) = await RunGitAsync("diff", root, ct);
            var (status, _)   = await RunGitAsync("status --short", root, ct);
            if (string.IsNullOrWhiteSpace(unstaged) && string.IsNullOrWhiteSpace(status))
            {
                await ShowInfoAsync(Strings.CheckNoDiff);
                return;
            }
            diff = Services.GitCommitPolicy.BuildUnstagedContext(status, unstaged);
        }
        else
        {
            diff = Services.GitCommitPolicy.BuildStagedContext(staged);
        }

        diff = Services.GitCommitPolicy.CapDiff(diff);

        var history = new List<ChatMessageDto>
        {
            new("system", Strings.CheckReviewSystemPrompt),
            new("user",   ChecksService.BuildReviewPrompt(checks, diff)),
        };

        await StreamAssistantReplyAsync(history, Strings.CheckReviewingLabel, ct);
    }

    private async Task HandleRulesCommandAsync(string[] parts, CancellationToken ct)
    {
        var result = Services.Commands.RulesChecksPromptsCommandHandler.Rules(FindProjectRoot(), parts);
        if (result.Scaffold is { } s)
            await ScaffoldFileAsync(s.Dir, s.FileName, s.Content, Strings.RulesScaffolded);
        else if (result.Message is { } msg)
            await ShowInfoAsync(msg);
    }

    private async Task HandlePromptsCommandAsync(string[] parts, CancellationToken ct)
    {
        var result = Services.Commands.RulesChecksPromptsCommandHandler.Prompts(FindProjectRoot(), parts);
        if (result.Scaffold is { } s)
        {
            await ScaffoldFileAsync(s.Dir, s.FileName, s.Content, Strings.PromptsScaffolded);
            PromptFilesService.InvalidateCache();   // show up in autocomplete immediately
        }
        else if (result.Message is { } msg)
            await ShowInfoAsync(msg);
    }

    private async Task HandleChecksCommandAsync(string[] parts, CancellationToken ct)
    {
        var result = Services.Commands.RulesChecksPromptsCommandHandler.Checks(FindProjectRoot(), parts);
        if (result.Scaffold is { } s)
            await ScaffoldFileAsync(s.Dir, s.FileName, s.Content, Strings.ChecksScaffolded);
        else if (result.Message is { } msg)
            await ShowInfoAsync(msg);
    }

    // Writes a scaffold file only if it does not already exist, creating the directory as needed,
    // then confirms with the localized message (which receives the file path).
    private async Task ScaffoldFileAsync(string dir, string fileName, string content, Func<string, string> confirm)
    {
        var path = Path.Combine(dir, fileName);
        try
        {
            Directory.CreateDirectory(dir);
            if (!File.Exists(path))
                await File.WriteAllTextAsync(path, content, System.Text.Encoding.UTF8);
        }
        catch (Exception ex)
        {
            await ShowInfoAsync(Strings.MsgError(ex.Message));
            return;
        }
        await ShowInfoAsync(confirm(path));
    }

    // Streams a one-shot assistant reply (no tools) into a fresh chat bubble, reusing the
    // empty-bubble guards and cancel/error handling from the /commit flow.
    private async Task StreamAssistantReplyAsync(List<ChatMessageDto> history, string label, CancellationToken ct)
    {
        ChatMessageItem? streamItem = null;
        await RunOnVMContextAsync(() =>
        {
            streamItem       = ChatMessageItem.StreamingMsg();
            streamItem.Label = label;
            ApplyItemTheme(streamItem);
            Messages.Insert(Messages.Count - 2, streamItem);
            IsLoading   = true;
            CurrentStep = Strings.StatusThinking;
            ScrollToBottom();
        });

        void Finalize()
        {
            streamItem  = FinalizeStreamingBubble(streamItem);
            IsLoading   = false;
            CurrentStep = string.Empty;
            ScrollToBottom();
        }

        try
        {
            using var sink = new ThrottledTokenSink(chunk => Post(() => { if (streamItem is not null) streamItem.Content += chunk; }));
            await _client.RunAgentAsync(
                model:   _config.DefaultModel,
                history: history,
                tools:   EmptyToolRegistry.Instance,
                onStep:  _ => { },
                onToken: token => sink.Append(token),
                ct:      ct);
            sink.Stop();
            await RunOnVMContextAsync(Finalize);
        }
        catch (OperationCanceledException)
        {
            await RunOnVMContextAsync(Finalize);
        }
        catch (Exception ex)
        {
            var msg = ex.Message;
            await RunOnVMContextAsync(() =>
            {
                Finalize();
                Messages.Insert(Messages.Count - 2, ChatMessageItem.AssistantMsg(Strings.MsgError(msg)));
                ScrollToBottom();
            });
        }
    }

    private async Task HandleCommitExecAsync(string message, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            await ShowInfoAsync(Strings.SlashUsage("/commit-exec <message>"));
            return;
        }

        var root    = FindProjectRoot();
        var safeMsg = Services.GitCommitPolicy.EscapeMessage(message);

        // If nothing is staged, auto-stage tracked modified files (git add -u)
        var (stagedFiles, _) = await RunGitAsync("diff --staged --name-only", root, ct);
        if (string.IsNullOrWhiteSpace(stagedFiles))
            await RunGitAsync("add -u", root, ct);

        var (output, exitCode) = await RunGitAsync($"commit -m \"{safeMsg}\"", root, ct);

        await RunOnVMContextAsync(() =>
        {
            var label = exitCode == 0 ? "✅ git commit" : "❌ git commit";
            var text  = string.IsNullOrWhiteSpace(output) ? "(no output)" : output;
            var item  = ChatMessageItem.ToolMsg(label, text, expanded: true);
            ApplyItemTheme(item);
            Messages.Insert(Messages.Count - 2, item);
            ScrollToBottom();
        });
    }

    private static async Task<(string Output, int ExitCode)> RunGitAsync(
        string args, string workDir, CancellationToken ct)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                WorkingDirectory       = workDir,
            };
            using var proc   = System.Diagnostics.Process.Start(psi)!;
            var stdout        = await proc.StandardOutput.ReadToEndAsync(ct);
            var stderr        = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            var combined      = stdout.Trim();
            if (!string.IsNullOrWhiteSpace(stderr))
                combined += (combined.Length > 0 ? "\n" : "") + stderr.Trim();
            return (combined, proc.ExitCode);
        }
        catch (Exception ex) { return (ex.Message, -1); }
    }

    #endregion

    #region Prompt en attente, fichier actif & sessions

    // ── Pending prompt from editor context menu ────────────────────────────────

    private void OnPendingPromptAvailable(object? sender, EventArgs e) => ConsumePendingPrompt();

    private void OnActiveFileChanged(object? sender, string filePath)
    {
        _activeFilePath = filePath;

        // Rebuild the system prompt when either persona auto-switching is on (existing behaviour)
        // or glob-scoped project rules exist — both depend on the active file. If neither applies,
        // the prompt is identical, so skip the rebuild.
        var language = _config.PersonaAutoSwitch ? DetectLanguage(filePath) : null;
        if (string.IsNullOrEmpty(language) && !HasGlobScopedRules()) return;

        _baseSystemPrompt = BuildSystemPrompt(language);
        Post(() =>
        {
            if (_history.Count > 0 && _history[0].Role == "system")
                _history[0] = new ChatMessageDto("system", _baseSystemPrompt);
        });
    }

    // True if any project rule is glob-scoped (not alwaysApply / not unscoped), meaning the
    // injected rule set can change with the active file and the prompt must be rebuilt on switch.
    private bool HasGlobScopedRules()
    {
        var dir = FindProjectRoot();
        var rulesDir = Path.Combine(dir, ".inferpal", "rules");
        return RulesService.Load(rulesDir).Any(r => !r.AlwaysApply && r.Globs.Count > 0);
    }

    private void ConsumePendingPrompt()
    {
        var p            = _contextHolder.ConsumePendingPrompt();
        var m            = _contextHolder.ConsumePendingModel();
        var attachLabel  = _contextHolder.ConsumePendingAttachLabel();
        var attachCode   = _contextHolder.ConsumePendingAttachContent();
        if (p is null) return;

        List<AttachmentItem> atts = [];
        if (!string.IsNullOrEmpty(attachLabel) && !string.IsNullOrEmpty(attachCode))
            atts.Add(new AttachmentItem(attachLabel, attachCode, () => {}));

        // Auto-send the code action directly on the VM context.
        // The model is captured in the closure — no volatile field or
        // cross-thread race condition possible.
        // clearPrompt: false so the user's current draft is preserved.
        // Performance Shield: cancel any in-flight request on the VM context
        // before starting, so rapid successive code actions don't pile up.
        Post(() =>
        {
            _currentCts?.Cancel();
            _ = SendCoreAsync(p, m, atts, CancellationToken.None, clearPrompt: false);
        });
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private async Task SaveNamedSessionAsync(string firstUserContent, List<SavedMessage> snapshot)
    {
        try
        {
            var title = await GenerateSessionTitleAsync(firstUserContent);
            await _store.SaveAsync(SessionManager.SessionFileName(DateTime.Now, title), snapshot, CancellationToken.None);
            await RunOnVMContextAsync(RefreshSessionsList);
        }
        catch { }
    }

    private async Task<string> GenerateSessionTitleAsync(string firstUserContent)
    {
        var fallback = SessionManager.MakeSnippet(firstUserContent);
        if (string.IsNullOrWhiteSpace(firstUserContent)) return fallback;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var result = await _client.RunAgentAsync(
                model:   _config.DefaultModel,
                history:
                [
                    new("system", SessionManager.TitleSystemPrompt),
                    new("user",   firstUserContent.Length > 400 ? firstUserContent[..400] : firstUserContent)
                ],
                tools:   EmptyToolRegistry.Instance,
                onStep:  _ => { },
                onToken: null,
                ct:      cts.Token);

            return SessionManager.SanitizeTitle(result.FinalResponse, fallback);
        }
        catch
        {
            return fallback;
        }
    }

    private async Task AutoSaveAsync()
    {
        try
        {
            List<SavedMessage> snapshot = [];
            await RunOnVMContextAsync(() =>
            {
                snapshot = SessionManager.BuildSnapshot(
                    Messages.Select(m => (m.Role, m.Content, m.ToolName, m.Timestamp)));
            });
            await _store.AutoSaveAsync(snapshot, CancellationToken.None);
        }
        catch { }
    }

    private async Task DeleteSessionAsync(object? _, CancellationToken ct)
    {
        var name = SelectedSession;
        if (string.IsNullOrEmpty(name)) return;

        var confirmed = await _vs.Shell().ShowPromptAsync(
            Strings.DeleteSessionConfirm(name), PromptOptions.OKCancel, ct);
        if (!confirmed) return;

        _store.Delete(name);
        await RunOnVMContextAsync(() =>
        {
            RefreshSessionsList();
            SelectedSession = string.Empty;
        });
    }

    private void RefreshSessionsList()
    {
        RecentSessions.Clear();
        foreach (var s in _store.ListSessions().Where(s => s != "last_session"))
            RecentSessions.Add(s);
    }

    // ── 30-fps UI throttle ─────────────────────────────────────────────────────

    // Batches incoming tokens and flushes them to the UI at ~30 fps (32 ms).
    // Prevents flooding the UI thread when the model streams tokens rapidly.
    #endregion

    #region Types imbriqués

    private sealed class ThrottledTokenSink : IDisposable
    {
        private const int FrameMs = 32;

        private readonly Action<string> _onFlush;
        private readonly StringBuilder  _buf  = new();
        private readonly object         _gate = new();
        private readonly Timer          _timer;
        private bool _stopped;

        public ThrottledTokenSink(Action<string> onFlush)
        {
            _onFlush = onFlush;
            _timer   = new Timer(_ => Flush(), null, FrameMs, FrameMs);
        }

        public void Append(string token)
        {
            lock (_gate) { _buf.Append(token); }
        }

        private void Flush()
        {
            string chunk;
            lock (_gate)
            {
                if (_buf.Length == 0) return;
                chunk = _buf.ToString();
                _buf.Clear();
            }
            _onFlush(chunk);
        }

        // Drops any buffered-but-not-yet-flushed tokens without flushing them. Called on a stream
        // reset between agent iterations: without it, tokens still sitting in _buf when the bubble
        // is removed get flushed on the next timer tick and resurrect a stale bubble, producing a
        // duplicated response (the previous iteration's text prepended to the next).
        public void Reset()
        {
            lock (_gate) { _buf.Clear(); }
        }

        // Stops the timer and flushes remaining tokens. Call before RunOnVMContextAsync.
        public void Stop()
        {
            lock (_gate)
            {
                if (_stopped) return;
                _stopped = true;
            }
            _timer.Dispose();
            Flush();
        }

        public void Dispose() => Stop();
    }

    #endregion
}
