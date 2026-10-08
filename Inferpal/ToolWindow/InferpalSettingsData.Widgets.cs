using System.Collections.ObjectModel;
using System.Runtime.Serialization;
using Inferpal.Localization;
using Inferpal.Services.Docs;
using Inferpal.Services.Presentation;
using Inferpal.Services.Rag;
using Microsoft.VisualStudio.Extensibility.UI;

namespace Inferpal.ToolWindow;

/// <summary>What the settings window's live blocks read: the code index, the @Docs index, the chat's conversation, and
/// the editor to open a file in.</summary>
/// <param name="ConversationUsage">The chat window's X-Ray counts; <c>null</c> while no chat window exists.</param>
/// <param name="OpenXray">Shows the chat window with its X-Ray panel open.</param>
/// <param name="OpenFile">Opens a file in the editor, a folder in the file explorer.</param>
internal sealed record SettingsLiveSources(
    ProjectIndexService Index, DocsIndexService Docs, Func<Task<XRayPanelModel?>> ConversationUsage,
    Func<Task> OpenXray, Func<string, Task> OpenFile);

/// <summary>
/// The live blocks of the settings pages (<see cref="SettingSection.Widget"/>): the code index, the project's exclusions,
/// the @Docs sites, how full the conversation is, the project's files, the model a feature uses — the facts the Core
/// presenters build for both editors (<see cref="SettingsWidgets"/>), bound here as primitives for Remote UI.
/// </summary>
internal partial class InferpalSettingsData
{
    private SettingsLiveSources? _live;
    private SettingsDocsActions? _docsActions;
    private System.Threading.Timer? _livePoll;

    // ── Index card ───────────────────────────────────────────────────────────
    private string _indexCardTitle = "", _indexCardDetail = "", _indexCardDot = "#808080", _indexCardOversizeNote = "",
                   _indexCardModelLine = "", _indexCardButton = "", _oversizeToggleLabel = "";
    private bool _hasIndexCard, _indexCardCanRebuild, _indexCardHasButton, _showOversizeFiles, _hasOversize;

    [DataMember] public bool   HasIndexCard          { get => _hasIndexCard;          set => SetProperty(ref _hasIndexCard,          value); }
    [DataMember] public string IndexCardTitle        { get => _indexCardTitle;        set => SetProperty(ref _indexCardTitle,        value); }
    [DataMember] public string IndexCardDetail       { get => _indexCardDetail;       set => SetProperty(ref _indexCardDetail,       value); }
    [DataMember] public string IndexCardDot          { get => _indexCardDot;          set => SetProperty(ref _indexCardDot,          value); }
    [DataMember] public ObservableCollection<string> IndexCardNotes { get; } = [];
    [DataMember] public bool   HasOversize           { get => _hasOversize;           set => SetProperty(ref _hasOversize,           value); }
    [DataMember] public string IndexCardOversizeNote { get => _indexCardOversizeNote; set => SetProperty(ref _indexCardOversizeNote, value); }
    [DataMember] public ObservableCollection<string> IndexCardOversizeFiles { get; } = [];
    [DataMember] public bool   ShowOversizeFiles     { get => _showOversizeFiles;     set => SetProperty(ref _showOversizeFiles,     value); }
    [DataMember] public string OversizeToggleLabel   { get => _oversizeToggleLabel;   set => SetProperty(ref _oversizeToggleLabel,   value); }
    [DataMember] public string IndexCardModelLine    { get => _indexCardModelLine;    set => SetProperty(ref _indexCardModelLine,    value); }
    [DataMember] public string IndexCardButton       { get => _indexCardButton;       set => SetProperty(ref _indexCardButton,       value); }
    [DataMember] public bool   IndexCardHasButton    { get => _indexCardHasButton;    set => SetProperty(ref _indexCardHasButton,    value); }
    [DataMember] public bool   IndexCardCanRebuild   { get => _indexCardCanRebuild;   set => SetProperty(ref _indexCardCanRebuild,   value); }
    [DataMember] public AsyncCommand RebuildIndexCommand   { get; private set; } = null!;
    [DataMember] public AsyncCommand ToggleOversizeCommand { get; private set; } = null!;

