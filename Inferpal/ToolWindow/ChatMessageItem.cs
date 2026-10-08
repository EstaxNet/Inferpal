using System.Collections.ObjectModel;
using System.Runtime.Serialization;
using System.Threading;
using Microsoft.VisualStudio.Extensibility.UI;
using Inferpal.Localization;
using Inferpal.Services;

namespace Inferpal.ToolWindow;

[DataContract]
internal class ChatMessageItem : NotifyPropertyChangedObject
{
    private string          _content          = string.Empty;
    private string          _label            = string.Empty;
    private string          _btnFixWithAi     = string.Empty;
    private string          _fixErrorOutput   = string.Empty;
    private Action<string>? _onFixWithAi;
    private Action?         _onResume;
    private string          _btnResume       = string.Empty;
    private bool            _isResumable;
    private Action?         _onRegenerate;
    private string          _btnRegenerate   = string.Empty;
    private bool            _isRegeneratable;
    private bool            _isStreaming;
    private bool            _hasBlocks;
    private bool            _isExpanded       = true;
    private bool            _isFixable;
    private bool            _isSearchDimmed;
    private string _bubbleBackground = "Transparent";
    private string _themeText        = "#E8E8EC";
    private string _themeSubtleText  = "#A3A3AD";
    private string _themeToolText    = "#C9C9D1";
    private string _themeCodeText    = "#E8E8EC";
    private string _themeCodeBg      = "#18181C";
    private string _themeCodeBorder  = "#34343C";

    [DataMember] public string Role      { get; set; } = string.Empty;
    [DataMember] public string ToolName  { get; set; } = string.Empty;
    [DataMember] public string Timestamp { get; set; } = string.Empty;
    [DataMember] public string Content          { get => _content;          set => SetProperty(ref _content,          value); }
    [DataMember] public string Label            { get => _label;            set => SetProperty(ref _label,            value); }
    [DataMember] public string BtnFixWithAi     { get => _btnFixWithAi;     set => SetProperty(ref _btnFixWithAi,     value); }
    [DataMember] public bool   HasBlocks        { get => _hasBlocks;        set => SetProperty(ref _hasBlocks,        value); }
    [DataMember] public bool   IsExpanded       { get => _isExpanded;       set => SetProperty(ref _isExpanded,       value); }
    [DataMember] public bool   IsFixable        { get => _isFixable;        set => SetProperty(ref _isFixable,        value); }
    [DataMember] public bool   IsRegeneratable  { get => _isRegeneratable;  set => SetProperty(ref _isRegeneratable,  value); }
    [DataMember] public string BtnRegenerate    { get => _btnRegenerate;    set => SetProperty(ref _btnRegenerate,    value); }
    [DataMember] public bool   IsSearchDimmed   { get => _isSearchDimmed;   set => SetProperty(ref _isSearchDimmed,   value); }

    [DataMember] public bool   IsResumable      { get => _isResumable;      set => SetProperty(ref _isResumable,      value); }
    [DataMember] public string BtnResume        { get => _btnResume;        set => SetProperty(ref _btnResume,        value); }
    [DataMember] public string BubbleBackground { get => _bubbleBackground; set => SetProperty(ref _bubbleBackground, value); }
    private string _bubbleBorder = "Transparent";
    /// <summary>A question's bubble edge: its own fill, drawn in high contrast.</summary>
    [DataMember] public string BubbleBorder     { get => _bubbleBorder;     set => SetProperty(ref _bubbleBorder,     value); }
    [DataMember] public string ThemeText        { get => _themeText;        set => SetProperty(ref _themeText,        value); }
    [DataMember] public string ThemeSubtleText  { get => _themeSubtleText;  set => SetProperty(ref _themeSubtleText,  value); }
    [DataMember] public string ThemeToolText    { get => _themeToolText;    set => SetProperty(ref _themeToolText,    value); }
    [DataMember] public string ThemeCodeText    { get => _themeCodeText;    set => SetProperty(ref _themeCodeText,    value); }
    [DataMember] public string ThemeCodeBg      { get => _themeCodeBg;      set => SetProperty(ref _themeCodeBg,      value); }
    [DataMember] public string ThemeCodeBorder  { get => _themeCodeBorder;  set => SetProperty(ref _themeCodeBorder,  value); }

