using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using Inferpal.Localization;
using Inferpal.Services;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Shell;
using Microsoft.VisualStudio.Extensibility.UI;

namespace Inferpal.ToolWindow;

internal partial class InferpalToolWindowData
{
    #region Chrome: the header's model button and menus, the composer's modes and ring, the welcome screen

    // ── Header: one button names the model, its server and what is in graphics memory ──────────────────────────
    /// <summary>The last heartbeat's answer: <c>null</c> until the first check (and while a retry waits for one).</summary>
    private bool?   _connected;
    /// <summary>The status the server refused the check with, when it answered at all (a wrong key): it is running.</summary>
    private string? _refusal;

    private string _modelName      = string.Empty;
    private string _modelMeta      = string.Empty;
    private string _modelDotColor  = "#808080";
    private string _modelButtonTip = string.Empty;
    private bool   _isModelMenuOpen;
    private bool   _isMoreMenuOpen;
    private string _modelMenuNote  = string.Empty;
    private bool   _hasModelMenuNote;

    [DataMember] public string ModelName      { get => _modelName;      set => SetProperty(ref _modelName,      value); }
    /// <summary>"LM Studio · VRAM 14 GB", or the server and why it does not answer.</summary>
    [DataMember] public string ModelMeta      { get => _modelMeta;      set => SetProperty(ref _modelMeta,      value); }
    [DataMember] public string ModelDotColor  { get => _modelDotColor;  set => SetProperty(ref _modelDotColor,  value); }
    [DataMember] public string ModelButtonTip { get => _modelButtonTip; set => SetProperty(ref _modelButtonTip, value); }
    [DataMember] public bool   IsModelMenuOpen { get => _isModelMenuOpen; set { SetProperty(ref _isModelMenuOpen, value); IsAnyMenuOpen = value || _isMoreMenuOpen; } }
    [DataMember] public bool   IsMoreMenuOpen  { get => _isMoreMenuOpen;  set { SetProperty(ref _isMoreMenuOpen,  value); IsAnyMenuOpen = value || _isModelMenuOpen; } }
    private bool _isAnyMenuOpen;
    /// <summary>A header menu is open: a click anywhere else on the conversation closes it.</summary>
    [DataMember] public bool   IsAnyMenuOpen   { get => _isAnyMenuOpen;   set => SetProperty(ref _isAnyMenuOpen,   value); }
    [DataMember] public string ModelMenuNote   { get => _modelMenuNote;   set => SetProperty(ref _modelMenuNote,   value); }
    [DataMember] public bool   HasModelMenuNote { get => _hasModelMenuNote; set => SetProperty(ref _hasModelMenuNote, value); }
    [DataMember] public ObservableCollection<ModelChoiceItem> ModelChoices { get; } = [];

    private string _labelNewConversation = string.Empty, _labelConversations = string.Empty, _labelMore = string.Empty,
                   _labelMenuSearch = string.Empty, _labelMenuExport = string.Empty, _labelMenuXray = string.Empty,
                   _labelMenuSettings = string.Empty, _labelMenuStepMode = string.Empty, _labelDocumentation = string.Empty;
    [DataMember] public string LabelNewConversation { get => _labelNewConversation; set => SetProperty(ref _labelNewConversation, value); }
    [DataMember] public string LabelConversations   { get => _labelConversations;   set => SetProperty(ref _labelConversations,   value); }
    [DataMember] public string LabelMore            { get => _labelMore;            set => SetProperty(ref _labelMore,            value); }
    [DataMember] public string LabelMenuSearch      { get => _labelMenuSearch;      set => SetProperty(ref _labelMenuSearch,      value); }
    [DataMember] public string LabelMenuExport      { get => _labelMenuExport;      set => SetProperty(ref _labelMenuExport,      value); }
    [DataMember] public string LabelMenuXray        { get => _labelMenuXray;        set => SetProperty(ref _labelMenuXray,        value); }
    [DataMember] public string LabelMenuSettings    { get => _labelMenuSettings;    set => SetProperty(ref _labelMenuSettings,    value); }
    [DataMember] public string LabelMenuStepMode    { get => _labelMenuStepMode;    set => SetProperty(ref _labelMenuStepMode,    value); }
    [DataMember] public string LabelDocumentation   { get => _labelDocumentation;   set => SetProperty(ref _labelDocumentation,   value); }

    private string _labelDropToAttach = string.Empty, _labelDropHint = string.Empty;
    /// <summary>The overlay of a file dragged over the window (shown by the in-process <c>ChatDropTarget</c>).</summary>
    [DataMember] public string LabelDropToAttach    { get => _labelDropToAttach;    set => SetProperty(ref _labelDropToAttach,    value); }
    [DataMember] public string LabelDropHint        { get => _labelDropHint;        set => SetProperty(ref _labelDropHint,        value); }

    [DataMember] public AsyncCommand ToggleModelMenuCommand { get; private set; } = null!;
    [DataMember] public AsyncCommand ToggleMoreMenuCommand  { get; private set; } = null!;
    [DataMember] public AsyncCommand CloseMenusCommand      { get; private set; } = null!;
    [DataMember] public AsyncCommand MenuSearchCommand      { get; private set; } = null!;
    [DataMember] public AsyncCommand MenuExportCommand      { get; private set; } = null!;
    [DataMember] public AsyncCommand MenuXrayCommand        { get; private set; } = null!;
    [DataMember] public AsyncCommand MenuSettingsCommand    { get; private set; } = null!;
    [DataMember] public AsyncCommand MenuStepModeCommand    { get; private set; } = null!;
    /// <summary>The header's "?": the documentation of this version, in the browser.</summary>
    [DataMember] public AsyncCommand OpenDocumentationCommand { get; private set; } = null!;

    // ── Composer: Chat / Agent / Plan, the context ring ─────────────────────────────────────────────────────────
    private string _chatMode = "agent";
    private string _labelModeChat = string.Empty, _labelModeAgent = string.Empty, _labelModePlan = string.Empty,
                   _tipModeChat = string.Empty, _tipModeAgent = string.Empty, _tipModePlan = string.Empty,
                   _composerPlaceholder = string.Empty, _tooltipAttach = string.Empty;
    /// <summary><c>chat</c>, <c>agent</c> or <c>plan</c>: the segment the switch shows selected.</summary>
    [DataMember] public string ChatMode            { get => _chatMode;            set => SetProperty(ref _chatMode,            value); }
    [DataMember] public string LabelModeChat       { get => _labelModeChat;       set => SetProperty(ref _labelModeChat,       value); }
    [DataMember] public string LabelModeAgent      { get => _labelModeAgent;      set => SetProperty(ref _labelModeAgent,      value); }
    [DataMember] public string LabelModePlan       { get => _labelModePlan;       set => SetProperty(ref _labelModePlan,       value); }
    [DataMember] public string TipModeChat         { get => _tipModeChat;         set => SetProperty(ref _tipModeChat,         value); }
    [DataMember] public string TipModeAgent        { get => _tipModeAgent;        set => SetProperty(ref _tipModeAgent,        value); }
    [DataMember] public string TipModePlan         { get => _tipModePlan;         set => SetProperty(ref _tipModePlan,         value); }
    [DataMember] public string ComposerPlaceholder { get => _composerPlaceholder; set => SetProperty(ref _composerPlaceholder, value); }
    [DataMember] public string TooltipAttach       { get => _tooltipAttach;       set => SetProperty(ref _tooltipAttach,       value); }
    [DataMember] public AsyncCommand SetModeCommand { get; private set; } = null!;