    // ── Exclusions ───────────────────────────────────────────────────────────
    private string _exclusionsFile = "";
    private bool _hasExclusions, _noExclusions = true, _exclusionsEditable;
    [DataMember] public ObservableCollection<string> IndexExclusions { get; } = [];
    [DataMember] public bool HasExclusions      { get => _hasExclusions;      set => SetProperty(ref _hasExclusions,      value); }
    [DataMember] public bool NoExclusions       { get => _noExclusions;       set => SetProperty(ref _noExclusions,       value); }
    [DataMember] public bool ExclusionsEditable { get => _exclusionsEditable; set => SetProperty(ref _exclusionsEditable, value); }
    [DataMember] public AsyncCommand EditExclusionsCommand { get; private set; } = null!;

    // ── @Docs sites ──────────────────────────────────────────────────────────
    private bool _docsEmpty = true, _isAddingSite;
    private string _newSiteUrl = "", _docsMessage = "";
    [DataMember] public ObservableCollection<DocsSiteItem> DocsSiteRows { get; } = [];
    [DataMember] public bool   DocsEmpty    { get => _docsEmpty;    set => SetProperty(ref _docsEmpty,    value); }
    [DataMember] public bool   IsAddingSite { get => _isAddingSite; set => SetProperty(ref _isAddingSite, value); }
    [DataMember] public string NewSiteUrl   { get => _newSiteUrl;   set => SetProperty(ref _newSiteUrl,   value); }
    [DataMember] public string DocsMessage  { get => _docsMessage;  set => SetProperty(ref _docsMessage,  value); }
    [DataMember] public AsyncCommand AddSiteCommand        { get; private set; } = null!;
    [DataMember] public AsyncCommand ConfirmAddSiteCommand { get; private set; } = null!;
    [DataMember] public AsyncCommand CancelAddSiteCommand  { get; private set; } = null!;

    // ── This conversation ────────────────────────────────────────────────────
    /// <summary>The width of the usage bar, in pixels — the XAML's own: Remote UI binds no star length, so the segments
    /// are sized here, against the width the bar is drawn at.</summary>
    private const double UsageBarWidth = 520;
    private string _usageSummary = "", _usageInstructions = "", _usageTools = "", _usageConversation = "";
    private double _usageInstructionsWidth, _usageToolsWidth, _usageConversationWidth;
    private bool _hasUsage, _usageNoChat;
    [DataMember] public bool   HasUsage               { get => _hasUsage;               set => SetProperty(ref _hasUsage,               value); }
    [DataMember] public bool   UsageNoChat            { get => _usageNoChat;            set => SetProperty(ref _usageNoChat,            value); }
    [DataMember] public string UsageSummary           { get => _usageSummary;           set => SetProperty(ref _usageSummary,           value); }
    [DataMember] public string UsageInstructions      { get => _usageInstructions;      set => SetProperty(ref _usageInstructions,      value); }
    [DataMember] public string UsageTools             { get => _usageTools;             set => SetProperty(ref _usageTools,             value); }
    [DataMember] public string UsageConversation      { get => _usageConversation;      set => SetProperty(ref _usageConversation,      value); }
    [DataMember] public double UsageInstructionsWidth { get => _usageInstructionsWidth; set => SetProperty(ref _usageInstructionsWidth, value); }
    [DataMember] public double UsageToolsWidth        { get => _usageToolsWidth;        set => SetProperty(ref _usageToolsWidth,        value); }
    [DataMember] public double UsageConversationWidth { get => _usageConversationWidth; set => SetProperty(ref _usageConversationWidth, value); }
    [DataMember] public AsyncCommand OpenXrayCommand     { get; private set; } = null!;
    [DataMember] public AsyncCommand ChangeWindowCommand { get; private set; } = null!;