    [DataMember] public ObservableCollection<MarkdownBlock> Blocks    { get; } = [];
    [DataMember] public ObservableCollection<DiffLine>      DiffLines { get; } = [];

    private bool _hasDiff;
    [DataMember] public bool HasDiff { get => _hasDiff; set => SetProperty(ref _hasDiff, value); }

    // ── A question: its text, and what went with it as chips (the saved Content keeps the sentence) ──
    private string _userText = string.Empty;
    private bool   _hasChips;
    [DataMember] public string UserText { get => _userText; set => SetProperty(ref _userText, value); }
    [DataMember] public bool   HasChips { get => _hasChips; set => SetProperty(ref _hasChips, value); }
    [DataMember] public ObservableCollection<string> Chips { get; } = [];

    // ── A turn's header line ("turn"): the model in Label, then how long, or what it is doing ──
    private string _turnState = string.Empty;
    [DataMember] public string TurnState { get => _turnState; set => SetProperty(ref _turnState, value); }

    // ── A run line ("run") and its steps ("tool"): folded together, the line says what the steps did ──
    private string _runDetail     = string.Empty;
    private string _stepSubject   = string.Empty;
    private string _runGlyph      = Glyphs.Tool;
    private bool   _runFailed;
    private bool   _hasErrors;
    private bool   _isFolded;
    [DataMember] public string RunDetail   { get => _runDetail;   set => SetProperty(ref _runDetail,   value); }
    [DataMember] public string StepSubject { get => _stepSubject; set => SetProperty(ref _stepSubject, value); }
    /// <summary>The run line's glyph: a tool while it runs, a check once it ended, a cross when its check failed.</summary>
    [DataMember] public string RunGlyph    { get => _runGlyph;    set => SetProperty(ref _runGlyph,    value); }
    [DataMember] public bool   RunFailed   { get => _runFailed;   set => SetProperty(ref _runFailed,   value); }
    [DataMember] public bool   HasErrors   { get => _hasErrors;   set => SetProperty(ref _hasErrors,   value); }
    /// <summary>A step whose run line is folded: drawn by the line, not by itself.</summary>
    [DataMember] public bool   IsFolded    { get => _isFolded;    set => SetProperty(ref _isFolded,    value); }

    /// <summary>A run line's steps, in order (never crosses the boundary: the steps are items of the list).</summary>
    internal List<ChatMessageItem> Steps { get; } = [];

    // ── The agent's plan ("plan"): its goal, then one row per step — drawn from the plan's Markdown (PlanCard) ──
    private string _planGoal = string.Empty;
    private bool   _hasPlanGoal;
    [DataMember] public string PlanGoal    { get => _planGoal;    set => SetProperty(ref _planGoal,    value); }
    [DataMember] public bool   HasPlanGoal { get => _hasPlanGoal; set => SetProperty(ref _hasPlanGoal, value); }
    [DataMember] public ObservableCollection<PlanStepItem> PlanSteps { get; } = [];

    // ── A result bar ("result"): the files the run changed, its last check, Undo run ──
    private string _checkText = string.Empty;
    private bool   _hasCheck;
    private bool   _checkFailed;
    private bool   _canUndo;
    private string _btnUndo = string.Empty;
    private Action? _onUndo;
    [DataMember] public string CheckText   { get => _checkText;   set => SetProperty(ref _checkText,   value); }
    [DataMember] public bool   HasCheck    { get => _hasCheck;    set => SetProperty(ref _hasCheck,    value); }
    [DataMember] public bool   CheckFailed { get => _checkFailed; set => SetProperty(ref _checkFailed, value); }
    /// <summary>Undo reverts the most recent run that changed files (<c>/undo-run</c>): only that run's bar offers it.</summary>
    [DataMember] public bool   CanUndo     { get => _canUndo;     set => SetProperty(ref _canUndo,     value); }
    [DataMember] public string BtnUndo     { get => _btnUndo;     set => SetProperty(ref _btnUndo,     value); }
    [DataMember] public ObservableCollection<ResultFileItem> ResultFiles { get; } = [];
    [DataMember] public AsyncCommand UndoCommand { get; }

