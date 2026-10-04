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
                   _labelMenuSettings = string.Empty, _labelMenuStepMode = string.Empty;
    [DataMember] public string LabelNewConversation { get => _labelNewConversation; set => SetProperty(ref _labelNewConversation, value); }
    [DataMember] public string LabelConversations   { get => _labelConversations;   set => SetProperty(ref _labelConversations,   value); }
    [DataMember] public string LabelMore            { get => _labelMore;            set => SetProperty(ref _labelMore,            value); }
    [DataMember] public string LabelMenuSearch      { get => _labelMenuSearch;      set => SetProperty(ref _labelMenuSearch,      value); }
    [DataMember] public string LabelMenuExport      { get => _labelMenuExport;      set => SetProperty(ref _labelMenuExport,      value); }
    [DataMember] public string LabelMenuXray        { get => _labelMenuXray;        set => SetProperty(ref _labelMenuXray,        value); }
    [DataMember] public string LabelMenuSettings    { get => _labelMenuSettings;    set => SetProperty(ref _labelMenuSettings,    value); }
    [DataMember] public string LabelMenuStepMode    { get => _labelMenuStepMode;    set => SetProperty(ref _labelMenuStepMode,    value); }

    [DataMember] public AsyncCommand ToggleModelMenuCommand { get; private set; } = null!;
    [DataMember] public AsyncCommand ToggleMoreMenuCommand  { get; private set; } = null!;
    [DataMember] public AsyncCommand CloseMenusCommand      { get; private set; } = null!;
    [DataMember] public AsyncCommand MenuSearchCommand      { get; private set; } = null!;
    [DataMember] public AsyncCommand MenuExportCommand      { get; private set; } = null!;
    [DataMember] public AsyncCommand MenuXrayCommand        { get; private set; } = null!;
    [DataMember] public AsyncCommand MenuSettingsCommand    { get; private set; } = null!;
    [DataMember] public AsyncCommand MenuStepModeCommand    { get; private set; } = null!;

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
        SetModeCommand         = new AsyncCommand((p, _) => SetModeAsync(p as string));
        RunUsagesCommand       = new AsyncCommand((_, ct) =>
            _activeFilePath is { Length: > 0 } file
                ? RunSuggestionAsync(Strings.WelcomeUsagesPrompt(Path.GetFileName(file)), ct)
                : Task.CompletedTask);
        DenyApprovalCommand    = new AsyncCommand((_, _) => RunOnVMContextAsync(() => _pendingApproval?.Answer(ApprovalDecision.Deny)));
        _contextHolder.InlineApproval = AskInlineAsync;
        _isCompact             = _config.IsCompactChat;
        // A save from the settings window can change the density: the open conversation follows at once.
        _config.Saved         += () => Post(() => IsCompact = _config.IsCompactChat);
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
        LabelModeChat        = Strings.ModeChat;
        LabelModeAgent       = Strings.ModeAgent;
        LabelModePlan        = Strings.ModePlan;
        TipModeChat          = Strings.ModeChatTip;
        TipModeAgent         = Strings.ModeAgentTip;
        TipModePlan          = Strings.ModePlanTip;
        ComposerPlaceholder  = Strings.ComposerPlaceholder;
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
        RefreshModelButton();
        RefreshWelcome();
        UpdateContextBudget();
    }

    private void CloseMenus()
    {
        IsModelMenuOpen = false;
        IsMoreMenuOpen  = false;
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

    private void RefreshWelcomeLine() =>
        WelcomeLine = string.IsNullOrEmpty(ActiveModelLabel)
            ? Strings.WelcomeLocal
            : Strings.WelcomeLine(ActiveModelLabel, Services.Inference.InferenceProviderFactory.DisplayName(_config.Provider));

    /// <summary>The welcome screen, from the open file and the last build.</summary>
    private void RefreshWelcome()
    {
        var file      = string.IsNullOrEmpty(_activeFilePath) ? null : Path.GetFileName(_activeFilePath);
        HasActiveFile = file is not null;
        WelcomeForFile     = file is null ? string.Empty : Strings.WelcomeForFile(file);
        WelcomeBuildFailed = Strings.WelcomeBuildFailed(_buildErrorCount);
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

        IReadOnlyList<string> listed;
        try
        {
            listed = await _client.ListModelsAsync(ct);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            Diagnostics.Swallow("Chat.ModelMenu", ex);
            listed = [];
        }
        await RunOnVMContextAsync(() =>
        {
            if (!IsModelMenuOpen) return;
            ModelChoices.Clear();
            foreach (var name in listed)
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

    private string CurrentMode() => _planMode ? "plan" : _agentMode ? "agent" : "chat";

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
            ChatMode = mode;
            if (IsPlanMode != plan)
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
            // Above what the turn is still writing (its live status, its answer so far), under its steps.
            var idx = Messages.Count - 2;
            while (idx > 0 && Messages[idx - 1] is { } prev && (prev.Role == "status" || prev.IsStreaming)) idx--;
            ApplyItemTheme(created);
            Messages.Insert(idx, created);
            _pendingApproval = created;
            if (_turnHead is not null) _turnHead.TurnState = Strings.TurnWaiting;
            ScrollToBottom();
            item = created;
        });
        if (item is null) return null;

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
                TurnWorking();
            });
        }
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
        catch (Exception ex) { Diagnostics.Swallow("Chat.ApprovalOpenDiff", ex); }
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
        if (summary.RunId.Length > 0)
        {
            // /undo-run reverts the most recent run that changed files: only that run's bar offers it.
            foreach (var older in Messages.Where(m => m.Role == "result"))
                older.CanUndo = false;
            undo = () => _ = RunSuggestionAsync("/undo-run", CancellationToken.None);
        }
        InsertThemed(ChatMessageItem.ResultMsg(summary, undo, path => _ = OpenInEditorAsync(path)));
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