    // ── The project's files ──────────────────────────────────────────────────
    [DataMember] public ObservableCollection<ProjectFileItem> ProjectFileRows { get; } = [];

    // ── Autocomplete: speed cards, and the model each feature uses ───────────
    private bool _inlineModeFast, _inlineModeDefault, _inlineModeAccurate;
    private string _fimModelLine = "", _editModelLine = "";
    [DataMember] public bool InlineModeFast
    {
        get => _inlineModeFast;
        set { if (SetProperty(ref _inlineModeFast, value) && value) PickInlineMode(0); }
    }
    [DataMember] public bool InlineModeDefault
    {
        get => _inlineModeDefault;
        set { if (SetProperty(ref _inlineModeDefault, value) && value) PickInlineMode(1); }
    }
    [DataMember] public bool InlineModeAccurate
    {
        get => _inlineModeAccurate;
        set { if (SetProperty(ref _inlineModeAccurate, value) && value) PickInlineMode(2); }
    }
    [DataMember] public string FimModelLine  { get => _fimModelLine;  set => SetProperty(ref _fimModelLine,  value); }
    [DataMember] public string EditModelLine { get => _editModelLine; set => SetProperty(ref _editModelLine, value); }
    [DataMember] public AsyncCommand ChangeFimModelCommand  { get; private set; } = null!;
    [DataMember] public AsyncCommand ChangeEditModelCommand { get; private set; } = null!;

    // ── The words of the blocks ──────────────────────────────────────────────
    private string _labelSectionAsYouType = "", _labelSectionDocs = "", _descSectionDocs = "", _noteDocs = "",
                   _labelThisConversation = "", _labelProjectFiles = "", _labelExclusions = "", _exclusionsFrom = "",
                   _exclusionsHowTo = "", _labelEditFile = "", _labelOpenFile = "", _labelChangeInServer = "",
                   _labelDocsAddSite = "", _labelDocsReindex = "", _labelDocsAddUrl = "", _labelDocsAddButton = "",
                   _labelOpenXray = "", _labelChangeWindow = "", _usageNoChatText = "", _docsNoSitesYet = "",
                   _emptySameAsChat = "", _emptySameAsCodeActions = "", _emptyAutomatic = "",
                   _slashColCommand = "", _slashColSends = "", _toolColName = "", _toolColRuns = "",
                   _fimFastName = "", _fimFastDesc = "", _fimDefaultName = "", _fimDefaultDesc = "",
                   _fimAccurateName = "", _fimAccurateDesc = "", _approvalRulesFrom = "";