    // ── An approval card ("approval"): what will happen, to what, the start of the change, three answers ──
    private TaskCompletionSource<ApprovalDecision>? _decision;
    private Action? _onOpenDiff;
    private bool    _isAnswered;
    private string  _chosen = string.Empty;
    [DataMember] public string CardTitle      { get; private set; } = string.Empty;
    [DataMember] public string CardSubject    { get; private set; } = string.Empty;
    [DataMember] public string CardMore       { get; private set; } = string.Empty;
    [DataMember] public string CardAlwaysTip  { get; private set; } = string.Empty;
    [DataMember] public bool   HasCardLines   { get; private set; }
    [DataMember] public string BtnAllowOnce   { get; private set; } = string.Empty;
    [DataMember] public string BtnAlwaysAllow { get; private set; } = string.Empty;
    [DataMember] public string BtnDeny        { get; private set; } = string.Empty;
    [DataMember] public string BtnOpenDiff    { get; private set; } = string.Empty;
    [DataMember] public ObservableCollection<DiffLine> CardLines { get; } = [];
    /// <summary>Answered (or retired with its run): the buttons stop answering.</summary>
    [DataMember] public bool   IsAnswered     { get => _isAnswered; set => SetProperty(ref _isAnswered, value); }
    /// <summary>The answer given — <c>once</c>, <c>always</c> or <c>deny</c> — so the card shows which.</summary>
    [DataMember] public string ChosenAnswer   { get => _chosen;     set => SetProperty(ref _chosen,     value); }
    [DataMember] public AsyncCommand AllowOnceCommand   { get; }
    [DataMember] public AsyncCommand AlwaysAllowCommand { get; }
    [DataMember] public AsyncCommand DenyCommand        { get; }
    [DataMember] public AsyncCommand OpenDiffCommand    { get; }

    /// <summary>The answer the card waits for; completes once, from a button, the keyboard or the run's end.</summary>
    internal Task<ApprovalDecision> Decision => _decision?.Task ?? Task.FromResult(ApprovalDecision.Deny);

    /// <summary>"Open diff" is offered: the approval carries a change the dialog can show whole.</summary>
    [DataMember] public bool   CanOpenDiff    { get; private set; }

    /// <summary>Answers the card: the first answer wins, the card freezes and shows it.</summary>
    internal bool Answer(ApprovalDecision decision)
    {
        if (_decision is null || !_decision.TrySetResult(decision)) return false;
        IsAnswered   = true;
        ChosenAnswer = decision switch { ApprovalDecision.Once => "once", ApprovalDecision.Always => "always", _ => "deny" };
        return true;
    }

    /// <summary>The run this card belonged to was stopped: the card freezes without showing an answer nobody gave, and
    /// its wait ends denied (fail closed).</summary>
    internal void Retire()
    {
        if (_decision is null || !_decision.TrySetResult(ApprovalDecision.Deny)) return;
        IsAnswered = true;
    }

    [DataMember] public AsyncCommand ToggleExpandCommand { get; }
    [DataMember] public AsyncCommand CopyCommand         { get; }
    [DataMember] public AsyncCommand FixWithAiCommand    { get; }
    [DataMember] public AsyncCommand ResumeCommand       { get; }
    [DataMember] public AsyncCommand RegenerateCommand   { get; }

    public ChatMessageItem()
    {
        ToggleExpandCommand = new AsyncCommand(ToggleExpandAsync);
        CopyCommand         = new AsyncCommand(CopyContentAsync);
        FixWithAiCommand    = new AsyncCommand(FixWithAiAsync);
        ResumeCommand       = new AsyncCommand(ResumeAsync);
        RegenerateCommand   = new AsyncCommand(RegenerateAsync);
        UndoCommand         = new AsyncCommand((_, _) => { _onUndo?.Invoke(); return Task.CompletedTask; });
        AllowOnceCommand    = new AsyncCommand((_, _) => { Answer(ApprovalDecision.Once);   return Task.CompletedTask; });
        AlwaysAllowCommand  = new AsyncCommand((_, _) => { Answer(ApprovalDecision.Always); return Task.CompletedTask; });
        DenyCommand         = new AsyncCommand((_, _) => { Answer(ApprovalDecision.Deny);   return Task.CompletedTask; });
        OpenDiffCommand     = new AsyncCommand((_, _) => { _onOpenDiff?.Invoke(); return Task.CompletedTask; });
    }

