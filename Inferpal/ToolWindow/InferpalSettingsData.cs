using System.Collections.ObjectModel;
using System.Runtime.Serialization;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Services;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Settings;
using Microsoft.VisualStudio.Extensibility.UI;
using Microsoft.VisualStudio.Threading;

namespace Inferpal.ToolWindow;

[DataContract]
internal class InferpalSettingsData : NotifyPropertyChangedObject
{
    private readonly InferpalConfig _config;
    private readonly IInferenceProvider _client;
    private readonly Services.Mcp.McpToolService _mcp;

    // ── Inference backend list (code → display name, fixed — never localized) ─────
    private static readonly (string Code, string Name)[] ProviderOptions =
    [
        (Services.InferenceProviderFactory.Ollama,           "Ollama"),
        (Services.InferenceProviderFactory.LmStudio,         "LM Studio"),
        (Services.InferenceProviderFactory.OpenAiCompatible, "OpenAI-compatible (generic)"),
    ];

    // ── Inline completion mode list (code → display name, fixed) ─────────────
    private static readonly (string Code, string Name)[] InlineModeOptions =
    [
        ("Fast",         "Fast (128 tok · 300 ms)"),
        ("Default",      "Default (256 tok · 600 ms)"),
        ("HighAccuracy", "High Accuracy (512 tok · 1 s)"),
    ];

    // ── Language list (code → display name, fixed — never localized so always readable) ───
    private static readonly (string Code, string Name)[] LanguageOptions =
    [
        ("",      ""),          // placeholder: filled from LangAuto at ApplyLabels time
        ("en",    "English"),
        ("fr",    "Français"),
        ("de",    "Deutsch"),
        ("it",    "Italiano"),
        ("es",    "Español"),
        ("ru",    "Русский"),
        ("ja",    "日本語"),
        ("ko",    "한국어"),
        ("zh-CN", "中文 (简体)"),
        ("pl",    "Polski"),
    ];

    private string _baseUrl;
    private string _selectedProvider  = string.Empty;
    private bool   _showKeepAliveSettings = true;   // keep_alive auto-unload — Ollama only
    private bool   _showInlineCompletions = true;   // FIM ghost text — Ollama + LM Studio
    private string _apiKey            = string.Empty;
    private string _labelProvider     = string.Empty;
    private string _hintProvider      = string.Empty;
    private string _labelApiKey       = string.Empty;
    private string _hintApiKey        = string.Empty;
    private string _selectedModel;
    private string _selectedLanguage  = string.Empty;
    private string _labelLanguage     = string.Empty;
    private string _hintLanguage      = string.Empty;
    private string _connectionStatus = string.Empty;
    private bool   _isConnectionOk;
    private string _saveStatus       = string.Empty;
    private bool   _isDarkTheme;
    private string _textForeground   = "#F1F1F1";
    private IDisposable? _themeSubscription;

    private string _labelUrl                     = string.Empty;
    private string _hintUrl                      = string.Empty;
    private string _labelChatModel               = string.Empty;
    private string _hintChatModel                = string.Empty;
    private string _labelCommandTimeout          = string.Empty;
    private string _hintCommandTimeout           = string.Empty;
    private string _labelToolBubblesExpanded     = string.Empty;
    private string _hintToolBubblesExpanded      = string.Empty;
    private string _labelSecurityAlertsDisabled  = string.Empty;
    private string _hintSecurityAlertsDisabled   = string.Empty;
    private bool   _smartFixEnabled;
    private string _labelSmartFixEnabled         = string.Empty;
    private string _hintSmartFixEnabled          = string.Empty;
    private bool   _agentModeEnabled;
    private string _labelAgentModeEnabled        = string.Empty;
    private string _hintAgentModeEnabled         = string.Empty;
    private string _agentMaxIterationsText       = string.Empty;
    private string _labelAgentMaxIterations      = string.Empty;
    private string _hintAgentMaxIterations       = string.Empty;
    private string _quickTimeoutHoursText        = string.Empty;
    private string _quickTimeoutMinutesText      = string.Empty;
    private string _quickTimeoutSecondsText      = string.Empty;
    private string _labelTaskTimeoutQuick        = string.Empty;
    private string _hintTaskTimeoutQuick         = string.Empty;
    private string _normalTimeoutHoursText       = string.Empty;
    private string _normalTimeoutMinutesText     = string.Empty;
    private string _normalTimeoutSecondsText     = string.Empty;
    private string _labelTaskTimeoutNormal       = string.Empty;
    private string _hintTaskTimeoutNormal        = string.Empty;
    private string _deepTimeoutHoursText         = string.Empty;
    private string _deepTimeoutMinutesText       = string.Empty;
    private string _deepTimeoutSecondsText       = string.Empty;
    private string _labelTaskTimeoutDeep         = string.Empty;
    private string _hintTaskTimeoutDeep          = string.Empty;
    private bool   _modelAutoUnloadEnabled;
    private string _labelModelAutoUnload         = string.Empty;
    private string _hintModelAutoUnload          = string.Empty;
    private string _modelIdleTimeoutText         = string.Empty;
    private string _labelModelIdleTimeout        = string.Empty;
    private string _hintModelIdleTimeout         = string.Empty;
    private string _labelContextWindowSize       = string.Empty;
    private string _hintContextWindowSize        = string.Empty;
    private string _labelContextWindowKeepTurns  = string.Empty;
    private string _hintContextWindowKeepTurns   = string.Empty;
    private string _labelVramBudget              = string.Empty;
    private string _hintVramBudget               = string.Empty;
    private string _hintCustomSystemPrompt       = string.Empty;
    private string _labelCustomSystemPrompt      = string.Empty;
    private string _labelPinnedContextFiles      = string.Empty;
    private string _hintPinnedContextFiles       = string.Empty;

    // ── Collapsible section state (Connection stays always expanded) ──
    private bool _sectionBehaviorExpanded;
    private bool _sectionInlineExpanded;
    private bool _sectionRagExpanded;
    private bool _sectionMcpExpanded;
    private bool _sectionContextExpanded;
    private bool _sectionPersonaExpanded;
    private string _sectionBehaviorChevron = "▶";
    private string _sectionInlineChevron   = "▶";
    private string _sectionRagChevron      = "▶";
    private string _sectionMcpChevron      = "▶";
    private string _sectionContextChevron  = "▶";
    private string _sectionPersonaChevron  = "▶";
    // ── Settings tabs: which group is shown + per-tab active styling (theme-aware) ──
    // Lateral-tab navigation over the existing accordion sections: one tab visible at a time,
    // its sections force-expanded on select. Each section block stays where it is in the XAML
    // and is wrapped in a Visibility StackPanel bound to its tab flag (non-contiguous blocks may
    // share a flag — Visibility binding does not require adjacency).
    private const string TabAccent = "#7C4DFF";   // brand accent for the active tab
    private string _activeSettingsTab = "connection";
    private bool   _tabConnectionVisible = true;
    private bool   _tabBehaviorVisible;
    private bool   _tabContextVisible;
    private bool   _tabToolsVisible;
    private string _tabConnectionFg = TabAccent;
    private string _tabBehaviorFg   = "#9D9D9D";
    private string _tabContextFg    = "#9D9D9D";
    private string _tabToolsFg       = "#9D9D9D";
    private string _tabConnectionUnderline = TabAccent;
    private string _tabBehaviorUnderline   = "#00000000";
    private string _tabContextUnderline    = "#00000000";
    private string _tabToolsUnderline       = "#00000000";
    private string _labelTabConnection = string.Empty;
    private string _labelTabBehavior   = string.Empty;
    private string _labelTabContext    = string.Empty;
    private string _labelTabTools      = string.Empty;
    private bool   _mcpEnabled;
    private string _mcpServersJson                      = string.Empty;
    private string _mcpStatusText                       = string.Empty;
    private string _labelSectionMcp                     = string.Empty;
    private string _labelMcpEnabled                     = string.Empty;
    private string _hintMcpEnabled                      = string.Empty;
    private string _labelMcpServers                     = string.Empty;
    private string _hintMcpServers                      = string.Empty;
    private string _hintMcpEditServer                   = string.Empty;
    private string _hintMcpDeleteServer                 = string.Empty;
    // MCP server-row theme brushes — kept on the root VM (not per-row) because RemoteUI does not
    // propagate property changes on a collection item once it is in the bound ObservableCollection.
    // The item template binds these via {Binding DataContext.X, ElementName=root}.
    private string _mcpSubtleForeground                 = "#9D9D9D";
    private string _mcpRowBackground                    = "#252526";
    private string _mcpRowBorder                        = "#3F3F46";
    private string _mcpEditGlyph                        = "#CCCCCC";
    private string _themeHoverBg                         = "#3F3F46";
    private string _editPanelBg                          = "#2D2D30";
    private string _secondaryButtonBg                    = "#3F3F46";
    private string _secondaryButtonHover                 = "#505050";
    private string _secondaryButtonFg                    = "#FFFFFF";
    // MCP server list / editor
    private bool   _sectionMcpJsonExpanded;
    private string _sectionMcpJsonChevron               = "▶";
    private bool   _isEditingServer;
    private string _editingTitle                        = string.Empty;
    private string _editServerName                      = string.Empty;
    private string _editServerCommand                   = string.Empty;
    private string _editServerArgs                      = string.Empty;
    private string _editServerEnv                       = string.Empty;
    private bool   _editServerIsHttp;
    private bool   _editServerIsStdio                   = true;
    private string _editServerUrl                       = string.Empty;
    private string _editServerHeaders                   = string.Empty;
    private string _editServerError                     = string.Empty;
    private string? _editingOriginalName;
    // localized labels for the MCP editor
    private string _labelMcpAddServer                   = string.Empty;
    private string _labelMcpName                        = string.Empty;
    private string _labelMcpCommand                     = string.Empty;
    private string _labelMcpArgs                        = string.Empty;
    private string _labelMcpEnv                         = string.Empty;
    private string _labelMcpHttpServer                  = string.Empty;
    private string _labelMcpUrl                         = string.Empty;
    private string _labelMcpHeaders                     = string.Empty;
    private string _labelMcpAuthorize                   = string.Empty;
    private string _btnMcpSaveServer                    = string.Empty;
    private string _btnMcpCancelServer                  = string.Empty;
    private string _labelMcpAdvancedJson                = string.Empty;
    private string _btnMcpImportJson                    = string.Empty;
    private bool   _personaAutoSwitch;
    private string _labelPersonaAutoSwitch       = string.Empty;
    private string _hintPersonaAutoSwitch        = string.Empty;
    private string _labelOodaTurnThreshold         = string.Empty;
    private string _hintOodaTurnThreshold          = string.Empty;
    private string _oodaTurnThresholdText;
    private string _labelCompactionEnabled         = string.Empty;
    private string _hintCompactionEnabled          = string.Empty;
    private string _labelCompactionTimeout         = string.Empty;
    private string _hintCompactionTimeout          = string.Empty;
    private string _labelKvCacheAnchor             = string.Empty;
    private string _hintKvCacheAnchor              = string.Empty;
    private string _kvCacheAnchorMessagesText;
    private string _labelSectionConnection              = string.Empty;
    private string _labelSectionBehavior                = string.Empty;
    private string _labelSectionContext                 = string.Empty;
    private string _labelSectionPersona                 = string.Empty;
    private string _labelSectionInlineCompletions       = string.Empty;
    private string _selectedInlineMode                  = string.Empty;
    private string _labelInlineCompletionMode           = string.Empty;
    private string _hintInlineCompletionMode            = string.Empty;
    private bool   _inlineCompletionEnabled;
    private string _inlineCompletionModel               = string.Empty;
    private string _labelInlineCompletionEnabled        = string.Empty;
    private string _hintInlineCompletionEnabled         = string.Empty;
    private string _labelInlineCompletionModel          = string.Empty;
    private string _hintInlineCompletionModel           = string.Empty;
    private string _codeActionsModel                    = string.Empty;
    private string _labelCodeActionsModel               = string.Empty;
    private string _hintCodeActionsModel                = string.Empty;
    private string _inlineEditModel                     = string.Empty;
    private string _labelInlineEditModel                = string.Empty;
    private string _hintInlineEditModel                 = string.Empty;
    private string _agentModel                          = string.Empty;
    private string _labelAgentModel                     = string.Empty;
    private string _hintAgentModel                      = string.Empty;
    // Simple vs per-role models: collapse the 4 chat-derived role pickers (agent, code actions, FIM,
    // inline edit) behind a toggle so the default view shows just the chat + embedding models.
    private bool   _showModelRoles;
    private string _labelModelRolesAdvanced             = string.Empty;
    private string _hintModelRolesAdvanced              = string.Empty;
    // Fold the "config-file-as-UI" timing knobs (command timeout, agent iterations, task timeouts)
    // behind a toggle — sensible defaults mean most users never touch them.
    private bool   _showAdvancedBehavior;
    private string _labelAdvancedBehavior               = string.Empty;
    private bool   _ragEnabled;
    private bool   _ragAutoContextEnabled;
    private string _ragEmbeddingModel                   = string.Empty;
    private string _ragTopKText;
    private string _labelSectionRag                     = string.Empty;
    private string _labelRagEnabled                     = string.Empty;
    private string _hintRagEnabled                      = string.Empty;
    private string _labelRagAutoContext                 = string.Empty;
    private string _hintRagAutoContext                  = string.Empty;
    private string _labelRagEmbeddingModel              = string.Empty;
    private string _hintRagEmbeddingModel               = string.Empty;
    private string _labelRagTopK                        = string.Empty;
    private string _hintRagTopK                         = string.Empty;
    private string _ragSimilarityThresholdText          = string.Empty;
    private string _labelRagSimilarityThreshold         = string.Empty;
    private string _hintRagSimilarityThreshold          = string.Empty;
    private bool   _lspEnabled;
    private string _labelLspEnabled                     = string.Empty;
    private string _hintLspEnabled                      = string.Empty;
    private bool   _compactionEnabled;
    private string _compactionTimeoutHoursText   = string.Empty;
    private string _compactionTimeoutMinutesText = string.Empty;
    private string _compactionTimeoutSecondsText = string.Empty;
    private string _btnTest                      = string.Empty;
    private string _btnSave                      = string.Empty;
    private string _tooltipRefreshModels         = string.Empty;
    private string _timeoutHoursText;
    private string _timeoutMinutesText;
    private string _timeoutSecondsText;
    private bool   _toolBubblesExpanded;
    private bool   _securityAlertsDisabled;
    private string _contextWindowSizeText;
    private string _contextWindowKeepTurnsText;
    private string _vramBudgetText;
    private string _customSystemPrompt;
    private string _pinnedContextFiles;
    private string _promptTemplates;
    private string _labelPromptTemplates = string.Empty;
    private string _hintPromptTemplates  = string.Empty;
    private string _customTools;
    private string _permissionRules;
    private string _labelPermissionRules = string.Empty;
    private string _hintPermissionRules  = string.Empty;
    private string _labelCustomTools     = string.Empty;
    private string _hintCustomTools      = string.Empty;

    // ── Editable lists (pinned files / slash commands / custom tools) ─────────
    // Shared editor chrome (localized labels + themed brushes live on the MCP row
    // brushes already on this VM, reused here).
    private string _hintRowEdit          = string.Empty;
    private string _hintRowDelete        = string.Empty;
    private string _labelRowAdvanced     = string.Empty;
    private string _btnRowImport         = string.Empty;
    // Pinned files
    private bool   _isEditingPinned;
    private string _pinnedEditingTitle   = string.Empty;
    private string _editPinnedPath       = string.Empty;
    private string _editPinnedError      = string.Empty;
    private string? _editingPinnedOriginal;
    private string _labelPinnedAddFile   = string.Empty;
    private string _labelPinnedPath      = string.Empty;
    private string _labelPinnedBrowse    = string.Empty;
    private bool   _sectionPinnedRawExpanded;
    private string _sectionPinnedRawChevron = "▶";
    // Slash commands
    private bool   _isEditingSlash;
    private string _slashEditingTitle    = string.Empty;
    private string _editSlashName        = string.Empty;
    private string _editSlashText        = string.Empty;
    private string _editSlashError       = string.Empty;
    private string? _editingSlashOriginal;
    private string _labelSlashAddCmd     = string.Empty;
    private string _labelSlashName       = string.Empty;
    private string _labelSlashText       = string.Empty;
    private bool   _sectionSlashRawExpanded;
    private string _sectionSlashRawChevron = "▶";
    // Custom tools
    private bool   _isEditingTool;
    private string _toolEditingTitle     = string.Empty;
    private string _editToolName         = string.Empty;
    private string _editToolCommand      = string.Empty;
    private string _editToolError        = string.Empty;
    private string? _editingToolOriginal;
    private string _labelToolAddTool     = string.Empty;
    private string _labelToolName        = string.Empty;
    private string _labelToolCommand     = string.Empty;
    private bool   _sectionToolRawExpanded;
    private string _sectionToolRawChevron = "▶";