    [DataMember] public string LabelSectionAsYouType  { get => _labelSectionAsYouType;  set => SetProperty(ref _labelSectionAsYouType,  value); }
    [DataMember] public string LabelSectionDocs       { get => _labelSectionDocs;       set => SetProperty(ref _labelSectionDocs,       value); }
    [DataMember] public string DescSectionDocs        { get => _descSectionDocs;        set => SetProperty(ref _descSectionDocs,        value); }
    [DataMember] public string NoteDocs               { get => _noteDocs;               set => SetProperty(ref _noteDocs,               value); }
    [DataMember] public string LabelThisConversation  { get => _labelThisConversation;  set => SetProperty(ref _labelThisConversation,  value); }
    [DataMember] public string LabelProjectFiles      { get => _labelProjectFiles;      set => SetProperty(ref _labelProjectFiles,      value); }
    [DataMember] public string LabelExclusions        { get => _labelExclusions;        set => SetProperty(ref _labelExclusions,        value); }
    [DataMember] public string ExclusionsFrom         { get => _exclusionsFrom;         set => SetProperty(ref _exclusionsFrom,         value); }
    [DataMember] public string ExclusionsHowTo        { get => _exclusionsHowTo;        set => SetProperty(ref _exclusionsHowTo,        value); }
    [DataMember] public string LabelEditFile          { get => _labelEditFile;          set => SetProperty(ref _labelEditFile,          value); }
    [DataMember] public string LabelOpenFile          { get => _labelOpenFile;          set => SetProperty(ref _labelOpenFile,          value); }
    [DataMember] public string LabelChangeInServer    { get => _labelChangeInServer;    set => SetProperty(ref _labelChangeInServer,    value); }
    [DataMember] public string LabelDocsAddSite       { get => _labelDocsAddSite;       set => SetProperty(ref _labelDocsAddSite,       value); }
    [DataMember] public string LabelDocsReindex       { get => _labelDocsReindex;       set => SetProperty(ref _labelDocsReindex,       value); }
    [DataMember] public string LabelDocsAddUrl        { get => _labelDocsAddUrl;        set => SetProperty(ref _labelDocsAddUrl,        value); }
    [DataMember] public string LabelDocsAddButton     { get => _labelDocsAddButton;     set => SetProperty(ref _labelDocsAddButton,     value); }
    [DataMember] public string LabelOpenXray          { get => _labelOpenXray;          set => SetProperty(ref _labelOpenXray,          value); }
    [DataMember] public string LabelChangeWindow      { get => _labelChangeWindow;      set => SetProperty(ref _labelChangeWindow,      value); }
    [DataMember] public string UsageNoChatText        { get => _usageNoChatText;        set => SetProperty(ref _usageNoChatText,        value); }
    [DataMember] public string DocsNoSitesYet         { get => _docsNoSitesYet;         set => SetProperty(ref _docsNoSitesYet,         value); }
    [DataMember] public string EmptySameAsChat        { get => _emptySameAsChat;        set => SetProperty(ref _emptySameAsChat,        value); }
    [DataMember] public string EmptySameAsCodeActions { get => _emptySameAsCodeActions; set => SetProperty(ref _emptySameAsCodeActions, value); }
    [DataMember] public string EmptyAutomatic         { get => _emptyAutomatic;         set => SetProperty(ref _emptyAutomatic,         value); }
    [DataMember] public string SlashColCommand        { get => _slashColCommand;        set => SetProperty(ref _slashColCommand,        value); }
    [DataMember] public string SlashColSends          { get => _slashColSends;          set => SetProperty(ref _slashColSends,          value); }
    [DataMember] public string ToolColName            { get => _toolColName;            set => SetProperty(ref _toolColName,            value); }
    [DataMember] public string ToolColRuns            { get => _toolColRuns;            set => SetProperty(ref _toolColRuns,            value); }
    [DataMember] public string FimFastName            { get => _fimFastName;            set => SetProperty(ref _fimFastName,            value); }
    [DataMember] public string FimFastDesc            { get => _fimFastDesc;            set => SetProperty(ref _fimFastDesc,            value); }
    [DataMember] public string FimDefaultName         { get => _fimDefaultName;         set => SetProperty(ref _fimDefaultName,         value); }
    [DataMember] public string FimDefaultDesc         { get => _fimDefaultDesc;         set => SetProperty(ref _fimDefaultDesc,         value); }
    [DataMember] public string FimAccurateName        { get => _fimAccurateName;        set => SetProperty(ref _fimAccurateName,        value); }
    [DataMember] public string FimAccurateDesc        { get => _fimAccurateDesc;        set => SetProperty(ref _fimAccurateDesc,        value); }
    [DataMember] public string ApprovalRulesFrom      { get => _approvalRulesFrom;      set => SetProperty(ref _approvalRulesFrom,      value); }