    private Task ToggleExpandAsync(object? _, CancellationToken ct)
    {
        if (Role == "tool")
            IsExpanded = !IsExpanded;
        else if (Role == "run")
            SetRunOpen(!IsExpanded);
        return Task.CompletedTask;
    }

    /// <summary>Opens or folds a run line: its steps are drawn when it is open.</summary>
    internal void SetRunOpen(bool open)
    {
        IsExpanded = open;
        foreach (var step in Steps)
            step.IsFolded = !open;
    }

    /// <summary>Adds a step to a run line, folded as the line is; a failed step opens the line.</summary>
    internal void AddStep(ChatMessageItem step)
    {
        Steps.Add(step);
        step.IsFolded = !IsExpanded;
        Label = Strings.RunSteps(Steps.Count);
        if (step.HasErrors)
            SetRunOpen(true);
    }

    /// <summary>The run has ended: its line says what it did and how its last check went.</summary>
    internal void EndRun(Services.Presentation.RunSummaryModel? summary)
    {
        if (summary is not null)
        {
            Label     = summary.Title;
            RunDetail = summary.Detail;
        }
        RunFailed = summary?.Check is Services.Presentation.RunCheck.BuildFailed or Services.Presentation.RunCheck.TestsFailed
                    || Steps.Exists(s => s.HasErrors) && summary?.Check is null or Services.Presentation.RunCheck.None;
        RunGlyph  = RunFailed ? Glyphs.Failed : Glyphs.Check;
    }

    internal void InitFixCallback(string fullErrorOutput, Action<string> onFixWithAi)
    {
        _fixErrorOutput = fullErrorOutput;
        _onFixWithAi    = onFixWithAi;
        BtnFixWithAi    = Strings.BtnFixWithAi;
        IsFixable       = true;
    }

    internal void InitRegenerateCallback(Action onRegenerate)
    {
        _onRegenerate   = onRegenerate;
        BtnRegenerate   = Strings.BtnRegenerate;
        IsRegeneratable = true;
    }

    /// <summary>Shows a "Resume" button on this bubble (used by the step-mode pause bubble).</summary>
    internal void InitResumeCallback(Action onResume)
    {
        _onResume   = onResume;
        BtnResume   = Strings.BtnResume;
        IsResumable = true;
    }

    private Task ResumeAsync(object? _, CancellationToken ct)
    {
        _onResume?.Invoke();
        return Task.CompletedTask;
    }

    private Task RegenerateAsync(object? _, CancellationToken ct)
    {
        _onRegenerate?.Invoke();
        return Task.CompletedTask;
    }

    internal void InitDiff(DiffInfo diff)
    {
        DiffLines.Clear();
        foreach (var line in DiffComputer.Compute(diff.OldText, diff.NewText))
            DiffLines.Add(DiffLine.FromModel(line));
        HasDiff = DiffLines.Count > 0;
    }

    private Task FixWithAiAsync(object? _, CancellationToken ct)
    {
        if (_onFixWithAi is not null)
            _onFixWithAi(_fixErrorOutput);
        return Task.CompletedTask;
    }

    private Task CopyContentAsync(object? _, CancellationToken ct)
    {
        // Copy what the bubble shows: a streamed answer keeps the model's inline reasoning in its content.
        var text = Services.Presentation.MarkdownParser.ShownText(Role, Content);
        ClipboardHelper.TrySet(text, "Clipboard.CopyMessage");
        return Task.CompletedTask;
    }

    [DataMember]
    public bool IsStreaming
    {
        get => _isStreaming;
        set
        {
            SetProperty(ref _isStreaming, value);
            if (!value && Role == "assistant")
                ParseMarkdown();
        }
    }

