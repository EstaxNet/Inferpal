using System.Collections.ObjectModel;
using System.Runtime.Serialization;
using Inferpal.Localization;
using Inferpal.Services.Presentation;
using Microsoft.VisualStudio.Extensibility.UI;

namespace Inferpal.ToolWindow;

/// <summary>
/// The tools assistant on the Tools page (pari 163): what you work on, what the agent may reach, what goes with each
/// question, then the review — the panel VS Code draws, from the same Core model (<see cref="ToolsAssistantPresenter"/>),
/// read through the chat window. Every choice asks again, so the figures are measured; nothing is written before Apply.
/// </summary>
internal partial class InferpalSettingsData
{
    /// <summary>The width of the assistant's bar, in pixels — the XAML's own (Remote UI binds no star length).</summary>
    private const double AssistBarWidth = 200;

    private bool _assistOpen, _assistApplying, _hasAssistError, _assistRare, _hasAssistShare, _assistNoRows, _hasAssistServers;
    private int _assistStep;
    private string _assistError = "", _assistTokens = "", _assistCounts = "", _assistShare = "", _assistContextHint = "",
                   _assistBackLabel = "", _assistNextLabel = "";
    private double _assistBarFill;
    /// <summary>Every request numbered: an answer older than the last one asked is dropped, never shown over a newer one.</summary>
    private int _assistSeq;
    private AssistantWork _assistWork;
    private AssistantCommands _assistCommands;
    private AssistantInternet _assistInternet;
    private AssistantDebugger _assistDebugger;
    private AssistantContext _assistContext;
    private bool? _assistRareTools;
    private readonly Dictionary<string, bool> _assistServers = new(StringComparer.Ordinal);

    [DataMember] public bool   AssistOpen       { get => _assistOpen;       set => SetProperty(ref _assistOpen,       value); }
    [DataMember] public bool   AssistStep0      => _assistStep == 0;
    [DataMember] public bool   AssistStep1      => _assistStep == 1;
    [DataMember] public bool   AssistStep2      => _assistStep == 2;
    [DataMember] public bool   AssistStep3      => _assistStep == 3;
    [DataMember] public bool   AssistCanAct     => !_assistApplying;
    [DataMember] public string AssistBackLabel  { get => _assistBackLabel;  set => SetProperty(ref _assistBackLabel,  value); }
    [DataMember] public string AssistNextLabel  { get => _assistNextLabel;  set => SetProperty(ref _assistNextLabel,  value); }
    [DataMember] public string AssistError      { get => _assistError;      set => SetProperty(ref _assistError,      value); }
    [DataMember] public bool   HasAssistError   { get => _hasAssistError;   set => SetProperty(ref _hasAssistError,   value); }
    [DataMember] public string AssistTokens     { get => _assistTokens;     set => SetProperty(ref _assistTokens,     value); }
    [DataMember] public string AssistCounts     { get => _assistCounts;     set => SetProperty(ref _assistCounts,     value); }
    [DataMember] public string AssistShare      { get => _assistShare;      set => SetProperty(ref _assistShare,      value); }
    [DataMember] public bool   HasAssistShare   { get => _hasAssistShare;   set => SetProperty(ref _hasAssistShare,   value); }
    [DataMember] public double AssistBarFill    { get => _assistBarFill;    set => SetProperty(ref _assistBarFill,    value); }
    [DataMember] public string AssistContextHint { get => _assistContextHint; set => SetProperty(ref _assistContextHint, value); }
    [DataMember] public bool   AssistNoRows     { get => _assistNoRows;     set => SetProperty(ref _assistNoRows,     value); }
    [DataMember] public bool   HasAssistServers { get => _hasAssistServers; set => SetProperty(ref _hasAssistServers, value); }
    /// <summary>The rarely needed tools on their own line; some on and some off shows unticked and is left as it is.</summary>
    [DataMember] public bool AssistRare
    {
        get => _assistRare;
        set { if (SetProperty(ref _assistRare, value)) Pick(() => _assistRareTools = value); }
    }