    /// <summary>Wires the live blocks to their sources; without them (a probe, a test) the blocks stay empty.</summary>
    private void InitWidgets(SettingsLiveSources? live)
    {
        _live        = live;
        _docsActions = live is null ? null : new SettingsDocsActions(_config, live.Docs);
        InitModelBlocks();

        RebuildIndexCommand   = new AsyncCommand((_, ct) => RebuildIndexAsync(ct));
        ToggleOversizeCommand = new AsyncCommand((_, _) => RunOnVMContextAsync(() =>
        {
            ShowOversizeFiles   = !ShowOversizeFiles;
            OversizeToggleLabel = ShowOversizeFiles ? Strings.IndexCardHideThem : Strings.IndexCardShowThem;
        }));
        EditExclusionsCommand = new AsyncCommand((_, _) => _live is { } l && _exclusionsFile.Length > 0
            ? l.OpenFile(_exclusionsFile) : Task.CompletedTask);
        AddSiteCommand        = new AsyncCommand((_, _) => RunOnVMContextAsync(() => { NewSiteUrl = string.Empty; IsAddingSite = true; }));
        CancelAddSiteCommand  = new AsyncCommand((_, _) => RunOnVMContextAsync(() => IsAddingSite = false));
        ConfirmAddSiteCommand = new AsyncCommand((_, ct) => RunDocsAsync("add", NewSiteUrl.Trim(), ct));
        OpenXrayCommand       = new AsyncCommand((_, _) => _live?.OpenXray() ?? Task.CompletedTask);
        ChangeWindowCommand   = new AsyncCommand((_, _) => RunOnVMContextAsync(() => SelectPage("server")));
        ChangeFimModelCommand = new AsyncCommand((_, _) => RunOnVMContextAsync(() => SelectPage("server")));
        // The edit model lives behind the Server page's fold: the link opens it, or it leads to a box nobody can see.
        ChangeEditModelCommand = new AsyncCommand((_, _) => RunOnVMContextAsync(() =>
        {
            ShowAdvanced = true;
            SelectPage("server");
        }));
    }

    /// <summary>The words of the blocks, from <see cref="ApplyLabels"/>.</summary>
    private void ApplyWidgetLabels()
    {
        ApplyModelBlockLabels();
        LabelSectionAsYouType  = Strings.SettingsSectionAsYouType;
        LabelSectionDocs       = Strings.SettingsSectionDocs;
        DescSectionDocs        = Strings.SettingsSectionDocsDesc;
        NoteDocs               = Strings.SettingsDocsNote;
        LabelThisConversation  = Strings.SettingsSectionThisConversation;
        LabelProjectFiles      = Strings.ProjectFilesTitle;
        LabelExclusions        = Strings.IndexExclusionsTitle;
        ExclusionsFrom         = Strings.IndexExclusionsFrom;
        ExclusionsHowTo        = Strings.IndexExclusionsHowTo;
        LabelEditFile          = Strings.SettingsEditFile;
        LabelOpenFile          = Strings.SettingsOpenFile;
        LabelChangeInServer    = Strings.SettingsChangeInServer;
        LabelDocsAddSite       = Strings.DocsAddSite;
        LabelDocsReindex       = Strings.DocsReindex;
        LabelDocsAddUrl        = Strings.DocsAddUrlLabel;
        LabelDocsAddButton     = Strings.DocsAddButton;
        LabelOpenXray          = Strings.ContextUsageOpenXray;
        LabelChangeWindow      = Strings.ContextUsageChangeWindow;
        UsageNoChatText        = Strings.ContextUsageNoChat;
        DocsNoSitesYet         = Strings.DocsNoSitesYet;
        EmptySameAsChat        = Strings.SettingsSameAsChat;
        EmptySameAsCodeActions = Strings.SettingsSameAsCodeActions;
        EmptyAutomatic         = Strings.SettingsAutomaticBest;
        SlashColCommand        = Strings.SlashColCommand;
        SlashColSends          = Strings.SlashColSends;
        ToolColName            = Strings.ToolColName;
        ToolColRuns            = Strings.ToolColRuns;
        var modes = SettingsSchema.FimModes;
        (FimFastName, FimFastDesc)         = (modes[0].Display, modes[0].Description);
        (FimDefaultName, FimDefaultDesc)   = (modes[1].Display, modes[1].Description);
        (FimAccurateName, FimAccurateDesc) = (modes[2].Display, modes[2].Description);
        OversizeToggleLabel = ShowOversizeFiles ? Strings.IndexCardHideThem : Strings.IndexCardShowThem;
        SyncInlineModeCards();
        UpdateModelLines();
    }