    private void ParseMarkdown()
    {
        Blocks.Clear();
        // The item knows its theme only by the colours it was given: the dark palette's code ground says which.
        var palette        = ThemePalette.WithCodeBg(_themeCodeBg);
        var tableBorder    = palette.Border;
        var tableHeaderBg  = palette.Surface;

        foreach (var model in MarkdownParser.Parse(Content))
        {
            // Map the neutral parse result to the Remote-UI observable block, then theme it.
            var b = MarkdownBlock.FromModel(model);
            b.ThemeText        = _themeText;
            b.ThemeCodeText    = _themeCodeText;
            b.ThemeCodeBg      = _themeCodeBg;
            b.ThemeCodeBorder  = _themeCodeBorder;
            b.ThemeTableBorder = tableBorder;
            foreach (var run in b.Inlines)
                run.Foreground = run.IsCode ? _themeCodeText : _themeText;
            foreach (var cell in b.Cells)
            {
                cell.ThemeText = _themeText;
                cell.ThemeBg   = cell.IsHeader ? tableHeaderBg : "Transparent";
            }
            Blocks.Add(b);
        }
        HasBlocks = Blocks.Count > 0;
    }

    private static string Now() => DateTime.Now.ToString("t", System.Globalization.CultureInfo.CurrentCulture);

    internal static ChatMessageItem UserMsg(string content)
    {
        var item = new ChatMessageItem { Role = "user", Content = content, Label = ConversationExporter.RoleLabel("user"), Timestamp = Now() };
        item.SplitQuestion();
        return item;
    }

    /// <summary>A question is drawn as its text, with what went with it as chips under it.</summary>
    private void SplitQuestion()
    {
        var (text, names) = Services.Agent.ChatTurnPolicy.SplitBubbleText(Content);
        UserText = text;
        Chips.Clear();
        foreach (var name in names) Chips.Add(name);
        HasChips = Chips.Count > 0;
    }

    /// <summary>A turn's header line: the model that answers, then what the turn is doing, then how long it took.</summary>
    internal static ChatMessageItem TurnMsg(string model) =>
        new() { Role = "turn", Label = model, TurnState = Strings.TurnWorking(1) };

    /// <summary>A run line: "N steps · …", folded unless the user keeps tool calls open.</summary>
    internal static ChatMessageItem RunMsg(bool open) =>
        new() { Role = "run", IsExpanded = open };

    /// <summary>A run's result bar: the files it changed, its last check, and Undo for the run that is still the last.</summary>
    internal static ChatMessageItem ResultMsg(Services.Presentation.RunSummaryModel summary, Action? onUndo, Action<string> openFile)
    {
        var item = new ChatMessageItem
        {
            Role        = "result",
            CheckText   = summary.CheckText,
            HasCheck    = summary.Check != Services.Presentation.RunCheck.None,
            CheckFailed = summary.Check is Services.Presentation.RunCheck.BuildFailed or Services.Presentation.RunCheck.TestsFailed,
            BtnUndo     = Strings.RunUndo,
            CanUndo     = onUndo is not null,
            _onUndo     = onUndo,
        };
        foreach (var f in summary.Files)
            item.ResultFiles.Add(new ResultFileItem(f, openFile));
        return item;
    }

    /// <summary>An approval card, waiting for its answer (<see cref="Decision"/>).</summary>
    internal static ChatMessageItem ApprovalMsg(Services.Presentation.ApprovalCardModel card, Action? onOpenDiff)
    {
        var item = new ChatMessageItem
        {
            Role           = "approval",
            Content        = card.Message,
            CardTitle      = card.Title,
            CardSubject    = string.Join(" · ", new[] { card.Subject, card.Meta }.Where(p => p.Length > 0)),
            CardMore       = card.More,
            CardAlwaysTip  = card.AlwaysTooltip,
            BtnAllowOnce   = Strings.ApprovalAllowOnce,
            BtnAlwaysAllow = Strings.ApprovalAlwaysAllow,
            BtnDeny        = Strings.ApprovalDeny,
            BtnOpenDiff    = Strings.ApprovalOpenDiff,
            _decision      = new TaskCompletionSource<ApprovalDecision>(TaskCreationOptions.RunContinuationsAsynchronously),
            _onOpenDiff    = onOpenDiff,
            CanOpenDiff    = onOpenDiff is not null,
        };
        foreach (var line in card.Preview)
        {
            item.CardLines.Add(new DiffLine
            {
                Prefix = line.Kind switch { "add" => "+", "del" => "-", "gap" => "…", _ => " " },
                Text   = line.Text,
            });
        }
        item.HasCardLines = item.CardLines.Count > 0;
        return item;
    }