    /// <summary>A choice the window made (Remote UI sets it on its own thread): the answers change on the VM context, as
    /// every other read of them, then the Core is asked again.</summary>
    private void Pick(Action change) => _ = PickAsync(change);

    private async Task PickAsync(Action change)
    {
        await RunOnVMContextAsync(change);
        await AskAssistAsync();
    }

    [DataMember] public ObservableCollection<AssistStepItem>   AssistSteps    { get; } = [];
    [DataMember] public ObservableCollection<AssistChoiceItem> AssistWorkCards { get; } = [];
    [DataMember] public ObservableCollection<AssistChoiceItem> AssistCommandCards { get; } = [];
    [DataMember] public ObservableCollection<AssistChoiceItem> AssistInternetCards { get; } = [];
    [DataMember] public ObservableCollection<AssistChoiceItem> AssistDebuggerCards { get; } = [];
    [DataMember] public ObservableCollection<AssistChoiceItem> AssistContextCards { get; } = [];
    [DataMember] public ObservableCollection<AssistChoiceItem> AssistServerRows { get; } = [];
    [DataMember] public ObservableCollection<AssistReviewItem> AssistReviewRows { get; } = [];

    [DataMember] public AsyncCommand OpenAssistCommand { get; private set; } = null!;
    [DataMember] public AsyncCommand AssistBackCommand { get; private set; } = null!;
    [DataMember] public AsyncCommand AssistNextCommand { get; private set; } = null!;

    // The words: from the Core's strings, as VS Code shows them.
    [DataMember] public string LabelAssistOpen     { get; private set; } = "";
    [DataMember] public string LabelAssistEntry    { get; private set; } = "";
    [DataMember] public string LabelAssistTitle    { get; private set; } = "";
    [DataMember] public string LabelAssistSubtitle { get; private set; } = "";
    [DataMember] public string LabelAssistWorkQuestion  { get; private set; } = "";
    [DataMember] public string LabelAssistWorkHint      { get; private set; } = "";
    [DataMember] public string LabelAssistWorkNote      { get; private set; } = "";
    [DataMember] public string LabelAssistReachQuestion { get; private set; } = "";
    [DataMember] public string LabelAssistReachHint     { get; private set; } = "";
    [DataMember] public string LabelAssistCommands      { get; private set; } = "";
    [DataMember] public string LabelAssistInternet      { get; private set; } = "";
    [DataMember] public string LabelAssistDebugger      { get; private set; } = "";
    [DataMember] public string LabelAssistContextQuestion { get; private set; } = "";
    [DataMember] public string LabelAssistRareTools    { get; private set; } = "";
    [DataMember] public string LabelAssistMcpTitle     { get; private set; } = "";
    [DataMember] public string LabelAssistReviewTitle  { get; private set; } = "";
    [DataMember] public string LabelAssistReviewNone   { get; private set; } = "";
    [DataMember] public string LabelAssistReviewNote   { get; private set; } = "";
    [DataMember] public string LabelAssistColBecomes   { get; private set; } = "";
    [DataMember] public string LabelAssistColTools     { get; private set; } = "";
    [DataMember] public string LabelAssistColBecause   { get; private set; } = "";
    [DataMember] public string LabelAssistColSaves     { get; private set; } = "";
    [DataMember] public string LabelAssistCarries      { get; private set; } = "";
    [DataMember] public string LabelAssistToolTokens   { get; private set; } = "";
    [DataMember] public string LabelAssistMeasured     { get; private set; } = "";