    // ── Speed cards ──────────────────────────────────────────────────────────

    /// <summary>A card checked: the select behind the cards takes its option — the value Save reads.</summary>
    private void PickInlineMode(int index)
    {
        if (index < AvailableInlineModes.Count && SelectedInlineMode != AvailableInlineModes[index])
            SelectedInlineMode = AvailableInlineModes[index];
        SyncInlineModeCards();
    }

    /// <summary>The cards follow the selected option (opened, cancelled, saved): exactly one is checked.</summary>
    private void SyncInlineModeCards()
    {
        var index = SelectedInlineMode is null ? -1 : AvailableInlineModes.IndexOf(SelectedInlineMode);
        // The fields, not the properties: a property's setter picks a mode, and this only follows the one picked.
        SetProperty(ref _inlineModeFast,     index == 0, nameof(InlineModeFast));
        SetProperty(ref _inlineModeDefault,  index == 1, nameof(InlineModeDefault));
        SetProperty(ref _inlineModeAccurate, index == 2, nameof(InlineModeAccurate));
    }

    /// <summary>"Model: X." from the FORM — the first non-empty choice, the way the router resolves the role.</summary>
    private void UpdateModelLines()
    {
        static string First(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;
        var fim  = First(InlineCompletionModel, SelectedModel);
        var edit = First(InlineEditModel, CodeActionsModel, SelectedModel);
        FimModelLine  = fim.Length  > 0 ? Strings.SettingsModelUsed(fim)  : string.Empty;
        EditModelLine = edit.Length > 0 ? Strings.SettingsModelUsed(edit) : string.Empty;
    }

    // ── Refresh: when a page with live blocks is shown, and while a pass runs ─

    /// <summary>The page just shown asks again: the index or the conversation may have moved since.</summary>
    private void OnPageShown(string page)
    {
        _livePoll?.Dispose();
        _livePoll = null;
        switch (page)
        {
            case "server":  _ = RefreshLoadedModelsAsync(CancellationToken.None); break;
            case "search":  _ = RefreshSearchPageAsync(); break;
            case "context": _ = RefreshContextPageAsync(); break;
        }
    }

    private async Task RefreshSearchPageAsync()
    {
        if (_live is not { } live || _docsActions is not { } docs) return;
        try
        {
            var card  = SettingsWidgets.IndexCard(await live.Index.SnapshotAsync(CancellationToken.None), _config.RagEnabled,
                                                  Services.Inference.EmbeddingModels.Configured(_config), DateTime.Now);
            var sites = await docs.RowsAsync(CancellationToken.None);
            await RunOnVMContextAsync(() =>
            {
                ApplyIndexCard(card);
                ApplyExclusions(live.Index.ProfileExcludes, live.Index.RootDir);
                ApplyDocsSites(sites);
            });
            // While a pass runs — an index or a crawl — the page follows it, only while it is the one shown.
            if (_page == "search" && (card.State == "indexing" || sites.Any(s => s.Busy)))
                _livePoll = new System.Threading.Timer(_ => { if (_page == "search") _ = RefreshSearchPageAsync(); }, null, 1500, Timeout.Infinite);
        }
        catch (Exception ex) { Services.Diagnostics.Swallow("Settings.RefreshSearchPage", ex); }
    }

    private async Task RefreshContextPageAsync()
    {
        if (_live is not { } live) return;
        try
        {
            var xray  = await live.ConversationUsage();
            var files = SettingsWidgets.ProjectFiles(live.Index.RootDir);
            await RunOnVMContextAsync(() =>
            {
                ApplyUsage(xray);
                ApplyProjectFiles(files);
                RefreshPinnedSizes();
            });
        }
        catch (Exception ex) { Services.Diagnostics.Swallow("Settings.RefreshContextPage", ex); }
    }

    private async Task RebuildIndexAsync(CancellationToken ct)
    {
        if (_live is not { } live || string.IsNullOrEmpty(live.Index.RootDir) || live.Index.IsIndexing) return;
        live.Index.StartIndexing(live.Index.RootDir);
        await RefreshSearchPageAsync();
    }

    private async Task RunDocsAsync(string verb, string arg, CancellationToken ct)
    {
        if (_docsActions is not { } docs || arg.Length == 0) return;
        try
        {
            var message = await docs.RunAsync(verb, arg, ct);
            var sites   = await docs.RowsAsync(ct);
            await RunOnVMContextAsync(() =>
            {
                IsAddingSite = false;
                DocsMessage  = message;
                ApplyDocsSites(sites);
            });
            await RefreshSearchPageAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Services.Diagnostics.Swallow("Settings.Docs", ex);
            await RunOnVMContextAsync(() => DocsMessage = Services.Diagnostics.RootMessage(ex));
        }
    }

    // ── Applying the facts (VM context) ──────────────────────────────────────

    private void ApplyIndexCard(IndexCardModel card)
    {
        HasIndexCard        = true;
        IndexCardTitle      = card.Title;
        IndexCardDetail     = card.Detail;
        IndexCardDot        = DotFor(card.State);
        IndexCardButton     = card.ButtonLabel;
        IndexCardHasButton  = card.State != "noWorkspace";
        IndexCardCanRebuild = card.CanRebuild;
        IndexCardModelLine  = card.ModelLine;
        Replace(IndexCardNotes, card.Notes);
        HasOversize           = card.OversizeNote.Length > 0;
        IndexCardOversizeNote = card.OversizeNote;
        Replace(IndexCardOversizeFiles, card.OversizeFiles);
    }

    private void ApplyExclusions(IReadOnlyList<string> patterns, string root)
    {
        Replace(IndexExclusions, patterns);
        HasExclusions = patterns.Count > 0;
        NoExclusions  = !HasExclusions;
        _exclusionsFile = string.IsNullOrEmpty(root) ? string.Empty : System.IO.Path.Combine(root, ".inferpal", "project.json");
        ExclusionsEditable = HasExclusions && System.IO.File.Exists(_exclusionsFile);
    }

    private void ApplyDocsSites(IReadOnlyList<DocsSiteRow> sites)
    {
        DocsSiteRows.Clear();
        foreach (var s in sites)
        {
            var id = s.Id;
            var state = s.State switch { "indexing" => "indexing", "partial" => "stopped", "indexed" => "ready", _ => "notBuilt" };
            DocsSiteRows.Add(new DocsSiteItem(s, DotFor(state),
                new AsyncCommand((_, ct) => RunDocsAsync("reindex", id, ct)),
                new AsyncCommand((_, ct) => RunDocsAsync("remove", id, ct))));
        }
        DocsEmpty = DocsSiteRows.Count == 0;
    }

    /// <summary>The colour of a state's dot, readable on both themes: green done, blue running, amber partial, red failed.</summary>
    private string DotFor(string state) => state switch
    {
        "ready"    => IsDarkTheme ? "#4CC27A" : "#2E8B4F",
        "indexing" => IsDarkTheme ? "#4DAAFC" : "#005FB8",
        "stopped"  => IsDarkTheme ? "#CCA700" : "#8A6D00",
        "failed"   => IsDarkTheme ? "#F85149" : "#C42B1C",
        _          => "#808080",
    };

    /// <summary>The prompt as last measured for the page: what the pinned-file sizes are read against.</summary>
    private XRayPanelModel? _lastPrompt;

    private void ApplyUsage(XRayPanelModel? xray)
    {
        _lastPrompt = xray;
        UsageNoChat = xray is null;
        HasUsage    = xray is not null;
        if (xray is null) return;
        var usage = SettingsWidgets.ContextUsage(xray);
        UsageSummary           = usage.Summary;
        UsageInstructions      = $"{usage.Parts[0].Label} {usage.Parts[0].Amount}";
        UsageTools             = $"{usage.Parts[1].Label} {usage.Parts[1].Amount}";
        UsageConversation      = $"{usage.Parts[2].Label} {usage.Parts[2].Amount}";
        UsageInstructionsWidth = UsageBarWidth * Math.Clamp(usage.Parts[0].Percent, 0, 100) / 100;
        UsageToolsWidth        = UsageBarWidth * Math.Clamp(usage.Parts[1].Percent, 0, 100) / 100;
        UsageConversationWidth = UsageBarWidth * Math.Clamp(usage.Parts[2].Percent, 0, 100) / 100;
    }

    private void ApplyProjectFiles(IReadOnlyList<ProjectFileRow> files)
    {
        ProjectFileRows.Clear();
        foreach (var f in files)
        {
            var path = f.FullPath;
            ProjectFileRows.Add(new ProjectFileItem(f, Strings.ProjectFileNotYet,
                new AsyncCommand((_, _) => _live?.OpenFile(path) ?? Task.CompletedTask)));
        }
    }

    /// <summary>What each pinned file costs the prompt, read again with the page (an edit to the file counts at once).</summary>
    private void RefreshPinnedSizes()
    {
        var sizes = SettingsWidgets.PinnedSizes(PinnedFileRows.Select(r => r.Field1), _lastPrompt);
        for (var i = 0; i < PinnedFileRows.Count && i < sizes.Count; i++)
            PinnedFileRows[i].Size = sizes[i].Size;
    }

    private static void Replace(ObservableCollection<string> target, IEnumerable<string> items)
    {
        target.Clear();
        foreach (var item in items) target.Add(item);
    }
}

/// <summary>One @Docs site of the Code search page, as Remote UI carries it.</summary>
[DataContract]
internal sealed class DocsSiteItem : NotifyPropertyChangedObject
{
    public DocsSiteItem(DocsSiteRow row, string dot, AsyncCommand reindex, AsyncCommand remove)
    {
        Title         = row.Title;
        Address       = row.Address;
        Status        = row.Status;
        HoleNote      = row.HoleNote;
        HasHoleNote   = row.HoleNote.Length > 0;
        NotBusy       = !row.Busy;
        Dot           = dot;
        RemoveLabel   = row.RemoveLabel;
        ReindexCommand = reindex;
        RemoveCommand  = remove;
    }