    internal static ChatMessageItem AssistantMsg(string content = "")
    {
        var item = new ChatMessageItem { Role = "assistant", Content = content, Label = ConversationExporter.RoleLabel("assistant"), Timestamp = Now() };
        item.ParseMarkdown();
        return item;
    }

    /// <summary>
    /// An assistant bubble that is a NOTICE, not an answer the model gave: an end notice, a slash command's output, a
    /// failed save. Same bubble on screen; marked so a restored conversation does not hand it back to the model as
    /// another answer — live, a turn keeps one (<c>SessionManager.NoticeMarker</c>).
    /// </summary>
    internal static ChatMessageItem NoticeMsg(string content)
    {
        var item = AssistantMsg(content);
        item.ToolName = Services.Persistence.SessionManager.NoticeMarker;
        return item;
    }

    internal static ChatMessageItem StreamingMsg(string? modelName = null) =>
        new() { Role = "assistant", Label = ConversationExporter.RoleLabel("assistant", modelName), IsStreaming = true, Timestamp = Now() };

    internal static ChatMessageItem ToolMsg(string toolName, string content, bool expanded = false) =>
        new() { Role = "tool", ToolName = toolName, Content = content,
                Label = ConversationExporter.RoleLabel("tool", toolName), IsExpanded = expanded, Timestamp = Now() };

    internal static ChatMessageItem Anchor() =>
        new() { Role = "anchor" };

    /// <summary>
    /// Creates a live "what-is-the-agent-doing" notification bubble (Role = "status").
    /// The bubble is displayed in the chat during agent execution and removed once the
    /// final response is inserted.  Its <see cref="Content"/> is updated in place as
    /// the agent moves through planning / tool-calling / observation phases.
    /// </summary>
    internal static ChatMessageItem StatusMsg(string step) =>
        new() { Role = "status", Content = step, Label = string.Empty };

    /// <summary>
    /// Creates a live plan bubble (Role = "plan") for the autonomous agent mode.
    /// The content is set once and later updated in-place via <see cref="RefreshPlan"/>.
    /// </summary>
    internal static ChatMessageItem AgentPlanMsg(Inferpal.Models.AgentPlan plan)
    {
        var item = new ChatMessageItem { Role = "plan", Label = Strings.AgentPlanLabel, Content = plan.ToMarkdown(), Timestamp = Now() };
        item.ApplyPlanCard();
        return item;
    }

    /// <summary>
    /// Regenerates the Markdown content of a plan bubble when any step status changes, and the card drawn from it.
    /// Called on the UI thread (the turn posts it), like every change to the chat's collections.
    /// </summary>
    internal void RefreshPlan(Inferpal.Models.AgentPlan plan)
    {
        Content = plan.ToMarkdown();
        ApplyPlanCard();
    }

    /// <summary>
    /// Draws the plan card from <see cref="Content"/> — the plan's Markdown, live or restored with a session. Rows are
    /// updated in place: a step changing state repaints its row instead of rebuilding the card.
    /// </summary>
    private void ApplyPlanCard()
    {
        var card = PlanCard.Read(Content);
        PlanGoal    = card.Goal;
        HasPlanGoal = card.Goal.Length > 0;
        for (var i = 0; i < card.Steps.Count; i++)
        {
            if (i < PlanSteps.Count) PlanSteps[i].Show(card.Steps[i]);
            else PlanSteps.Add(PlanStepItem.Of(card.Steps[i]));
        }
        while (PlanSteps.Count > card.Steps.Count) PlanSteps.RemoveAt(PlanSteps.Count - 1);
    }

    /// <summary>The message as the other editor saved it, for a role this one does not draw (VS Code's <c>error</c>).</summary>
    private SavedMessage? _foreign;

    /// <summary>What a session save writes for this bubble: the message as it was read, when it came from a role this
    /// editor does not draw — shown as a notice, never rewritten as one.</summary>
    internal (string Role, string Content, string ToolName, string Timestamp) Saved =>
        _foreign is { } f ? (f.Role, f.Content, f.ToolName ?? string.Empty, f.Timestamp ?? string.Empty)
                          : (Role, Content, ToolName, Timestamp);