    private bool _isCompact;
    /// <summary>The conversation is drawn compact (Language and appearance › Density): less space around its turns.</summary>
    [DataMember] public bool IsCompact { get => _isCompact; set => SetProperty(ref _isCompact, value); }

    private string _contextRingPath  = "M 9,2";
    private string _contextRingText  = string.Empty;
    private string _contextRingColor = "#A3A3AD";
    private string _contextRingTip   = string.Empty;
    /// <summary>The ring's arc, as path data on an 18×18 canvas: from the top, clockwise, the share of the window used.</summary>
    [DataMember] public string ContextRingPath  { get => _contextRingPath;  set => SetProperty(ref _contextRingPath,  value); }
    [DataMember] public string ContextRingText  { get => _contextRingText;  set => SetProperty(ref _contextRingText,  value); }
    [DataMember] public string ContextRingColor { get => _contextRingColor; set => SetProperty(ref _contextRingColor, value); }
    [DataMember] public string ContextRingTip   { get => _contextRingTip;   set => SetProperty(ref _contextRingTip,   value); }

    // ── Welcome: what to work on — the open file's actions, the last build, the keys ────────────────────────────
    private string _welcomeTitle = string.Empty, _welcomeLine = string.Empty, _welcomeForFile = string.Empty,
                   _welcomeExplainTitle = string.Empty, _welcomeExplainDesc = string.Empty,
                   _welcomeTestsTitle = string.Empty, _welcomeTestsDesc = string.Empty,
                   _welcomeUsagesTitle = string.Empty, _welcomeUsagesDesc = string.Empty,
                   _welcomeBuildFailed = string.Empty, _welcomeFixThem = string.Empty,
                   _hintAttach = string.Empty, _hintCommands = string.Empty, _hintNewLine = string.Empty;
    private bool   _hasActiveFile;
    private int    _buildErrorCount;
    [DataMember] public string WelcomeTitle        { get => _welcomeTitle;        set => SetProperty(ref _welcomeTitle,        value); }
    /// <summary>"Devstral on LM Studio. Your code stays on this machine."</summary>
    [DataMember] public string WelcomeLine         { get => _welcomeLine;         set => SetProperty(ref _welcomeLine,         value); }
    [DataMember] public bool   HasActiveFile       { get => _hasActiveFile;       set => SetProperty(ref _hasActiveFile,       value); }
    [DataMember] public string WelcomeForFile      { get => _welcomeForFile;      set => SetProperty(ref _welcomeForFile,      value); }
    [DataMember] public string WelcomeExplainTitle { get => _welcomeExplainTitle; set => SetProperty(ref _welcomeExplainTitle, value); }
    [DataMember] public string WelcomeExplainDesc  { get => _welcomeExplainDesc;  set => SetProperty(ref _welcomeExplainDesc,  value); }
    [DataMember] public string WelcomeTestsTitle   { get => _welcomeTestsTitle;   set => SetProperty(ref _welcomeTestsTitle,   value); }
    [DataMember] public string WelcomeTestsDesc    { get => _welcomeTestsDesc;    set => SetProperty(ref _welcomeTestsDesc,    value); }
    [DataMember] public string WelcomeUsagesTitle  { get => _welcomeUsagesTitle;  set => SetProperty(ref _welcomeUsagesTitle,  value); }
    [DataMember] public string WelcomeUsagesDesc   { get => _welcomeUsagesDesc;   set => SetProperty(ref _welcomeUsagesDesc,   value); }
    /// <summary>"The last build failed — 2 error(s)": the build banner, as the welcome screen draws it.</summary>
    [DataMember] public string WelcomeBuildFailed  { get => _welcomeBuildFailed;  set => SetProperty(ref _welcomeBuildFailed,  value); }
    [DataMember] public string WelcomeFixThem      { get => _welcomeFixThem;      set => SetProperty(ref _welcomeFixThem,      value); }
    [DataMember] public string HintAttach          { get => _hintAttach;          set => SetProperty(ref _hintAttach,          value); }
    [DataMember] public string HintCommands        { get => _hintCommands;        set => SetProperty(ref _hintCommands,        value); }
    [DataMember] public string HintNewLine         { get => _hintNewLine;         set => SetProperty(ref _hintNewLine,         value); }
    [DataMember] public AsyncCommand RunUsagesCommand { get; private set; } = null!;

    // ── The redesign's tokens, for what the items draw from the window (ElementName=root) ───────────────────────
    private string _themeSurface = "#232328", _themeChip = "#2E2B3D", _themeAccent = "#7C4DFF", _themeAccentText = "#B39DFF",
                   _themeAccentLine = "#4B3D80", _themeOk = "#4CC27A", _themeErrorText = "#F2B8B5", _themeErrorBg = "#2A1E1F",
                   _themeErrorLine = "#5C3030";
    [DataMember] public string ThemeSurface    { get => _themeSurface;    set => SetProperty(ref _themeSurface,    value); }
    [DataMember] public string ThemeChip       { get => _themeChip;       set => SetProperty(ref _themeChip,       value); }
    [DataMember] public string ThemeAccent     { get => _themeAccent;     set => SetProperty(ref _themeAccent,     value); }
    [DataMember] public string ThemeAccentText { get => _themeAccentText; set => SetProperty(ref _themeAccentText, value); }
    [DataMember] public string ThemeAccentLine { get => _themeAccentLine; set => SetProperty(ref _themeAccentLine, value); }
    [DataMember] public string ThemeOk         { get => _themeOk;         set => SetProperty(ref _themeOk,         value); }
    [DataMember] public string ThemeErrorText  { get => _themeErrorText;  set => SetProperty(ref _themeErrorText,  value); }
    [DataMember] public string ThemeErrorBg    { get => _themeErrorBg;    set => SetProperty(ref _themeErrorBg,    value); }
    [DataMember] public string ThemeErrorLine  { get => _themeErrorLine;  set => SetProperty(ref _themeErrorLine,  value); }
    private string _themeChipLine = "#2E2B3D", _themeOnAccent = "#FFFFFF";
    [DataMember] public string ThemeChipLine   { get => _themeChipLine;   set => SetProperty(ref _themeChipLine,   value); }
    [DataMember] public string ThemeOnAccent   { get => _themeOnAccent;   set => SetProperty(ref _themeOnAccent,   value); }
    private string _themeWarn = "#E2B341";
    /// <summary>A wait past its measured read: a warning, not a failure.</summary>
    [DataMember] public string ThemeWarn       { get => _themeWarn;       set => SetProperty(ref _themeWarn,       value); }