    private void ApplyToolsAssistLabels()
    {
        LabelAssistOpen            = Strings.AssistOpen;
        LabelAssistEntry           = Strings.AssistEntry;
        LabelAssistTitle           = Strings.AssistTitle;
        LabelAssistSubtitle        = Strings.AssistSubtitle;
        LabelAssistWorkQuestion    = Strings.AssistWorkQuestion;
        LabelAssistWorkHint        = Strings.AssistWorkHint;
        LabelAssistWorkNote        = Strings.AssistWorkNote;
        LabelAssistReachQuestion   = Strings.AssistReachQuestion;
        LabelAssistReachHint       = Strings.AssistReachHint;
        LabelAssistCommands        = Strings.AssistCommands;
        LabelAssistInternet        = Strings.AssistInternet;
        LabelAssistDebugger        = Strings.AssistDebugger;
        LabelAssistContextQuestion = Strings.AssistContextQuestion;
        LabelAssistRareTools       = Strings.AssistRareTools;
        LabelAssistMcpTitle        = Strings.AssistMcpTitle;
        LabelAssistReviewTitle     = Strings.AssistReviewTitle;
        LabelAssistReviewNone      = Strings.AssistReviewNone;
        LabelAssistReviewNote      = Strings.AssistReviewNote;
        LabelAssistColBecomes      = Strings.AssistColBecomes;
        LabelAssistColTools        = Strings.AssistColTools;
        LabelAssistColBecause      = Strings.AssistColBecause;
        LabelAssistColSaves        = Strings.AssistColSaves;
        LabelAssistCarries         = Strings.AssistCarries;
        LabelAssistToolTokens      = Strings.AssistToolTokens;
        LabelAssistMeasured        = Strings.AssistMeasured;
    }

    private void InitToolsAssist()
    {
        OpenAssistCommand = new AsyncCommand((_, _) => OpenAssistAsync());
        AssistBackCommand = new AsyncCommand((_, _) => RunOnVMContextAsync(() =>
        {
            if (_assistApplying) return;
            if (_assistStep == 0) CloseAssist(null);
            else SetAssistStep(_assistStep - 1);
        }));
        AssistNextCommand = new AsyncCommand((_, _) => _assistStep < 3
            ? RunOnVMContextAsync(() => SetAssistStep(_assistStep + 1))
            : ApplyAssistAsync());
    }

    /// <summary>Opens on what the settings describe now: the first answer sets every choice.</summary>
    private async Task OpenAssistAsync(bool reopen = false)
    {
        if (_live?.ToolsAssist is not { } assist) return;
        var seq = 0;
        // ⚠ Judged on the VM context: a save seen from another thread just before Apply set its flag would otherwise start a
        // reopen that outnumbers the Apply, and the Apply — the settings already written — would not close.
        // A reopen on a save seen from another thread: only if the assistant is still open (Cancel may have closed it since).
        await RunOnVMContextAsync(() => seq = _assistApplying || reopen && !_assistOpen ? 0 : ++_assistSeq);
        if (seq == 0) return;
        try
        {
            var result = await assist(null, false);
            await RunOnVMContextAsync(() =>
            {
                if (seq != _assistSeq) return;
                if (result is not { } r) { ToolListNoChat = true; HasToolList = false; return; }
                SeedAssist(r.Model);
                ShowAssistModel(r.Model);
                SetAssistError(null);
                SetAssistStep(0);
                AssistOpen = true;
            });
        }
        catch (Exception ex) { Services.Diagnostics.Swallow("Settings.ToolsAssist", ex); }
    }

    private AssistantAnswers AssistAnswers() =>
        new(_assistWork, _assistCommands, _assistInternet, _assistDebugger, _assistContext,
            new Dictionary<string, bool>(_assistServers, StringComparer.Ordinal), _assistRareTools);