    // ── List view-mode (Liste ⇄ JSON/Texte), count badges and empty states ───────
    // Each list shows EITHER the editable rows OR the raw editor — two views of one
    // source of truth, switched from the section header (replaces the old always-on
    // "Avancé" footer disclosure). *ViewVisible mirrors !*RawExpanded; *ViewLabel is the
    // header toggle caption (offers the OTHER view); *CountText feeds the header badge;
    // *Empty drives the empty-state card shown when a list has no rows.
    private bool   _mcpListViewVisible    = true;
    private string _mcpViewLabel          = string.Empty;
    private string _mcpCountText          = string.Empty;
    private bool   _mcpEmpty              = true;
    private bool   _pinnedListViewVisible = true;
    private string _pinnedViewLabel       = string.Empty;
    private string _pinnedCountText       = string.Empty;
    private bool   _pinnedEmpty           = true;
    private bool   _slashListViewVisible  = true;
    private string _slashViewLabel        = string.Empty;
    private string _slashCountText        = string.Empty;
    private bool   _slashEmpty            = true;
    private bool   _toolListViewVisible   = true;
    private string _toolViewLabel         = string.Empty;
    private string _toolCountText         = string.Empty;
    private bool   _toolEmpty             = true;
    private string _mcpEmptyTitle         = string.Empty;
    private string _pinnedEmptyTitle      = string.Empty;
    private string _slashEmptyTitle       = string.Empty;
    private string _toolEmptyTitle        = string.Empty;
    private string _labelSectionCommandsTools = string.Empty;

    internal NonConcurrentSynchronizationContext SynchronizationContext { get; } =
        new NonConcurrentSynchronizationContext(sticky: true);

    public InferpalSettingsData(InferpalConfig config, IInferenceProvider client, VisualStudioExtensibility extensibility, Services.Mcp.McpToolService mcp)
    {
        _config               = config;
        _client               = client;
        _mcp                  = mcp;
        _baseUrl              = config.BaseUrl;
        _selectedProvider     = ProviderOptions.FirstOrDefault(p => p.Code == config.Provider).Name
                                    is { Length: > 0 } pn ? pn : ProviderOptions[0].Name;
        _apiKey               = config.ApiKey;
        _selectedModel        = config.DefaultModel;
        // Language is initialized in ApplyLabels() once LangAuto is resolved
        var matchedLang = LanguageOptions.FirstOrDefault(l => l.Code == config.Language);
        _selectedLanguage = matchedLang.Name ?? string.Empty;
        var ts = TimeSpan.FromSeconds(config.CommandTimeoutSeconds);
        _timeoutHoursText   = ((int)ts.TotalHours).ToString();
        _timeoutMinutesText = ts.Minutes.ToString("D2");
        _timeoutSecondsText = ts.Seconds.ToString("D2");
        _toolBubblesExpanded         = config.ToolBubblesExpanded;
        _securityAlertsDisabled      = config.SecurityAlertsDisabled;
        _smartFixEnabled             = config.SmartFixEnabled;
        _agentModeEnabled            = config.AgentModeEnabled;
        _config.AgentModeEnabledChanged += OnAgentModeConfigChanged;   // live-sync with the toolbar switch
        _agentMaxIterationsText      = config.AgentMaxIterations.ToString();
        (_quickTimeoutHoursText,  _quickTimeoutMinutesText,  _quickTimeoutSecondsText)  = SplitDuration(config.QuickTimeoutSeconds);
        (_normalTimeoutHoursText, _normalTimeoutMinutesText, _normalTimeoutSecondsText) = SplitDuration(config.NormalTimeoutSeconds);
        (_deepTimeoutHoursText,   _deepTimeoutMinutesText,   _deepTimeoutSecondsText)   = SplitDuration(config.DeepTimeoutSeconds);
        _modelAutoUnloadEnabled      = config.ModelAutoUnloadEnabled;
        _modelIdleTimeoutText        = config.ModelIdleTimeoutMinutes.ToString();
        _contextWindowSizeText       = config.ContextWindowSize.ToString();
        _contextWindowKeepTurnsText  = config.ContextWindowKeepTurns.ToString();
        _vramBudgetText              = config.VramBudgetGb > 0
            ? config.VramBudgetGb.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture)
            : string.Empty;
        _customSystemPrompt          = config.CustomSystemPrompt;
        _pinnedContextFiles          = config.PinnedContextFiles;
        _promptTemplates             = config.PromptTemplates;
        _customTools                 = config.CustomTools;
        _permissionRules             = config.PermissionRules;
        _personaAutoSwitch           = config.PersonaAutoSwitch;
        _oodaTurnThresholdText       = config.OodaTurnThreshold.ToString();
        _compactionEnabled           = config.CompactionEnabled;
        (_compactionTimeoutHoursText, _compactionTimeoutMinutesText, _compactionTimeoutSecondsText) = SplitDuration(config.CompactionTimeoutSeconds);
        _kvCacheAnchorMessagesText   = config.KvCacheAnchorMessages.ToString();
        _selectedInlineMode = InlineModeOptions
            .FirstOrDefault(m => m.Code == config.InlineCompletionMode).Name
            is { Length: > 0 } mn ? mn : InlineModeOptions[1].Name;
        _inlineCompletionEnabled = config.InlineCompletionEnabled;
        _inlineCompletionModel   = config.InlineCompletionModel;
        _codeActionsModel        = config.CodeActionsModel;
        _inlineEditModel         = config.InlineEditModel;
        _agentModel              = config.AgentModel;
        // Start expanded only when a power user has already assigned a per-role model — otherwise the
        // simple view (chat + embeddings) is the default and the 4 role pickers stay folded.
        _showModelRoles          = !string.IsNullOrEmpty(config.AgentModel)
                                   || !string.IsNullOrEmpty(config.CodeActionsModel)
                                   || !string.IsNullOrEmpty(config.InlineCompletionModel)
                                   || !string.IsNullOrEmpty(config.InlineEditModel);
        _ragEnabled              = config.RagEnabled;
        _ragAutoContextEnabled   = config.RagAutoContextEnabled;
        _ragEmbeddingModel       = config.RagEmbeddingModel;
        _ragTopKText                    = config.RagTopK.ToString();
        _ragSimilarityThresholdText     = config.RagSimilarityThreshold.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
        _lspEnabled              = config.LspEnabled;
        _mcpEnabled              = config.McpEnabled;
        _mcpServersJson          = config.McpServersJson;
        _mcpStatusText           = BuildMcpStatus();
        BuildRowsFromConfig();
        BuildPinnedRows();
        BuildSlashRows();
        BuildToolRows();

        SaveCommand           = new AsyncCommand(SaveAsync);
        TestConnectionCommand = new AsyncCommand(TestConnectionAsync);
        RefreshModelsCommand  = new AsyncCommand(RefreshModelsAsync);
        ToggleSectionBehaviorCommand = new AsyncCommand((_, _) => { SectionBehaviorExpanded = !SectionBehaviorExpanded; return Task.CompletedTask; });
        ToggleSectionInlineCommand   = new AsyncCommand((_, _) => { SectionInlineExpanded   = !SectionInlineExpanded;   return Task.CompletedTask; });
        ToggleSectionRagCommand      = new AsyncCommand((_, _) => { SectionRagExpanded      = !SectionRagExpanded;      return Task.CompletedTask; });
        ToggleSectionMcpCommand      = new AsyncCommand((_, _) => { SectionMcpExpanded      = !SectionMcpExpanded;      return Task.CompletedTask; });
        ToggleSectionMcpJsonCommand  = new AsyncCommand((_, _) => { SectionMcpJsonExpanded  = !SectionMcpJsonExpanded;  return Task.CompletedTask; });
        AddServerCommand             = new AsyncCommand((_, _) => { BeginAddServer();   return Task.CompletedTask; });
        SaveServerCommand            = new AsyncCommand((_, _) => { CommitServer();     return Task.CompletedTask; });
        CancelEditServerCommand      = new AsyncCommand((_, _) => { IsEditingServer = false; return Task.CompletedTask; });
        ImportJsonCommand            = new AsyncCommand((_, _) => { BuildRowsFromConfigJson(McpServersJson); return Task.CompletedTask; });
        ToggleSectionContextCommand  = new AsyncCommand((_, _) => { SectionContextExpanded  = !SectionContextExpanded;  return Task.CompletedTask; });
        ToggleSectionPersonaCommand  = new AsyncCommand((_, _) => { SectionPersonaExpanded  = !SectionPersonaExpanded;  return Task.CompletedTask; });

        SelectTabConnectionCommand = new AsyncCommand((_, _) => { SelectTab("connection"); return Task.CompletedTask; });
        SelectTabBehaviorCommand   = new AsyncCommand((_, _) => { SelectTab("behavior");   return Task.CompletedTask; });
        SelectTabContextCommand    = new AsyncCommand((_, _) => { SelectTab("context");    return Task.CompletedTask; });
        SelectTabToolsCommand      = new AsyncCommand((_, _) => { SelectTab("tools");      return Task.CompletedTask; });

        AddPinnedCommand            = new AsyncCommand((_, _) => { BeginAddPinned();   return Task.CompletedTask; });
        BrowsePinnedCommand         = new AsyncCommand(async (_, _) => await BrowsePinnedAsync());
        SavePinnedCommand           = new AsyncCommand((_, _) => { CommitPinned();     return Task.CompletedTask; });
        CancelEditPinnedCommand     = new AsyncCommand((_, _) => { IsEditingPinned = false; return Task.CompletedTask; });
        ImportPinnedCommand         = new AsyncCommand((_, _) => { BuildPinnedRowsFrom(PinnedContextFiles); PersistPinned(); return Task.CompletedTask; });
        ToggleSectionPinnedRawCommand = new AsyncCommand((_, _) => { SectionPinnedRawExpanded = !SectionPinnedRawExpanded; return Task.CompletedTask; });

        AddSlashCommand             = new AsyncCommand((_, _) => { BeginAddSlash();    return Task.CompletedTask; });
        SaveSlashCommand            = new AsyncCommand((_, _) => { CommitSlash();      return Task.CompletedTask; });
        CancelEditSlashCommand      = new AsyncCommand((_, _) => { IsEditingSlash = false; return Task.CompletedTask; });
        ImportSlashCommand          = new AsyncCommand((_, _) => { BuildSlashRowsFrom(PromptTemplates); PersistSlash(); return Task.CompletedTask; });
        ToggleSectionSlashRawCommand = new AsyncCommand((_, _) => { SectionSlashRawExpanded = !SectionSlashRawExpanded; return Task.CompletedTask; });

        AddToolCommand              = new AsyncCommand((_, _) => { BeginAddTool();     return Task.CompletedTask; });
        SaveToolCommand             = new AsyncCommand((_, _) => { CommitTool();       return Task.CompletedTask; });
        CancelEditToolCommand       = new AsyncCommand((_, _) => { IsEditingTool = false; return Task.CompletedTask; });
        ImportToolCommand           = new AsyncCommand((_, _) => { BuildToolRowsFrom(CustomTools); PersistTool(); return Task.CompletedTask; });
        ToggleSectionToolRawCommand = new AsyncCommand((_, _) => { SectionToolRawExpanded = !SectionToolRawExpanded; return Task.CompletedTask; });