    /// <summary>A cut turn's "Open time limits": the settings window, on the page that holds them — the open window shows
    /// it, else the one built next opens on it.</summary>
    private void OpenTimeLimits() => _ = OpenSettingsPageAsync(
        Services.Presentation.CutCard.TimeLimitsPage, Services.Presentation.CutCard.TimeLimitsSection);

    private async Task OpenSettingsPageAsync(string page, string section)
    {
        if (_contextHolder.ShowSettingsPage is { } show) show(page, section);
        else _contextHolder.SettingsPage = (page, section);
        try { await _vs.Shell().ShowToolWindowAsync<InferpalSettingsToolWindow>(activate: true, CancellationToken.None); }
        catch (Exception ex)
        {
            // Not opened: a later plain open must not land on this page.
            _contextHolder.SettingsPage = null;
            Diagnostics.Swallow("Chat.OpenSettingsPage", ex);
        }
    }

    /// <summary>Builds the chrome's commands; called by the constructor.</summary>
    private void InitChrome()
    {
        ToggleModelMenuCommand = new AsyncCommand((_, ct) => ToggleModelMenuAsync(ct));
        ToggleMoreMenuCommand  = new AsyncCommand((_, _) => RunOnVMContextAsync(() =>
        {
            var open = !IsMoreMenuOpen;
            IsModelMenuOpen = false;
            IsMoreMenuOpen  = open;
        }));
        CloseMenusCommand      = new AsyncCommand((_, _) => RunOnVMContextAsync(CloseMenus));
        MenuSearchCommand      = new AsyncCommand(async (p, ct) => { await RunOnVMContextAsync(CloseMenus); await ToggleSearchAsync(p, ct); });
        MenuExportCommand      = new AsyncCommand(async (p, ct) => { await RunOnVMContextAsync(CloseMenus); await ExportAsync(p, ct); });
        MenuXrayCommand        = new AsyncCommand(async (p, ct) => { await RunOnVMContextAsync(CloseMenus); await ToggleXrayPanelAsync(p, ct); });
        MenuSettingsCommand    = new AsyncCommand(async (_, ct) =>
        {
            await RunOnVMContextAsync(CloseMenus);
            try { await _vs.Shell().ShowToolWindowAsync<InferpalSettingsToolWindow>(activate: true, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { Diagnostics.Swallow("Chat.OpenSettings", ex); }
        });
        MenuStepModeCommand    = new AsyncCommand(async (_, _) => { await RunOnVMContextAsync(CloseMenus); await ToggleStepModeAsync(); });
        OpenDocumentationCommand = new AsyncCommand(async (_, _) =>
        {
            await RunOnVMContextAsync(CloseMenus);
            var docs = DocsLinks.Index;
            // Not opened, the address is said instead.
            if (!WebLink.Open(docs)) await ShowInfoAsync(Strings.HelpDocumentation(docs));
        });
        SetModeCommand         = new AsyncCommand((p, _) => SetModeAsync(p as string));
        RunUsagesCommand       = new AsyncCommand((_, ct) =>
            _activeFilePath is { Length: > 0 } file
                ? RunSuggestionAsync(Strings.WelcomeUsagesPrompt(Path.GetFileName(file)), ct)
                : Task.CompletedTask);
        DenyApprovalCommand    = new AsyncCommand((_, _) => RunOnVMContextAsync(() => _pendingApproval?.Answer(ApprovalDecision.Deny)));
        _contextHolder.InlineApproval = AskInlineAsync;
        _contextHolder.TurnRuns       = () => IsLoading;
        _contextHolder.InlineInput    = AskInputInlineAsync;
        _contextHolder.InputReceipt   = receipt => Post(() => ShowInputReceipt(receipt));
        _contextHolder.Notice         = text => Post(() => InsertThemed(ChatMessageItem.NoticeMsg(text)));
        _isCompact             = _config.IsCompactChat;
        // A save from the settings window can change the density: the open conversation follows at once.
        _config.Saved         += () => Post(() => IsCompact = _config.IsCompactChat);
        // The pinned chips follow the setting: the settings window pins and disables files too.
        _config.Saved         += () => Post(LoadPinnedFilesFromConfig);
    }

    /// <summary>The chrome's labels, in the interface language; part of <see cref="ApplyLabels"/>.</summary>
    private void ApplyChromeLabels()
    {
        LabelNewConversation = Strings.ChatNewConversation;
        LabelConversations   = Strings.ChatConversations;
        LabelMore            = Strings.ChatMore;
        LabelMenuSearch      = Strings.ChatMenuSearch;
        LabelMenuExport      = Strings.ChatMenuExport;
        LabelMenuXray        = Strings.ChatMenuXray;
        LabelMenuSettings    = Strings.ChatMenuSettings;
        LabelMenuStepMode    = Strings.ChatMenuStepMode;
        LabelDocumentation   = Strings.ChatDocumentation;
        LabelDropToAttach    = Strings.DropToAttach;
        LabelDropHint        = Strings.DropHint;
        LabelModeChat        = Strings.ModeChat;
        LabelModeAgent       = Strings.ModeAgent;
        LabelModePlan        = Strings.ModePlan;
        TipModeChat          = Strings.ModeChatTip;
        TipModeAgent         = Strings.ModeAgentTip;
        TipModePlan          = Strings.ModePlanTip;
        RefreshComposerPlaceholder();
        QueueTitle           = Strings.QueueTitle;
        QueueRemoveLabel     = Strings.QueueRemove;
        TooltipAttach        = Strings.ChatAttach;
        WelcomeTitle         = Strings.WelcomeTitle;
        WelcomeExplainTitle  = Strings.WelcomeExplainFile;
        WelcomeExplainDesc   = Strings.WelcomeExplainFileDesc;
        WelcomeTestsTitle    = Strings.WelcomeTestsFile;
        WelcomeTestsDesc     = Strings.WelcomeTestsFileDesc;
        WelcomeUsagesTitle   = Strings.WelcomeUsagesFile;
        WelcomeUsagesDesc    = Strings.WelcomeUsagesFileDesc;
        WelcomeFixThem       = Strings.WelcomeFixThem;
        HintAttach           = Strings.WelcomeHintAttach;
        HintCommands         = Strings.WelcomeHintCommands;
        HintNewLine          = Strings.WelcomeHintNewLine;
        ChatMode             = CurrentMode();
        RefreshModesSegment();
        _ = RefreshModesQuietlyAsync();
        RefreshModelButton();
        RefreshWelcome();
        UpdateContextBudget();
    }

    private void CloseMenus()
    {
        IsModelMenuOpen = false;
        IsMoreMenuOpen  = false;
        IsModeMenuOpen  = false;
    }

    /// <summary>The model button, from what is known now: the model, the server, the last check, the VRAM line.</summary>
    private void RefreshModelButton()
    {
        var server = Services.Inference.InferenceProviderFactory.DisplayName(_config.Provider);
        var p      = ThemePalette.For(_isDark, _isHighContrast);
        ModelName  = string.IsNullOrEmpty(ActiveModelLabel) ? "—" : ActiveModelLabel;
        // A server that refused the check (a wrong API key) is running: its own sentence names it.
        var state = _connected == false ? (_refusal is null ? Strings.StatusUnreachable : Strings.StatusRefused(_refusal)) : string.Empty;
        var vram  = _connected == true && HasVramStatus ? "VRAM " + VramStatus : string.Empty;
        ModelMeta = string.Join(" · ", new[] { server, state, vram }.Where(s => s.Length > 0));
        ModelDotColor = _connected switch
        {
            true                    => p.Ok,
            false when _refusal is not null => p.Warn,
            false                   => p.ErrorText,
            null                    => p.SubtleText,
        };
        ModelButtonTip = Strings.ChatModelButton(string.Join(" · ", new[] { ModelName, ModelMeta }.Where(s => s.Length > 0)));
        RefreshWelcomeLine();
    }

    /// <summary>The last check, in the badge's words — what the diagnostics bundle names as the connection state.</summary>
    private string ConnectionBadgeText() => _connected switch
    {
        true  => Strings.StatusConnected,
        false => _refusal is null ? Strings.StatusUnreachable : Strings.StatusRefused(_refusal),
        null  => "● …",
    };

    /// <summary>The model, its server, and where the code goes: "stays on this machine" only when every role in use runs
    /// here (<see cref="Services.Inference.RoleServers.OffMachineHosts"/>), the other machines named otherwise.</summary>
    /// <remarks>⚠ Judged on the settings FILE: the other editor can move a role to another machine, and this window's
    /// copy follows only its own saves. An unreadable file falls back to that copy.</remarks>
    private void RefreshWelcomeLine()
    {
        var servers   = Config.InferpalConfig.LoadOwnCopyToFollow() ?? _config;
        var elsewhere = string.Join(", ", Services.Inference.RoleServers.OffMachineHosts(servers));
        var server    = Services.Inference.InferenceProviderFactory.DisplayName(_config.Provider);
        WelcomeLine = string.IsNullOrEmpty(ActiveModelLabel)
            ? (elsewhere.Length == 0 ? Strings.WelcomeLocal : Strings.WelcomeRemote(elsewhere))
            : elsewhere.Length == 0 ? Strings.WelcomeLine(ActiveModelLabel, server)
            : Strings.WelcomeLineRemote(ActiveModelLabel, server, elsewhere);
    }

    /// <summary>The welcome screen, from the open file and the last build.</summary>
    private void RefreshWelcome()
    {
        var file      = string.IsNullOrEmpty(_activeFilePath) ? null : Path.GetFileName(_activeFilePath);
        HasActiveFile = file is not null;
        WelcomeForFile     = file is null ? string.Empty : Strings.WelcomeForFile(file);
        // No count is not zero errors: the Error List was not filled when the build ended. "0 error(s)" under "the
        // build failed" contradicts itself.
        WelcomeBuildFailed = _buildErrorCount > 0 ? Strings.WelcomeBuildFailed(_buildErrorCount) : Strings.WelcomeBuildFailedUncounted;
        RefreshWelcomeLine();
    }

    private async Task ToggleModelMenuAsync(CancellationToken ct)
    {
        var open = false;
        await RunOnVMContextAsync(() =>
        {
            open = !IsModelMenuOpen;
            IsMoreMenuOpen  = false;
            IsModelMenuOpen = open;
            if (!open) return;
            // The current model at once; the server's list when it answers.
            ModelChoices.Clear();
            ModelChoices.Add(new ModelChoiceItem(ActiveModelLabel, current: true, PickModelAsync));
            ModelMenuNote    = string.Empty;
            HasModelMenuNote = false;
        });
        if (!open) return;

        ModelsByKind listed;
        try
        {
            listed = await _client.ListModelsByKindAsync(ct);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            Diagnostics.Swallow("Chat.ModelMenu", ex);
            listed = ModelsByKind.None;
        }
        await RunOnVMContextAsync(() =>
        {
            if (!IsModelMenuOpen) return;
            ModelChoices.Clear();
            // A question goes to the model picked here: an embedding or decision model would answer none.
            foreach (var name in listed.Chat)
                ModelChoices.Add(new ModelChoiceItem(name, current: name == ActiveModelLabel, PickModelAsync));
            if (ModelChoices.Count == 0)
            {
                // Nothing listed: the server is unreachable, or holds no model — never an empty menu that says nothing.
                ModelMenuNote    = Strings.ChatNoModelListed;
                HasModelMenuNote = true;
            }
        });
    }

    /// <summary>A model picked in the header becomes the chat model, as <c>/model</c> makes it — without a bubble: the
    /// button names it.</summary>
    private async Task PickModelAsync(string model)
    {
        await RunOnVMContextAsync(CloseMenus);
        if (string.IsNullOrEmpty(model) || model == _config.DefaultModel) return;
        _config.DefaultModel = model;
        try
        {
            _config.Save();
        }
        catch (Exception ex)
        {
            // The choice holds for this session; unsaved, it reverts at the next start — said, not silent.
            Diagnostics.Swallow("Chat.PickModel", ex);
            await ShowInfoAsync(Strings.SettingsSaveFailed(ex.Message));
        }
        await RunOnVMContextAsync(() => ActiveModelLabel = model);
    }

    /// <summary>The composer's switch: "found" while a mode found for the workspace holds the conversation (the
    /// fourth segment), else Chat, Agent or Plan.</summary>
    private string CurrentMode() => _heldMode is not null ? "found" : _planMode ? "plan" : _agentMode ? "agent" : "chat";

    /// <summary>
    /// Chat, Agent or Plan, as the composer's switch sets them: agent mode is on for Agent and Plan, plan mode for Plan
    /// only. Silent — the switch shows the mode; the toggles of the slash commands keep their sentence.
    /// </summary>
    private async Task SetModeAsync(string? mode)
    {
        if (mode is not ("chat" or "agent" or "plan")) return;
        var agent = mode != "chat";
        var plan  = mode == "plan";
        await RunOnVMContextAsync(() =>
        {
            // Chat, Agent and Plan leave a held mode: one selector.
            var leftMode = LeaveHeldMode();
            ChatMode = mode;
            if (IsPlanMode != plan || leftMode)
            {
                IsPlanMode = plan;
                RefreshSystemPrompt();
            }
            IsAgentMode    = agent;
        });
        if (_config.AgentModeEnabled == agent) return;
        _config.AgentModeEnabled = agent;
        try
        {
            _config.Save();
        }
        catch (Exception ex)
        {
            // The switch applies to this session; unsaved, it reverts at the next start — said, not silent.
            Diagnostics.Swallow("Chat.SetMode", ex);
            await ShowInfoAsync(Strings.SettingsSaveFailed(ex.Message));
        }
    }

    /// <summary>The ring's arc and words, from the gauge (<see cref="UpdateContextBudget"/>).</summary>
    private void UpdateContextRing(Services.Presentation.ContextBudget? budget)
    {
        if (budget is null)
        {
            ContextRingPath = "M 9,2";
            ContextRingText = string.Empty;
            ContextRingTip  = string.Empty;
            return;
        }
        ContextRingPath  = Services.Presentation.ContextBudgetGauge.RingArc(budget.FillPercent);
        ContextRingText  = budget.FillPercent.ToString("F0", CultureInfo.CurrentCulture) + " %";
        // Below half the window the ring is the accent; past it, the gauge's warning colours.
        ContextRingColor = budget.FillPercent < 50 ? ThemePalette.For(_isDark, _isHighContrast).AccentText : budget.Color;
        ContextRingTip   = Strings.ContextRingTip(
                               (int)Math.Round(budget.FillPercent),
                               ContextWindowInUse.ToString("N0", CultureInfo.CurrentCulture))
                           + (HasTokenInfo ? "\n" + TokenInfo : string.Empty);
    }

    // ── A turn as the conversation draws it: its header line, its run line, its approval cards ─────────────────
    /// <summary>The running turn's header line ("model · working · step 2"); <c>null</c> between turns.</summary>
    private ChatMessageItem? _turnHead;
    /// <summary>The running turn's run line, from its first step; <c>null</c> until a tool runs.</summary>
    private ChatMessageItem? _runLine;
    /// <summary>The approval card waiting for an answer: Enter allows once, Esc denies.</summary>
    private ChatMessageItem? _pendingApproval;

    [DataMember] public AsyncCommand DenyApprovalCommand { get; private set; } = null!;

    /// <summary>"working · step N" on the header line, once the turn moves again.</summary>
    private void TurnWorking()
    {
        if (_turnHead is not null && IsLoading)
            _turnHead.TurnState = Strings.TurnWorking((_runLine?.Steps.Count ?? 0) + 1);
    }

    /// <summary>
    /// An approval asked during a turn, as a card in the conversation (<see cref="VsContextHolder.InlineApproval"/>);
    /// <c>null</c> when no turn is running — a card in a conversation nobody is watching would hold the tool, so the
    /// dialog asks instead.
    /// </summary>
    private async Task<ApprovalDecision?> AskInlineAsync(
        Services.Presentation.ApprovalCardModel card, Func<CancellationToken, Task<ApprovalDecision?>>? openDiff,
        CancellationToken ct)
    {
        using var answered = new CancellationTokenSource();
        var answeredToken  = answered.Token;
        ChatMessageItem? item = null;
        await RunOnVMContextAsync(() =>
        {
            if (!IsLoading) return;
            ChatMessageItem? created = null;
            created = ChatMessageItem.ApprovalMsg(card,
                openDiff is null ? null : () => _ = OpenCardDiffAsync(created!, openDiff, answeredToken));
            InsertCard(created);
            _pendingApproval = created;
            RefreshComposerPlaceholder();
            if (_turnHead is not null) _turnHead.TurnState = Strings.TurnWaiting;
            ScrollToBottom();
            item = created;
        });
        if (item is null) return null;

        // A card left unanswered while its author is elsewhere says so outside the conversation: the taskbar flashes.
        _ = DrawAttentionAfterAsync(answeredToken);

        try
        {
            // A stopped run retires its card: it freezes, and the tool's wait ends (fail closed).
            using (ct.Register(() => Post(item.Retire)))
            {
                // VSTHRD003 as in RunPendingTurnAsync: the card's TCS completes asynchronously
                // (RunContinuationsAsynchronously) in this out-of-process host — no JTF main thread to deadlock against.
#pragma warning disable VSTHRD003
                var decision = await item.Decision;
#pragma warning restore VSTHRD003
                ct.ThrowIfCancellationRequested();
                return decision;
            }
        }
        finally
        {
            await answered.CancelAsync();   // a dialog opened from the card closes with it
            await RunOnVMContextAsync(() =>
            {
                if (ReferenceEquals(_pendingApproval, item)) _pendingApproval = null;
                RefreshComposerPlaceholder();
                TurnWorking();
            });
        }
    }

    /// <summary>What the keys do in the EMPTY composer — the only one that shows this text: send; a note for the running
    /// turn; or, while an approval card waits, answer it (Enter allows once, Esc denies: the composer's key handler).
    /// VM context.</summary>
    private void RefreshComposerPlaceholder() =>
        ComposerPlaceholder = !IsLoading ? Strings.ComposerPlaceholder
            : _pendingApproval is { IsAnswered: false } ? Strings.ComposerPlaceholderCard
            : Strings.ComposerPlaceholderBusy;

    /// <summary>A card the turn waits on — an approval, a server's form — above what the turn is still writing (its live
    /// status, its answer so far), under its steps. VM context.</summary>
    private void InsertCard(ChatMessageItem card)
    {
        var idx = Messages.Count - 2;
        while (idx > 0 && Messages[idx - 1] is { } prev && (prev.Role == "status" || prev.IsStreaming)) idx--;
        ApplyItemTheme(card);
        Messages.Insert(idx, card);
    }

    /// <summary>The input card waiting for the person (an MCP server's form): a note typed during the turn sets it aside.</summary>
    private ChatMessageItem? _pendingInput;

    /// <summary>
    /// An MCP server's form, as a card in the conversation of the running turn (<see cref="VsContextHolder.InlineInput"/>);
    /// <c>null</c> when no turn runs. The same card id again redraws the card already there, with what to fix.
    /// </summary>
    /// <remarks>⚠ The card never takes the keyboard: a key typed for the composer or the code must not land in a form.</remarks>
    private async Task<InputReply?> AskInputInlineAsync(InputCardModel card, CancellationToken ct)
    {
        ChatMessageItem? item = null;
        await RunOnVMContextAsync(() =>
        {
            if (!IsLoading) return;
            if (Messages.FirstOrDefault(m => m.Role == InputCard.CardRole && m.InputCardId == card.Id) is { } shown)
            {
                shown.ShowInput(card);
                item = shown;
            }
            else
            {
                var created = ChatMessageItem.InputCardMsg(card);
                InsertCard(created);
                item = created;
            }
            _pendingInput = item;
            if (_turnHead is not null) _turnHead.TurnState = Strings.TurnWaiting;
            ScrollToBottom();
        });
        if (item is null) return null;

        using var answered = new CancellationTokenSource();
        // Left unanswered while its author is elsewhere, the card flashes the taskbar, as an approval card does.
        _ = DrawAttentionAfterAsync(answered.Token);
        try
        {
            using (ct.Register(() => Post(item.RetireInput)))
            {
                // VSTHRD003 as in AskInlineAsync: the card's TCS completes asynchronously in this out-of-process host.
#pragma warning disable VSTHRD003
                var reply = await item.InputReply;
#pragma warning restore VSTHRD003
                ct.ThrowIfCancellationRequested();
                return reply;
            }
        }
        finally
        {
            await answered.CancelAsync();
            await RunOnVMContextAsync(() =>
            {
                if (ReferenceEquals(_pendingInput, item)) _pendingInput = null;
                TurnWorking();
            });
        }
    }

    /// <summary>The line an answered card leaves, where the card was; at the end of the turn when it is not drawn. VM
    /// context.</summary>
    private void ShowInputReceipt(InputReceipt receipt)
    {
        var line = ChatMessageItem.InputReceiptMsg(receipt);
        ApplyItemTheme(line);
        var at = -1;
        for (var i = 0; i < Messages.Count; i++)
            if (Messages[i].Role == InputCard.CardRole && Messages[i].InputCardId == receipt.CardId) { at = i; break; }
        if (at >= 0)
        {
            Messages.RemoveAt(at);
            Messages.Insert(at, line);
        }
        else Messages.Insert(Messages.Count - 2, line);
        ScrollToBottom();
    }

    /// <summary>
    /// After <see cref="Services.Presentation.ApprovalCard.AttentionDelay"/> without an answer, the Visual Studio window
    /// asks for attention (<see cref="Services.VsIntegration.WindowAttention"/>) — and if it is in front then, as soon as it
    /// is left: the card waits as long once its author has gone elsewhere. An answer, or the run's end, cancels it.
    /// </summary>
    private static async Task DrawAttentionAfterAsync(CancellationToken answered)
    {
        var flashed = false;
        try
        {
            await Task.Delay(Services.Presentation.ApprovalCard.AttentionDelay, answered);
            Services.VsIntegration.WindowAttention.Outcome outcome;
            while ((outcome = Services.VsIntegration.WindowAttention.Request()) == Services.VsIntegration.WindowAttention.Outcome.Looking)
                await Task.Delay(TimeSpan.FromSeconds(2), answered);
            flashed = outcome == Services.VsIntegration.WindowAttention.Outcome.Flashed;
            await Task.Delay(Timeout.InfiniteTimeSpan, answered);   // until the answer
        }
        catch (OperationCanceledException) { }
        // ⚠ Answered, the flash stops: left to itself it stops only when the main window comes to the front.
        if (flashed) Services.VsIntegration.WindowAttention.Stop();
    }

    /// <summary>"Open diff": the dialog shows the whole change, and its answer, when it gives one, answers the card.</summary>
    private async Task OpenCardDiffAsync(
        ChatMessageItem card, Func<CancellationToken, Task<ApprovalDecision?>> openDiff, CancellationToken answered)
    {
        try
        {
            if (await openDiff(answered) is { } decision)
                await RunOnVMContextAsync(() => card.Answer(decision));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // ⚠ Swallowed, the click did nothing — and the card still asked for an answer: approved, the change was one
            // nobody had seen in full.
            Diagnostics.Swallow("Chat.ApprovalOpenDiff", ex);
            var reason = Diagnostics.RootMessage(ex);
            await RunOnVMContextAsync(() => InsertThemed(ChatMessageItem.NoticeMsg(Strings.ApprovalDiffNotOpened(reason))));
        }
    }

    /// <summary>A step of the running turn, under its run line (created with the first step), at <paramref name="idx"/>;
    /// returns the index after it. VM context.</summary>
    private int InsertStep(int idx, ChatMessageItem step)
    {
        if (_runLine is null)
        {
            // Open while the run works: its steps show as they come; the run folds into its line when it ends.
            _runLine = ChatMessageItem.RunMsg(open: true);
            ApplyItemTheme(_runLine);
            Messages.Insert(idx++, _runLine);
        }
        ApplyItemTheme(step);
        Messages.Insert(idx++, step);
        _runLine.AddStep(step);
        TurnWorking();
        return idx;
    }

    /// <summary>A run's result bar, at the end of its turn; Undo moves to it from the previous one. VM context.</summary>
    private void InsertResultBar(Services.Presentation.RunSummaryModel summary)
    {
        Action? undo = null;
        Action? review = null;
        if (summary.RunId.Length > 0)
        {
            // /undo-run reverts the most recent run that changed files: only that run's bar offers it.
            foreach (var older in Messages.Where(m => m.Role == "result"))
                older.CanUndo = false;
            var runId = summary.RunId;
            undo = () => _ = UndoFromResultBarAsync(runId);
            // Any run's files can be reviewed: a file changed since by a later run is refused its undo, and says so.
            review = () => _ = ReviewRunAsync(runId, null, null);
        }
        InsertThemed(ChatMessageItem.ResultMsg(summary, undo, review, path => _ = OpenInEditorAsync(path)));
    }

    /// <summary>
    /// A run's review card, drawn or drawn again under its result bar — after a file's Keep, Undo or Redo when
    /// <paramref name="path"/> is set. Like the bar's Undo, nothing is changed while a turn runs: its run still writes.
    /// </summary>
    /// <param name="under">Where the card goes when the run's bar is not on screen (a reloaded conversation): the refused
    /// "Go back here" card that asked for it.</param>
    /// <param name="piece">The change of <paramref name="path"/> the action is for (1-based); null: the whole file.</param>
    private async Task ReviewRunAsync(string runId, string? path, Services.Commands.UndoRunCommandHandler.RunFileAction? action,
                                      ChatMessageItem? under = null, int? piece = null)
    {
        try
        {
            Services.Presentation.RunReviewModel? review;
            string? status = null;
            if (path is not null && action is { } act)
            {
                var busy = false;
                await RunOnVMContextAsync(() => busy = IsLoading || _sendStarting);
                PinWorkspaceRoot();
                (status, review) = await UndoOutsideAnyRunAsync(() => piece is { } number
                    ? Services.Commands.UndoRunCommandHandler.PieceActionAsync(
                        _tools.History, runId, path, number, act, FindProjectRoot(), busy, CancellationToken.None)
                    : Services.Commands.UndoRunCommandHandler.FileActionAsync(
                        _tools.History, runId, path, act, FindProjectRoot(), busy, CancellationToken.None));
            }
            else
            {
                var run = _tools.History.Runs.FirstOrDefault(r => r.Id == runId && r.FileCount > 0);
                review = run is null ? null : Services.Presentation.RunReview.Build(_tools.History, run);
                if (run is null) status = Strings.UndoRunNone;
            }
            await RunOnVMContextAsync(() =>
            {
                var card = Messages.FirstOrDefault(m => m.Role == "review" && m.RunId == runId);
                if (card is null)
                {
                    var bar = Messages.FirstOrDefault(m => m.Role == "result" && m.RunId == runId)
                              ?? (under is not null && Messages.Contains(under) ? under : null);
                    if (bar is null) return;
                    card = ChatMessageItem.ReviewMsg(runId,
                        onClose: () => _ = RunOnVMContextAsync(() =>
                        {
                            if (Messages.FirstOrDefault(m => m.Role == "review" && m.RunId == runId) is { } shown) Messages.Remove(shown);
                        }),
                        act: (file, piece, what) => _ = ReviewRunAsync(runId, file, what, under, piece));
                    card.ShowReview(review, status);
                    ApplyItemTheme(card);
                    Messages.Insert(Messages.IndexOf(bar) + 1, card);
                    return;
                }
                card.ShowReview(review, status);
                ApplyItemTheme(card);
            });
        }
        catch (Exception ex)
        {
            Diagnostics.Swallow("InferpalToolWindowData.ReviewRun", ex);
            // No answer to draw the card from: its actions come back, and the failure is said.
            await RunOnVMContextAsync(() =>
                Messages.FirstOrDefault(m => m.Role == "review" && m.RunId == runId)?.EnableReviewActions(true));
            await ShowInfoAsync(Strings.RunFileFailed(path ?? runId));
        }
    }

    /// <summary><paramref name="id"/> when that run changed files; null otherwise.</summary>
    private string? ChangedRun(string? id) =>
        id is not null && _tools.History.Runs.Any(r => r.Id == id && r.FileCount > 0) ? id : null;

    /// <summary>The saved conversation, and the index in it of <paramref name="question"/> (-1 when it is not there). VM
    /// context.</summary>
    private (List<SavedMessage> Snapshot, int Index) SnapshotWithIndexOf(ChatMessageItem question)
    {
        var index = 0;
        var found = -1;
        foreach (var m in Messages)
        {
            if (ReferenceEquals(m, question)) { found = index; break; }
            if (m.Role != "result" && !SessionManager.ScreenOnlyRoles.Contains(m.Role)) index++;
        }
        return (SessionManager.BuildSnapshot(Messages.Select(m => m.Saved)), found);
    }

    /// <summary>A question's "Go back here": its card under it — what going back would do to the files, or why it cannot.</summary>
    private async Task RewindPlanAsync(ChatMessageItem question)
    {
        try
        {
            List<SavedMessage> snapshot = [];
            var index = -1;
            await RunOnVMContextAsync(() => (snapshot, index) = SnapshotWithIndexOf(question));
            var turn = index < 0 ? 0 : Services.Execution.Rewind.TurnAt(snapshot, index);
            if (turn == 0 || Services.Execution.Rewind.Plan(_tools.History, snapshot, turn) is not { } plan) return;
            var card = Services.Presentation.RewindCard.Build(plan);
            await RunOnVMContextAsync(() => ShowRewindCard(question, card, null));
        }
        catch (Exception ex)
        {
            Diagnostics.Swallow("InferpalToolWindowData.RewindPlan", ex);
        }
    }

    /// <summary>Draws (or draws again) the "rewind" card under <paramref name="question"/>. VM context.</summary>
    private void ShowRewindCard(ChatMessageItem question, Services.Presentation.RewindCardModel card, string? status)
    {
        var shown = Messages.FirstOrDefault(m => m.Role == "rewind" && ReferenceEquals(m.RewindFor, question));
        if (shown is null)
        {
            var at = Messages.IndexOf(question);
            if (at < 0) return;
            shown = ChatMessageItem.RewindMsg(question,
                go:               () => _ = RewindAsync(question, conversationOnly: false),
                cancel:           () => _ = RunOnVMContextAsync(() =>
                {
                    if (Messages.FirstOrDefault(m => m.Role == "rewind" && ReferenceEquals(m.RewindFor, question)) is { } open)
                        Messages.Remove(open);
                }),
                conversationOnly: () => _ = RewindAsync(question, conversationOnly: true),
                // The runs the card shows now — it is drawn again from each answer — their cards under it.
                review:           () => _ = RunOnVMContextAsync(() =>
                {
                    if (Messages.FirstOrDefault(m => m.Role == "rewind" && ReferenceEquals(m.RewindFor, question)) is not { } open) return;
                    foreach (var runId in open.RewindRunIds) _ = ReviewRunAsync(runId, null, null, under: open);
                }));
            shown.ShowRewind(card, status);
            ApplyItemTheme(shown);
            Messages.Insert(at + 1, shown);
            return;
        }
        shown.ShowRewind(card, status);
    }

    /// <summary>
    /// Goes back to <paramref name="question"/>: the runs that followed it undone — all or none, unless
    /// <paramref name="conversationOnly"/> — then the conversation cut before it, the whole one kept as a session, and the
    /// question back in the message box. Like the bar's Undo, nothing is changed while a turn runs.
    /// </summary>
    private async Task RewindAsync(ChatMessageItem question, bool conversationOnly)
    {
        ChatMessageItem? CardOf() => Messages.FirstOrDefault(m => m.Role == "rewind" && ReferenceEquals(m.RewindFor, question));
        Task SayAsync(string status) => RunOnVMContextAsync(() => CardOf()?.RewindSays(status));
        // Claimed like a send: a question sent while the files go back would be cut away by the fork, unsaved.
        var claim = 0;
        var undone = false;
        var turn = 0;
        try
        {
            List<SavedMessage> snapshot = [];
            var index = -1;
            var currentName = string.Empty;
            string? root = null;
            IReadOnlyList<string> expected = [];
            await RunOnVMContextAsync(() =>
            {
                if (IsLoading || _sendStarting) return;
                _sendStarting = true;
                claim = ++_sendClaim;
                (snapshot, index) = SnapshotWithIndexOf(question);
                currentName = _currentSessionName;
                root        = _conversationRoot.For(_indexService.RootDir);
                expected    = CardOf()?.RewindRunIds ?? [];
            });
            if (claim == 0) { await SayAsync(Strings.UndoRunWhileBusy); return; }
            turn = index < 0 ? 0 : Services.Execution.Rewind.TurnAt(snapshot, index);
            if (turn == 0) { await SayAsync(Strings.RewindQuestionGone); return; }
            Services.Execution.RewindOutcome? outcome = null;
            if (!conversationOnly)
            {
                PinWorkspaceRoot();
                outcome = await UndoOutsideAnyRunAsync(() =>
                    Services.Execution.Rewind.ApplyAsync(_tools.History, snapshot, turn, expected));
                if (outcome is null) { await SayAsync(Strings.RewindQuestionGone); return; }
                if (outcome.Plan.Refusal == Services.Execution.RewindRefusal.Stale)
                {
                    // The card showed other runs: drawn again from what follows the question now, and said.
                    var fresh = Services.Presentation.RewindCard.Build(Services.Execution.Rewind.Plan(_tools.History, snapshot, turn)!);
                    await RunOnVMContextAsync(() => ShowRewindCard(question, fresh, Strings.RewindPlanChanged));
                    return;
                }
                if (outcome.Plan.Refusal != Services.Execution.RewindRefusal.None)
                {
                    var refused = Services.Presentation.RewindCard.Build(outcome.Plan);
                    await RunOnVMContextAsync(() => ShowRewindCard(question, refused, null));
                    return;
                }
                if (outcome.Failed.Count > 0) { await SayAsync(Strings.RewindStopped(Path.GetFileName(outcome.Failed[0]))); return; }
                undone = true;
            }
            var saved    = Services.Execution.Rewind.QuestionAt(snapshot, turn)!;
            var sessions = await _store.ListWithPreviewAsync(CancellationToken.None);
            if (await ForkAsync(snapshot, turn - 1, currentName, root, sessions, CancellationToken.None) is null)
            {
                await SayAsync(Strings.RewindQuestionGone);
                return;
            }
            await ShowInfoAsync(Services.Presentation.RewindCard.Done(outcome, turn, saved));
            var asked = Services.Presentation.RewindCard.Typed(saved).Text;
            // Above a draft typed meanwhile, never over it.
            await RunOnVMContextAsync(() => Prompt = string.IsNullOrWhiteSpace(Prompt) ? asked : asked + "\n\n" + Prompt);
        }
        catch (Exception ex)
        {
            Diagnostics.Swallow("InferpalToolWindowData.Rewind", ex);
            // The files moved: said, never "nothing changed".
            await SayAsync(undone ? Strings.RewindConversationNotCut(turn, Diagnostics.RootMessage(ex))
                             : Strings.MsgError(Diagnostics.RootMessage(ex)));
        }
        finally
        {
            if (claim != 0) await RunOnVMContextAsync(() => { if (_sendClaim == claim) _sendStarting = false; });
        }
    }

    /// <summary>One undo at a time — the bar's, a typed one, a review card's — and in no run: two at once on one file
    /// would save the first's result as the version to redo, and a turn starting meanwhile would count the undo's own
    /// backups as its edits (its result bar, its Undo).</summary>
    private readonly SemaphoreSlim _undoGate = new(1, 1);

    private async Task<T> UndoOutsideAnyRunAsync<T>(Func<Task<T>> undo)
    {
        await _undoGate.WaitAsync();
        try
        {
            using var _ = _tools.History.OutsideRun();
            return await undo();
        }
        finally { _undoGate.Release(); }
    }

    /// <summary>A result bar's Undo: <c>/undo-run #&lt;its run&gt;</c>, run straight — never through the prompt box. Named: the
    /// most recent run that changed files may be another one by now (a <c>/fix-build</c>, a <c>/tdd</c>).</summary>
    /// <remarks>
    /// ⚠ Through the prompt and <c>SendAsync</c> the click IS the Send button: during a turn it stops that turn (or, with
    /// an approval card waiting, types "/undo-run" over the draft and answers nothing), and idle it replaces the draft.
    /// While a turn runs, its run is still writing: the undo waits for it, and says so.
    /// </remarks>
    private async Task UndoFromResultBarAsync(string runId)
    {
        var busy = false;
        await RunOnVMContextAsync(() => busy = IsLoading || _sendStarting);
        if (busy)
        {
            await ShowInfoAsync(Strings.UndoRunWhileBusy);
            return;
        }
        PinWorkspaceRoot();
        await HandleSlashCommandAsync("/undo-run #" + runId, CancellationToken.None);
    }

    /// <summary>A file of a result bar opens in the editor; one that no longer opens is said, not a dead link.</summary>
    private async Task OpenInEditorAsync(string path)
    {
        try
        {
            await _vs.Documents().OpenTextDocumentAsync(new Uri(path), CancellationToken.None);
        }
        catch (Exception ex)
        {
            Diagnostics.Swallow("Chat.OpenResultFile", ex);
            await ShowInfoAsync(Strings.MsgError(ex.Message));
        }
    }

    /// <summary>
    /// A restored conversation's steps, grouped under a run line as they were live — a session file keeps the tool
    /// calls, not the runs. VM context.
    /// </summary>
    private void GroupRestoredSteps()
    {
        ChatMessageItem? run = null;
        for (var i = 0; i < Messages.Count; i++)
        {
            var m = Messages[i];
            if (m.Role != "tool")
            {
                run?.EndRun(null);
                run = null;
                continue;
            }
            if (run is null)
            {
                run = ChatMessageItem.RunMsg(_config.ToolBubblesExpanded);
                ApplyItemTheme(run);
                Messages.Insert(i++, run);
            }
            run.AddStep(m);
        }
        run?.EndRun(null);
    }

    /// <summary>The redesign's tokens for the theme; part of <see cref="ApplyThemeColors"/>.</summary>
    private void ApplyChromeTheme(ThemePalette p)
    {
        ThemeSurface    = p.Surface;
        ThemeChip       = p.Chip;
        ThemeAccent     = p.Accent;
        ThemeAccentText = p.AccentText;
        ThemeAccentLine = p.AccentLine;
        ThemeOk         = p.Ok;
        ThemeErrorText  = p.ErrorText;
        ThemeErrorBg    = p.ErrorBg;
        ThemeErrorLine  = p.ErrorLine;
        ThemeChipLine   = p.ChipLine;
        ThemeOnAccent   = p.OnAccent;
        ThemeWarn       = p.Warn;
        RefreshModelButton();
    }

    #endregion
}

/// <summary>One model of the header's menu: its name, whether it is the one in use; a click makes it the chat model.</summary>
[DataContract]
internal sealed class ModelChoiceItem
{
    public ModelChoiceItem(string name, bool current, Func<string, Task> pick)
    {
        Name        = name;
        IsCurrent   = current;
        PickCommand = new AsyncCommand((_, _) => pick(name));
    }

    [DataMember] public string Name      { get; }
    [DataMember] public bool   IsCurrent { get; }
    [DataMember] public AsyncCommand PickCommand { get; }
}