    /// <summary>A choice changed: the review and the figures asked again.</summary>
    private async Task AskAssistAsync()
    {
        if (_live?.ToolsAssist is not { } assist) return;
        var seq = 0;
        AssistantAnswers? answers = null;
        await RunOnVMContextAsync(() =>
        {
            if (!_assistOpen) return;
            seq     = ++_assistSeq;
            answers = AssistAnswers();
        });
        if (answers is null) return;
        try
        {
            var result = await assist(answers, false);
            await RunOnVMContextAsync(() =>
            {
                if (seq != _assistSeq || !_assistOpen) return;
                if (result is { } r) { ShowAssistModel(r.Model); SetAssistError(null); }
                else SetAssistError(Strings.ToolListNoChat);
            });
        }
        catch (Exception ex)
        {
            Services.Diagnostics.Swallow("Settings.ToolsAssist", ex);
            await RunOnVMContextAsync(() => SetAssistError(Services.Diagnostics.RootMessage(ex)));
        }
    }

    private async Task ApplyAssistAsync()
    {
        if (_live?.ToolsAssist is not { } assist) return;
        var seq = 0;
        AssistantAnswers? answers = null;
        await RunOnVMContextAsync(() =>
        {
            if (_assistApplying || !_assistOpen) return;
            _assistApplying = true;
            RaiseNotifyPropertyChangedEvent(nameof(AssistCanAct));
            seq     = ++_assistSeq;
            answers = AssistAnswers();
        });
        if (answers is null) return;
        try
        {
            var result = await assist(answers, true);
            await RunOnVMContextAsync(() =>
            {
                _assistApplying = false;
                RaiseNotifyPropertyChangedEvent(nameof(AssistCanAct));
                // What was written is closed on, whatever was asked since: the settings changed, the assistant is done.
                if (result is { Applied: false } || result is null)
                {
                    if (seq != _assistSeq) return;
                    if (result is not { } refused) { SetAssistError(Strings.ToolListNoChat); return; }
                    ShowAssistModel(refused.Model);
                    SetAssistError(refused.Message);
                    return;
                }
                var r = result.Value;
                // The form follows what was written, as opened too: else a later Save writes the old auto-context back, and
                // the form counts an unsaved change nobody made (and stops following the file).
                SetProperty(ref _ragAutoContextEnabled, r.Model.Plan.AutoContext, nameof(RagAutoContextEnabled));
                _opened["ragAutoContextEnabled"] = r.Model.Plan.AutoContext;
                RefreshUnsaved();
                CloseAssist(r.Message);
            });
        }
        catch (Exception ex)
        {
            Services.Diagnostics.Swallow("Settings.ToolsAssistApply", ex);
            await RunOnVMContextAsync(() =>
            {
                _assistApplying = false;
                RaiseNotifyPropertyChangedEvent(nameof(AssistCanAct));
                SetAssistError(Services.Diagnostics.RootMessage(ex));
            });
        }
    }

    /// <summary>The settings were saved elsewhere (<c>/tools off</c> in the chat, the other editor) while the assistant is
    /// open: its answers describe what was, and applying them would undo that change unseen — it opens again on what is.
    /// Its own Apply saves while <see cref="_assistApplying"/> holds, and is left alone.</summary>
    private void OnConfigSavedForAssist()
    {
        if (_assistOpen && !_assistApplying) _ = OpenAssistAsync(reopen: true);
    }

    private void CloseAssist(string? said)
    {
        ++_assistSeq;
        AssistOpen = false;
        if (!string.IsNullOrEmpty(said))
        {
            ToolListMessage    = said!;
            HasToolListMessage = true;
        }
        _ = RefreshToolListAsync();
    }

    private void SetAssistError(string? error)
    {
        AssistError    = error ?? string.Empty;
        HasAssistError = AssistError.Length > 0;
    }

    private void SetAssistStep(int step)
    {
        _assistStep = step;
        foreach (var name in new[] { nameof(AssistStep0), nameof(AssistStep1), nameof(AssistStep2), nameof(AssistStep3) })
            RaiseNotifyPropertyChangedEvent(name);
        AssistBackLabel = step == 0 ? Strings.AssistCancel : Strings.AssistBack;
        AssistNextLabel = step == 3 ? Strings.AssistApply : step == 2 ? Strings.AssistSeeChanges : Strings.AssistNext;
        string[] titles = [Strings.AssistStepWork, Strings.AssistStepReach, Strings.AssistStepContext, Strings.AssistStepReview];
        AssistSteps.Clear();
        for (var i = 0; i < titles.Length; i++)
            AssistSteps.Add(new AssistStepItem($"{i + 1}. {titles[i]}", i == step,
                                               i == step ? LinkForeground : i < step ? TextForeground : SubtleForeground));
        SetAssistError(null);
    }