    [DataMember] public string Title       { get; }
    [DataMember] public string Address     { get; }
    [DataMember] public string Status      { get; }
    [DataMember] public string HoleNote    { get; }
    [DataMember] public bool   HasHoleNote { get; }
    [DataMember] public bool   NotBusy     { get; }
    [DataMember] public string Dot         { get; }
    [DataMember] public string RemoveLabel { get; }
    [DataMember] public AsyncCommand ReindexCommand { get; }
    [DataMember] public AsyncCommand RemoveCommand  { get; }
}

/// <summary>One of the project's files on the Context page, as Remote UI carries it.</summary>
[DataContract]
internal sealed class ProjectFileItem : NotifyPropertyChangedObject
{
    public ProjectFileItem(ProjectFileRow row, string notYet, AsyncCommand open)
    {
        FileName    = row.Name;
        Description = row.Description;
        Exists      = row.Exists;
        Missing     = !row.Exists;
        NotYet      = notYet;
        OpenCommand = open;
    }

    // ⚠ Never a member called Name: reserved by Remote UI's serialization, it renders blank.
    [DataMember] public string FileName    { get; }
    [DataMember] public string Description { get; }
    [DataMember] public bool   Exists      { get; }
    [DataMember] public bool   Missing     { get; }
    [DataMember] public string NotYet      { get; }
    [DataMember] public AsyncCommand OpenCommand { get; }
}
