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
internal partial class InferpalToolWindowData : NotifyPropertyChangedObject
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
}