    /// <summary>The choices, from the answers the settings describe: built once per opening, so a pick is never redrawn
    /// under the pointer.</summary>
    private void SeedAssist(ToolsAssistantModel model)
    {
        var a = model.Answers;
        (_assistWork, _assistCommands, _assistInternet, _assistDebugger, _assistContext, _assistRareTools) =
            (a.Work, a.Commands, a.Internet, a.Debugger, a.Context, a.RareTools);
        _assistServers.Clear();
        foreach (var (name, on) in a.McpServersOn) _assistServers[name] = on;
        SetProperty(ref _assistRare, a.RareTools == true, nameof(AssistRare));

        AssistWorkCards.Clear();
        foreach (var (family, title, desc) in new (AssistantWork, string, string)[]
                 {
                     (AssistantWork.Code, "AssistWorkCode", "AssistWorkCodeDesc"), (AssistantWork.Tests, "AssistWorkTests", "AssistWorkTestsDesc"),
                     (AssistantWork.WebPages, "AssistWorkWebPages", "AssistWorkWebPagesDesc"),
                     (AssistantWork.DesktopApps, "AssistWorkDesktopApps", "AssistWorkDesktopAppsDesc"),
                     (AssistantWork.Data, "AssistWorkData", "AssistWorkDataDesc"), (AssistantWork.Apis, "AssistWorkApis", "AssistWorkApisDesc"),
                     (AssistantWork.Refactoring, "AssistWorkRefactoring", "AssistWorkRefactoringDesc"),
                     (AssistantWork.Documents, "AssistWorkDocuments", "AssistWorkDocumentsDesc"),
                 })
        {
            var f = family;
            AssistWorkCards.Add(new AssistChoiceItem("", Strings.ByName(title), Strings.ByName(desc), _assistWork.HasFlag(f), on =>
                Pick(() => _assistWork = on ? _assistWork | f : _assistWork & ~f)));
        }

        void Radios<T>(ObservableCollection<AssistChoiceItem> target, string group, T current, Action<T> pick,
                       params (T Value, string Title, string Desc)[] options) where T : struct, Enum
        {
            target.Clear();
            foreach (var (value, title, desc) in options)
            {
                var v = value;
                target.Add(new AssistChoiceItem(group, Strings.ByName(title), Strings.ByName(desc), v.Equals(current), on =>
                {
                    if (on) Pick(() => pick(v));
                }));
            }
        }
        Radios(AssistCommandCards, "assistCmd", _assistCommands, v => _assistCommands = v,
               (AssistantCommands.Free, "AssistCommandsFree", "AssistCommandsFreeDesc"),
               (AssistantCommands.BuildAndTests, "AssistCommandsBuild", "AssistCommandsBuildDesc"),
               (AssistantCommands.Nothing, "AssistCommandsNothing", "AssistCommandsNothingDesc"));
        Radios(AssistInternetCards, "assistNet", _assistInternet, v => _assistInternet = v,
               (AssistantInternet.SearchAndRead, "AssistInternetSearch", "AssistInternetSearchDesc"),
               (AssistantInternet.GivenPages, "AssistInternetGiven", "AssistInternetGivenDesc"),
               (AssistantInternet.Never, "AssistInternetNever", "AssistInternetNeverDesc"));
        Radios(AssistDebuggerCards, "assistDbg", _assistDebugger, v => _assistDebugger = v,
               (AssistantDebugger.May, "AssistDebuggerMay", "AssistDebuggerMayDesc"),
               (AssistantDebugger.Never, "AssistDebuggerNever", "AssistDebuggerNeverDesc"));
        Radios(AssistContextCards, "assistCtx", _assistContext, v =>
               {
                   _assistContext = v;
                   // As in VS Code: "Full" turns the rare tools on, the others off.
                   _assistRareTools = v == AssistantContext.Full;
                   SetProperty(ref _assistRare, _assistRareTools == true, nameof(AssistRare));
               },
               (AssistantContext.Lean, "AssistContextLean", "AssistContextLeanDesc"),
               (AssistantContext.Balanced, "AssistContextBalanced", "AssistContextBalancedDesc"),
               (AssistantContext.Full, "AssistContextFull", "AssistContextFullDesc"));

        AssistServerRows.Clear();
        foreach (var server in model.Servers)
        {
            var name = server.Name;
            AssistServerRows.Add(new AssistChoiceItem("", name, server.Line, _assistServers.TryGetValue(name, out var on) ? on : server.On, picked =>
                Pick(() => _assistServers[name] = picked)));
        }
        HasAssistServers = AssistServerRows.Count > 0;
    }

