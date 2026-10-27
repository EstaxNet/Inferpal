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
    private Action?         _onContinue;
    private string          _btnContinue     = string.Empty;
    private bool            _isContinuable;
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
    private string _themeLinkText    = "#B39DFF";

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
    /// <summary>The end notice of a turn stopped short (iteration limit, length limit) offers Continue — the last turn's
    /// only, until another question leaves. Screen only: a reloaded session has none.</summary>
    [DataMember] public bool   IsContinuable    { get => _isContinuable;    set => SetProperty(ref _isContinuable,    value); }
    [DataMember] public string BtnContinue      { get => _btnContinue;      set => SetProperty(ref _btnContinue,      value); }
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
    /// <summary>The colour of a link's words: the palette's accent for text.</summary>
    [DataMember] public string ThemeLinkText    { get => _themeLinkText;    set => SetProperty(ref _themeLinkText,    value); }

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

    // ── A note typed while the run worked: a user bubble, with the line that says whether the model read it ──
    private string _noteState = string.Empty;
    private bool   _isNote;
    [DataMember] public string NoteState { get => _noteState; set => SetProperty(ref _noteState, value); }
    [DataMember] public bool   IsNote    { get => _isNote;    set => SetProperty(ref _isNote,    value); }

    /// <summary>The note's id in the run's steering: the run says by it that the model read the note.</summary>
    internal string? NoteId { get; private init; }

    /// <summary>The model read this note: saved with <c>SessionManager.NoteMarker</c>, folded into its question by a
    /// restore. A note not read yet is not saved as one — the model never read it with its question.</summary>
    internal bool NoteRead { get; private set; }

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
    private Action? _onReview;
    /// <summary>The bar's "Review changes": every bar of a run that changed files offers it.</summary>
    [DataMember] public bool   CanReview   { get; private set; }
    [DataMember] public string BtnReview   { get; private set; } = string.Empty;
    [DataMember] public AsyncCommand ReviewCommand { get; }
    /// <summary>The run a result bar or a review card belongs to; empty for any other item.</summary>
    internal string RunId { get; private set; } = string.Empty;

    // ── A run's review card ("review"): each file of the run against the state before it ──
    private string _reviewStatus = string.Empty;
    private Action? _onCloseReview;
    [DataMember] public string ReviewStatus { get => _reviewStatus; set { SetProperty(ref _reviewStatus, value); HasReviewStatus = value.Length > 0; } }
    private bool _hasReviewStatus;
    [DataMember] public bool   HasReviewStatus { get => _hasReviewStatus; private set => SetProperty(ref _hasReviewStatus, value); }
    private string _reviewNote = string.Empty;
    [DataMember] public string ReviewNote { get => _reviewNote; private set => SetProperty(ref _reviewNote, value); }
    [DataMember] public ObservableCollection<ReviewFileItem> ReviewFiles { get; } = [];
    [DataMember] public AsyncCommand CloseReviewCommand { get; }

    // ── A question's way back: its "Go back here", and the card ("rewind") it opens under it ──
    private Action<ChatMessageItem>? _onRewindHere;
    /// <summary>A question the model was asked — never a slash command's echo or a note — can be gone back to.</summary>
    [DataMember] public bool CanRewind => Role == "user" && !IsNote
        && Services.Persistence.SessionManager.IsQuestion(new SavedMessage(Role, Content, ToolName.Length > 0 ? ToolName : null));
    [DataMember] public AsyncCommand RewindHereCommand { get; }

    /// <summary>What a question's "Go back here" does; set when the item is shown (<c>ApplyItemTheme</c>).</summary>
    internal void HookRewind(Action<ChatMessageItem> rewindHere) => _onRewindHere = rewindHere;

    /// <summary>The runs the card shows: what Go back expects to undo, and what Review opens.</summary>
    internal IReadOnlyList<string> RewindRunIds { get; private set; } = [];

    /// <summary>The question a "rewind" card is under.</summary>
    internal ChatMessageItem? RewindFor { get; private set; }
    private Action? _onRewindGo, _onRewindCancel, _onRewindConversationOnly, _onRewindReview;
    private string _rewindTitle = string.Empty, _rewindBody = string.Empty, _rewindNote = string.Empty, _rewindStatus = string.Empty;
    private bool _isRewindRefused, _canRewindAct = true, _hasRewindReview;
    [DataMember] public string RewindTitle  { get => _rewindTitle;  private set => SetProperty(ref _rewindTitle,  value); }
    [DataMember] public string RewindBody   { get => _rewindBody;   private set => SetProperty(ref _rewindBody,   value); }
    [DataMember] public string RewindNote   { get => _rewindNote;   private set => SetProperty(ref _rewindNote,   value); }
    [DataMember] public string RewindStatus { get => _rewindStatus; private set { SetProperty(ref _rewindStatus, value); RaiseNotifyPropertyChangedEvent(nameof(HasRewindStatus)); } }
    [DataMember] public bool   HasRewindStatus => _rewindStatus.Length > 0;
    /// <summary>Nothing can be undone: the card offers the conversation only, or the runs' reviews, and no Go back.</summary>
    [DataMember] public bool   IsRewindRefused { get => _isRewindRefused; private set => SetProperty(ref _isRewindRefused, value); }
    /// <summary>False from a click until the answer: one going back at a time.</summary>
    [DataMember] public bool   CanRewindAct    { get => _canRewindAct;    internal set => SetProperty(ref _canRewindAct,    value); }
    [DataMember] public bool   HasRewindReview { get => _hasRewindReview; private set => SetProperty(ref _hasRewindReview, value); }
    [DataMember] public ObservableCollection<RewindFileItem> RewindFiles { get; } = [];
    [DataMember] public string BtnRewindGo { get; private set; } = string.Empty;
    [DataMember] public string BtnRewindCancel { get; private set; } = string.Empty;
    [DataMember] public string BtnRewindConversationOnly { get; private set; } = string.Empty;
    [DataMember] public string RewindConversationOnlyHint { get; private set; } = string.Empty;
    [DataMember] public string BtnRewindReview { get; private set; } = string.Empty;
    [DataMember] public string RewindReviewHint { get; private set; } = string.Empty;
    [DataMember] public AsyncCommand RewindGoCommand { get; }
    [DataMember] public AsyncCommand RewindCancelCommand { get; }
    [DataMember] public AsyncCommand RewindConversationOnlyCommand { get; }
    [DataMember] public AsyncCommand RewindReviewCommand { get; }

    /// <summary>A question's "Go back here" card, under <paramref name="question"/>.</summary>
    internal static ChatMessageItem RewindMsg(ChatMessageItem question, Action go, Action cancel, Action conversationOnly,
                                              Action review) =>
        new()
        {
            Role = "rewind", RewindFor = question, _onRewindGo = go, _onRewindCancel = cancel,
            _onRewindConversationOnly = conversationOnly, _onRewindReview = review,
            BtnRewindGo = Strings.RewindGo, BtnRewindCancel = Strings.RewindCancel,
            BtnRewindConversationOnly = Strings.RewindConversationOnly, RewindConversationOnlyHint = Strings.RewindConversationOnlyHint,
            BtnRewindReview = Strings.RewindReviewRuns, RewindReviewHint = Strings.RewindReviewRunsHint,
        };

    /// <summary>Draws <paramref name="card"/> in this card, its actions back; <paramref name="status"/> under it.</summary>
    internal void ShowRewind(Services.Presentation.RewindCardModel card, string? status)
    {
        RewindTitle     = card.Title;
        RewindBody      = card.Body;
        RewindNote      = card.Note;
        IsRewindRefused = card.Refused;
        HasRewindReview = card.Refused && card.RunIds.Count > 0;
        RewindRunIds    = card.RunIds;
        RewindFiles.Clear();
        foreach (var f in card.Files) RewindFiles.Add(new RewindFileItem(f));
        RewindStatus    = status ?? string.Empty;
        CanRewindAct    = true;
    }

    /// <summary>The card as it was, its actions back, under <paramref name="status"/>: what failed, or why nothing ran.</summary>
    internal void RewindSays(string status)
    {
        RewindStatus = status;
        CanRewindAct = true;
    }

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
    /// <summary>The narrow rule the card offers ("Always: commands that start with `dotnet test`"); empty when none.</summary>
    [DataMember] public string BtnAlwaysRule  { get; private set; } = string.Empty;
    [DataMember] public string CardRuleTip    { get; private set; } = string.Empty;
    [DataMember] public bool   HasRule        { get; private set; }
    [DataMember] public ObservableCollection<DiffLine> CardLines { get; } = [];
    /// <summary>Answered (or retired with its run): the buttons stop answering.</summary>
    [DataMember] public bool   IsAnswered     { get => _isAnswered; set => SetProperty(ref _isAnswered, value); }
    /// <summary>The answer given — <c>once</c>, <c>always</c>, <c>rule</c> or <c>deny</c> — so the card shows which.</summary>
    [DataMember] public string ChosenAnswer   { get => _chosen;     set => SetProperty(ref _chosen,     value); }
    [DataMember] public AsyncCommand AllowOnceCommand   { get; }
    [DataMember] public AsyncCommand AlwaysAllowCommand { get; }
    [DataMember] public AsyncCommand AlwaysRuleCommand  { get; }
    [DataMember] public AsyncCommand DenyCommand        { get; }
    [DataMember] public AsyncCommand OpenDiffCommand    { get; }

    /// <summary>The answer the card waits for; completes once, from a button, the keyboard or the run's end.</summary>
    internal Task<ApprovalDecision> Decision => _decision?.Task ?? Task.FromResult(ApprovalDecision.Deny);

    /// <summary>"Open diff" is offered: the approval carries a change the dialog can show whole.</summary>
    [DataMember] public bool   CanOpenDiff    { get; private set; }

    // ── An import card ("import"): a box per item, the reasons for the rest folded, Apply ──
    // Its title and summary are CardTitle and CardSubject, and IsAnswered freezes it, as for an approval card.
    private Func<IReadOnlyList<int>, Task<bool>>? _onImportApply;
    [DataMember] public ObservableCollection<ImportRowItem>  ImportRows  { get; } = [];
    [DataMember] public ObservableCollection<ImportSkipItem> ImportSkips { get; } = [];
    [DataMember] public string ImportSkippedTitle { get; private set; } = string.Empty;
    [DataMember] public bool   HasImportRows      { get; private set; }
    [DataMember] public bool   HasImportSkips     { get; private set; }
    [DataMember] public string BtnImportApply     { get; private set; } = string.Empty;
    [DataMember] public AsyncCommand ImportApplyCommand { get; }
    private bool _importSkipsOpen;
    /// <summary>The reasons are folded by default: a Roo export names some sixty settings it leaves out.</summary>
    [DataMember] public bool ImportSkipsOpen { get => _importSkipsOpen; set => SetProperty(ref _importSkipsOpen, value); }
    [DataMember] public AsyncCommand ToggleImportSkipsCommand { get; }

    // ── An input card ("inputcard"): an MCP server's form, then Send, Decline, Cancel — the card the Core builds ──
    // Its title is CardTitle, IsAnswered freezes it. Screen only: what it leaves is a receipt ("input"), saved.
    private TaskCompletionSource<InputReply?>? _inputReply;
    private string _inputErrorSummary = string.Empty;
    private bool   _hasInputError;
    [DataMember] public string InputSubtitle  { get; private set; } = string.Empty;
    [DataMember] public string InputMessage   { get; private set; } = string.Empty;
    [DataMember] public string InputReminder  { get; private set; } = string.Empty;
    [DataMember] public string InputErrorSummary { get => _inputErrorSummary; private set => SetProperty(ref _inputErrorSummary, value); }
    [DataMember] public bool   HasInputError  { get => _hasInputError; private set => SetProperty(ref _hasInputError, value); }
    [DataMember] public string BtnInputSend    { get; private set; } = string.Empty;
    [DataMember] public string BtnInputDecline { get; private set; } = string.Empty;
    [DataMember] public string BtnInputCancel  { get; private set; } = string.Empty;
    [DataMember] public string InputDeclineTip { get; private set; } = string.Empty;
    [DataMember] public string InputCancelTip  { get; private set; } = string.Empty;
    [DataMember] public ObservableCollection<InputFieldItem> InputFields { get; } = [];
    [DataMember] public AsyncCommand InputSendCommand    { get; }
    [DataMember] public AsyncCommand InputDeclineCommand { get; }
    [DataMember] public AsyncCommand InputCancelCommand  { get; }

    /// <summary>The Core's id of the card: the same card shown again with what to fix is drawn in this item.</summary>
    internal string InputCardId { get; private set; } = string.Empty;

    /// <summary>What the person did on the card; <c>null</c> when it was taken away unanswered (its run stopped).</summary>
    internal Task<InputReply?> InputReply => _inputReply?.Task ?? Task.FromResult<InputReply?>(null);

    /// <summary>An input card, waiting for its answer (<see cref="InputReply"/>).</summary>
    internal static ChatMessageItem InputCardMsg(InputCardModel card)
    {
        var item = new ChatMessageItem
        {
            Role          = InputCard.CardRole,
            Content       = card.Message,
            CardTitle     = card.Title,
            InputSubtitle = card.RequiredNote.Length > 0 ? card.Subtitle + " · " + card.RequiredNote : card.Subtitle,
            // The server's own words, as plain text: never Markdown, never a link.
            InputMessage  = "“" + card.Message + "”",
            InputReminder = card.Reminder,
            BtnInputSend    = card.SendLabel,
            BtnInputDecline = card.DeclineLabel,
            BtnInputCancel  = card.CancelLabel,
            InputDeclineTip = card.DeclineTooltip,
            InputCancelTip  = card.CancelTooltip,
            InputCardId   = card.Id,
        };
        item.ShowInput(card);
        return item;
    }

    /// <summary>The card shown again, under the same id, with what to fix: its fields as the Core rebuilt them, live.</summary>
    internal void ShowInput(InputCardModel card)
    {
        InputFields.Clear();
        foreach (var f in card.Fields)
            InputFields.Add(new InputFieldItem(f, card.Id, () => AnswerInput(Services.Mcp.McpInputAction.Accept)));
        InputErrorSummary = card.ErrorSummary;
        HasInputError     = card.ErrorSummary.Length > 0;
        _inputReply       = new TaskCompletionSource<InputReply?>(TaskCreationOptions.RunContinuationsAsynchronously);
        IsAnswered        = false;
    }

    /// <summary>Answers the card with the fields as they stand: the first answer wins, and the card freezes.</summary>
    internal bool AnswerInput(Services.Mcp.McpInputAction action)
    {
        if (_inputReply is null || IsAnswered) return false;
        var values = action == Services.Mcp.McpInputAction.Accept
            ? InputFields.ToDictionary(f => f.Key, f => f.Value(), StringComparer.Ordinal)
            : new Dictionary<string, Services.Mcp.McpInputValue>(StringComparer.Ordinal);
        // ⚠ Frozen BEFORE the answer goes: its continuation may draw the card again with what to fix (ShowInput), and a
        // freeze landing after it would leave that card unanswerable, the run waiting until Stop.
        IsAnswered = true;
        return _inputReply.TrySetResult(new InputReply(action, values));
    }

    /// <summary>The run this card belonged to was stopped: frozen, its wait ends unanswered.</summary>
    internal void RetireInput()
    {
        if (_inputReply is null || _inputReply.Task.IsCompleted) return;
        IsAnswered = true;
        _inputReply.TrySetResult(null);
    }

    // ── An input card's receipt ("input"): what the person did with the form, and what they sent, folded ──
    [DataMember] public string ReceiptTitle  { get; private set; } = string.Empty;
    [DataMember] public string ReceiptDetail { get; private set; } = string.Empty;
    [DataMember] public string ReceiptBody   { get; private set; } = string.Empty;
    [DataMember] public bool   HasReceiptBody { get; private set; }

    /// <summary>The line an answered card leaves; its <see cref="Content"/> is what the conversation saves.</summary>
    internal static ChatMessageItem InputReceiptMsg(InputReceipt receipt, string? timestamp = null)
    {
        var item = new ChatMessageItem
        {
            Role           = InputCard.ReceiptRole,
            Content        = receipt.Saved,
            Timestamp      = timestamp ?? Now(),
            ReceiptTitle   = receipt.Title,
            ReceiptDetail  = receipt.Detail,
            ReceiptBody    = receipt.Body,
            HasReceiptBody = receipt.Body.Length > 0,
            IsExpanded     = false,
        };
        item.RunGlyph = item.HasReceiptBody ? Glyphs.Check : Glyphs.Info;
        return item;
    }

    /// <summary>
    /// The import card's Apply: once, with the boxes left ticked. Refused (a turn running), the card comes back live.
    /// </summary>
    private async Task ApplyImportAsync(object? _, CancellationToken ct)
    {
        if (IsAnswered || _onImportApply is null) return;
        IsAnswered = true;
        if (!await _onImportApply([.. ImportRows.Where(r => r.IsChecked).Select(r => r.Number)]))
            IsAnswered = false;
    }

    /// <summary>Answers the card: the first answer wins, the card freezes and shows it.</summary>
    internal bool Answer(ApprovalDecision decision)
    {
        if (_decision is null || !_decision.TrySetResult(decision)) return false;
        IsAnswered   = true;
        ChosenAnswer = decision switch
        {
            ApprovalDecision.Once => "once", ApprovalDecision.Always => "always", ApprovalDecision.Rule => "rule", _ => "deny",
        };
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
    [DataMember] public AsyncCommand ContinueCommand     { get; }
    [DataMember] public AsyncCommand RegenerateCommand   { get; }

    public ChatMessageItem()
    {
        ToggleExpandCommand = new AsyncCommand(ToggleExpandAsync);
        CopyCommand         = new AsyncCommand(CopyContentAsync);
        FixWithAiCommand    = new AsyncCommand(FixWithAiAsync);
        ResumeCommand       = new AsyncCommand(ResumeAsync);
        ContinueCommand     = new AsyncCommand((_, _) => { if (IsContinuable) _onContinue?.Invoke(); return Task.CompletedTask; });
        RegenerateCommand   = new AsyncCommand(RegenerateAsync);
        UndoCommand         = new AsyncCommand((_, _) => { _onUndo?.Invoke(); return Task.CompletedTask; });
        ReviewCommand       = new AsyncCommand((_, _) => { _onReview?.Invoke(); return Task.CompletedTask; });
        CloseReviewCommand  = new AsyncCommand((_, _) => { _onCloseReview?.Invoke(); return Task.CompletedTask; });
        RewindHereCommand   = new AsyncCommand((_, _) => { if (CanRewind) _onRewindHere?.Invoke(this); return Task.CompletedTask; });
        // One going back at a time: the card is drawn again from the answer (ShowRewind, RewindSays).
        AsyncCommand RewindAction(Func<Action?> act) => new((_, _) =>
        {
            if (!CanRewindAct || act() is not { } run) return Task.CompletedTask;
            CanRewindAct = false;
            run();
            return Task.CompletedTask;
        });
        RewindGoCommand               = RewindAction(() => _onRewindGo);
        RewindConversationOnlyCommand = RewindAction(() => _onRewindConversationOnly);
        RewindCancelCommand = new AsyncCommand((_, _) => { _onRewindCancel?.Invoke(); return Task.CompletedTask; });
        RewindReviewCommand = new AsyncCommand((_, _) => { _onRewindReview?.Invoke(); return Task.CompletedTask; });
        AllowOnceCommand    = new AsyncCommand((_, _) => { Answer(ApprovalDecision.Once);   return Task.CompletedTask; });
        AlwaysAllowCommand  = new AsyncCommand((_, _) => { Answer(ApprovalDecision.Always); return Task.CompletedTask; });
        AlwaysRuleCommand   = new AsyncCommand((_, _) => { if (HasRule) Answer(ApprovalDecision.Rule); return Task.CompletedTask; });
        DenyCommand         = new AsyncCommand((_, _) => { Answer(ApprovalDecision.Deny);   return Task.CompletedTask; });
        OpenDiffCommand     = new AsyncCommand((_, _) => { _onOpenDiff?.Invoke(); return Task.CompletedTask; });
        ImportApplyCommand  = new AsyncCommand(ApplyImportAsync);
        ToggleImportSkipsCommand = new AsyncCommand((_, _) => { ImportSkipsOpen = !ImportSkipsOpen; return Task.CompletedTask; });
        InputSendCommand    = new AsyncCommand((_, _) => { AnswerInput(Services.Mcp.McpInputAction.Accept);  return Task.CompletedTask; });
        InputDeclineCommand = new AsyncCommand((_, _) => { AnswerInput(Services.Mcp.McpInputAction.Decline); return Task.CompletedTask; });
        InputCancelCommand  = new AsyncCommand((_, _) => { AnswerInput(Services.Mcp.McpInputAction.Cancel);  return Task.CompletedTask; });
    }

    private Task ToggleExpandAsync(object? _, CancellationToken ct)
    {
        if (Role == "tool" || Role == InputCard.ReceiptRole && HasReceiptBody)
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

    /// <summary>Shows a "Continue" button on this end notice (<c>ChatTurnPolicy.OffersContinue</c>).</summary>
    internal void InitContinueCallback(Action onContinue)
    {
        _onContinue   = onContinue;
        BtnContinue   = Strings.BtnContinue;
        IsContinuable = true;
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
            b.ApplyTheme(_themeText, _themeCodeText, _themeCodeBg, _themeCodeBorder, _themeLinkText, tableBorder, tableHeaderBg);
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

    /// <summary>
    /// A note typed while the run worked (<c>RunSteering</c>): drawn like a question, under a line saying it waits for the
    /// run's next step, then that the model read it. Never a question: <c>ToolName</c> carries
    /// <c>SessionManager.NoteMarker</c> once read (see <see cref="Saved"/>), and Regenerate skips it.
    /// </summary>
    internal static ChatMessageItem NoteMsg(string id, string text, bool read = false, int afterCalls = 0)
    {
        var item = new ChatMessageItem
        {
            Role = "user", Content = text, UserText = text, IsNote = true, NoteId = id,
            Label = Strings.NoteLabel, Timestamp = Now(),
        };
        if (read) item.MarkNoteRead(afterCalls);
        else item.NoteState = Strings.NoteWaiting;
        return item;
    }

    /// <summary>The model read this note, after <paramref name="afterCalls"/> tool calls.</summary>
    internal void MarkNoteRead(int afterCalls)
    {
        NoteRead  = true;
        ToolName  = Services.Persistence.SessionManager.NoteMarker;
        NoteState = afterCalls == 0 ? Strings.NoteReadFirst : Strings.NoteReadAfter(afterCalls);
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
    internal static ChatMessageItem ResultMsg(Services.Presentation.RunSummaryModel summary, Action? onUndo,
                                              Action? onReview, Action<string> openFile)
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
            BtnReview   = Strings.RunReviewButton,
            CanReview   = onReview is not null,
            _onReview   = onReview,
            RunId       = summary.RunId,
        };
        foreach (var f in summary.Files)
            item.ResultFiles.Add(new ResultFileItem(f, openFile));
        return item;
    }

    /// <summary>A run's review card, under its result bar (<see cref="Services.Presentation.RunReviewModel"/>).</summary>
    /// <param name="act">A file's Keep, Undo or Redo: the file's path, the change of it (1-based; null: the whole file),
    /// and the action.</param>
    internal static ChatMessageItem ReviewMsg(string runId, Action onClose,
                                              Action<string, int?, Services.Commands.UndoRunCommandHandler.RunFileAction> act) =>
        new()
        {
            Role = "review", RunId = runId, _onCloseReview = onClose, _onReviewAction = act, ReviewNote = Strings.RunReviewNote,
            BtnCloseReview = Strings.TooltipXrayClose,
        };

    [DataMember] public string BtnCloseReview { get; private set; } = string.Empty;

    private Action<string, int?, Services.Commands.UndoRunCommandHandler.RunFileAction>? _onReviewAction;

    /// <summary>Draws <paramref name="review"/> in this card: its files replaced, the ones left open kept open.</summary>
    /// <param name="review"><c>null</c>: the run is no longer kept — only <paramref name="status"/> remains.</param>
    internal void ShowReview(Services.Presentation.RunReviewModel? review, string? status)
    {
        ReviewStatus = status ?? string.Empty;
        var open = ReviewFiles.Where(f => f.IsExpanded).Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        ReviewFiles.Clear();
        if (review is null)
        {
            CardTitle = string.Empty;
            CardSubject = string.Empty;
            return;
        }
        CardTitle   = Strings.RunReviewTitle;
        CardSubject = Strings.RunReviewSubtitle(review.Files.Count, review.Started);
        ReviewNote  = Services.Presentation.RunReview.Note(review);
        foreach (var f in review.Files)
            ReviewFiles.Add(new ReviewFileItem(f, open.Contains(f.Path), (path, piece, action) =>
            {
                EnableReviewActions(false);
                _onReviewAction?.Invoke(path, piece, action);
            }));
        RaiseNotifyPropertyChangedEvent(nameof(CardTitle));
        RaiseNotifyPropertyChangedEvent(nameof(CardSubject));
    }

    /// <summary>Every file action of the card on or off: off while one runs, on again when it fails without an answer.</summary>
    internal void EnableReviewActions(bool on)
    {
        foreach (var f in ReviewFiles) f.CanAct = on;
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
            BtnAlwaysRule  = card.RuleLabel,
            CardRuleTip    = card.RuleTooltip,
            HasRule        = card.RuleLabel.Length > 0,
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

    /// <summary>
    /// An <c>/import</c> preview, drawn as a card. A saved conversation keeps its Markdown as a notice — what VS Code
    /// keeps, and what the agent protocol shows.
    /// </summary>
    /// <param name="onApply">Runs the import with the numbers left ticked; <c>false</c> when it could not run.</param>
    internal static ChatMessageItem ImportMsg(Services.Import.ImportCard card, string markdown,
                                              Func<IReadOnlyList<int>, Task<bool>> onApply)
    {
        var item = new ChatMessageItem
        {
            Role               = "import",
            Content            = markdown,
            CardTitle          = card.Title,
            CardSubject        = card.Summary,
            ImportSkippedTitle = card.SkippedTitle,
            BtnImportApply     = card.ApplyLabel,
            Timestamp          = Now(),
            _onImportApply     = onApply,
        };
        item._foreign = new SavedMessage("assistant", markdown, Services.Persistence.SessionManager.NoticeMarker, item.Timestamp);
        foreach (var row in card.Items) item.ImportRows.Add(new ImportRowItem(row));
        foreach (var group in card.Skipped) item.ImportSkips.Add(new ImportSkipItem(group.Reason, string.Join(", ", group.Paths)));
        item.HasImportRows  = item.ImportRows.Count > 0;
        item.HasImportSkips = item.ImportSkips.Count > 0;
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
    internal (string Role, string Content, string ToolName, string Timestamp, string Run) Saved =>
        _foreign is { } f ? (f.Role, f.Content, f.ToolName ?? string.Empty, f.Timestamp ?? string.Empty, f.Run ?? RunId)
        // A note the model has not read is a notice: saved as a note, a reload folded it into a question it never went with.
        : IsNote && !NoteRead ? (Role, Content, Services.Persistence.SessionManager.NoticeMarker, Timestamp, string.Empty)
        : (Role, Content, ToolName, Timestamp, RunId);

    /// <summary>A restored message keeps the run its turn made (<see cref="SavedMessage.Run"/>): saved again, it says it.</summary>
    internal ChatMessageItem WithRun(string? run)
    {
        RunId = run ?? string.Empty;
        return this;
    }

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
        if (role == InputCard.ReceiptRole)
            return InputReceiptMsg(InputReceipt.FromSaved(content), timestamp);

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
        if (role == "user" && toolName == Services.Persistence.SessionManager.NoteMarker)
        {
            // A note keeps its look across a reload; how many steps it waited is not saved.
            item.UserText  = content;
            item.IsNote    = true;
            item.NoteRead  = true;
            item.NoteState = Strings.NoteLabel;
            item.Label     = Strings.NoteLabel;
        }
        else if (role == "user")
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

/// <summary>One box of an import card: what ticking it writes, and what is said under it.</summary>
[DataContract]
internal sealed class ImportRowItem : NotifyPropertyChangedObject
{
    private bool _isChecked;

    public ImportRowItem(Services.Import.ImportCardItem row)
    {
        Number     = row.Number;
        Target     = row.Target;
        Label      = row.Label;
        // The box's name is the whole line, as in VS Code: a value alone ("16 384", a model's name) does not say what
        // ticking it writes.
        AccessibleName = row.Target + " — " + row.Label;
        Notes      = string.Join("\n", row.Notes);
        HasNotes   = row.Notes.Count > 0;
        _isChecked = row.Checked;
    }

    [DataMember] public int    Number   { get; }
    [DataMember] public string Target   { get; }
    [DataMember] public string Label    { get; }
    [DataMember] public string AccessibleName { get; }
    [DataMember] public string Notes    { get; }
    [DataMember] public bool   HasNotes { get; }
    [DataMember] public bool   IsChecked { get => _isChecked; set => SetProperty(ref _isChecked, value); }
}

/// <summary>Parts of an import's source left out for one reason, and their paths.</summary>
[DataContract]
internal sealed class ImportSkipItem(string reason, string paths)
{
    [DataMember] public string Reason { get; } = reason;
    [DataMember] public string Paths  { get; } = paths;
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

/// <summary>One file of a question's "Go back here" card: what going back does to it.</summary>
[DataContract]
internal sealed class RewindFileItem
{
    public RewindFileItem(Services.Presentation.RewindCardLine line)
    {
        Name      = line.Name;
        Path      = line.Path;
        Label     = line.Label;
        IsDeleted = line.Kind == Services.Execution.RewindFileKind.Deleted;
    }

    [DataMember] public string Name      { get; }
    [DataMember] public string Path      { get; }
    [DataMember] public string Label     { get; }
    [DataMember] public bool   IsDeleted { get; }
}

/// <summary>One file of a run's review card: its diff folded, and the actions its state allows.</summary>
[DataContract]
internal sealed class ReviewFileItem : NotifyPropertyChangedObject
{
    private bool _isExpanded;

    /// <param name="act">The file's path, the change of it (1-based; null: the whole file), and the action.</param>
    public ReviewFileItem(Services.Presentation.RunReviewFile file, bool expanded,
                          Action<string, int?, Services.Commands.UndoRunCommandHandler.RunFileAction> act)
    {
        Name       = file.Name;
        Path       = file.Path;
        Added      = "+" + file.Added.ToString(System.Globalization.CultureInfo.CurrentCulture);
        Removed    = "−" + file.Removed.ToString(System.Globalization.CultureInfo.CurrentCulture);
        NewLabel   = file.Created ? Strings.RunFileNew : string.Empty;
        StateLabel = Services.Presentation.RunReview.StateLabel(file);
        IsChangedSince = file.State == Services.Presentation.RunFileState.ChangedSince;
        ChangedWhy = IsChangedSince ? Strings.RunFileChangedWhy : string.Empty;
        HasKeep    = file.State == Services.Presentation.RunFileState.Pending;
        HasUndo    = file.State is Services.Presentation.RunFileState.Pending or Services.Presentation.RunFileState.Kept;
        HasRedo    = file.CanRedo;
        BtnKeep    = Strings.RunFileKeep;
        BtnUndo    = file.Created ? Strings.RunFileDelete : Strings.RunFileUndo;
        BtnRedo    = Strings.RunFileRedo;
        WholeOnly  = Services.Presentation.RunReview.WholeOnlyLabel(file);
        PiecesSummary = Services.Presentation.RunReview.PiecesSummary(file);
        HasPieces  = file.Pieces is { Count: > 0 };
        _isExpanded = expanded;
        // A file of several changes shows each with its own actions instead of its diff.
        if (!HasPieces)
            foreach (var line in file.Diff)
                Lines.Add(new DiffLine { Prefix = line.Prefix, Text = line.Text });
        foreach (var piece in file.Pieces ?? [])
            Pieces.Add(new ReviewPieceItem(piece, action => act(Path, piece.Number, action)));
        CanAct     = true;
        ToggleCommand = new AsyncCommand((_, _) => { IsExpanded = !IsExpanded; return Task.CompletedTask; });
        // One action at a time on the card (it disables them all): the card is drawn again from the answer.
        AsyncCommand Act(Services.Commands.UndoRunCommandHandler.RunFileAction action) => new((_, _) =>
        {
            if (CanAct) act(Path, null, action);
            return Task.CompletedTask;
        });
        KeepCommand = Act(Services.Commands.UndoRunCommandHandler.RunFileAction.Keep);
        UndoCommand = Act(Services.Commands.UndoRunCommandHandler.RunFileAction.Undo);
        RedoCommand = Act(Services.Commands.UndoRunCommandHandler.RunFileAction.Redo);
    }

    [DataMember] public string Name           { get; }
    [DataMember] public string Path           { get; }
    [DataMember] public string Added          { get; }
    [DataMember] public string Removed        { get; }
    [DataMember] public string NewLabel       { get; }
    [DataMember] public string StateLabel     { get; }
    [DataMember] public bool   IsChangedSince { get; }
    [DataMember] public string ChangedWhy     { get; }
    [DataMember] public bool   HasKeep        { get; }
    [DataMember] public bool   HasUndo        { get; }
    [DataMember] public bool   HasRedo        { get; }
    [DataMember] public string BtnKeep        { get; }
    [DataMember] public string BtnUndo        { get; }
    [DataMember] public string BtnRedo        { get; }
    [DataMember] public string WholeOnly      { get; }
    [DataMember] public string PiecesSummary  { get; }
    [DataMember] public bool   HasPieces      { get; }
    private bool _canAct;
    /// <summary>False while an action of the card runs, its changes' too; set by the card
    /// (<see cref="ChatMessageItem.ShowReview"/>).</summary>
    [DataMember] public bool   CanAct
    {
        get => _canAct;
        internal set
        {
            SetProperty(ref _canAct, value);
            foreach (var piece in Pieces) piece.CanAct = value;
        }
    }
    [DataMember] public bool   IsExpanded { get => _isExpanded; private set => SetProperty(ref _isExpanded, value); }
    [DataMember] public ObservableCollection<DiffLine> Lines { get; } = [];
    [DataMember] public ObservableCollection<ReviewPieceItem> Pieces { get; } = [];
    [DataMember] public AsyncCommand ToggleCommand { get; }
    [DataMember] public AsyncCommand KeepCommand   { get; }
    [DataMember] public AsyncCommand UndoCommand   { get; }
    [DataMember] public AsyncCommand RedoCommand   { get; }
}

/// <summary>One change of a file of a run's review card: its lines, and the actions its state allows.</summary>
[DataContract]
internal sealed class ReviewPieceItem : NotifyPropertyChangedObject
{
    public ReviewPieceItem(Services.Presentation.RunReviewPiece piece, Action<Services.Commands.UndoRunCommandHandler.RunFileAction> act)
    {
        Title      = Strings.RunPieceTitle(piece.Number);
        LineLabel  = Strings.RunPieceLine(piece.Line);
        StateLabel = Services.Presentation.RunReview.PieceStateLabel(piece.State);
        IsUndone   = piece.State == Services.Presentation.RunPieceState.Undone;
        IsKept     = piece.State == Services.Presentation.RunPieceState.Kept;
        HasKeep    = piece.State == Services.Presentation.RunPieceState.Pending;
        HasUndo    = !IsUndone;
        HasRedo    = IsUndone;
        BtnKeep    = Strings.RunFileKeep;
        BtnUndo    = Strings.RunFileUndo;
        BtnRedo    = Strings.RunFileRedo;
        // A panel has no automation peer: each button carries which change it acts on, or a screen reader hears
        // "Keep, Undo, Keep, Undo" for a whole file.
        KeepName   = $"{BtnKeep}, {Title}";
        UndoName   = $"{BtnUndo}, {Title}";
        RedoName   = $"{BtnRedo}, {Title}";
        foreach (var line in piece.Diff)
            Lines.Add(new DiffLine { Prefix = line.Prefix, Text = line.Text });
        _canAct = true;
        AsyncCommand Act(Services.Commands.UndoRunCommandHandler.RunFileAction action) => new((_, _) =>
        {
            if (CanAct) act(action);
            return Task.CompletedTask;
        });
        KeepCommand = Act(Services.Commands.UndoRunCommandHandler.RunFileAction.Keep);
        UndoCommand = Act(Services.Commands.UndoRunCommandHandler.RunFileAction.Undo);
        RedoCommand = Act(Services.Commands.UndoRunCommandHandler.RunFileAction.Redo);
    }

    [DataMember] public string Title          { get; }
    [DataMember] public string LineLabel      { get; }
    [DataMember] public string StateLabel     { get; }
    [DataMember] public string KeepName       { get; }
    [DataMember] public string UndoName       { get; }
    [DataMember] public string RedoName       { get; }
    [DataMember] public bool   IsUndone       { get; }
    [DataMember] public bool   IsKept         { get; }
    [DataMember] public bool   HasKeep        { get; }
    [DataMember] public bool   HasUndo        { get; }
    [DataMember] public bool   HasRedo        { get; }
    [DataMember] public string BtnKeep        { get; }
    [DataMember] public string BtnUndo        { get; }
    [DataMember] public string BtnRedo        { get; }
    private bool _canAct;
    [DataMember] public bool   CanAct { get => _canAct; internal set => SetProperty(ref _canAct, value); }
    [DataMember] public ObservableCollection<DiffLine> Lines { get; } = [];
    [DataMember] public AsyncCommand KeepCommand { get; }
    [DataMember] public AsyncCommand UndoCommand { get; }
    [DataMember] public AsyncCommand RedoCommand { get; }
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
    public const string Info    = "\uE946";  // Info: an input card answered with nothing sent
}