    internal static ChatMessageItem FromSaved(string role, string content, string toolName, bool toolBubblesExpanded = false, string timestamp = "")
    {
        // ⚠ Both editors write the same session files. A role only the other draws (a failed turn, "Cancelled.", a
        // backend that did not answer: VS Code's `error`) had no template here — restored, the bubble was there and
        // invisible. Shown as a notice, it is kept as read for the next save.
        if (!Services.Persistence.SessionManager.VisualStudioBubbleRoles.Contains(role))
        {
            var notice = NoticeMsg(content);
            notice.Timestamp = timestamp;
            notice._foreign  = new SavedMessage(role, content, string.IsNullOrEmpty(toolName) ? null : toolName,
                                                string.IsNullOrEmpty(timestamp) ? null : timestamp);
            return notice;
        }

        var item = new ChatMessageItem
        {
            Role       = role,
            Content    = content,
            ToolName   = toolName,
            Timestamp  = timestamp,
            IsExpanded = role != "tool" || toolBubblesExpanded,
            // The same labels as live bubbles: the export heads every turn with them.
            Label      = role switch
            {
                "plan" => Strings.AgentPlanLabel,
                _      => ConversationExporter.RoleLabel(role, toolName),
            },
        };
        if (role == "assistant")
            item.ParseMarkdown();
        if (role == "user")
            item.SplitQuestion();
        if (role == "plan")
            item.ApplyPlanCard();
        return item;
    }
}

/// <summary>One step of the plan card: its text, its state, and the glyph that says it.</summary>
[DataContract]
internal sealed class PlanStepItem : NotifyPropertyChangedObject
{
    private string _text  = string.Empty;
    private string _state = string.Empty;
    private string _glyph = string.Empty;

    [DataMember] public string Text  { get => _text;  set => SetProperty(ref _text,  value); }
    /// <summary>The <see cref="Inferpal.Models.AgentStepStatus"/> name: the XAML colours the row by it.</summary>
    [DataMember] public string State { get => _state; set => SetProperty(ref _state, value); }
    [DataMember] public string Glyph { get => _glyph; set => SetProperty(ref _glyph, value); }

    internal static PlanStepItem Of(PlanCardStep step)
    {
        var item = new PlanStepItem();
        item.Show(step);
        return item;
    }

    internal void Show(PlanCardStep step)
    {
        Text  = step.Text;
        State = step.Status.ToString();
        Glyph = step.Status switch
        {
            Inferpal.Models.AgentStepStatus.Done    => Glyphs.Check,
            Inferpal.Models.AgentStepStatus.Active  => Glyphs.Running,
            Inferpal.Models.AgentStepStatus.Failed  => Glyphs.Failed,
            Inferpal.Models.AgentStepStatus.Skipped => Glyphs.Skipped,
            _                                      => Glyphs.Pending,
        };
    }
}

/// <summary>One file a run changed, as its result bar shows it: name, lines added and removed; a click opens it.</summary>
[DataContract]
internal sealed class ResultFileItem
{
    private readonly Action<string> _open;

    public ResultFileItem(Services.Presentation.RunFileLine file, Action<string> open)
    {
        _open   = open;
        Name    = file.Name;
        Path    = file.Path;
        Added   = "+" + file.Added.ToString(System.Globalization.CultureInfo.CurrentCulture);
        Removed = "−" + file.Removed.ToString(System.Globalization.CultureInfo.CurrentCulture);
        CanOpen = !file.Gone;
        OpenCommand = new AsyncCommand((_, _) => { if (CanOpen) _open(Path); return Task.CompletedTask; });
    }

    [DataMember] public string Name    { get; }
    [DataMember] public string Path    { get; }
    [DataMember] public string Added   { get; }
    [DataMember] public string Removed { get; }
    public bool CanOpen { get; }
    [DataMember] public AsyncCommand OpenCommand { get; }
}

/// <summary>The Segoe MDL2 Assets glyphs the chat draws from code (the XAML holds the rest).</summary>
internal static class Glyphs
{
    public const string Tool   = "";   // Repair: a run still working
    public const string Check  = "";   // CheckMark: a run or a step that passed
    public const string Failed = "";   // Cancel: a run or a step that failed
    public const string Running = "";  // Recent (a clock): the plan step being worked on
    public const string Pending = "";  // CircleRing: a plan step not started
    public const string Skipped = "";  // Next: a plan step the agent skipped
}