    /// <summary>What the Core measured for the answers: the review rows and what each question then carries.</summary>
    private void ShowAssistModel(ToolsAssistantModel model)
    {
        AssistTokens      = model.Texts.Tokens;
        AssistCounts      = model.Texts.Counts;
        AssistShare       = model.Texts.Share;
        HasAssistShare    = model.Texts.Share.Length > 0;
        AssistContextHint = model.Texts.ContextHint;
        AssistBarFill     = model.Window > 0 ? AssistBarWidth * Math.Clamp((double)model.Plan.ToolTokensAfter / model.Window, 0, 1) : 0;
        AssistReviewRows.Clear();
        foreach (var row in model.Rows)
            AssistReviewRows.Add(new AssistReviewItem(row.On ? Strings.AssistOn : Strings.AssistOff,
                                                      row.On ? LinkForeground : WarningForeground,
                                                      string.Join(" ", row.Tools), row.Because, row.On ? string.Empty : row.Saves));
        AssistNoRows = AssistReviewRows.Count == 0;
    }
}

/// <summary>One step of the assistant's header.</summary>
[DataContract]
internal sealed class AssistStepItem(string title, bool current, string foreground) : NotifyPropertyChangedObject
{
    [DataMember] public string Title      { get; } = title;
    [DataMember] public bool   Current    { get; } = current;
    [DataMember] public string Foreground { get; } = foreground;
}

/// <summary>One card or line of the assistant: a radio of <see cref="Group"/>, or a box when the group is empty. Ticking it
/// tells the settings form, which asks the Core again.</summary>
[DataContract]
internal sealed class AssistChoiceItem : NotifyPropertyChangedObject
{
    private readonly Action<bool> _picked;
    private bool _isPicked;

    public AssistChoiceItem(string group, string title, string description, bool picked, Action<bool> onPicked)
    {
        Group       = group;
        Title       = title;
        Description = description;
        _isPicked   = picked;
        _picked     = onPicked;
    }

    [DataMember] public string Group       { get; }
    [DataMember] public string Title       { get; }
    [DataMember] public string Description { get; }
    [DataMember] public bool Picked
    {
        get => _isPicked;
        set { if (SetProperty(ref _isPicked, value)) _picked(value); }
    }
}

/// <summary>One row of the assistant's review, as Remote UI carries it.</summary>
[DataContract]
internal sealed class AssistReviewItem(string becomes, string becomesForeground, string tools, string because, string saves)
    : NotifyPropertyChangedObject
{
    [DataMember] public string Becomes           { get; } = becomes;
    [DataMember] public string BecomesForeground { get; } = becomesForeground;
    [DataMember] public string Tools             { get; } = tools;
    [DataMember] public string Because           { get; } = because;
    [DataMember] public string Saves             { get; } = saves;
}