        ApplyTheme(VsThemeDetector.OsDarkMode());   // placeholder until the VS-theme subscription resolves
        _ = RefreshModelsAsync(null, CancellationToken.None);
        _ = InitThemeAsync(extensibility);
    }

    private static readonly SettingIdentifier<string> ColorThemeId = "environment.visualExperience.colorTheme";

    private async Task InitThemeAsync(VisualStudioExtensibility extensibility)
    {
        try
        {
            _themeSubscription = await extensibility.Settings().SubscribeAsync(
                ColorThemeId,
                CancellationToken.None,
                value => Post(() => ApplyTheme(VsThemeDetector.IsDark(value.ValueOrDefault(string.Empty)))));
        }
        catch { }
    }

    private void ApplyTheme(bool isDark)
    {
        IsDarkTheme    = isDark;
        TextForeground = isDark ? "#F1F1F1" : "#1E1E1E";
        ApplyRowTheme();
    }

    internal void ApplyLabels()
    {
        // The active VS color theme owns the dark/light choice (set by InitThemeAsync's subscription).
        // Re-apply from the cached IsDarkTheme rather than the Windows app theme — the two differ when
        // Windows is dark but VS is light, and deriving from the OS here would clobber a correct choice.
        ApplyTheme(IsDarkTheme);

        // Update the language list in place — never Clear(), which triggers a TwoWay
        // write-back from RemoteUI that would reset SelectedLanguage to empty.
        var autoName = Strings.LangAuto;
        var currentCode = _config.Language;
        if (AvailableLanguages.Count == 0)
        {
            AvailableLanguages.Add(autoName);
            for (int i = 1; i < LanguageOptions.Length; i++)
                AvailableLanguages.Add(LanguageOptions[i].Name);
        }
        else
        {
            // Only the Auto entry is localized; native names are always fixed.
            if (AvailableLanguages[0] != autoName)
                AvailableLanguages[0] = autoName;
        }

        // Re-select the correct entry (auto entry display name may have changed).
        SelectedLanguage = string.IsNullOrEmpty(currentCode)
            ? autoName
            : (LanguageOptions.FirstOrDefault(l => l.Code == currentCode).Name is { Length: > 0 } n ? n : autoName);

        LabelLanguage                = Strings.LabelLanguage;
        HintLanguage                 = Strings.HintLanguage;
        LabelProvider                = Strings.LabelProvider;
        HintProvider                 = Strings.HintProvider;
        LabelApiKey                  = Strings.LabelApiKey;
        HintApiKey                   = Strings.HintApiKey;
        LabelUrl                     = Strings.LabelUrl;
        HintUrl                      = Strings.HintUrl;
        LabelChatModel               = Strings.LabelChatModel;
        HintChatModel                = Strings.HintChatModel;
        LabelCommandTimeout          = Strings.LabelCommandTimeout;
        HintCommandTimeout           = Strings.HintCommandTimeout;
        LabelToolBubblesExpanded     = Strings.LabelToolBubblesExpanded;
        HintToolBubblesExpanded      = Strings.HintToolBubblesExpanded;
        LabelSecurityAlertsDisabled  = Strings.LabelSecurityAlertsDisabled;
        HintSecurityAlertsDisabled   = Strings.HintSecurityAlertsDisabled;
        LabelSmartFixEnabled         = Strings.LabelSmartFixEnabled;
        HintSmartFixEnabled          = Strings.HintSmartFixEnabled;
        LabelAgentModeEnabled        = Strings.LabelAgentModeEnabled;
        HintAgentModeEnabled         = Strings.HintAgentModeEnabled;
        LabelAgentMaxIterations      = Strings.LabelAgentMaxIterations;
        HintAgentMaxIterations       = Strings.HintAgentMaxIterations;
        LabelTaskTimeoutQuick        = Strings.LabelTaskTimeoutQuick;
        HintTaskTimeoutQuick         = Strings.HintTaskTimeoutQuick;
        LabelTaskTimeoutNormal       = Strings.LabelTaskTimeoutNormal;
        HintTaskTimeoutNormal        = Strings.HintTaskTimeoutNormal;
        LabelTaskTimeoutDeep         = Strings.LabelTaskTimeoutDeep;
        HintTaskTimeoutDeep          = Strings.HintTaskTimeoutDeep;
        LabelModelAutoUnload         = Strings.LabelModelAutoUnload;
        HintModelAutoUnload          = Strings.HintModelAutoUnload;
        LabelModelIdleTimeout        = Strings.LabelModelIdleTimeout;
        HintModelIdleTimeout         = Strings.HintModelIdleTimeout;
        LabelContextWindowSize       = Strings.LabelContextWindowSize;
        HintContextWindowSize        = Strings.HintContextWindowSize;
        LabelContextWindowKeepTurns  = Strings.LabelContextWindowKeepTurns;
        HintContextWindowKeepTurns   = Strings.HintContextWindowKeepTurns;
        LabelVramBudget              = Strings.LabelVramBudget;
        HintVramBudget               = Strings.HintVramBudget;
        LabelCustomSystemPrompt      = Strings.LabelCustomSystemPrompt;
        HintCustomSystemPrompt       = Strings.HintCustomSystemPrompt;
        LabelPinnedContextFiles      = Strings.LabelPinnedContextFiles;
        HintPinnedContextFiles       = Strings.HintPinnedContextFiles;
        LabelPromptTemplates         = Strings.LabelPromptTemplates;
        HintPromptTemplates          = Strings.HintPromptTemplates;
        LabelCustomTools             = Strings.LabelCustomTools;
        HintCustomTools              = Strings.HintCustomTools;
        LabelPermissionRules         = Strings.LabelPermissionRules;
        HintPermissionRules          = Strings.HintPermissionRules;
        HintRowEdit                  = Strings.HintRowEdit;
        HintRowDelete                = Strings.HintRowDelete;
        LabelRowAdvanced             = Strings.LabelRowAdvanced;
        BtnRowImport                 = Strings.BtnRowImport;
        LabelPinnedAddFile           = Strings.PinnedAddFile;
        LabelPinnedPath              = Strings.LabelPinnedPath;
        LabelPinnedBrowse            = Strings.PinnedBrowse;
        LabelSlashAddCmd             = Strings.SlashAddCmd;
        LabelSlashName               = Strings.LabelSlashName;
        LabelSlashText               = Strings.LabelSlashText;
        LabelToolAddTool             = Strings.ToolAddTool;
        LabelToolName                = Strings.LabelToolName;
        LabelToolCommand             = Strings.LabelToolCommand;
        LabelPersonaAutoSwitch       = Strings.LabelPersonaAutoSwitch;
        HintPersonaAutoSwitch        = Strings.HintPersonaAutoSwitch;
        LabelOodaTurnThreshold       = Strings.LabelOodaTurnThreshold;
        HintOodaTurnThreshold        = Strings.HintOodaTurnThreshold;
        LabelCompactionEnabled       = Strings.LabelCompactionEnabled;
        HintCompactionEnabled        = Strings.HintCompactionEnabled;
        LabelCompactionTimeout       = Strings.LabelCompactionTimeout;
        HintCompactionTimeout        = Strings.HintCompactionTimeout;
        LabelKvCacheAnchor           = Strings.LabelKvCacheAnchor;
        HintKvCacheAnchor            = Strings.HintKvCacheAnchor;
        LabelSectionConnection          = Strings.SectionConnection;
        LabelSectionBehavior            = Strings.SectionBehavior;
        LabelSectionContext             = Strings.SectionContext;
        LabelTabConnection              = Strings.SettingsTabConnection;
        LabelTabBehavior                = Strings.SettingsTabBehavior;
        LabelTabContext                 = Strings.SettingsTabContext;
        LabelTabTools                   = Strings.SettingsTabTools;
        LabelSectionPersona             = Strings.SectionPersona;
        LabelSectionInlineCompletions   = Strings.SectionInlineCompletions;
        LabelInlineCompletionMode       = Strings.LabelInlineCompletionMode;
        HintInlineCompletionMode        = Strings.HintInlineCompletionMode;
        LabelInlineCompletionEnabled    = Strings.LabelInlineCompletionEnabled;
        HintInlineCompletionEnabled     = Strings.HintInlineCompletionEnabled;
        LabelInlineCompletionModel      = Strings.LabelInlineCompletionModel;
        HintInlineCompletionModel       = Strings.HintInlineCompletionModel;
        LabelCodeActionsModel           = Strings.LabelCodeActionsModel;
        HintCodeActionsModel            = Strings.HintCodeActionsModel;
        LabelInlineEditModel            = Strings.LabelInlineEditModel;
        HintInlineEditModel             = Strings.HintInlineEditModel;
        LabelAgentModel                 = Strings.LabelAgentModel;
        HintAgentModel                  = Strings.HintAgentModel;
        LabelModelRolesAdvanced         = Strings.LabelModelRolesAdvanced;
        HintModelRolesAdvanced          = Strings.HintModelRolesAdvanced;
        LabelAdvancedBehavior           = Strings.LabelAdvancedBehavior;
        LabelSectionRag                 = Strings.SectionRag;
        LabelRagEnabled                 = Strings.LabelRagEnabled;
        HintRagEnabled                  = Strings.HintRagEnabled;
        LabelRagAutoContext             = Strings.LabelRagAutoContext;
        HintRagAutoContext              = Strings.HintRagAutoContext;
        LabelRagEmbeddingModel          = Strings.LabelRagEmbeddingModel;
        HintRagEmbeddingModel           = Strings.HintRagEmbeddingModel;
        LabelRagTopK                    = Strings.LabelRagTopK;
        HintRagTopK                     = Strings.HintRagTopK;
        LabelRagSimilarityThreshold     = Strings.LabelRagSimilarityThreshold;
        HintRagSimilarityThreshold      = Strings.HintRagSimilarityThreshold;
        LabelLspEnabled                 = Strings.LabelLspEnabled;
        HintLspEnabled                  = Strings.HintLspEnabled;
        LabelSectionMcp                 = Strings.SectionMcp;
        LabelMcpEnabled                 = Strings.LabelMcpEnabled;
        HintMcpEnabled                  = Strings.HintMcpEnabled;
        LabelMcpServers                 = Strings.LabelMcpServers;
        HintMcpServers                  = Strings.HintMcpServers;
        HintMcpEditServer               = Strings.HintMcpEditServer;
        HintMcpDeleteServer             = Strings.HintMcpDeleteServer;
        LabelMcpAddServer               = Strings.McpAddServer;
        LabelMcpName                    = Strings.LabelMcpName;
        LabelMcpCommand                 = Strings.LabelMcpCommand;
        LabelMcpArgs                    = Strings.LabelMcpArgs;
        LabelMcpEnv                     = Strings.LabelMcpEnv;
        LabelMcpHttpServer              = Strings.LabelMcpHttpServer;
        LabelMcpUrl                     = Strings.LabelMcpUrl;
        LabelMcpHeaders                 = Strings.LabelMcpHeaders;
        LabelMcpAuthorize               = Strings.BtnMcpAuthorize;
        BtnMcpSaveServer                = Strings.BtnMcpSaveServer;
        BtnMcpCancelServer              = Strings.BtnMcpCancelServer;
        LabelMcpAdvancedJson            = Strings.McpAdvancedJson;
        BtnMcpImportJson                = Strings.McpImportJson;

        // List view-mode toggle captions (offer the OTHER view), empty-state titles, new section header.
        McpViewLabel        = SectionMcpJsonExpanded    ? Strings.ViewList : Strings.ViewJson;
        PinnedViewLabel     = SectionPinnedRawExpanded  ? Strings.ViewList : Strings.ViewText;
        SlashViewLabel      = SectionSlashRawExpanded   ? Strings.ViewList : Strings.ViewText;
        ToolViewLabel       = SectionToolRawExpanded    ? Strings.ViewList : Strings.ViewText;
        McpEmptyTitle       = Strings.McpEmptyTitle;
        PinnedEmptyTitle    = Strings.PinnedEmptyTitle;
        SlashEmptyTitle     = Strings.SlashEmptyTitle;
        ToolEmptyTitle      = Strings.ToolEmptyTitle;
        LabelSectionCommandsTools = Strings.SectionCommandsTools;

        if (AvailableProviders.Count == 0)
            foreach (var (_, name) in ProviderOptions)
                AvailableProviders.Add(name);
        SelectedProvider = ProviderOptions
            .FirstOrDefault(p => p.Code == _config.Provider).Name
            is { Length: > 0 } pn3 ? pn3 : ProviderOptions[0].Name;

        if (AvailableInlineModes.Count == 0)
            foreach (var (_, name) in InlineModeOptions)
                AvailableInlineModes.Add(name);
        SelectedInlineMode = InlineModeOptions
            .FirstOrDefault(m => m.Code == _config.InlineCompletionMode).Name
            is { Length: > 0 } mn2 ? mn2 : InlineModeOptions[1].Name;
        BtnTest                  = Strings.BtnTest;
        BtnSave                  = Strings.BtnSave;
        TooltipRefreshModels     = Strings.TooltipRefreshModels;

        ApplyProviderCapabilities();   // gate capability-specific options + pick the right num_ctx hint
    }

    /// <summary>
    /// Toggles capability-gated options to match the <em>currently selected</em> provider in the
    /// dropdown so the UI never surfaces something that backend can't honour: the keep_alive
    /// auto-unload settings (Ollama only) and the Inline Completions / FIM section (not on generic
    /// OpenAI servers). The context-window field stays for everyone — it still drives client-side
    /// trimming — but its hint clarifies that only Ollama receives it as <c>num_ctx</c> (other servers
    /// set the context at model load, the root of the earlier LM Studio context mismatch).
    /// </summary>
    private void ApplyProviderCapabilities()
    {
        var code = ProviderOptions.FirstOrDefault(p => p.Name == _selectedProvider).Code
                   ?? Services.InferenceProviderFactory.Ollama;
        var caps = Services.InferenceProviderFactory.CapabilitiesFor(code);

        ShowKeepAliveSettings = caps.KeepAlive;
        ShowInlineCompletions = caps.Fim;
        HintContextWindowSize = code == Services.InferenceProviderFactory.Ollama
            ? Strings.HintContextWindowSize
            : Strings.HintContextWindowSizeClientTrim;
    }

    /// <summary>
    /// Live-sync handler for <see cref="InferpalConfig.AgentModeEnabledChanged"/>: keeps the
    /// checkbox aligned when the main-window Chat/Agent switch flips the value while this window is
    /// open. Marshals to the VM context. Setting the property only raises PropertyChanged (it never
    /// writes back to the config), so there is no feedback loop.
    /// </summary>
    private void OnAgentModeConfigChanged(bool enabled) => Post(() => AgentModeEnabled = enabled);

    /// <summary>Detaches config event handlers; called when the settings control is disposed.</summary>
    internal void Detach() => _config.AgentModeEnabledChanged -= OnAgentModeConfigChanged;

    // ── VM context helpers ─────────────────────────────────────────────────────

    private void Post(Action action) =>
        SynchronizationContext.Post(_ =>
        {
            try { action(); }
            catch { }
        }, null);

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

    // ── Embedding model keyword filter ────────────────────────────────────────
    // Shared with the tool-window VM's first-run discovery (see ModelCatalog).
    private static bool IsEmbeddingModel(string name) =>
        Services.ModelCatalog.IsEmbeddingModel(name);

    // ── Collections ────────────────────────────────────────────────────────────
    [DataMember] public ObservableCollection<string> AvailableLanguages       { get; } = [];
    [DataMember] public ObservableCollection<string> AvailableModels          { get; } = [];
    [DataMember] public ObservableCollection<string> AvailableOptionalModels  { get; } = [];
    [DataMember] public ObservableCollection<string> AvailableEmbeddingModels { get; } = [];
    [DataMember] public ObservableCollection<string> AvailableInlineModes     { get; } = [];
    [DataMember] public ObservableCollection<string> AvailableProviders       { get; } = [];

    // ── UI Labels ──────────────────────────────────────────────────────────────
    [DataMember] public string LabelLanguage { get => _labelLanguage; set => SetProperty(ref _labelLanguage, value); }
    [DataMember] public string HintLanguage  { get => _hintLanguage;  set => SetProperty(ref _hintLanguage,  value); }
    [DataMember] public string LabelProvider             { get => _labelProvider;             set => SetProperty(ref _labelProvider,             value); }
    [DataMember] public string HintProvider              { get => _hintProvider;              set => SetProperty(ref _hintProvider,              value); }
    [DataMember] public string LabelApiKey               { get => _labelApiKey;               set => SetProperty(ref _labelApiKey,               value); }
    [DataMember] public string HintApiKey                { get => _hintApiKey;                set => SetProperty(ref _hintApiKey,                value); }
    [DataMember] public string LabelUrl                  { get => _labelUrl;                  set => SetProperty(ref _labelUrl,                  value); }
    [DataMember] public string HintUrl                   { get => _hintUrl;                   set => SetProperty(ref _hintUrl,                   value); }
    [DataMember] public string LabelChatModel            { get => _labelChatModel;            set => SetProperty(ref _labelChatModel,            value); }
    [DataMember] public string HintChatModel             { get => _hintChatModel;             set => SetProperty(ref _hintChatModel,             value); }
    [DataMember] public string LabelCommandTimeout       { get => _labelCommandTimeout;       set => SetProperty(ref _labelCommandTimeout,       value); }
    [DataMember] public string HintCommandTimeout        { get => _hintCommandTimeout;        set => SetProperty(ref _hintCommandTimeout,        value); }
    [DataMember] public string LabelToolBubblesExpanded  { get => _labelToolBubblesExpanded;  set => SetProperty(ref _labelToolBubblesExpanded,  value); }
    [DataMember] public string HintToolBubblesExpanded   { get => _hintToolBubblesExpanded;   set => SetProperty(ref _hintToolBubblesExpanded,   value); }
    [DataMember] public string LabelSecurityAlertsDisabled { get => _labelSecurityAlertsDisabled; set => SetProperty(ref _labelSecurityAlertsDisabled, value); }
    [DataMember] public string HintSecurityAlertsDisabled  { get => _hintSecurityAlertsDisabled;  set => SetProperty(ref _hintSecurityAlertsDisabled,  value); }
    [DataMember] public bool   SmartFixEnabled             { get => _smartFixEnabled;             set => SetProperty(ref _smartFixEnabled,             value); }
    [DataMember] public string LabelSmartFixEnabled        { get => _labelSmartFixEnabled;        set => SetProperty(ref _labelSmartFixEnabled,        value); }
    [DataMember] public string HintSmartFixEnabled         { get => _hintSmartFixEnabled;         set => SetProperty(ref _hintSmartFixEnabled,         value); }
    [DataMember] public bool   AgentModeEnabled            { get => _agentModeEnabled;            set => SetProperty(ref _agentModeEnabled,            value); }
    [DataMember] public string LabelAgentModeEnabled       { get => _labelAgentModeEnabled;       set => SetProperty(ref _labelAgentModeEnabled,       value); }
    [DataMember] public string HintAgentModeEnabled        { get => _hintAgentModeEnabled;        set => SetProperty(ref _hintAgentModeEnabled,        value); }
    [DataMember] public string AgentMaxIterationsText      { get => _agentMaxIterationsText;      set => SetProperty(ref _agentMaxIterationsText,      value); }
    [DataMember] public string LabelAgentMaxIterations     { get => _labelAgentMaxIterations;     set => SetProperty(ref _labelAgentMaxIterations,     value); }
    [DataMember] public string HintAgentMaxIterations      { get => _hintAgentMaxIterations;      set => SetProperty(ref _hintAgentMaxIterations,      value); }
    [DataMember] public string QuickTimeoutHoursText       { get => _quickTimeoutHoursText;       set => SetProperty(ref _quickTimeoutHoursText,       ClampDurationField(value, 99)); }
    [DataMember] public string QuickTimeoutMinutesText     { get => _quickTimeoutMinutesText;     set => SetProperty(ref _quickTimeoutMinutesText,     ClampDurationField(value, 59)); }
    [DataMember] public string QuickTimeoutSecondsText     { get => _quickTimeoutSecondsText;     set => SetProperty(ref _quickTimeoutSecondsText,     ClampDurationField(value, 59)); }
    [DataMember] public string LabelTaskTimeoutQuick       { get => _labelTaskTimeoutQuick;       set => SetProperty(ref _labelTaskTimeoutQuick,       value); }
    [DataMember] public string HintTaskTimeoutQuick        { get => _hintTaskTimeoutQuick;        set => SetProperty(ref _hintTaskTimeoutQuick,        value); }
    [DataMember] public string NormalTimeoutHoursText      { get => _normalTimeoutHoursText;      set => SetProperty(ref _normalTimeoutHoursText,      ClampDurationField(value, 99)); }
    [DataMember] public string NormalTimeoutMinutesText    { get => _normalTimeoutMinutesText;    set => SetProperty(ref _normalTimeoutMinutesText,    ClampDurationField(value, 59)); }
    [DataMember] public string NormalTimeoutSecondsText    { get => _normalTimeoutSecondsText;    set => SetProperty(ref _normalTimeoutSecondsText,    ClampDurationField(value, 59)); }
    [DataMember] public string LabelTaskTimeoutNormal      { get => _labelTaskTimeoutNormal;      set => SetProperty(ref _labelTaskTimeoutNormal,      value); }
    [DataMember] public string HintTaskTimeoutNormal       { get => _hintTaskTimeoutNormal;       set => SetProperty(ref _hintTaskTimeoutNormal,       value); }
    [DataMember] public string DeepTimeoutHoursText        { get => _deepTimeoutHoursText;        set => SetProperty(ref _deepTimeoutHoursText,        ClampDurationField(value, 99)); }
    [DataMember] public string DeepTimeoutMinutesText      { get => _deepTimeoutMinutesText;      set => SetProperty(ref _deepTimeoutMinutesText,      ClampDurationField(value, 59)); }
    [DataMember] public string DeepTimeoutSecondsText      { get => _deepTimeoutSecondsText;      set => SetProperty(ref _deepTimeoutSecondsText,      ClampDurationField(value, 59)); }
    [DataMember] public string LabelTaskTimeoutDeep        { get => _labelTaskTimeoutDeep;        set => SetProperty(ref _labelTaskTimeoutDeep,        value); }
    [DataMember] public string HintTaskTimeoutDeep         { get => _hintTaskTimeoutDeep;         set => SetProperty(ref _hintTaskTimeoutDeep,         value); }
    [DataMember] public bool   ModelAutoUnloadEnabled      { get => _modelAutoUnloadEnabled;      set => SetProperty(ref _modelAutoUnloadEnabled,      value); }
    [DataMember] public string LabelModelAutoUnload        { get => _labelModelAutoUnload;        set => SetProperty(ref _labelModelAutoUnload,        value); }
    [DataMember] public string HintModelAutoUnload         { get => _hintModelAutoUnload;         set => SetProperty(ref _hintModelAutoUnload,         value); }
    [DataMember] public string ModelIdleTimeoutText        { get => _modelIdleTimeoutText;        set => SetProperty(ref _modelIdleTimeoutText,        value); }
    [DataMember] public string LabelModelIdleTimeout       { get => _labelModelIdleTimeout;       set => SetProperty(ref _labelModelIdleTimeout,       value); }
    [DataMember] public string HintModelIdleTimeout        { get => _hintModelIdleTimeout;        set => SetProperty(ref _hintModelIdleTimeout,        value); }
    [DataMember] public string LabelContextWindowSize       { get => _labelContextWindowSize;      set => SetProperty(ref _labelContextWindowSize,      value); }
    [DataMember] public string HintContextWindowSize        { get => _hintContextWindowSize;       set => SetProperty(ref _hintContextWindowSize,       value); }
    [DataMember] public string LabelContextWindowKeepTurns  { get => _labelContextWindowKeepTurns; set => SetProperty(ref _labelContextWindowKeepTurns, value); }
    [DataMember] public string HintContextWindowKeepTurns   { get => _hintContextWindowKeepTurns;  set => SetProperty(ref _hintContextWindowKeepTurns,  value); }
    [DataMember] public string LabelVramBudget              { get => _labelVramBudget;             set => SetProperty(ref _labelVramBudget,             value); }
    [DataMember] public string HintVramBudget               { get => _hintVramBudget;              set => SetProperty(ref _hintVramBudget,              value); }
    [DataMember] public string LabelCustomSystemPrompt  { get => _labelCustomSystemPrompt;  set => SetProperty(ref _labelCustomSystemPrompt,  value); }
    [DataMember] public string HintCustomSystemPrompt   { get => _hintCustomSystemPrompt;   set => SetProperty(ref _hintCustomSystemPrompt,   value); }
    [DataMember] public string LabelPinnedContextFiles  { get => _labelPinnedContextFiles;  set => SetProperty(ref _labelPinnedContextFiles,  value); }
    [DataMember] public string HintPinnedContextFiles   { get => _hintPinnedContextFiles;   set => SetProperty(ref _hintPinnedContextFiles,   value); }
    [DataMember] public string LabelPromptTemplates     { get => _labelPromptTemplates;     set => SetProperty(ref _labelPromptTemplates,     value); }
    [DataMember] public string HintPromptTemplates      { get => _hintPromptTemplates;      set => SetProperty(ref _hintPromptTemplates,      value); }
    [DataMember] public string LabelCustomTools         { get => _labelCustomTools;         set => SetProperty(ref _labelCustomTools,         value); }
    [DataMember] public string HintCustomTools          { get => _hintCustomTools;          set => SetProperty(ref _hintCustomTools,          value); }
    [DataMember] public string LabelPermissionRules     { get => _labelPermissionRules;     set => SetProperty(ref _labelPermissionRules,     value); }
    [DataMember] public string HintPermissionRules      { get => _hintPermissionRules;      set => SetProperty(ref _hintPermissionRules,      value); }
    [DataMember] public bool   PersonaAutoSwitch        { get => _personaAutoSwitch;        set => SetProperty(ref _personaAutoSwitch,        value); }
    [DataMember] public string LabelPersonaAutoSwitch   { get => _labelPersonaAutoSwitch;   set => SetProperty(ref _labelPersonaAutoSwitch,   value); }
    [DataMember] public string HintPersonaAutoSwitch    { get => _hintPersonaAutoSwitch;    set => SetProperty(ref _hintPersonaAutoSwitch,    value); }
    [DataMember] public string LabelOodaTurnThreshold   { get => _labelOodaTurnThreshold;   set => SetProperty(ref _labelOodaTurnThreshold,   value); }
    [DataMember] public string HintOodaTurnThreshold    { get => _hintOodaTurnThreshold;    set => SetProperty(ref _hintOodaTurnThreshold,    value); }
    [DataMember] public string LabelCompactionEnabled   { get => _labelCompactionEnabled;   set => SetProperty(ref _labelCompactionEnabled,   value); }
    [DataMember] public string HintCompactionEnabled    { get => _hintCompactionEnabled;    set => SetProperty(ref _hintCompactionEnabled,    value); }
    [DataMember] public string LabelCompactionTimeout   { get => _labelCompactionTimeout;   set => SetProperty(ref _labelCompactionTimeout,   value); }
    [DataMember] public string HintCompactionTimeout    { get => _hintCompactionTimeout;    set => SetProperty(ref _hintCompactionTimeout,    value); }
    [DataMember] public string LabelKvCacheAnchor       { get => _labelKvCacheAnchor;       set => SetProperty(ref _labelKvCacheAnchor,       value); }
    [DataMember] public string HintKvCacheAnchor        { get => _hintKvCacheAnchor;        set => SetProperty(ref _hintKvCacheAnchor,        value); }
    [DataMember] public string LabelSectionConnection        { get => _labelSectionConnection;        set => SetProperty(ref _labelSectionConnection,        value); }

    // ── Collapsible sections: expanded flag + chevron glyph (set together) ──
    [DataMember] public bool SectionBehaviorExpanded
    {
        get => _sectionBehaviorExpanded;
        set { if (SetProperty(ref _sectionBehaviorExpanded, value)) SectionBehaviorChevron = value ? "▼" : "▶"; }
    }
    [DataMember] public bool SectionInlineExpanded
    {
        get => _sectionInlineExpanded;
        set { if (SetProperty(ref _sectionInlineExpanded, value)) SectionInlineChevron = value ? "▼" : "▶"; }
    }
    [DataMember] public bool SectionRagExpanded
    {
        get => _sectionRagExpanded;
        set { if (SetProperty(ref _sectionRagExpanded, value)) SectionRagChevron = value ? "▼" : "▶"; }
    }
    [DataMember] public bool SectionMcpExpanded
    {
        get => _sectionMcpExpanded;
        set { if (SetProperty(ref _sectionMcpExpanded, value)) SectionMcpChevron = value ? "▼" : "▶"; }
    }
    [DataMember] public bool SectionContextExpanded
    {
        get => _sectionContextExpanded;
        set { if (SetProperty(ref _sectionContextExpanded, value)) SectionContextChevron = value ? "▼" : "▶"; }
    }
    [DataMember] public bool SectionPersonaExpanded
    {
        get => _sectionPersonaExpanded;
        set { if (SetProperty(ref _sectionPersonaExpanded, value)) SectionPersonaChevron = value ? "▼" : "▶"; }
    }
    [DataMember] public string SectionBehaviorChevron { get => _sectionBehaviorChevron; set => SetProperty(ref _sectionBehaviorChevron, value); }
    [DataMember] public string SectionInlineChevron   { get => _sectionInlineChevron;   set => SetProperty(ref _sectionInlineChevron,   value); }
    [DataMember] public string SectionRagChevron      { get => _sectionRagChevron;      set => SetProperty(ref _sectionRagChevron,      value); }
    [DataMember] public string SectionMcpChevron      { get => _sectionMcpChevron;      set => SetProperty(ref _sectionMcpChevron,      value); }
    [DataMember] public string SectionContextChevron  { get => _sectionContextChevron;  set => SetProperty(ref _sectionContextChevron,  value); }
    [DataMember] public string SectionPersonaChevron  { get => _sectionPersonaChevron;  set => SetProperty(ref _sectionPersonaChevron,  value); }
    [DataMember] public AsyncCommand ToggleSectionBehaviorCommand { get; }
    [DataMember] public AsyncCommand ToggleSectionInlineCommand   { get; }
    [DataMember] public AsyncCommand ToggleSectionRagCommand      { get; }
    [DataMember] public AsyncCommand ToggleSectionMcpCommand      { get; }
    [DataMember] public AsyncCommand ToggleSectionContextCommand  { get; }
    [DataMember] public AsyncCommand ToggleSectionPersonaCommand  { get; }

    // ── Settings tabs ────────────────────────────────────────────────────────
    [DataMember] public bool   TabConnectionVisible   { get => _tabConnectionVisible;   set => SetProperty(ref _tabConnectionVisible,   value); }
    [DataMember] public bool   TabBehaviorVisible     { get => _tabBehaviorVisible;     set => SetProperty(ref _tabBehaviorVisible,     value); }
    [DataMember] public bool   TabContextVisible      { get => _tabContextVisible;      set => SetProperty(ref _tabContextVisible,      value); }
    [DataMember] public bool   TabToolsVisible        { get => _tabToolsVisible;        set => SetProperty(ref _tabToolsVisible,        value); }
    [DataMember] public string TabConnectionFg        { get => _tabConnectionFg;        set => SetProperty(ref _tabConnectionFg,        value); }
    [DataMember] public string TabBehaviorFg          { get => _tabBehaviorFg;          set => SetProperty(ref _tabBehaviorFg,          value); }
    [DataMember] public string TabContextFg           { get => _tabContextFg;           set => SetProperty(ref _tabContextFg,           value); }
    [DataMember] public string TabToolsFg             { get => _tabToolsFg;             set => SetProperty(ref _tabToolsFg,             value); }
    [DataMember] public string TabConnectionUnderline { get => _tabConnectionUnderline; set => SetProperty(ref _tabConnectionUnderline, value); }
    [DataMember] public string TabBehaviorUnderline   { get => _tabBehaviorUnderline;   set => SetProperty(ref _tabBehaviorUnderline,   value); }
    [DataMember] public string TabContextUnderline    { get => _tabContextUnderline;    set => SetProperty(ref _tabContextUnderline,    value); }
    [DataMember] public string TabToolsUnderline       { get => _tabToolsUnderline;       set => SetProperty(ref _tabToolsUnderline,       value); }
    [DataMember] public string LabelTabConnection     { get => _labelTabConnection;     set => SetProperty(ref _labelTabConnection,     value); }
    [DataMember] public string LabelTabBehavior       { get => _labelTabBehavior;       set => SetProperty(ref _labelTabBehavior,       value); }
    [DataMember] public string LabelTabContext        { get => _labelTabContext;        set => SetProperty(ref _labelTabContext,        value); }
    [DataMember] public string LabelTabTools          { get => _labelTabTools;          set => SetProperty(ref _labelTabTools,          value); }
    [DataMember] public AsyncCommand SelectTabConnectionCommand { get; }
    [DataMember] public AsyncCommand SelectTabBehaviorCommand   { get; }
    [DataMember] public AsyncCommand SelectTabContextCommand    { get; }
    [DataMember] public AsyncCommand SelectTabToolsCommand      { get; }
    [DataMember] public bool   McpEnabled               { get => _mcpEnabled;               set => SetProperty(ref _mcpEnabled,               value); }
    [DataMember] public string McpServersJson           { get => _mcpServersJson;           set => SetProperty(ref _mcpServersJson,           value); }
    [DataMember] public string McpStatusText            { get => _mcpStatusText;            set => SetProperty(ref _mcpStatusText,            value); }
    [DataMember] public string LabelSectionMcp          { get => _labelSectionMcp;          set => SetProperty(ref _labelSectionMcp,          value); }
    [DataMember] public string LabelMcpEnabled          { get => _labelMcpEnabled;          set => SetProperty(ref _labelMcpEnabled,          value); }
    [DataMember] public string HintMcpEnabled           { get => _hintMcpEnabled;           set => SetProperty(ref _hintMcpEnabled,           value); }
    [DataMember] public string LabelMcpServers          { get => _labelMcpServers;          set => SetProperty(ref _labelMcpServers,          value); }
    [DataMember] public string HintMcpServers           { get => _hintMcpServers;           set => SetProperty(ref _hintMcpServers,           value); }
    [DataMember] public string HintMcpEditServer        { get => _hintMcpEditServer;        set => SetProperty(ref _hintMcpEditServer,        value); }
    [DataMember] public string HintMcpDeleteServer      { get => _hintMcpDeleteServer;      set => SetProperty(ref _hintMcpDeleteServer,      value); }
    [DataMember] public string McpSubtleForeground      { get => _mcpSubtleForeground;      set => SetProperty(ref _mcpSubtleForeground,      value); }
    [DataMember] public string McpRowBackground         { get => _mcpRowBackground;         set => SetProperty(ref _mcpRowBackground,         value); }
    [DataMember] public string McpRowBorder             { get => _mcpRowBorder;             set => SetProperty(ref _mcpRowBorder,             value); }
    [DataMember] public string McpEditGlyph             { get => _mcpEditGlyph;             set => SetProperty(ref _mcpEditGlyph,             value); }
    [DataMember] public string ThemeHoverBg             { get => _themeHoverBg;             set => SetProperty(ref _themeHoverBg,             value); }
    [DataMember] public string EditPanelBg              { get => _editPanelBg;              set => SetProperty(ref _editPanelBg,              value); }
    [DataMember] public string SecondaryButtonBg        { get => _secondaryButtonBg;        set => SetProperty(ref _secondaryButtonBg,        value); }
    [DataMember] public string SecondaryButtonHover     { get => _secondaryButtonHover;     set => SetProperty(ref _secondaryButtonHover,     value); }
    [DataMember] public string SecondaryButtonFg        { get => _secondaryButtonFg;        set => SetProperty(ref _secondaryButtonFg,        value); }

    // ── MCP server list + inline editor ──────────────────────────────────────
    [DataMember] public ObservableCollection<McpServerRow> McpServers { get; } = [];
    [DataMember] public bool   SectionMcpJsonExpanded
    {
        get => _sectionMcpJsonExpanded;
        set { if (SetProperty(ref _sectionMcpJsonExpanded, value)) { SectionMcpJsonChevron = value ? "▼" : "▶"; McpListViewVisible = !value; McpViewLabel = value ? Strings.ViewList : Strings.ViewJson; } }
    }
    [DataMember] public string SectionMcpJsonChevron    { get => _sectionMcpJsonChevron;    set => SetProperty(ref _sectionMcpJsonChevron,    value); }
    [DataMember] public bool   IsEditingServer          { get => _isEditingServer;          set => SetProperty(ref _isEditingServer,          value); }
    [DataMember] public string EditingTitle             { get => _editingTitle;             set => SetProperty(ref _editingTitle,             value); }
    [DataMember] public string EditServerName           { get => _editServerName;           set => SetProperty(ref _editServerName,           value); }
    [DataMember] public string EditServerCommand        { get => _editServerCommand;        set => SetProperty(ref _editServerCommand,        value); }
    [DataMember] public string EditServerArgs           { get => _editServerArgs;           set => SetProperty(ref _editServerArgs,           value); }
    [DataMember] public string EditServerEnv            { get => _editServerEnv;            set => SetProperty(ref _editServerEnv,            value); }
    /// <summary>Transport toggle in the editor. Setting it keeps <see cref="EditServerIsStdio"/> in sync
    /// so the two field groups (command/args/env vs url/headers) swap visibility in the bound template.</summary>
    [DataMember] public bool   EditServerIsHttp         { get => _editServerIsHttp;         set { if (SetProperty(ref _editServerIsHttp, value)) EditServerIsStdio = !value; } }
    [DataMember] public bool   EditServerIsStdio        { get => _editServerIsStdio;        private set => SetProperty(ref _editServerIsStdio, value); }
    [DataMember] public string EditServerUrl            { get => _editServerUrl;            set => SetProperty(ref _editServerUrl,            value); }
    [DataMember] public string EditServerHeaders        { get => _editServerHeaders;        set => SetProperty(ref _editServerHeaders,        value); }
    [DataMember] public string EditServerError          { get => _editServerError;          set => SetProperty(ref _editServerError,          value); }
    [DataMember] public string LabelMcpAddServer        { get => _labelMcpAddServer;        set => SetProperty(ref _labelMcpAddServer,        value); }
    [DataMember] public string LabelMcpName             { get => _labelMcpName;             set => SetProperty(ref _labelMcpName,             value); }
    [DataMember] public string LabelMcpCommand          { get => _labelMcpCommand;          set => SetProperty(ref _labelMcpCommand,          value); }
    [DataMember] public string LabelMcpArgs             { get => _labelMcpArgs;             set => SetProperty(ref _labelMcpArgs,             value); }
    [DataMember] public string LabelMcpEnv              { get => _labelMcpEnv;              set => SetProperty(ref _labelMcpEnv,              value); }
    [DataMember] public string LabelMcpHttpServer       { get => _labelMcpHttpServer;       set => SetProperty(ref _labelMcpHttpServer,       value); }
    [DataMember] public string LabelMcpUrl              { get => _labelMcpUrl;              set => SetProperty(ref _labelMcpUrl,              value); }
    [DataMember] public string LabelMcpHeaders          { get => _labelMcpHeaders;          set => SetProperty(ref _labelMcpHeaders,          value); }
    [DataMember] public string LabelMcpAuthorize        { get => _labelMcpAuthorize;        set => SetProperty(ref _labelMcpAuthorize,        value); }
    [DataMember] public string BtnMcpSaveServer         { get => _btnMcpSaveServer;         set => SetProperty(ref _btnMcpSaveServer,         value); }
    [DataMember] public string BtnMcpCancelServer       { get => _btnMcpCancelServer;       set => SetProperty(ref _btnMcpCancelServer,       value); }
    [DataMember] public string LabelMcpAdvancedJson     { get => _labelMcpAdvancedJson;     set => SetProperty(ref _labelMcpAdvancedJson,     value); }
    [DataMember] public string BtnMcpImportJson         { get => _btnMcpImportJson;         set => SetProperty(ref _btnMcpImportJson,         value); }
    [DataMember] public AsyncCommand ToggleSectionMcpJsonCommand { get; }
    [DataMember] public AsyncCommand AddServerCommand           { get; }
    [DataMember] public AsyncCommand SaveServerCommand          { get; }
    [DataMember] public AsyncCommand CancelEditServerCommand    { get; }
    [DataMember] public AsyncCommand ImportJsonCommand          { get; }
    [DataMember] public string LabelSectionBehavior          { get => _labelSectionBehavior;          set => SetProperty(ref _labelSectionBehavior,          value); }
    [DataMember] public string LabelSectionContext           { get => _labelSectionContext;           set => SetProperty(ref _labelSectionContext,           value); }
    [DataMember] public string LabelSectionPersona           { get => _labelSectionPersona;           set => SetProperty(ref _labelSectionPersona,           value); }
    [DataMember] public string LabelSectionInlineCompletions { get => _labelSectionInlineCompletions; set => SetProperty(ref _labelSectionInlineCompletions, value); }
    [DataMember] public string SelectedInlineMode            { get => _selectedInlineMode;            set => SetProperty(ref _selectedInlineMode,            value); }
    [DataMember] public string LabelInlineCompletionMode     { get => _labelInlineCompletionMode;     set => SetProperty(ref _labelInlineCompletionMode,     value); }
    [DataMember] public string HintInlineCompletionMode      { get => _hintInlineCompletionMode;      set => SetProperty(ref _hintInlineCompletionMode,      value); }
    [DataMember] public bool   InlineCompletionEnabled       { get => _inlineCompletionEnabled;       set => SetProperty(ref _inlineCompletionEnabled,       value); }
    [DataMember] public string InlineCompletionModel         { get => _inlineCompletionModel;         set => SetProperty(ref _inlineCompletionModel,         value); }
    [DataMember] public string LabelInlineCompletionEnabled  { get => _labelInlineCompletionEnabled;  set => SetProperty(ref _labelInlineCompletionEnabled,  value); }
    [DataMember] public string HintInlineCompletionEnabled   { get => _hintInlineCompletionEnabled;   set => SetProperty(ref _hintInlineCompletionEnabled,   value); }
    [DataMember] public string LabelInlineCompletionModel    { get => _labelInlineCompletionModel;    set => SetProperty(ref _labelInlineCompletionModel,    value); }
    [DataMember] public string HintInlineCompletionModel     { get => _hintInlineCompletionModel;     set => SetProperty(ref _hintInlineCompletionModel,     value); }
    [DataMember] public string CodeActionsModel              { get => _codeActionsModel;              set => SetProperty(ref _codeActionsModel,              value); }
    [DataMember] public string LabelCodeActionsModel         { get => _labelCodeActionsModel;         set => SetProperty(ref _labelCodeActionsModel,         value); }
    [DataMember] public string HintCodeActionsModel          { get => _hintCodeActionsModel;          set => SetProperty(ref _hintCodeActionsModel,          value); }
    [DataMember] public string InlineEditModel               { get => _inlineEditModel;               set => SetProperty(ref _inlineEditModel,               value); }
    [DataMember] public string LabelInlineEditModel          { get => _labelInlineEditModel;          set => SetProperty(ref _labelInlineEditModel,          value); }
    [DataMember] public string HintInlineEditModel           { get => _hintInlineEditModel;           set => SetProperty(ref _hintInlineEditModel,           value); }
    [DataMember] public string AgentModel                    { get => _agentModel;                    set => SetProperty(ref _agentModel,                    value); }
    [DataMember] public string LabelAgentModel               { get => _labelAgentModel;               set => SetProperty(ref _labelAgentModel,               value); }
    [DataMember] public string HintAgentModel                { get => _hintAgentModel;                set => SetProperty(ref _hintAgentModel,                value); }
    /// <summary>When <c>false</c> (default), only the chat + embedding models show; the 4 per-role
    /// pickers (agent, code actions, FIM, inline edit) collapse behind the "advanced" toggle.</summary>
    [DataMember] public bool   ShowModelRoles                { get => _showModelRoles;                set => SetProperty(ref _showModelRoles,                value); }
    [DataMember] public string LabelModelRolesAdvanced       { get => _labelModelRolesAdvanced;       set => SetProperty(ref _labelModelRolesAdvanced,       value); }
    [DataMember] public string HintModelRolesAdvanced        { get => _hintModelRolesAdvanced;        set => SetProperty(ref _hintModelRolesAdvanced,        value); }
    /// <summary>When <c>false</c> (default), the Behavior section's timing knobs (command timeout,
    /// agent iterations, task timeouts) are folded away behind the "advanced" toggle.</summary>
    [DataMember] public bool   ShowAdvancedBehavior          { get => _showAdvancedBehavior;          set => SetProperty(ref _showAdvancedBehavior,          value); }
    [DataMember] public string LabelAdvancedBehavior         { get => _labelAdvancedBehavior;         set => SetProperty(ref _labelAdvancedBehavior,         value); }
    [DataMember] public string LabelSectionRag               { get => _labelSectionRag;               set => SetProperty(ref _labelSectionRag,               value); }
    [DataMember] public bool   RagEnabled                    { get => _ragEnabled;                    set => SetProperty(ref _ragEnabled,                    value); }
    [DataMember] public string LabelRagEnabled               { get => _labelRagEnabled;               set => SetProperty(ref _labelRagEnabled,               value); }
    [DataMember] public string HintRagEnabled                { get => _hintRagEnabled;                set => SetProperty(ref _hintRagEnabled,                value); }
    [DataMember] public bool   RagAutoContextEnabled         { get => _ragAutoContextEnabled;         set => SetProperty(ref _ragAutoContextEnabled,         value); }
    [DataMember] public string LabelRagAutoContext           { get => _labelRagAutoContext;           set => SetProperty(ref _labelRagAutoContext,           value); }
    [DataMember] public string HintRagAutoContext            { get => _hintRagAutoContext;            set => SetProperty(ref _hintRagAutoContext,            value); }
    [DataMember] public string RagEmbeddingModel             { get => _ragEmbeddingModel;             set => SetProperty(ref _ragEmbeddingModel,             value); }
    [DataMember] public string LabelRagEmbeddingModel        { get => _labelRagEmbeddingModel;        set => SetProperty(ref _labelRagEmbeddingModel,        value); }
    [DataMember] public string HintRagEmbeddingModel         { get => _hintRagEmbeddingModel;         set => SetProperty(ref _hintRagEmbeddingModel,         value); }
    [DataMember] public string RagTopKText                   { get => _ragTopKText;                   set => SetProperty(ref _ragTopKText,                   value); }
    [DataMember] public string LabelRagTopK                  { get => _labelRagTopK;                  set => SetProperty(ref _labelRagTopK,                  value); }
    [DataMember] public string HintRagTopK                   { get => _hintRagTopK;                   set => SetProperty(ref _hintRagTopK,                   value); }
    [DataMember] public string RagSimilarityThresholdText    { get => _ragSimilarityThresholdText;    set => SetProperty(ref _ragSimilarityThresholdText,    value); }
    [DataMember] public string LabelRagSimilarityThreshold   { get => _labelRagSimilarityThreshold;   set => SetProperty(ref _labelRagSimilarityThreshold,   value); }
    [DataMember] public string HintRagSimilarityThreshold    { get => _hintRagSimilarityThreshold;    set => SetProperty(ref _hintRagSimilarityThreshold,    value); }
    [DataMember] public bool   LspEnabled                    { get => _lspEnabled;                    set => SetProperty(ref _lspEnabled,                    value); }
    [DataMember] public string LabelLspEnabled               { get => _labelLspEnabled;               set => SetProperty(ref _labelLspEnabled,               value); }
    [DataMember] public string HintLspEnabled                { get => _hintLspEnabled;                set => SetProperty(ref _hintLspEnabled,                value); }
    [DataMember] public string BtnTest                  { get => _btnTest;                  set => SetProperty(ref _btnTest,                  value); }
    [DataMember] public string BtnSave                  { get => _btnSave;                  set => SetProperty(ref _btnSave,                  value); }
    [DataMember] public string TooltipRefreshModels     { get => _tooltipRefreshModels;     set => SetProperty(ref _tooltipRefreshModels,     value); }

    // ── Bound properties ───────────────────────────────────────────────────────
    [DataMember] public string SelectedLanguage    { get => _selectedLanguage;    set => SetProperty(ref _selectedLanguage,    value); }
    [DataMember] public string SelectedProvider     { get => _selectedProvider;     set { if (SetProperty(ref _selectedProvider, value)) ApplyProviderCapabilities(); } }
    /// <summary>Visibility of the keep_alive auto-unload settings — only Ollama honours a per-request keep_alive.</summary>
    [DataMember] public bool   ShowKeepAliveSettings { get => _showKeepAliveSettings; set => SetProperty(ref _showKeepAliveSettings, value); }
    /// <summary>Visibility of the Inline Completions (FIM) section — unsupported on generic OpenAI servers.</summary>
    [DataMember] public bool   ShowInlineCompletions { get => _showInlineCompletions; set => SetProperty(ref _showInlineCompletions, value); }
    [DataMember] public string ApiKey               { get => _apiKey;               set => SetProperty(ref _apiKey,               value); }
    [DataMember] public string BaseUrl              { get => _baseUrl;              set => SetProperty(ref _baseUrl,              value); }
    [DataMember] public string SelectedModel        { get => _selectedModel;        set => SetProperty(ref _selectedModel,        value); }
    // h/min/s composite: clamp each sub-field live (setters fire per-keystroke via
    // UpdateSourceTrigger=PropertyChanged) so minutes/seconds can't exceed 59 and hours 99 —
    // the binding pushes the corrected value straight back to the TextBox.
    [DataMember] public string TimeoutHoursText     { get => _timeoutHoursText;     set => SetProperty(ref _timeoutHoursText,     ClampDurationField(value, 99)); }
    [DataMember] public string TimeoutMinutesText   { get => _timeoutMinutesText;   set => SetProperty(ref _timeoutMinutesText,   ClampDurationField(value, 59)); }
    [DataMember] public string TimeoutSecondsText   { get => _timeoutSecondsText;   set => SetProperty(ref _timeoutSecondsText,   ClampDurationField(value, 59)); }
    [DataMember] public bool   ToolBubblesExpanded      { get => _toolBubblesExpanded;      set => SetProperty(ref _toolBubblesExpanded,      value); }
    [DataMember] public bool   SecurityAlertsDisabled       { get => _securityAlertsDisabled;       set => SetProperty(ref _securityAlertsDisabled,       value); }
    [DataMember] public string ContextWindowSizeText        { get => _contextWindowSizeText;        set => SetProperty(ref _contextWindowSizeText,        value); }
    [DataMember] public string ContextWindowKeepTurnsText   { get => _contextWindowKeepTurnsText;   set => SetProperty(ref _contextWindowKeepTurnsText,   value); }
    [DataMember] public string VramBudgetText               { get => _vramBudgetText;               set => SetProperty(ref _vramBudgetText,               value); }
    [DataMember] public string CustomSystemPrompt           { get => _customSystemPrompt;            set => SetProperty(ref _customSystemPrompt,            value); }
    [DataMember] public string PinnedContextFiles           { get => _pinnedContextFiles;            set => SetProperty(ref _pinnedContextFiles,            value); }
    [DataMember] public string PromptTemplates              { get => _promptTemplates;               set => SetProperty(ref _promptTemplates,               value); }
    [DataMember] public string CustomTools                  { get => _customTools;                   set => SetProperty(ref _customTools,                   value); }
    [DataMember] public string PermissionRules              { get => _permissionRules;               set => SetProperty(ref _permissionRules,               value); }

    // ── Editable lists: collections, shared editor chrome, per-list editor state ──
    [DataMember] public ObservableCollection<EditableListRow> PinnedFileRows   { get; } = [];
    [DataMember] public ObservableCollection<EditableListRow> SlashCommandRows { get; } = [];
    [DataMember] public ObservableCollection<EditableListRow> CustomToolRows   { get; } = [];

    [DataMember] public string HintRowEdit      { get => _hintRowEdit;      set => SetProperty(ref _hintRowEdit,      value); }
    [DataMember] public string HintRowDelete    { get => _hintRowDelete;    set => SetProperty(ref _hintRowDelete,    value); }
    [DataMember] public string LabelRowAdvanced { get => _labelRowAdvanced; set => SetProperty(ref _labelRowAdvanced, value); }
    [DataMember] public string BtnRowImport     { get => _btnRowImport;     set => SetProperty(ref _btnRowImport,     value); }

    // Pinned files editor
    [DataMember] public bool   IsEditingPinned    { get => _isEditingPinned;    set => SetProperty(ref _isEditingPinned,    value); }
    [DataMember] public string PinnedEditingTitle { get => _pinnedEditingTitle; set => SetProperty(ref _pinnedEditingTitle, value); }
    [DataMember] public string EditPinnedPath     { get => _editPinnedPath;     set => SetProperty(ref _editPinnedPath,     value); }
    [DataMember] public string EditPinnedError    { get => _editPinnedError;    set => SetProperty(ref _editPinnedError,    value); }
    [DataMember] public string LabelPinnedAddFile { get => _labelPinnedAddFile; set => SetProperty(ref _labelPinnedAddFile, value); }
    [DataMember] public string LabelPinnedPath    { get => _labelPinnedPath;    set => SetProperty(ref _labelPinnedPath,    value); }
    [DataMember] public string LabelPinnedBrowse  { get => _labelPinnedBrowse;  set => SetProperty(ref _labelPinnedBrowse,  value); }
    [DataMember] public bool   SectionPinnedRawExpanded
    {
        get => _sectionPinnedRawExpanded;
        set { if (SetProperty(ref _sectionPinnedRawExpanded, value)) { SectionPinnedRawChevron = value ? "▼" : "▶"; PinnedListViewVisible = !value; PinnedViewLabel = value ? Strings.ViewList : Strings.ViewText; } }
    }
    [DataMember] public string SectionPinnedRawChevron { get => _sectionPinnedRawChevron; set => SetProperty(ref _sectionPinnedRawChevron, value); }
    [DataMember] public AsyncCommand AddPinnedCommand              { get; }
    [DataMember] public AsyncCommand BrowsePinnedCommand           { get; }
    [DataMember] public AsyncCommand SavePinnedCommand             { get; }
    [DataMember] public AsyncCommand CancelEditPinnedCommand       { get; }
    [DataMember] public AsyncCommand ImportPinnedCommand           { get; }
    [DataMember] public AsyncCommand ToggleSectionPinnedRawCommand { get; }

    // Slash commands editor
    [DataMember] public bool   IsEditingSlash    { get => _isEditingSlash;    set => SetProperty(ref _isEditingSlash,    value); }
    [DataMember] public string SlashEditingTitle { get => _slashEditingTitle; set => SetProperty(ref _slashEditingTitle, value); }
    [DataMember] public string EditSlashName     { get => _editSlashName;     set => SetProperty(ref _editSlashName,     value); }
    [DataMember] public string EditSlashText     { get => _editSlashText;     set => SetProperty(ref _editSlashText,     value); }
    [DataMember] public string EditSlashError    { get => _editSlashError;    set => SetProperty(ref _editSlashError,    value); }
    [DataMember] public string LabelSlashAddCmd  { get => _labelSlashAddCmd;  set => SetProperty(ref _labelSlashAddCmd,  value); }
    [DataMember] public string LabelSlashName    { get => _labelSlashName;    set => SetProperty(ref _labelSlashName,    value); }
    [DataMember] public string LabelSlashText    { get => _labelSlashText;    set => SetProperty(ref _labelSlashText,    value); }
    [DataMember] public bool   SectionSlashRawExpanded
    {
        get => _sectionSlashRawExpanded;
        set { if (SetProperty(ref _sectionSlashRawExpanded, value)) { SectionSlashRawChevron = value ? "▼" : "▶"; SlashListViewVisible = !value; SlashViewLabel = value ? Strings.ViewList : Strings.ViewText; } }
    }
    [DataMember] public string SectionSlashRawChevron { get => _sectionSlashRawChevron; set => SetProperty(ref _sectionSlashRawChevron, value); }
    [DataMember] public AsyncCommand AddSlashCommand              { get; }
    [DataMember] public AsyncCommand SaveSlashCommand             { get; }
    [DataMember] public AsyncCommand CancelEditSlashCommand       { get; }
    [DataMember] public AsyncCommand ImportSlashCommand           { get; }
    [DataMember] public AsyncCommand ToggleSectionSlashRawCommand { get; }

    // Custom tools editor
    [DataMember] public bool   IsEditingTool    { get => _isEditingTool;    set => SetProperty(ref _isEditingTool,    value); }
    [DataMember] public string ToolEditingTitle { get => _toolEditingTitle; set => SetProperty(ref _toolEditingTitle, value); }
    [DataMember] public string EditToolName     { get => _editToolName;     set => SetProperty(ref _editToolName,     value); }
    [DataMember] public string EditToolCommand  { get => _editToolCommand;  set => SetProperty(ref _editToolCommand,  value); }
    [DataMember] public string EditToolError    { get => _editToolError;    set => SetProperty(ref _editToolError,    value); }
    [DataMember] public string LabelToolAddTool { get => _labelToolAddTool; set => SetProperty(ref _labelToolAddTool, value); }
    [DataMember] public string LabelToolName    { get => _labelToolName;    set => SetProperty(ref _labelToolName,    value); }
    [DataMember] public string LabelToolCommand { get => _labelToolCommand; set => SetProperty(ref _labelToolCommand, value); }
    [DataMember] public bool   SectionToolRawExpanded
    {
        get => _sectionToolRawExpanded;
        set { if (SetProperty(ref _sectionToolRawExpanded, value)) { SectionToolRawChevron = value ? "▼" : "▶"; ToolListViewVisible = !value; ToolViewLabel = value ? Strings.ViewList : Strings.ViewText; } }
    }
    [DataMember] public string SectionToolRawChevron { get => _sectionToolRawChevron; set => SetProperty(ref _sectionToolRawChevron, value); }
    [DataMember] public AsyncCommand AddToolCommand              { get; }
    [DataMember] public AsyncCommand SaveToolCommand             { get; }
    [DataMember] public AsyncCommand CancelEditToolCommand       { get; }
    [DataMember] public AsyncCommand ImportToolCommand           { get; }
    [DataMember] public AsyncCommand ToggleSectionToolRawCommand { get; }

    // ── List view-mode / count badge / empty-state (see backing-field comment) ───
    [DataMember] public bool   McpListViewVisible    { get => _mcpListViewVisible;    set => SetProperty(ref _mcpListViewVisible,    value); }
    [DataMember] public string McpViewLabel          { get => _mcpViewLabel;          set => SetProperty(ref _mcpViewLabel,          value); }
    [DataMember] public string McpCountText          { get => _mcpCountText;          set => SetProperty(ref _mcpCountText,          value); }
    [DataMember] public bool   McpEmpty              { get => _mcpEmpty;              set => SetProperty(ref _mcpEmpty,              value); }
    [DataMember] public string McpEmptyTitle         { get => _mcpEmptyTitle;         set => SetProperty(ref _mcpEmptyTitle,         value); }
    [DataMember] public bool   PinnedListViewVisible { get => _pinnedListViewVisible; set => SetProperty(ref _pinnedListViewVisible, value); }
    [DataMember] public string PinnedViewLabel       { get => _pinnedViewLabel;       set => SetProperty(ref _pinnedViewLabel,       value); }
    [DataMember] public string PinnedCountText       { get => _pinnedCountText;       set => SetProperty(ref _pinnedCountText,       value); }
    [DataMember] public bool   PinnedEmpty           { get => _pinnedEmpty;           set => SetProperty(ref _pinnedEmpty,           value); }
    [DataMember] public string PinnedEmptyTitle      { get => _pinnedEmptyTitle;      set => SetProperty(ref _pinnedEmptyTitle,      value); }
    [DataMember] public bool   SlashListViewVisible  { get => _slashListViewVisible;  set => SetProperty(ref _slashListViewVisible,  value); }
    [DataMember] public string SlashViewLabel        { get => _slashViewLabel;        set => SetProperty(ref _slashViewLabel,        value); }
    [DataMember] public string SlashCountText        { get => _slashCountText;        set => SetProperty(ref _slashCountText,        value); }
    [DataMember] public bool   SlashEmpty            { get => _slashEmpty;            set => SetProperty(ref _slashEmpty,            value); }
    [DataMember] public string SlashEmptyTitle       { get => _slashEmptyTitle;       set => SetProperty(ref _slashEmptyTitle,       value); }
    [DataMember] public bool   ToolListViewVisible   { get => _toolListViewVisible;   set => SetProperty(ref _toolListViewVisible,   value); }
    [DataMember] public string ToolViewLabel         { get => _toolViewLabel;         set => SetProperty(ref _toolViewLabel,         value); }
    [DataMember] public string ToolCountText         { get => _toolCountText;         set => SetProperty(ref _toolCountText,         value); }
    [DataMember] public bool   ToolEmpty             { get => _toolEmpty;             set => SetProperty(ref _toolEmpty,             value); }
    [DataMember] public string ToolEmptyTitle        { get => _toolEmptyTitle;        set => SetProperty(ref _toolEmptyTitle,        value); }
    [DataMember] public string LabelSectionCommandsTools { get => _labelSectionCommandsTools; set => SetProperty(ref _labelSectionCommandsTools, value); }

    /// <summary>Recomputes the count badge + empty-state flag for every editable list. Called from each
    /// list's Sync*FromRows() choke point, so it stays fresh on add / edit / delete / enable-toggle / import.</summary>
    private void RefreshListMeta()
    {
        McpCountText    = FormatCount(McpServers.Count,      McpServers.Count(r => r.Enabled));
        McpEmpty        = McpServers.Count == 0;
        PinnedCountText = FormatCount(PinnedFileRows.Count,  PinnedFileRows.Count(r => r.Enabled));
        PinnedEmpty     = PinnedFileRows.Count == 0;
        SlashCountText  = FormatCount(SlashCommandRows.Count, SlashCommandRows.Count(r => r.Enabled));
        SlashEmpty      = SlashCommandRows.Count == 0;
        ToolCountText   = FormatCount(CustomToolRows.Count,  CustomToolRows.Count(r => r.Enabled));
        ToolEmpty       = CustomToolRows.Count == 0;
    }

    /// <summary>Badge text: empty when the list is empty (the header pill then hides itself), the total
    /// when all rows are enabled, or "enabled / total" when some are disabled.</summary>
    private static string FormatCount(int total, int enabled) => ListCountBadge.Format(total, enabled);

    /// <summary>Sanitises a duration sub-field (h/min/s box) live as the user types: keeps digits only
    /// and clamps to [0, max]. Empty is preserved so the field can be cleared mid-edit; it resolves to 0
    /// at save time.</summary>
    private static string ClampDurationField(string value, int max) => DurationFields.Clamp(value, max);

    /// <summary>Splits a total-seconds duration into the (hours, min, sec) display strings of an h/min/s
    /// composite — hours plain, minutes/seconds zero-padded, matching the command-timeout boxes.</summary>
    private static (string h, string m, string s) SplitDuration(int totalSeconds) =>
        DurationFields.Split(totalSeconds);

    /// <summary>Recombines the (already-clamped) h/min/s sub-fields of a composite back into total seconds.</summary>
    private static int CombineDuration(string h, string m, string s) =>
        DurationFields.Combine(h, m, s);

    [DataMember] public string OodaTurnThresholdText          { get => _oodaTurnThresholdText;           set => SetProperty(ref _oodaTurnThresholdText,           value); }
    [DataMember] public bool   CompactionEnabled               { get => _compactionEnabled;               set => SetProperty(ref _compactionEnabled,               value); }
    [DataMember] public string CompactionTimeoutHoursText      { get => _compactionTimeoutHoursText;      set => SetProperty(ref _compactionTimeoutHoursText,      ClampDurationField(value, 99)); }
    [DataMember] public string CompactionTimeoutMinutesText    { get => _compactionTimeoutMinutesText;    set => SetProperty(ref _compactionTimeoutMinutesText,    ClampDurationField(value, 59)); }
    [DataMember] public string CompactionTimeoutSecondsText    { get => _compactionTimeoutSecondsText;    set => SetProperty(ref _compactionTimeoutSecondsText,    ClampDurationField(value, 59)); }
    [DataMember] public string KvCacheAnchorMessagesText       { get => _kvCacheAnchorMessagesText;       set => SetProperty(ref _kvCacheAnchorMessagesText,       value); }
    [DataMember] public string ConnectionStatus { get => _connectionStatus; set => SetProperty(ref _connectionStatus, value); }
    [DataMember] public bool   IsConnectionOk   { get => _isConnectionOk;   set => SetProperty(ref _isConnectionOk,   value); }
    [DataMember] public string SaveStatus       { get => _saveStatus;       set => SetProperty(ref _saveStatus,       value); }
    [DataMember] public bool   IsDarkTheme      { get => _isDarkTheme;      set => SetProperty(ref _isDarkTheme,      value); }
    [DataMember] public string TextForeground   { get => _textForeground;   set => SetProperty(ref _textForeground,   value); }

    // ── Commandes ──────────────────────────────────────────────────────────────
    [DataMember] public AsyncCommand SaveCommand           { get; }
    [DataMember] public AsyncCommand TestConnectionCommand { get; }
    [DataMember] public AsyncCommand RefreshModelsCommand  { get; }

    // ── Handlers ───────────────────────────────────────────────────────────────

    private async Task SaveAsync(object? _, CancellationToken ct)
    {
        string url = string.Empty, model = string.Empty, customPrompt = string.Empty, pinnedContextFiles = string.Empty, promptTemplates = string.Empty, customTools = string.Empty, permissionRules = string.Empty;
        string selectedProviderName = string.Empty, apiKey = string.Empty;
        string th = string.Empty, tm = string.Empty, ts = string.Empty;
        string ctxSizeText = string.Empty, ctxKeepText = string.Empty, oodaThreshText = string.Empty, vramBudgetText = string.Empty;
        string kvAnchorText = string.Empty, selectedLangName = string.Empty;
        string selectedInlineModeName = string.Empty, inlineModel = string.Empty, codeActionsModel = string.Empty, inlineEditModel = string.Empty, agentModel = string.Empty;
        string ragEmbeddingModel = string.Empty, ragTopKText = string.Empty, ragSimilarityThresholdText = string.Empty;
        string agentMaxIterationsText = string.Empty;
        // h/min/s composites recombined into total seconds inside the VM-context capture below.
        int quickTimeoutSec = 0, normalTimeoutSec = 0, deepTimeoutSec = 0, compactTimeoutSec = 0;
        string modelIdleTimeoutText = string.Empty;
        string mcpServersJson = string.Empty;
        bool toolExpanded = false, secAlertsDisabled = false, compactionEnabled = true, inlineEnabled = true, ragEnabled = true, ragAutoContextEnabled = true, smartFixEnabled = true, agentModeEnabled = false, lspEnabled = false, modelAutoUnload = true, personaAutoSwitch = true, mcpEnabled = false;
        await RunOnVMContextAsync(() =>
        {
            url                  = BaseUrl.Trim();
            selectedProviderName = SelectedProvider;
            apiKey               = ApiKey.Trim();
            model                = SelectedModel.Trim();
            th                   = TimeoutHoursText.Trim();
            tm                   = TimeoutMinutesText.Trim();
            ts                   = TimeoutSecondsText.Trim();
            toolExpanded         = ToolBubblesExpanded;
            secAlertsDisabled    = SecurityAlertsDisabled;
            smartFixEnabled      = SmartFixEnabled;
            agentModeEnabled         = AgentModeEnabled;
            agentMaxIterationsText   = AgentMaxIterationsText.Trim();
            quickTimeoutSec          = CombineDuration(QuickTimeoutHoursText,  QuickTimeoutMinutesText,  QuickTimeoutSecondsText);
            normalTimeoutSec         = CombineDuration(NormalTimeoutHoursText, NormalTimeoutMinutesText, NormalTimeoutSecondsText);
            deepTimeoutSec           = CombineDuration(DeepTimeoutHoursText,   DeepTimeoutMinutesText,   DeepTimeoutSecondsText);
            ctxSizeText          = ContextWindowSizeText.Trim();
            ctxKeepText          = ContextWindowKeepTurnsText.Trim();
            vramBudgetText       = VramBudgetText.Trim();
            customPrompt         = CustomSystemPrompt;
            SyncPinnedTextFromRows();   // rows are the source of truth → refresh the text mirrors
            SyncSlashTextFromRows();
            SyncToolTextFromRows();
            pinnedContextFiles   = PinnedContextFiles;
            promptTemplates      = PromptTemplates;
            customTools          = CustomTools;
            permissionRules      = PermissionRules;
            personaAutoSwitch    = PersonaAutoSwitch;
            oodaThreshText       = OodaTurnThresholdText.Trim();
            compactionEnabled    = CompactionEnabled;
            compactTimeoutSec    = CombineDuration(CompactionTimeoutHoursText, CompactionTimeoutMinutesText, CompactionTimeoutSecondsText);
            kvAnchorText         = KvCacheAnchorMessagesText.Trim();
            selectedLangName     = SelectedLanguage;
            selectedInlineModeName = SelectedInlineMode;
            inlineEnabled          = InlineCompletionEnabled;
            inlineModel            = InlineCompletionModel;
            codeActionsModel       = CodeActionsModel;
            inlineEditModel        = InlineEditModel;
            agentModel             = AgentModel;
            ragEnabled             = RagEnabled;
            ragAutoContextEnabled  = RagAutoContextEnabled;
            ragEmbeddingModel      = RagEmbeddingModel;
            ragTopKText                 = RagTopKText.Trim();
            ragSimilarityThresholdText  = RagSimilarityThresholdText.Trim();
            lspEnabled             = LspEnabled;
            mcpEnabled             = McpEnabled;
            SyncJsonFromRows();          // rows are the source of truth → refresh the JSON mirror
            mcpServersJson         = McpServersJson;
            modelAutoUnload        = ModelAutoUnloadEnabled;
            modelIdleTimeoutText   = ModelIdleTimeoutText.Trim();
        });
        var h       = int.TryParse(th, out var hv) ? Math.Clamp(hv,  0, 99) : 0;
        var m       = int.TryParse(tm, out var mv) ? Math.Clamp(mv,  0, 59) : 30;
        var s       = int.TryParse(ts, out var sv) ? Math.Clamp(sv,  0, 59) : 0;
        var totalSec = h * 3600 + m * 60 + s;

        // Resolve language code from display name (index 0 = auto = "").
        var langCode = LanguageOptions.FirstOrDefault(l => l.Name == selectedLangName).Code ?? string.Empty;
        var inlineModeCode = InlineModeOptions.FirstOrDefault(m => m.Name == selectedInlineModeName).Code ?? "Default";

        var providerCode = ProviderOptions.FirstOrDefault(p => p.Name == selectedProviderName).Code
                           ?? Services.InferenceProviderFactory.Ollama;

        _config.Language              = langCode;
        _config.Provider              = providerCode;
        _config.ApiKey                = apiKey;
        _config.BaseUrl               = url;
        _config.DefaultModel          = model;
        _config.CommandTimeoutSeconds = totalSec < 1 ? 1 : totalSec;
        _config.ToolBubblesExpanded      = toolExpanded;
        _config.SecurityAlertsDisabled   = secAlertsDisabled;
        _config.SmartFixEnabled          = smartFixEnabled;
        _config.AgentModeEnabled         = agentModeEnabled;
        _config.AgentMaxIterations       = int.TryParse(agentMaxIterationsText, out var ami)  ? Math.Max(0, ami)        : 20;
        _config.QuickTimeoutSeconds      = quickTimeoutSec  > 0 ? Math.Clamp(quickTimeoutSec,  10, 3600) : 120;
        _config.NormalTimeoutSeconds     = normalTimeoutSec > 0 ? Math.Clamp(normalTimeoutSec, 10, 3600) : 300;
        _config.DeepTimeoutSeconds       = deepTimeoutSec   > 0 ? Math.Clamp(deepTimeoutSec,   10, 3600) : 600;
        _config.ContextWindowSize        = int.TryParse(ctxSizeText, out var cs) ? Math.Max(0, cs) : 0;
        _config.ContextWindowKeepTurns   = int.TryParse(ctxKeepText, out var ck) ? Math.Max(1, ck) : 4;
        _config.VramBudgetGb             = double.TryParse(vramBudgetText, System.Globalization.NumberStyles.Float,
                                               System.Globalization.CultureInfo.CurrentCulture, out var vb) && vb > 0
                                               ? Math.Round(vb, 1) : 0;
        _config.CustomSystemPrompt        = customPrompt;
        _config.PinnedContextFiles        = pinnedContextFiles;
        _config.PromptTemplates           = promptTemplates;
        _config.CustomTools               = customTools;
        _config.PermissionRules           = permissionRules;
        _config.PersonaAutoSwitch         = personaAutoSwitch;
        _config.OodaTurnThreshold         = int.TryParse(oodaThreshText, out var ot) ? Math.Max(0, ot) : 10;
        _config.CompactionEnabled         = compactionEnabled;
        _config.CompactionTimeoutSeconds  = compactTimeoutSec > 0 ? Math.Clamp(compactTimeoutSec, 10, 300) : 45;
        _config.KvCacheAnchorMessages     = int.TryParse(kvAnchorText, out var kv) ? Math.Clamp(kv, 0, 20) : 3;
        _config.InlineCompletionMode      = inlineModeCode;
        _config.InlineCompletionEnabled   = inlineEnabled;
        _config.InlineCompletionModel     = inlineModel.Trim();
        _config.CodeActionsModel          = codeActionsModel.Trim();
        _config.InlineEditModel           = inlineEditModel.Trim();
        _config.AgentModel                = agentModel.Trim();
        _config.RagEnabled                = ragEnabled;
        _config.RagAutoContextEnabled     = ragAutoContextEnabled;
        _config.RagEmbeddingModel         = ragEmbeddingModel.Trim();
        _config.RagTopK                   = int.TryParse(ragTopKText, out var rk) ? Math.Clamp(rk, 1, 20) : 5;
        _config.RagSimilarityThreshold    = float.TryParse(ragSimilarityThresholdText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var rst) ? Math.Clamp(rst, 0f, 1f) : 0.20f;
        _config.LspEnabled                = lspEnabled;
        _config.McpEnabled                = mcpEnabled;
        _config.McpServersJson            = mcpServersJson;
        _config.ModelAutoUnloadEnabled    = modelAutoUnload;
        _config.ModelIdleTimeoutMinutes   = int.TryParse(modelIdleTimeoutText, out var mit) ? Math.Max(1, mit) : 10;
        _config.Save();

        // Reconnect MCP servers from the freshly-saved config and report status.
        await _mcp.RefreshAsync();
        await RunOnVMContextAsync(() => { McpStatusText = BuildMcpStatus(); RefreshRowStatuses(); });

        // Apply the culture override and refresh all labels immediately.
        Strings.ApplyLanguage(langCode);
        await RunOnVMContextAsync(() => { ApplyLabels(); SaveStatus = "✓"; });
    }

    // ── MCP server list management ───────────────────────────────────────────

    /// <summary>Rebuilds the <see cref="McpServers"/> rows from the current saved config JSON.</summary>
    private void BuildRowsFromConfig() => BuildRowsFromConfigJson(_config.McpServersJson);

    /// <summary>Parses <paramref name="json"/> into rows (used at startup and by "Import JSON").</summary>
    private void BuildRowsFromConfigJson(string json)
    {
        McpServers.Clear();
        foreach (var s in Services.Mcp.McpServerConfig.Parse(json))
        {
            var row = new McpServerRow
            {
                ServerName = s.Name,
                Command  = s.Command ?? string.Empty,
                ArgsText = string.Join(" ", s.Args),
                EnvText  = string.Join("\n", s.Env.Select(kv => $"{kv.Key}={kv.Value}")),
                Url      = s.Url ?? string.Empty,
                HeadersText = s.Headers is null ? string.Empty : string.Join("\n", s.Headers.Select(kv => $"{kv.Key}={kv.Value}")),
                Enabled  = s.Enabled,
            };
            WireRow(row);
            McpServers.Add(row);
        }
        RefreshRowStatuses();
        ApplyRowTheme();
        SyncJsonFromRows();
    }

    /// <summary>
    /// Refreshes the themed brushes used by the server-row template. These live on the root VM (not
    /// on each <see cref="McpServerRow"/>) because RemoteUI does not propagate a property change made
    /// on a collection item after it has been added to the bound collection — the row would freeze at
    /// its construction-time colour (e.g. the dark default, invisible on a light theme). The template
    /// binds them off the root data context via <c>ElementName=root</c>, so they always track the
    /// current theme. The server name itself uses the existing <see cref="TextForeground"/>.
    /// </summary>
    private void ApplyRowTheme()
    {
        var dark = IsDarkTheme;
        McpSubtleForeground = dark ? "#9D9D9D" : "#6E6E6E";
        McpRowBackground    = dark ? "#252526" : "#F5F5F5";
        McpRowBorder        = dark ? "#3F3F46" : "#CCCCCC";
        McpEditGlyph        = dark ? "#CCCCCC" : "#444444";
        ThemeHoverBg        = dark ? "#3F3F46" : "#D6D6E0";   // ghost-button hover, theme-aware (cf. main window)
        EditPanelBg         = dark ? "#2D2D30" : "#ECECEC";   // inline add/edit form surface
        SecondaryButtonBg   = dark ? "#3F3F46" : "#E0E0E0";   // neutral filled button (Cancel / Import)
        SecondaryButtonHover= dark ? "#505050" : "#D0D0D0";
        SecondaryButtonFg   = dark ? "#FFFFFF" : "#1E1E1E";   // white text only reads on the dark surface
        UpdateTabState();   // inactive tab labels follow the theme's subtle foreground
    }

    /// <summary>Switches the visible settings tab and force-expands its sections so content shows
    /// immediately (the per-section chevrons still collapse within the tab).</summary>
    private void SelectTab(string tab)
    {
        _activeSettingsTab = tab;
        switch (tab)
        {
            case "behavior": SectionBehaviorExpanded = true; SectionInlineExpanded   = true; SectionPersonaExpanded = true; break;
            case "context":  SectionRagExpanded      = true; SectionContextExpanded  = true; break;
            case "tools":    SectionMcpExpanded       = true; break;
        }
        UpdateTabState();
    }

    /// <summary>Recomputes per-tab visibility + active styling (accent for the selected tab,
    /// theme-subtle for the rest). Called on tab select and whenever the theme changes.</summary>
    private void UpdateTabState()
    {
        TabConnectionVisible = _activeSettingsTab == "connection";
        TabBehaviorVisible   = _activeSettingsTab == "behavior";
        TabContextVisible    = _activeSettingsTab == "context";
        TabToolsVisible      = _activeSettingsTab == "tools";

        const string none = "#00000000";
        var inactive = _mcpSubtleForeground;   // theme-aware subtle, set in ApplyRowTheme
        TabConnectionFg = TabConnectionVisible ? TabAccent : inactive;
        TabBehaviorFg   = TabBehaviorVisible   ? TabAccent : inactive;
        TabContextFg    = TabContextVisible    ? TabAccent : inactive;
        TabToolsFg      = TabToolsVisible      ? TabAccent : inactive;
        TabConnectionUnderline = TabConnectionVisible ? TabAccent : none;
        TabBehaviorUnderline   = TabBehaviorVisible   ? TabAccent : none;
        TabContextUnderline    = TabContextVisible    ? TabAccent : none;
        TabToolsUnderline      = TabToolsVisible      ? TabAccent : none;
    }

    /// <summary>Hooks a row's Edit/Delete callbacks and re-syncs JSON when its enabled flag flips.</summary>
    private void WireRow(McpServerRow row)
    {
        row.OnEdit      = EditServer;
        row.OnDelete    = DeleteServer;
        row.OnAuthorize = AuthorizeServerAsync;
        row.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(McpServerRow.Enabled))
            {
                if (!row.Enabled) row.StatusText = Strings.McpServerDisabled;
                PersistMcpServers();
            }
        };
    }

    /// <summary>Applies the latest connection status from <see cref="_mcp"/> onto each row.</summary>
    private void RefreshRowStatuses()
    {
        var status = _mcp.Status.ToDictionary(s => s.Name, StringComparer.Ordinal);
        foreach (var row in McpServers)
        {
            if (!row.Enabled)
            {
                row.StatusText = Strings.McpServerDisabled;
                row.AuthRequired = false;
            }
            else if (status.TryGetValue(row.ServerName, out var st))
            {
                row.AuthRequired = st.AuthRequired;
                row.StatusText = st.Connected ? $"✓ {st.ToolCount}"
                               : st.AuthRequired ? Strings.McpAuthRequired
                               : $"✗ {st.Error}";
            }
            else
            {
                row.StatusText = string.Empty;
                row.AuthRequired = false;
            }
        }
    }

    /// <summary>Runs the interactive OAuth flow (browser) for an HTTP server row, then refreshes statuses.</summary>
    private async Task AuthorizeServerAsync(McpServerRow row)
    {
        try
        {
            await _mcp.AuthorizeAsync(row.ServerName);
            row.StatusText = string.Empty;   // RefreshRowStatuses will set the post-connect status
        }
        catch (Exception ex)
        {
            row.AuthRequired = true;
            row.StatusText = $"✗ {ex.Message}";
        }
        RefreshRowStatuses();
    }

    private void BeginAddServer()
    {
        _editingOriginalName = null;
        EditServerName = string.Empty; EditServerCommand = string.Empty;
        EditServerArgs = string.Empty; EditServerEnv = string.Empty;
        EditServerIsHttp = false; EditServerUrl = string.Empty; EditServerHeaders = string.Empty;
        EditServerError = string.Empty;
        EditingTitle = Strings.McpAddTitle;
        IsEditingServer = true;
    }

    private void EditServer(McpServerRow row)
    {
        _editingOriginalName = row.ServerName;
        EditServerName = row.ServerName; EditServerCommand = row.Command;
        EditServerArgs = row.ArgsText; EditServerEnv = row.EnvText;
        EditServerIsHttp = !string.IsNullOrWhiteSpace(row.Url);
        EditServerUrl = row.Url; EditServerHeaders = row.HeadersText;
        EditServerError = string.Empty;
        EditingTitle = Strings.McpEditTitle(row.ServerName);
        IsEditingServer = true;
    }

    private void DeleteServer(McpServerRow row)
    {
        McpServers.Remove(row);
        if (_editingOriginalName == row.ServerName) IsEditingServer = false;
        PersistMcpServers();
    }

    /// <summary>Validates the editor form and upserts the row into <see cref="McpServers"/>.</summary>
    private void CommitServer()
    {
        var name = (EditServerName ?? string.Empty).Trim();
        var cmd  = (EditServerCommand ?? string.Empty).Trim();
        var url  = (EditServerUrl ?? string.Empty).Trim();
        if (EditServerIsHttp)
        {
            if (name.Length == 0 || url.Length == 0) { EditServerError = Strings.McpValidationNameUrl; return; }
        }
        else if (name.Length == 0 || cmd.Length == 0) { EditServerError = Strings.McpValidationNameCommand; return; }
        if (McpServers.Any(r => string.Equals(r.ServerName, name, StringComparison.Ordinal) && r.ServerName != _editingOriginalName))
        { EditServerError = Strings.McpValidationDuplicate; return; }

        // Build the updated definition list from the current rows, apply the add/edit, then rebuild
        // the rows fresh from it. We never mutate a McpServerRow after it has been added to the bound
        // collection: in the Extensibility RemoteUI a property set on an item after the initial
        // snapshot is not propagated to the template, so a freshly typed name/summary would stay
        // invisible until the next reload. Rebuilding re-emits a complete snapshot.
        var defs = McpServers.Select(ToConfig).ToList();
        var enabled = _editingOriginalName is null
            ? true
            : McpServers.FirstOrDefault(r => r.ServerName == _editingOriginalName)?.Enabled ?? true;
        var newDef = EditServerIsHttp
            ? new Services.Mcp.McpServerConfig(name, null, [], new Dictionary<string, string>(),
                                               Url: url, Headers: ParseEnv(EditServerHeaders), Enabled: enabled)
            : new Services.Mcp.McpServerConfig(name, cmd, ParseArgs(EditServerArgs), ParseEnv(EditServerEnv), Enabled: enabled);

        var idx = _editingOriginalName is null ? -1 : defs.FindIndex(d => d.Name == _editingOriginalName);
        if (idx >= 0) defs[idx] = newDef; else defs.Add(newDef);

        IsEditingServer = false;
        BuildRowsFromConfigJson(Services.Mcp.McpServerConfig.Serialize(defs));
        PersistMcpServers();
    }

    /// <summary>Snapshots a row's editable fields into a serialisable server definition. An HTTP row
    /// (non-empty <see cref="McpServerRow.Url"/>) round-trips its url/headers; otherwise it's stdio.</summary>
    private static Services.Mcp.McpServerConfig ToConfig(McpServerRow r) =>
        string.IsNullOrWhiteSpace(r.Url)
            ? new(r.ServerName.Trim(), r.Command.Trim(), ParseArgs(r.ArgsText), ParseEnv(r.EnvText), Enabled: r.Enabled)
            : new(r.ServerName.Trim(), null, [], new Dictionary<string, string>(), Url: r.Url.Trim(), Headers: ParseEnv(r.HeadersText), Enabled: r.Enabled);

    /// <summary>Serialises the current rows back into <see cref="McpServersJson"/> (the persisted form).</summary>
    private void SyncJsonFromRows()
    {
        McpServersJson = Services.Mcp.McpServerConfig.Serialize(McpServers.Select(ToConfig).ToList());
        RefreshListMeta();
    }

    /// <summary>
    /// Persists the current server rows to disk immediately (add / edit / delete / enable-toggle),
    /// then reconnects MCP in the background. The list no longer relies on the global Settings "Save"
    /// button, so servers configured here survive an extension restart even if the window is closed
    /// without an explicit save.
    /// </summary>
    private void PersistMcpServers()
    {
        SyncJsonFromRows();
        _config.McpServersJson = McpServersJson;
        _config.Save();
        _ = ReconnectMcpAsync();
    }

    /// <summary>Reconnects the MCP servers from the freshly-saved config and refreshes row statuses.</summary>
    private async Task ReconnectMcpAsync()
    {
        try
        {
            await _mcp.RefreshAsync();
            await RunOnVMContextAsync(() => { McpStatusText = BuildMcpStatus(); RefreshRowStatuses(); });
        }
        catch { /* status stays as last known; never break the editor on a reconnect error */ }
    }

    private static IReadOnlyList<string> ParseArgs(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    private static IReadOnlyDictionary<string, string> ParseEnv(string? text)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text)) return d;
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            d[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return d;
    }

    /// <summary>Renders the MCP connection result (per-server tool count or error) for the settings UI.</summary>
    private string BuildMcpStatus()
    {
        if (!_config.McpEnabled) return string.Empty;
        var status = _mcp.Status;
        if (status.Count == 0) return Strings.McpNoServers;
        return string.Join("\n", status.Select(s =>
            s.Connected ? $"✓ {s.Name} — {s.ToolCount}" : $"✗ {s.Name} — {s.Error}"));
    }

    // ── Editable list management (pinned files / slash commands / custom tools) ──
    //
    // All three lists persist as newline-separated text in their existing config string.
    // A leading '#' marks a DISABLED entry; the consumers skip those lines. Rows are never
    // mutated after being added to the bound collection (RemoteUI snapshot caveat, see
    // EditableListRow) — every add/edit/delete rebuilds the collection from the freshly
    // serialised text. Changes persist immediately (no global Save needed), like MCP.

    private static IEnumerable<string> SplitListLines(string? text) =>
        (text ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Splits a stored line into (enabled, payload), stripping a leading '#' marker.</summary>
    private static (bool Enabled, string Payload) SplitEntry(string line)
    {
        var enabled = !line.StartsWith('#');
        return (enabled, enabled ? line : line.TrimStart('#').Trim());
    }

    private void WireListRow(EditableListRow row, Action<EditableListRow> edit, Action<EditableListRow> delete, Action persist)
    {
        row.OnEdit   = edit;
        row.OnDelete = delete;
        row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(EditableListRow.Enabled)) persist(); };
    }

    // ── Pinned context files ────────────────────────────────────────────────
    private void BuildPinnedRows() => BuildPinnedRowsFrom(_config.PinnedContextFiles);

    private void BuildPinnedRowsFrom(string text)
    {
        PinnedFileRows.Clear();
        foreach (var line in SplitListLines(text))
        {
            var (enabled, path) = SplitEntry(line);
            if (path.Length == 0) continue;
            var row = new EditableListRow { Enabled = enabled, Field1 = path, Label = System.IO.Path.GetFileName(path), Summary = path };
            WireListRow(row, EditPinned, DeletePinned, PersistPinned);
            PinnedFileRows.Add(row);
        }
        SyncPinnedTextFromRows();
    }

    private void SyncPinnedTextFromRows()
    {
        PinnedContextFiles = string.Join("\n", PinnedFileRows.Select(r => (r.Enabled ? "" : "#") + r.Field1));
        RefreshListMeta();
    }

    private void PersistPinned()
    {
        SyncPinnedTextFromRows();
        _config.PinnedContextFiles = PinnedContextFiles;
        _config.Save();
    }

    private void BeginAddPinned()
    {
        _editingPinnedOriginal = null;
        EditPinnedPath = string.Empty; EditPinnedError = string.Empty;
        PinnedEditingTitle = Strings.PinnedAddTitle;
        IsEditingPinned = true;
    }

    private void EditPinned(EditableListRow row)
    {
        _editingPinnedOriginal = row.Field1;
        EditPinnedPath = row.Field1; EditPinnedError = string.Empty;
        PinnedEditingTitle = Strings.RowEditTitle(row.Label);
        IsEditingPinned = true;
    }

    private void DeletePinned(EditableListRow row)
    {
        PinnedFileRows.Remove(row);
        if (_editingPinnedOriginal == row.Field1) IsEditingPinned = false;
        PersistPinned();
    }

    private void CommitPinned()
    {
        var path = (EditPinnedPath ?? string.Empty).Trim();
        if (path.Length == 0) { EditPinnedError = Strings.PinnedValidationPath; return; }
        if (PinnedFileRows.Any(r => string.Equals(r.Field1, path, StringComparison.OrdinalIgnoreCase) && r.Field1 != _editingPinnedOriginal))
        { EditPinnedError = Strings.PinnedValidationDuplicate; return; }

        var entries = PinnedFileRows.Select(r => (r.Enabled, r.Field1)).ToList();
        var enabled = _editingPinnedOriginal is null
            ? true
            : PinnedFileRows.FirstOrDefault(r => r.Field1 == _editingPinnedOriginal)?.Enabled ?? true;
        var idx = _editingPinnedOriginal is null ? -1 : entries.FindIndex(e => e.Field1 == _editingPinnedOriginal);
        if (idx >= 0) entries[idx] = (enabled, path); else entries.Add((true, path));

        IsEditingPinned = false;
        BuildPinnedRowsFrom(string.Join("\n", entries.Select(e => (e.Enabled ? "" : "#") + e.Field1)));
        PersistPinned();
    }

    /// <summary>Opens a native file picker and drops the chosen path into the editor's path box.</summary>
    private async Task BrowsePinnedAsync()
    {
        try
        {
            var picked = await ShowOpenFileDialogAsync();
            if (!string.IsNullOrEmpty(picked))
            {
                EditPinnedPath  = picked!;
                EditPinnedError = string.Empty;
            }
        }
        catch (Exception ex)
        {
            // Surface the failure instead of letting the click look like a no-op.
            EditPinnedError = ex.Message;
        }
    }

    /// <summary>
    /// Shows a Win32 open-file dialog on a dedicated STA thread (RemoteUI has no picker).
    /// The dialog is parented to a hidden topmost owner window so it surfaces in front of the
    /// floating (topmost) settings tool window instead of opening behind it.
    /// </summary>
    private static Task<string?> ShowOpenFileDialogAsync()
    {
        var tcs = new TaskCompletionSource<string?>();
        var thread = new System.Threading.Thread(() =>
        {
            System.Windows.Window? owner = null;
            try
            {
                owner = new System.Windows.Window
                {
                    Width         = 1,
                    Height        = 1,
                    Left          = -32000,
                    Top           = -32000,
                    WindowStyle   = System.Windows.WindowStyle.None,
                    ShowInTaskbar = false,
                    Topmost       = true,
                };
                owner.Show();
                owner.Activate();

                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title  = Strings.PinnedPickerTitle,
                    Filter = "All Files (*.*)|*.*"
                };
                var ok = dlg.ShowDialog(owner) == true;
                tcs.SetResult(ok ? dlg.FileName : null);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
            finally
            {
                owner?.Close();
            }
        });
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    // ── Custom slash commands (/name=text) ──────────────────────────────────
    private void BuildSlashRows() => BuildSlashRowsFrom(_config.PromptTemplates);

    private void BuildSlashRowsFrom(string text)
    {
        SlashCommandRows.Clear();
        foreach (var line in SplitListLines(text))
        {
            var (enabled, body) = SplitEntry(line);
            var eq = body.IndexOf('=');
            if (eq <= 0) continue;
            var name = body[..eq].Trim();
            var tmpl = body[(eq + 1)..].Trim();
            if (name.Length == 0 || tmpl.Length == 0) continue;
            var row = new EditableListRow { Enabled = enabled, Field1 = name, Field2 = tmpl, Label = name, Summary = tmpl };
            WireListRow(row, EditSlash, DeleteSlash, PersistSlash);
            SlashCommandRows.Add(row);
        }
        SyncSlashTextFromRows();
    }

    private void SyncSlashTextFromRows()
    {
        PromptTemplates = string.Join("\n", SlashCommandRows.Select(r => (r.Enabled ? "" : "#") + r.Field1 + "=" + r.Field2));
        RefreshListMeta();
    }

    private void PersistSlash()
    {
        SyncSlashTextFromRows();
        _config.PromptTemplates = PromptTemplates;
        _config.Save();
    }

    private void BeginAddSlash()
    {
        _editingSlashOriginal = null;
        EditSlashName = string.Empty; EditSlashText = string.Empty; EditSlashError = string.Empty;
        SlashEditingTitle = Strings.SlashAddTitle;
        IsEditingSlash = true;
    }

    private void EditSlash(EditableListRow row)
    {
        _editingSlashOriginal = row.Field1;
        EditSlashName = row.Field1; EditSlashText = row.Field2; EditSlashError = string.Empty;
        SlashEditingTitle = Strings.RowEditTitle(row.Label);
        IsEditingSlash = true;
    }

    private void DeleteSlash(EditableListRow row)
    {
        SlashCommandRows.Remove(row);
        if (_editingSlashOriginal == row.Field1) IsEditingSlash = false;
        PersistSlash();
    }

    private void CommitSlash()
    {
        var name = (EditSlashName ?? string.Empty).Trim();
        var tmpl = (EditSlashText ?? string.Empty).Trim();
        if (name.Length > 0 && !name.StartsWith('/')) name = "/" + name;   // be forgiving
        if (name.Length <= 1 || tmpl.Length == 0) { EditSlashError = Strings.SlashValidationNameText; return; }
        if (SlashCommandRows.Any(r => string.Equals(r.Field1, name, StringComparison.OrdinalIgnoreCase) && r.Field1 != _editingSlashOriginal))
        { EditSlashError = Strings.SlashValidationDuplicate; return; }

        var entries = SlashCommandRows.Select(r => (r.Enabled, r.Field1, r.Field2)).ToList();
        var enabled = _editingSlashOriginal is null
            ? true
            : SlashCommandRows.FirstOrDefault(r => r.Field1 == _editingSlashOriginal)?.Enabled ?? true;
        var idx = _editingSlashOriginal is null ? -1 : entries.FindIndex(e => e.Field1 == _editingSlashOriginal);
        if (idx >= 0) entries[idx] = (enabled, name, tmpl); else entries.Add((true, name, tmpl));

        IsEditingSlash = false;
        BuildSlashRowsFrom(string.Join("\n", entries.Select(e => (e.Enabled ? "" : "#") + e.Field1 + "=" + e.Field2)));
        PersistSlash();
    }

    // ── Custom agent tools (name=powershell_command) ────────────────────────
    private void BuildToolRows() => BuildToolRowsFrom(_config.CustomTools);

    private void BuildToolRowsFrom(string text)
    {
        CustomToolRows.Clear();
        foreach (var line in SplitListLines(text))
        {
            var (enabled, body) = SplitEntry(line);
            var eq = body.IndexOf('=');
            if (eq <= 0) continue;
            var name = body[..eq].Trim();
            var cmd  = body[(eq + 1)..].Trim();
            if (name.Length == 0 || cmd.Length == 0) continue;
            var row = new EditableListRow { Enabled = enabled, Field1 = name, Field2 = cmd, Label = name, Summary = cmd };
            WireListRow(row, EditTool, DeleteTool, PersistTool);
            CustomToolRows.Add(row);
        }
        SyncToolTextFromRows();
    }

    private void SyncToolTextFromRows()
    {
        CustomTools = string.Join("\n", CustomToolRows.Select(r => (r.Enabled ? "" : "#") + r.Field1 + "=" + r.Field2));
        RefreshListMeta();
    }

    private void PersistTool()
    {
        SyncToolTextFromRows();
        _config.CustomTools = CustomTools;
        _config.Save();
    }

    private void BeginAddTool()
    {
        _editingToolOriginal = null;
        EditToolName = string.Empty; EditToolCommand = string.Empty; EditToolError = string.Empty;
        ToolEditingTitle = Strings.ToolAddTitle;
        IsEditingTool = true;
    }

    private void EditTool(EditableListRow row)
    {
        _editingToolOriginal = row.Field1;
        EditToolName = row.Field1; EditToolCommand = row.Field2; EditToolError = string.Empty;
        ToolEditingTitle = Strings.RowEditTitle(row.Label);
        IsEditingTool = true;
    }

    private void DeleteTool(EditableListRow row)
    {
        CustomToolRows.Remove(row);
        if (_editingToolOriginal == row.Field1) IsEditingTool = false;
        PersistTool();
    }

    private void CommitTool()
    {
        var name = (EditToolName ?? string.Empty).Trim().ToLowerInvariant().Replace(' ', '_');
        var cmd  = (EditToolCommand ?? string.Empty).Trim();
        if (name.Length == 0 || cmd.Length == 0) { EditToolError = Strings.ToolValidationNameCommand; return; }
        if (CustomToolRows.Any(r => string.Equals(r.Field1, name, StringComparison.OrdinalIgnoreCase) && r.Field1 != _editingToolOriginal))
        { EditToolError = Strings.ToolValidationDuplicate; return; }

        var entries = CustomToolRows.Select(r => (r.Enabled, r.Field1, r.Field2)).ToList();
        var enabled = _editingToolOriginal is null
            ? true
            : CustomToolRows.FirstOrDefault(r => r.Field1 == _editingToolOriginal)?.Enabled ?? true;
        var idx = _editingToolOriginal is null ? -1 : entries.FindIndex(e => e.Field1 == _editingToolOriginal);
        if (idx >= 0) entries[idx] = (enabled, name, cmd); else entries.Add((true, name, cmd));

        IsEditingTool = false;
        BuildToolRowsFrom(string.Join("\n", entries.Select(e => (e.Enabled ? "" : "#") + e.Field1 + "=" + e.Field2)));
        PersistTool();
    }

    private async Task TestConnectionAsync(object? _, CancellationToken ct)
    {
        string url = string.Empty, apiKey = string.Empty;
        await RunOnVMContextAsync(() =>
        {
            SaveStatus       = string.Empty;
            ConnectionStatus = Strings.StatusConnecting;
            url              = BaseUrl.Trim();
            apiKey           = ApiKey?.Trim() ?? string.Empty;
        });

        // Auto-detect the backend from the URL (Ollama / LM Studio / OpenAI-compatible). A successful
        // probe also confirms reachability, so it replaces the provider-specific connection check.
        var detected     = await ProviderProbe.DetectAsync(url, apiKey, ct);
        var ok           = detected is not null;
        var detectedName = detected is not null
            ? ProviderOptions.FirstOrDefault(p => p.Code == detected).Name
            : null;

        await RunOnVMContextAsync(() =>
        {
            IsConnectionOk   = ok;
            ConnectionStatus = ok ? $"{Strings.StatusConnected} — {detectedName}" : Strings.StatusUnreachable;
            // Auto-select the detected backend; the user can still change it before saving.
            if (!string.IsNullOrEmpty(detectedName)) SelectedProvider = detectedName!;
        });

        if (ok)
            await RefreshModelsAsync(url, ct);
    }

    // Called with url=null from constructor (uses saved config URL),
    // or with explicit url after a successful Test.
    private async Task RefreshModelsAsync(object? urlOrNull, CancellationToken ct)
    {
        try
        {
            var url = urlOrNull as string;

            // List models from the provider currently selected in the dropdown, not the startup
            // singleton (_client) — so switching or auto-detecting the backend populates the right
            // models before the user saves + reloads.
            string providerCode = _config.Provider, apiKey = _config.ApiKey;
            await RunOnVMContextAsync(() =>
            {
                providerCode = ProviderOptions.FirstOrDefault(p => p.Name == SelectedProvider).Code ?? _config.Provider;
                apiKey       = ApiKey?.Trim() ?? string.Empty;
            });
            var client = InferenceProviderFactory.Create(new InferpalConfig
            {
                Provider = providerCode, BaseUrl = url ?? _config.BaseUrl, ApiKey = apiKey,
            });
            var models = await client.ListModelsAsync(ct, url);

            await RunOnVMContextAsync(() =>
            {
                var current = SelectedModel;

                // Update in place to avoid Clear() which resets SelectedItem to null via TwoWay binding write-back.
                for (int i = AvailableModels.Count - 1; i >= 0; i--)
                    if (!models.Contains(AvailableModels[i]))
                        AvailableModels.RemoveAt(i);
                foreach (var m in models)
                    if (!AvailableModels.Contains(m))
                        AvailableModels.Add(m);

                if (AvailableModels.Count > 0)
                    SelectedModel = AvailableModels.Contains(current) ? current : AvailableModels[0];

                // AvailableOptionalModels = [""] + AvailableModels (empty = use chat model)
                if (AvailableOptionalModels.Count == 0 || AvailableOptionalModels[0] != string.Empty)
                    AvailableOptionalModels.Insert(0, string.Empty);
                for (int i = AvailableOptionalModels.Count - 1; i >= 1; i--)
                    if (!models.Contains(AvailableOptionalModels[i]))
                        AvailableOptionalModels.RemoveAt(i);
                foreach (var m in models)
                    if (!AvailableOptionalModels.Contains(m))
                        AvailableOptionalModels.Add(m);

                // Keep configured models visible in the dropdown even when Ollama doesn't list them
                // (e.g. model not currently loaded but still installed). Resetting them here would
                // silently wipe the config the next time the user clicks Save.
                if (!string.IsNullOrEmpty(CodeActionsModel) && !AvailableOptionalModels.Contains(CodeActionsModel))
                    AvailableOptionalModels.Add(CodeActionsModel);
                if (!string.IsNullOrEmpty(InlineCompletionModel) && !AvailableOptionalModels.Contains(InlineCompletionModel))
                    AvailableOptionalModels.Add(InlineCompletionModel);
                if (!string.IsNullOrEmpty(InlineEditModel) && !AvailableOptionalModels.Contains(InlineEditModel))
                    AvailableOptionalModels.Add(InlineEditModel);
                if (!string.IsNullOrEmpty(AgentModel) && !AvailableOptionalModels.Contains(AgentModel))
                    AvailableOptionalModels.Add(AgentModel);

                // AvailableEmbeddingModels — only models matching embedding keywords
                var embeddingModels = models.Where(IsEmbeddingModel).ToList();
                for (int i = AvailableEmbeddingModels.Count - 1; i >= 0; i--)
                    if (!embeddingModels.Contains(AvailableEmbeddingModels[i]) && AvailableEmbeddingModels[i] != RagEmbeddingModel)
                        AvailableEmbeddingModels.RemoveAt(i);
                foreach (var m in embeddingModels)
                    if (!AvailableEmbeddingModels.Contains(m))
                        AvailableEmbeddingModels.Add(m);
                // Keep configured embedding model visible even if not recognized by keyword filter.
                if (!string.IsNullOrEmpty(RagEmbeddingModel) && !AvailableEmbeddingModels.Contains(RagEmbeddingModel))
                    AvailableEmbeddingModels.Add(RagEmbeddingModel);
                // Auto-select first available when nothing is configured yet.
                if (string.IsNullOrEmpty(RagEmbeddingModel) && AvailableEmbeddingModels.Count > 0)
                    RagEmbeddingModel = AvailableEmbeddingModels[0];
            });
        }
        catch { }
    }
}
