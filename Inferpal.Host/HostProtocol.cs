namespace Inferpal.Host;

// ── Wire DTOs of the Host ⇄ editor-adapter protocol ──────────────────────────
// Serialized by StreamJsonRpc's SystemTextJsonFormatter; every request carries a
// single named-parameter object (UseSingleObjectParameterDeserialization), which is
// what vscode-jsonrpc sends by default from the TypeScript side.

/// <summary>`initialize` — first request of a session; builds the whole service graph.</summary>
/// <param name="RootDir">Workspace root: file-tool confinement + `.inferpal/` project layers.</param>
/// <param name="Locale">Editor display language (e.g. <c>fr</c>, <c>zh-cn</c>); null keeps the OS culture.</param>
/// <param name="ClientName">Free-form adapter identity for diagnostics (e.g. <c>vscode/1.102</c>).</param>
/// <param name="Debug">
/// The adapter serves the reverse <c>debug/*</c> requests. Declared rather than assumed: an older
/// extension talking to a newer host would answer "method not found" to every one of them, and the
/// two debugger tools would then be offered to the model as capabilities that always fail. Absent
/// (the default) means no debugger, and the tools are simply not registered.
/// </param>
/// <param name="OtherFolders">
/// The editor's other workspace folders (a multi-root VS Code workspace). The tools serve <paramref name="RootDir"/>
/// only; the ones it does not hold are stated to the model, so that their code is not concluded absent.
/// </param>
/// <param name="Editor">The editor as the model is told it ("Zed"); absent = Visual Studio Code, the first adapter.</param>
/// <param name="EditorSurface">The adapter has an editor to answer about (active document, selection, cursor). False for
/// an Agent Client Protocol client: the editor tools are then not offered at all.</param>
/// <param name="BufferProbe">The adapter answers the reverse <c>editor/buffer</c> request (the text it holds for one
/// path): unsaved changes are found by asking about the files a call names, not from pushed <c>textDocument/*</c>.</param>
/// <param name="ToolEvents">The adapter wants every tool call announced when it starts and when it ends
/// (<c>chat/toolStart</c>, <c>chat/toolEnd</c>), and the approval request tied to its call with the files' changes.</param>
/// <param name="ReasoningChunks">The adapter shows the model's reasoning as it streams: <c>chat/reasoning</c> carries it
/// in batched pieces, besides the bounded preview of <c>chat/thinking</c>.</param>
/// <param name="Secrets">The adapter serves the reverse <c>secrets/*</c> requests (an OS keychain) off Windows.</param>
/// <param name="IndexesTheFolder">With semantic search on, the host indexes the folder on its own, as a chat needs.
/// False for the settings window, which shows the index saved for the folder and builds none.</param>
/// <param name="AutoSaveSlot">The adapter keeps the workspace's last conversation in VS Code's auto-save slot, restored at
/// its next start. False for an Agent Client Protocol client and the settings window: neither writes that slot, so a
/// conversation they discard (<c>/clear</c>, <c>/template</c>) is not the one it holds.</param>
/// <param name="OpenExternal">The adapter serves the reverse <c>env/openExternal</c> request: a sign-in page opens in the
/// editor's browser — on the machine the person sits at, VS Code Remote included — with its redirect forwarded here.</param>
/// <param name="CompactionEvents">The adapter wants a compaction of the conversation as <c>chat/compaction</c> — its
/// outcome and the summary the model now reads — instead of the <c>context_compact</c> tool note. True for an Agent Client
/// Protocol session, whose client may draw it as a compaction (ACP 1.11).</param>
/// <param name="InputCards">The adapter draws the card an MCP server's form is filled on (<c>input/form</c>, answered with
/// what the person did) and the line it leaves (<c>chat/inputReceipt</c>): its calls declare forms to the servers. False for
/// an Agent Client Protocol client, which has no such card.</param>
/// <param name="RemoteName">Where the editor runs this host when it is connected to another machine (VS Code's
/// <c>vscode.env.remoteName</c>: <c>wsl</c>, <c>ssh-remote</c>, <c>dev-container</c>…); absent when it runs where the person
/// sits. There, <c>localhost</c> is the other machine (<see cref="Services.Inference.RemoteSide"/>).</param>
internal sealed record InitializeParams(
    string    RootDir,
    string?   Locale          = null,
    string?   ClientName      = null,
    bool      Debug           = false,
    string[]? OtherFolders    = null,
    string?   Editor          = null,
    bool      EditorSurface   = true,
    bool      BufferProbe     = false,
    bool      ToolEvents      = false,
    bool      ReasoningChunks = false,
    bool      Secrets         = true,
    bool      IndexesTheFolder = true,
    bool      AutoSaveSlot     = true,
    bool      OpenExternal     = false,
    bool      CompactionEvents = false,
    bool      InputCards       = false,
    string?   RemoteName       = null);

/// <summary>`workspace/folders` — the editor's other workspace folders changed (a folder added or removed while the
/// root stayed): restarting the host for it would kill the turn in flight, its shells and MCP servers.</summary>
internal sealed record WorkspaceFoldersParams(string[]? OtherFolders = null);

/// <summary>What the adapter learns about the backend at startup (gates UI features).</summary>
internal sealed record InitializeResult(
    string HostVersion,
    string Provider,
    string DefaultModel,
    bool   ModelManagement,
    bool   VramMonitoring,
    bool   Fim,
    bool   KeepAlive);

/// <summary>`chat/send` — one user turn. Tokens/steps stream back as notifications.</summary>
/// <param name="AgentMode">Overrides the configured agent-mode switch for this turn; null = config.</param>
/// <param name="AttachedPaths">Workspace-relative (or full) paths of files the adapter inlined as attachments —
/// lets the RAG auto-context skip their chunks (parity with the VS VM, revue lot 4).</param>
/// <param name="CodeAction">A read-only code action (<c>/explain</c>, <c>/review</c>): answered by the code-actions
/// model, without tools, as in Visual Studio — whatever the agent-mode switch says.</param>
/// <param name="Query">The question as the user wrote it, before the adapter appended the attached files: what the
/// per-turn auto-context searches for, as Visual Studio searches the typed text. Null = <paramref name="Prompt"/>.</param>
/// <param name="ModelOverridesRoles"><see cref="Model"/> is a model the user picked for this thread: it answers
/// whatever the turn's role — an ACP client sends one only after a pick, so the per-role models stay reachable
/// otherwise.</param>
/// <param name="Notes">The notes the question carried when it was first answered (Regenerate): folded into it as its run
/// folded them (<c>ChatTurnPolicy.WithUserNotes</c>), so the question asked again is the one the history kept.</param>
internal sealed record ChatSendParams(
    string Prompt, string? Model = null, bool? AgentMode = null,
    List<string>? AttachedPaths = null,
    bool CodeAction = false,
    string? Query = null,
    bool ModelOverridesRoles = false,
    List<string>? Notes = null);

/// <summary><c>chat/steer</c>: a note typed while the turn runs, for its next boundary.</summary>
/// <param name="Id">The adapter's id for the note, given back by <c>chat/noteApplied</c> and in the turn's result.</param>
internal sealed record ChatSteerParams(string Id, string Text);

/// <summary>The answer to <c>chat/steer</c>.</summary>
/// <param name="Reason">Why the note was not taken: <c>noTurn</c> (no turn runs), <c>noBoundary</c> (the turn has no step
/// to read it before — a chat without tools, a code action, a slash command), <c>closed</c> (the run is ending). The
/// adapter then sends it as the next message.</param>
internal sealed record ChatSteerResult(bool Accepted, string? Reason = null);

/// <summary>A note of the turn: the adapter's id, its words, and — read — after how many tool calls the model read it.</summary>
internal sealed record TurnNoteDto(string Id, string Text, int AfterCalls = 0);

/// <summary>Final outcome of a turn. <paramref name="Text"/> holds the partial stream on cancel.</summary>
/// <param name="EndNotice">Why the run stopped, when it is not because the model was done. The answer stays in
/// <see cref="Text"/>; this is added after it, never in its place.</param>
/// <param name="ContextWindow">The window this turn was measured against — the loaded one when the server reports a
/// smaller one than configured. The adapter's context gauge shows this, not the setting. 0 = unknown.</param>
/// <param name="NextTurnTokens">What the next question will send, in tokens: the conversation plus the tool definitions
/// (<c>ContextManager.NextTurnLoad</c>) — the fill the context gauge shows, the X-Ray's figure. Not
/// <see cref="PromptTokens"/>, which measured this turn's last request, the run's own transcript included.</param>
/// <param name="Run">The run of the turn — its steps, the files it changed, its last check — for the line under the
/// answer and the result bar; <c>null</c> when no tool ran.</param>
/// <param name="Duration">How long the turn took, as the answer's header shows it ("21 s").</param>
/// <param name="Model">The model that answered.</param>
/// <param name="QuestionKept">False when the turn stopped before its question entered the history (a Stop during the
/// context build): Regenerate then has nothing to take back — taking back "the last question" would remove the one
/// before.</param>
/// <param name="Ended">Why the turn ended short of the model finishing, as a fact rather than a sentence:
/// <c>iterationLimit</c> (the run reached its tool-call budget) or <c>cut</c> (the answer stopped at the length limit);
/// <c>null</c> otherwise. <see cref="EndNotice"/> says it to the user; this is for an adapter that maps it to its own
/// vocabulary.</param>
/// <param name="AppliedNotes">The notes typed during the turn that the model read (<c>chat/steer</c>), now part of the
/// question in the history.</param>
/// <param name="UnappliedNotes">The notes no step read — typed during the final answer, or the turn stopped first. The
/// adapter sends them as the next message after a turn that ended normally, and gives them back otherwise.</param>
/// <param name="OffersContinue">The end notice offers Continue (<see cref="Services.Agent.ChatTurnPolicy.OffersContinue"/>):
/// the turn stopped at the iteration limit or its answer at the length limit — never after a loop or a repetition.</param>
/// <param name="AnnounceEnd">The turn was long and the setting, as it stands in the file now, says to announce its end
/// (<see cref="Services.Agent.ChatTurnPolicy.AnnouncesEnd"/>): the adapter does, when its chat is out of sight.</param>
/// <param name="RunId">The run of changes the turn made, whatever way it ended (<see cref="RunSummaryDto.RunId"/> says it
/// only for a turn that finished): saved with the turn's last message, it is what going back to the question undoes.</param>
internal sealed record ChatSendResult(
    string  Text,
    bool    Cancelled,
    int     TokensUsed,
    int     PromptTokens,
    string? Error = null,
    string? EndNotice = null,
    int     ContextWindow = 0,
    int     NextTurnTokens = 0,
    RunSummaryDto? Run = null,
    string? Duration = null,
    string? Model = null,
    bool    QuestionKept = true,
    string? Ended = null,
    List<TurnNoteDto>? AppliedNotes = null,
    List<TurnNoteDto>? UnappliedNotes = null,
    bool    OffersContinue = false,
    bool    AnnounceEnd = false,
    string? RunId = null,
    CutCardDto? Cut = null);

/// <summary>Under the cause of a turn cut at its bound (<see cref="Services.Presentation.CutCard"/>): what it wrote, and the
/// way to the time limits.</summary>
/// <param name="Page">The settings page the button opens.</param>
/// <param name="Section">The section of that page it opens (folded by default).</param>
internal sealed record CutCardDto(string? Note, string OpenLimits, string Page, string Section)
{
    public static CutCardDto? Of(bool waitRanOut, IReadOnlyCollection<string> toolsRun, int filesChanged) =>
        Services.Presentation.CutCard.Of(waitRanOut, toolsRun, filesChanged) is { } card
            ? new(card.Note, card.OpenLimits, Services.Presentation.CutCard.TimeLimitsPage,
                  Services.Presentation.CutCard.TimeLimitsSection)
            : null;
}

/// <summary>One agent run as the chat shows it (<see cref="Services.Presentation.RunSummaryModel"/>).</summary>
/// <param name="Check"><c>none</c>, <c>buildPassed</c>, <c>buildFailed</c>, <c>testsPassed</c> or <c>testsFailed</c>.</param>
/// <param name="RunId">What <c>/undo-run</c> takes; empty when the run changed no file.</param>
internal sealed record RunSummaryDto(
    int Steps, string Title, string Detail, List<Services.Presentation.RunFileLine> Files, string Check, string CheckText,
    string RunId)
{
    public static RunSummaryDto? Of(Services.Presentation.RunSummaryModel? m) => m is null ? null : new(
        m.Steps, m.Title, m.Detail, [.. m.Files],
        char.ToLowerInvariant(m.Check.ToString()[0]) + m.Check.ToString()[1..], m.CheckText, m.RunId);
}

/// <summary>`chat/wait` — the card of a request waiting on a server that reads its prompt (<see cref="ModelWait"/>).</summary>
/// <param name="Fraction">How far along, 0 to 1; <c>null</c> with nothing measured: no bar.</param>
/// <param name="Longer">Past the measured read: drawn as a warning, measured against the bound.</param>
internal sealed record ModelWaitDto(string Title, string Times, string Detail, double? Fraction, bool Longer)
{
    public static ModelWaitDto? Of(ModelWait? wait) =>
        wait is null ? null : new(wait.Title, wait.Times, wait.Detail, wait.Fraction, wait.Longer);
}

/// <summary>`run/review` — the review card of a run (<see cref="Services.Presentation.RunReviewModel"/>).</summary>
internal sealed record RunReviewParams(string RunId);

/// <summary>`run/file` — one file of a run undone (<c>undo</c>), redone (<c>redo</c>) or kept (<c>keep</c>); with
/// <paramref name="Piece"/>, only that change of it (1-based, <see cref="RunReviewPieceDto.Number"/>).</summary>
internal sealed record RunFileParams(string RunId, string Path, string Action, int? Piece = null);

/// <summary>One file of a run's review card.</summary>
/// <param name="State"><c>pending</c>, <c>kept</c>, <c>undone</c>, <c>changedSince</c> or <c>noCopy</c>.</param>
/// <param name="StateLabel">What the card says of that state (<see cref="Services.Presentation.RunReview.StateLabel"/>);
/// empty for a pending file.</param>
/// <param name="Diff">The file before the run against now, <c>+ - " " …</c> prefixes, cut and said past its cap.</param>
/// <param name="Pieces">Its changes, kept or undone one by one, when it holds two or more; empty otherwise.</param>
/// <param name="PiecesSummary">How many changes, kept and undone; empty without pieces.</param>
/// <param name="WholeOnly">Why its changes are not offered one by one; empty when they are, or when it does not arise.</param>
internal sealed record RunReviewFileDto(
    string Path, string Name, int Added, int Removed, bool Created, bool Gone, string State, string StateLabel, bool CanRedo,
    List<RunReviewLineDto> Diff, List<RunReviewPieceDto> Pieces, string PiecesSummary, string WholeOnly);

/// <summary>One change of a file of a run's review card.</summary>
/// <param name="State"><c>pending</c>, <c>kept</c> or <c>undone</c>.</param>
/// <param name="StateLabel">What the card says of that state; empty for a pending change.</param>
/// <param name="Diff">Its lines from before the run and from the run, between unchanged ones.</param>
internal sealed record RunReviewPieceDto(
    int Number, string Title, string Line, int Added, int Removed, string State, string StateLabel, List<RunReviewLineDto> Diff);

internal sealed record RunReviewLineDto(string Prefix, string Text);

/// <summary>A run's review card, its words already in the person's language.</summary>
internal sealed record RunReviewDto(
    string RunId, string Title, string Subtitle, string Note, string ChangedWhy, List<RunReviewFileDto> Files,
    string Keep, string Undo, string Delete, string Redo, string New)
{
    public static RunReviewDto Of(Services.Presentation.RunReviewModel m) => new(
        m.RunId, Localization.Strings.RunReviewTitle, Localization.Strings.RunReviewSubtitle(m.Files.Count, m.Started),
        Services.Presentation.RunReview.Note(m),
        Localization.Strings.RunFileChangedWhy,
        [.. m.Files.Select(f => new RunReviewFileDto(
            f.Path, f.Name, f.Added, f.Removed, f.Created, f.Gone,
            char.ToLowerInvariant(f.State.ToString()[0]) + f.State.ToString()[1..],
            Services.Presentation.RunReview.StateLabel(f),
            f.CanRedo, [.. f.Diff.Select(l => new RunReviewLineDto(l.Prefix, l.Text))],
            [.. (f.Pieces ?? []).Select(p => new RunReviewPieceDto(
                p.Number, Localization.Strings.RunPieceTitle(p.Number), Localization.Strings.RunPieceLine(p.Line),
                p.Added, p.Removed, p.State.ToString().ToLowerInvariant(), Services.Presentation.RunReview.PieceStateLabel(p.State),
                [.. p.Diff.Select(l => new RunReviewLineDto(l.Prefix, l.Text))]))],
            Services.Presentation.RunReview.PiecesSummary(f), Services.Presentation.RunReview.WholeOnlyLabel(f)))],
        Localization.Strings.RunFileKeep, Localization.Strings.RunFileUndo, Localization.Strings.RunFileDelete,
        Localization.Strings.RunFileRedo, Localization.Strings.RunFileNew);
}

/// <summary>What `run/file` answers: the sentence to show, and the card as it stands after (null: the run is gone).</summary>
internal sealed record RunFileResult(string Message, RunReviewDto? Review);

/// <summary>`chat/tool` notification — one executed tool call (uncapped output, like the VS bubble).</summary>
/// <param name="Diff">The change a writing call made, in the lines the approval card showed (<c>+ - …</c> prefixes,
/// context collapsed) — what Visual Studio draws in its tool bubble; null when the call wrote nothing.</param>
internal sealed record ToolNotice(string Name, string Input, string Output, bool HasErrors, string? Diff = null)
{
    /// <summary>The notice of an executed call, its diff rendered as the approval card renders it, every line kept.</summary>
    public static ToolNotice Of(ToolExecution te) => new(te.Name, te.Input, te.Output, te.HasErrors,
        te.Diff is { } d ? Services.CodeActions.DiffComputer.ComputeText(d.OldText, d.NewText, maxLines: int.MaxValue) : null);
}

/// <summary>`chat/compaction` — the conversation was made to fit before a question (<see cref="InitializeParams.CompactionEvents"/>).</summary>
/// <param name="Failed">The summary was asked for and could not be written: the turns were dropped instead. A truncation
/// made on purpose (compaction off, nothing worth summarising) is not a failure.</param>
/// <param name="Summary">The summary the model reads instead of the older turns; null when there is none.</param>
/// <param name="Notice">What the person is told, the same sentence the <c>context_compact</c> note carries.</param>
/// <param name="Degraded">Turns were lost, or the summary is incomplete.</param>
internal sealed record CompactionNote(bool Failed, string? Summary, string Notice, bool Degraded);

/// <summary>`fim/complete` — ghost-text request; cancellation aborts the LLM call via the RPC token.</summary>
/// <param name="MaxTokens">Absent: the tokens of the configured inline-completion mode.</param>
/// <param name="Temperature">Absent: the temperature of the configured inline-completion mode.</param>
/// <param name="Model">Absent: the configured FIM model, else the client's default.</param>
/// <param name="Path">The document's file: excluded by the workspace's ignore files, nothing is completed.</param>
internal sealed record FimParams(
    string  Prefix,
    string  Suffix,
    int?    MaxTokens   = null,
    double? Temperature = null,
    string? Model       = null,
    string? Path        = null);

/// <summary>`exclusions/check` — files whose content the adapter is about to send (a chip, a typed <c>@path</c>).</summary>
internal sealed record ExclusionsCheckParams(List<string> Paths);

/// <summary>`exclusions/check` answer, one per path: <c>null</c> = not excluded, else the sentence for the person.</summary>
/// <param name="ForModel">The same, one per path, for the model (the prompt names what was asked and not sent).</param>
internal sealed record ExclusionsCheckResult(List<string?> Notices, List<string?> ForModel);

/// <summary>`fim/settings` — whether inline completion is on, and the debounce of its mode.</summary>
/// <param name="YieldsToChat">The completion server is on the chat's machine, so it steps aside while a chat answers;
/// on another machine it keeps completing.</param>
/// <param name="OnDemand">Only the key asks (<c>inlineCompletionTrigger</c>): a pause in typing asks for nothing.</param>
internal sealed record FimSettingsResult(bool Enabled, int DebounceMs, bool YieldsToChat = true, bool OnDemand = false);

/// <summary>`fim/ask` — a completion asked for by its key: the suggestion, or what the status bar says instead.</summary>
/// <param name="Text">The suggestion; <c>null</c> when there is none.</param>
/// <param name="Notice">Why there is none — not sent, or nothing came back; <c>null</c> with a suggestion.</param>
internal sealed record FimAskResult(string? Text, string? Notice);

/// <summary>`problems/fix` — the diagnostics a quick fix was asked on.</summary>
/// <param name="Problems">The diagnostics.</param>
/// <param name="Model">The model the chat picked: the attachment is sized for its window.</param>
internal sealed record ProblemsFixParams(IReadOnlyList<Services.CodeActions.ProblemEntry> Problems, string? Model = null);

/// <summary>`debug/analyze` — the model the extension's chat picked: the attachment is sized for the window of the model
/// the code action asks.</summary>
internal sealed record DebugAnalyzeParams(string? Model = null);

/// <summary>`explorer/explain` — the item clicked in the file explorer (absolute), and the model the chat picked.</summary>
internal sealed record ExplorerExplainParams(string Path, string? Model = null);

/// <summary>`tests/explain` — a test of VS Code's Test view.</summary>
/// <param name="Label">The test's name as the view shows it.</param>
/// <param name="Filter">The <c>run_tests</c> filter that runs it (the name without its parameters).</param>
/// <param name="Failure">The failure message the editor holds; <c>null</c>: the host runs the test.</param>
/// <param name="File">The test's file, when the editor knows it; <paramref name="Line"/> its 1-based line, 0 otherwise.</param>
/// <param name="FailureFile">Where the failure message points, when it does; <paramref name="FailureLine"/> its line.</param>
/// <param name="Model">The model the chat picked.</param>
internal sealed record TestsExplainParams(string Label, string Filter, string? Failure = null, string? File = null, int Line = 0,
                                          string? FailureFile = null, int FailureLine = 0, string? Model = null);

/// <summary>`git/proposeCommitMessage` — the repository whose commit message box the proposal goes to.</summary>
/// <param name="Root">Its root folder, as the editor's Git extension names it.</param>
internal sealed record ProposeCommitMessageParams(string Root);

/// <summary>`git/proposeCommitMessage`'s answer.</summary>
/// <param name="Refusal">Why nothing is proposed — nothing to commit, git refused, the model failed; <c>null</c> otherwise.</param>
/// <param name="Notice">Said beside the proposal: nothing staged, untracked files not described, the diff or the reply cut.</param>
/// <param name="Message">The proposed commit message.</param>
internal sealed record CommitMessageProposal(string? Refusal, string? Notice, string? Message);

/// <summary>`problems/fix` and `debug/analyze` — what goes to the chat: a question and its attachment.</summary>
/// <param name="Question">The question, in the interface language.</param>
/// <param name="Chip">The attachment's label.</param>
/// <param name="Content">The attachment: each problem as a build prints it, and the code around it.</param>
/// <param name="Refusal">Why nothing is sent — no problem in a file the agent can edit, nothing paused; <c>null</c>
/// otherwise.</param>
internal sealed record GestureQuestion(string Question, string Chip, string Content, string? Refusal = null);

/// <summary>`textDocument/didOpen|didChange|didClose` + `editor/didChangeActiveDocument`.</summary>
/// <param name="Dirty">Whether the buffer has unsaved changes; absent means it does.</param>
internal sealed record DocumentParams(string Path, string? Text = null, bool? Dirty = null);

/// <summary>`editor/didChangeTabs`: the files the editor's tabs show, every group, in order.</summary>
internal sealed record TabsParams(IReadOnlyList<string>? Paths);

/// <summary>Reverse `editor/activeDocument` answer from the adapter.</summary>
internal sealed record ActiveDocumentDto(string? Path, string? Text);

/// <summary>Reverse `editor/replaceSelection` answer from the adapter.</summary>
internal sealed record EditResultDto(string? Path, bool ReplacedSelection);

// ── Reverse `debug/*` requests (host → adapter) ──────────────────────────────
// Deliberately a flat mirror of the Core's Services/Debugging port rather than a rendering of
// the Debug Adapter Protocol: the adapter speaks DAP on its side, and what crosses this wire is
// only what IDebugSession needs. Values stay opaque strings — the same list renders as
// `Count = 3` under Visual Studio and `(3) [21, 42, 43]` under VS Code, so parsing one of them
// breaks the other.

/// <summary>Reverse `debug/addBreakpoint`, `debug/removeBreakpoint` parameters.</summary>
internal sealed record DebugBreakpointParams(string File, int Line);

/// <summary>A breakpoint as the adapter's debugger bound it.</summary>
internal sealed record DebugBreakpointDto(string File, int Line, bool Enabled);

/// <summary>
/// `debug/continue` and `debug/step`: the stop, or why there is none — <c>Outcome</c> is one of
/// <c>DebugOps.Resumed.Ended</c> / <c>StillRunning</c> / <c>NotPaused</c>, <c>Failure</c> the editor's words
/// when the resume itself failed.
/// </summary>
internal sealed record DebugResumeDto(DebugStopStateDto? State, string? Outcome, string? Failure = null);

/// <summary>Reverse `debug/step` parameter: <c>over</c> | <c>into</c> | <c>out</c>.</summary>
internal sealed record DebugStepParams(string Kind);

/// <summary>Reverse `debug/evaluate` parameters. <paramref name="FrameId"/> null = top frame.</summary>
internal sealed record DebugEvaluateParams(string Expression, int? FrameId);

/// <summary>One call-stack frame, top of stack first.</summary>
internal sealed record DebugFrameDto(int Id, string Function, string? File, int? Line);

/// <summary>One variable of the current frame; <paramref name="Value"/> is the adapter's rendering.</summary>
internal sealed record DebugVariableDto(string Name, string Type, string Value);

/// <summary>
/// Where and why execution is paused. <paramref name="LocalsFrameId"/> is the frame the locals
/// were read from — the bridge's ordinary capture reads the top frame, its `/tdd` capture reads
/// the first frame under the workspace root, so it cannot be inferred here.
/// </summary>
internal sealed record DebugStopStateDto(
    string?                  Reason,
    int                      ThreadId,
    List<DebugFrameDto>?     Frames,
    List<DebugVariableDto>?  Locals,
    string?                  Exception = null,
    int?                     LocalsFrameId = null);

/// <summary>
/// Answer to `debug/start`: the three outcomes of asking a debugger to run, kept apart on the
/// wire because that is the whole point of <c>DebugStartResult</c>. A workspace with no launch
/// configuration is the common case in VS Code, and it is neither a stop nor a completed run.
/// </summary>
internal sealed record DebugStartDto(DebugStopStateDto? State, string? Failure, bool StillRunning = false);

/// <summary>`debug/captureTest`: the repro-runner launch the adapter debugs.</summary>
internal sealed record DebugCaptureTestParams(string Program, List<string> Args, string Cwd, string ProjectRoot);

/// <summary>`index/status` snapshot.</summary>
internal sealed record IndexStatusResult(bool IsIndexing, int ChunkCount, string RootDir);

/// <summary>`backend/status` answer — the adapter's connection badge. <paramref name="VramBadge"/>
/// is the compact "model · X.X GB" line (empty when unreachable, unsupported or nothing loaded).</summary>
/// <param name="EdgeNotice">
/// The sentence to put IN THE THREAD when this heartbeat just crossed an edge — went unreachable,
/// or came back — and <c>null</c> the rest of the time, which is nearly always.
/// </param>
/// <remarks>
/// ⚠ Did not exist: in VS Code an outage only changed the colour of a dot, while the Visual Studio
/// window has always put a line in the conversation and lit its Retry button. The decision (which
/// edge, and is the first check silent?) belongs to the Core — <c>ConnectionStatusPresenter</c> —
/// not to a second state machine written here.
/// </remarks>
/// <param name="Refused">Not connected, but the server answered the check with a refusal (a wrong API key: 401): the
/// badge text to show instead of "unreachable", already localized — and the pre-flight's cue to say "refused".</param>
/// <param name="Server">The configured backend, as the chat header names it ("LM Studio").</param>
/// <param name="OffMachine">The machines other than this one that the roles in use send code to
/// (<c>RoleServers.OffMachineHosts</c>): empty, the welcome line says the code stays on this machine.</param>
internal sealed record BackendStatusResult(bool Connected, string VramBadge, string? EdgeNotice = null, string? Refused = null,
                                           string? Server = null, string[]? OffMachine = null);

/// <summary>`models/adoptDefault` — the model now used in place of a default nobody chose and the backend lacks, with
/// the sentence that says so; both null when nothing changed.</summary>
internal sealed record ModelsAdoptResult(string? Model, string? Notice);

/// <summary>`host/runningWork` — what this host runs beyond a turn, as the notice to show once it is stopped (null when
/// nothing runs).</summary>
internal sealed record RunningWorkResult(string? Notice, string? IfItCrashes = null);

/// <summary>`models/useForSession` — the window's chat model, set for the host session without being saved.</summary>
internal sealed record ModelUseParams(string Model);

/// <summary>
/// `connection/check` — what the settings panel's Test button found AT THE URL IT WAS GIVEN.
/// </summary>
/// <param name="Ok">A backend answered, carrying the root property that signs it.</param>
/// <param name="Provider">
/// The detected backend code (<c>ollama</c> | <c>lmstudio</c> | <c>openai-compatible</c>), so the
/// panel can pre-select it the way the Visual Studio window does; <c>null</c> when nothing answered.
/// </param>
/// <param name="Refused">When nothing was detected but a server answered with a refusal (a wrong API key: 401), the
/// status line to show in its place, in the user's language — "unreachable" sends them to check a server that is up.</param>
/// <remarks>
/// ⚠ The probe uses the url the webview sent, never the SAVED <c>Config.BaseUrl</c>: otherwise the
/// panel reports "Connected" about a different address, and the case that misleads most is the
/// ordinary one — the old url works, the new one is wrong. Same behaviour as the Visual Studio
/// window: read the typed value, pre-select the detected backend, then refresh the models from
/// that url.
/// </remarks>
internal sealed record ConnectionCheckResult(bool Ok, string? Provider, string? Refused = null);

/// <summary>`connection/check` — the url and API key to probe. Empty = the configured ones.</summary>
internal sealed record ConnectionCheckParams(string? BaseUrl = null, string? ApiKey = null);

/// <summary>
/// `models/list` — the settings FORM's values when it has any. Each empty one falls back to the
/// saved configuration: that is the panel's first load, and the ↻ of an ordinary session.
/// </summary>
internal sealed record ModelsListParams(string? BaseUrl = null, string? Provider = null, string? ApiKey = null);

/// <summary>`models/listByKind` — the listed models, split by what each can be picked for: <paramref name="Chat"/> for
/// the chat and every task model, <paramref name="Embedding"/> for code search (<see cref="ModelsByKind"/>).</summary>
/// <param name="Listed">Every listed model, those offered for neither included: what tells "not a chat model" from "not
/// listed" when a name is typed by hand.</param>
internal sealed record ModelsByKindResult(IReadOnlyList<string> Chat, IReadOnlyList<string> Embedding,
                                          IReadOnlyList<string>? Listed = null);

// ── Conversation export (the rendering lives in the Core, not in the adapter) ─

/// <summary>One adapter bubble, flattened for export.</summary>
/// <param name="Role">"user", "assistant" or "tool" — anything else is ignored.</param>
/// <param name="Name">Model name (assistant turn) or tool name (tool turn).</param>
/// <param name="Timestamp">Already-formatted local time, exactly as the adapter shows it.</param>
/// <param name="Notice">A slash command shown as typed: exported, never counted as a turn.</param>
internal sealed record ChatExportMessage(string Role, string? Name, string Content, string? Timestamp, bool Notice = false);

/// <summary>`rag/typingChips` — the draft the person is typing.</summary>
internal sealed record TypingChipsParams(string Text);

/// <summary>One chip of a draft (<see cref="Services.Rag.RagAutoContext.TypingChip"/>).</summary>
/// <param name="Path">The file, absolute: what the turn's relevant code reads to leave out the chunks the chip sends.</param>
/// <param name="Name">The file's name.</param>
/// <param name="Lines">The lines its excerpts span (<c>38–61</c>); empty when they cover the whole file.</param>
/// <param name="Content">What the chip sends: the excerpts, said to be excerpts when they are.</param>
internal sealed record TypingChipDto(string Path, string Name, string Lines, string Content);

/// <summary>
/// `chat/export` — the adapter sends what it SHOWS, the Core renders the document.
/// </summary>
/// <remarks>
/// ⚠ This method exists because the export was written TWICE: `ConversationExporter` in the Core for
/// Visual Studio, and eleven lines of TypeScript for VS Code. The copy dropped the whole stats
/// header (model, turns, tool calls, tokens, date, duration) and ignored the `.txt` filter its own
/// save dialog offered — picking "Text" wrote Markdown. The rendering is shared; what each adapter
/// keeps is what it sees.
/// </remarks>
/// <param name="SessionTokens">The thread's token total, held by the adapter as the VS window does.</param>
/// <param name="DurationSeconds">Age of the thread; absent = "—", as on the VS side.</param>
/// <param name="Model">The model that answers the thread, when it is not the configured default (an ACP thread's own
/// choice); absent = the default model.</param>
internal sealed record ChatExportParams(
    bool                             AsPlainText,
    IReadOnlyList<ChatExportMessage> Messages,
    int                              SessionTokens   = 0,
    int?                             DurationSeconds = null,
    string?                          Model           = null);

/// <summary>`command/list` entry — one slash command for the adapter's autocomplete popup
/// (built-ins with their localized hints, then user templates).</summary>
internal sealed record SlashCommandInfoDto(string Command, string Hint);

// ── Typed @-mentions (categories served by the Core's MentionController) ─────

/// <summary>`mention/categories` entry — one @mention category with its localized
/// description. Query-based categories (@file/@code/@folder) drill into a sub-search;
/// instant ones attach their context directly.</summary>
internal sealed record MentionCategoryDto(string Token, string Description, bool QueryBased);

/// <summary>`mention/search` — sub-search of a query-based category (<c>file</c> | <c>folder</c>).</summary>
internal sealed record MentionSearchParams(string Category, string Query);

/// <summary>One `mention/search` hit: display label + relative detail + the value to
/// resolve or insert (full path for files/folders).</summary>
internal sealed record MentionItemDto(string Label, string Detail, string Value);

/// <summary>`mention/resolve` — materializes an instant or selected mention host-side.
/// <paramref name="Category"/> ∈ <c>tree</c> | <c>diff</c> | <c>debugger</c> |
/// <c>folder</c> (Value = full path) | <c>code</c> (Value = semantic query).</summary>
internal sealed record MentionResolveParams(string Category, string? Value = null);

/// <summary>
/// `mention/resolve` answer: the chip label + attached content (null = nothing to attach).
/// </summary>
/// <param name="Notice">
/// A localized sentence to show the user <b>instead of</b> a chip — "nothing is paused", not
/// "something went wrong". Without it the adapter had no way to tell an empty answer from a
/// failure, and both came out as silence: the user types <c>@debugger</c> and nothing happens.
/// </param>
internal sealed record MentionResolveResult(string? Name, string? Content, string? Notice = null);

/// <summary>`command/slash` — a chat input starting with <c>/</c>. The host executes the
/// commands it can serve headlessly; <c>Handled = false</c> tells the adapter to send the
/// text as a normal chat prompt instead. <paramref name="PromptHistory"/> is the adapter's
/// prompt-box history (most-recent-last), consumed by <c>/phistory</c>.</summary>
internal sealed record SlashCommandParams(string Text, List<string>? PromptHistory = null);

/// <summary>One editor-side effect a handled slash command asks the adapter to apply.
/// <paramref name="Kind"/> ∈ <c>setPrompt</c> (Value = text to put in the prompt box) |
/// <c>sendAsPrompt</c> (Value = expanded template to send as a normal chat turn) |
/// <c>attachChip</c> (Name = chip label, Value = content to attach to the next turn) |
/// <c>copyToClipboard</c> (Value) | <c>clearTranscript</c> | <c>stateChange</c>
/// (Name = key e.g. <c>model</c>, Value) | <c>openFile</c> (Value = absolute path) |
/// <c>exportRequest</c> | <c>configSaved</c> | <c>openSettings</c> (the front-end's settings: VS Code's panel, an ACP
/// thread's browser window). Unknown kinds must be ignored (forward compatibility).</summary>
internal sealed record SlashEffectDto(string Kind, string? Value = null, string? Name = null);

/// <summary>`command/slash` answer: <paramref name="Markdown"/> is the bubble to render
/// when <paramref name="Handled"/> is true; <paramref name="Effects"/> are the editor-side
/// side effects to apply (null/empty = none).</summary>
internal sealed record SlashCommandResult(
    bool Handled, string? Markdown = null, List<SlashEffectDto>? Effects = null,
    Inferpal.Services.Import.ImportCard? ImportCard = null);

/// <summary>`config/update` — full config JSON, as previously returned by `config/get`.</summary>
/// <param name="Base">
/// The config JSON the editor started from. When present, only the properties that differ between it
/// and <paramref name="Json"/> are applied: the editor sends back the whole object it opened, and a
/// change made elsewhere since — a documentation source, a model picked from the chat — would otherwise
/// be reverted by a save that never touched it.
/// </param>
internal sealed record ConfigUpdateParams(string Json, string? Base = null);

/// <summary>What the save could not use. One thing today, and the type exists so the next one joins
/// it instead of becoming a second round trip.</summary>
/// <remarks>
/// The count is computed HERE because the decision lives in the Core (<c>PermissionPolicy</c>) and
/// the VS Code panel is TypeScript: making it re-read the DSL would be a second implementation of
/// the same rule, hence a programmed divergence.
/// </remarks>
internal sealed record ConfigUpdateResult(int PermissionRulesIgnored);

/// <summary>`config/reload` — the settings the session took from the file (JSON keys), its default and agent models after
/// them, and whether a new KIND of model server was saved (the session keeps talking to the one it started with; a new
/// address or key of the same kind is taken, and applies at once).
/// <paramref name="Unreadable"/>: the file exists but cannot be read — nothing was taken.</summary>
internal sealed record ConfigReloadResult(IReadOnlyList<string> Taken, string DefaultModel, bool ServerChanged,
                                          string AgentModel = "", bool Unreadable = false);

/// <summary>`pins/add` / `pins/remove` — a file to pin into, or take out of, every request.</summary>
internal sealed record PinParams(string Path);

/// <summary>The pinned files after the call, and what the host had to say (the cap reached, a failed save).</summary>
internal sealed record PinsResult(List<string> Pins, string? Notice = null);

/// <summary>`tests/generate`: the source file, the code to test (the selection, or the whole file), what the user typed
/// after <c>/test</c>.</summary>
internal sealed record TestsGenerateParams(string Path, string Code, string? Request = null);

/// <summary>`tests/generate` answer: the sentence to show, and — when tests were written — the test file, its new
/// content, and whether it extends an existing file (replaced in its buffer) or creates one.</summary>
internal sealed record TestsGenerateResult(
    string? Message, string? TestPath = null, string? Content = null, bool Extended = false, bool Cancelled = false);

/// <summary>`codeAction/run` — headless in-place code action (<paramref name="Kind"/> =
/// <c>fix</c> | <c>refactor</c> | <c>doc</c> | <c>edit</c>, Edit with AI, whose instruction is the request) over the
/// adapter's document text and selection offsets. The host only runs the model step; applying (and previewing) stays
/// editor-side.</summary>
internal sealed record CodeActionParams(
    string  Kind,
    string  Text,
    int     SelStart,
    int     SelEnd,
    string? Model = null,
    // The document's path, so /doc gets the same semantic block the Visual Studio window has:
    // the extractor dispatches on the extension and reads neighbouring files for the contracts.
    // Optional on the wire — an older adapter simply gets the unenriched prompt.
    string? Path  = null,
    // What the user typed after the command (/fix the off-by-one in the loop): given to the model first.
    string? Request = null);

/// <summary>One accepted-or-rejected-independently hunk of a code action rewrite, as a
/// character-offset edit against the submitted text (mirror of the Core's <c>DiffEdit</c>).</summary>
internal sealed record CodeActionEditDto(int Index, int Start, int End, string NewText);

/// <summary>`codeAction/run` answer. <paramref name="Outcome"/> is <c>edited</c> (apply or
/// preview <paramref name="Edits"/>), <c>noChange</c> (model judged the code already good) or
/// <c>failed</c>, or <c>refused</c> (the document is excluded by the workspace's ignore files: nothing was sent to the
/// model, <paramref name="FailureDetail"/> says why). <paramref name="NewText"/> is the full rewritten document when edited;
/// <paramref name="FailureDetail"/> is the underlying error message when failed.</summary>
internal sealed record CodeActionResultDto(
    string                  Outcome,
    List<CodeActionEditDto> Edits,
    string?                 NewText = null,
    string?                 FailureDetail = null);

/// <summary>`code/excerpt` — the code a read-only action (<c>/explain</c>, <c>/review</c>) is about to
/// send, with the file it comes from and whether it is a selection (the label says so). <paramref name="Model"/>
/// is the model picked in the chat; the excerpt is sized for the window of the model that answers a code action —
/// the configured code-actions model, else that pick (null = the chat model of the settings).</summary>
/// <remarks>The turn the code goes into, as <c>chat/send</c> receives it: a code action by default (VS Code's <c>/explain</c>,
/// <c>/review</c>); an ACP thread's attachments go into a chat or agent turn.</remarks>
/// <param name="Path">The file the code comes from, when it is one: excluded by the workspace's ignore files, the
/// excerpt is refused.</param>
internal sealed record CodeExcerptParams(string Code, string FileName, bool Selection = false, string? Model = null,
                                         bool CodeAction = true, bool? AgentMode = null, bool ModelOverridesRoles = false,
                                         string? Path = null);

/// <summary>`code/excerpt` answer: <paramref name="Text"/> is what goes into the prompt (the code, or
/// its first lines and a marker naming the count), <paramref name="Label"/> what the chat shows under
/// the question — carrying the count too when <paramref name="Truncated"/>.</summary>
/// <param name="Refusal">The file is excluded by the workspace's ignore files: the sentence that says so, nothing else to
/// send (<paramref name="Text"/> is empty).</param>
internal sealed record CodeExcerptResult(string Text, string Label, bool Truncated, string? Refusal = null);

// ── Context X-Ray panel (interactive /xray V2) ───────────────────────────────

/// <summary>One prompt layer of the X-Ray panel (wire mirror of the Core's <c>XRaySectionModel</c>).</summary>
internal sealed record XRaySectionDto(
    string Id, string Label, int Tokens, double Percent, string Content, bool Enabled, bool CanToggle);

/// <summary>`xray/panel` (and `xray/toggle`) answer: the full panel model, ready to render.</summary>
internal sealed record XRayPanelDto(
    List<XRaySectionDto> Sections,
    int    TotalTokens,
    int    HistoryTokens,
    int    ContextWindow,
    double FillPercent,
    bool   OverheadWarning,
    string RawPrompt,
    int    ToolTokens,
    string? ToolsOnDemand = null,
    string? ToolsTurnedOff = null);

/// <summary>`xray/toggle` — switches one section on/off for the next turns of this session.</summary>
internal sealed record XRayToggleParams(string Id, bool Enabled);

// ── Sessions (persisted in %AppData%/Inferpal/sessions/, shared with the VS extension) ──

/// <summary>One display message of a saved session (wire mirror of <c>SavedMessage</c>).</summary>
/// <param name="Run">The run of changes its turn made, when it closes one (<c>SavedMessage.Run</c>).</param>
internal sealed record SavedMessageDto(string Role, string Content, string? ToolName = null, string? Timestamp = null,
                                       string? Run = null);

/// <summary>`session/save` — persists the adapter's transcript under <paramref name="Name"/>.</summary>
internal sealed record SessionSaveParams(string Name, List<SavedMessageDto> Messages, bool Archive = false);

/// <summary>`session/archive` argument: the conversation just cleared, as the adapter's screen held it.</summary>
internal sealed record SessionArchiveParams(List<SavedMessageDto> Messages);

/// <summary>`session/archive` answer: the name the archive is under until its title renames it.</summary>
internal sealed record SessionArchiveResult(string Name);

/// <summary>`session/load` / `session/delete` argument.</summary>
internal sealed record SessionRefParams(string Name);

/// <summary>`session/list` entry. <paramref name="Parent"/>/<paramref name="ForkTurn"/> are set on
/// branches (<c>/branch</c>) so the adapter can show the family in its session picker.</summary>
/// <param name="WorkspaceRoot">The workspace the session was saved from; null for a session
/// saved before it was recorded, or by an editor that does not record it.</param>
internal sealed record SessionSummaryDto(string Name, DateTime SavedAt, int MessageCount, string Preview,
                                         string? Parent = null, int? ForkTurn = null,
                                         string? WorkspaceRoot = null);

/// <summary>`mcp/sessionServers` — the MCP servers the editor hands this session (an ACP client's <c>mcpServers</c>).</summary>
internal sealed record McpSessionServersParams(List<McpSessionServerDto>? Servers);

/// <summary>One server the editor passes: stdio (<paramref name="Command"/>) or HTTP (<paramref name="Url"/>).</summary>
internal sealed record McpSessionServerDto(
    string Name, string? Command = null, List<string>? Args = null, Dictionary<string, string>? Env = null,
    string? Url = null, Dictionary<string, string>? Headers = null);

/// <summary>`session/list` answer: the readable sessions, the files that could not be read (their
/// names still taken), and the sentence naming them — <c>null</c> when every file was read.</summary>
internal sealed record SessionListResult(List<SessionSummaryDto> Sessions, List<string> Unreadable, string? Notice);

/// <summary>`session/load` answer: the transcript to re-render (host history already rebuilt), and what the next
/// question will send — the context gauge's fill (see <see cref="ChatSendResult"/>'s <c>NextTurnTokens</c>).</summary>
internal sealed record SessionLoadResult(string Name, List<SavedMessageDto> Messages, int NextTurnTokens = 0);

/// <summary>`session/branch` — fork the adapter's transcript at 1-based <paramref name="Turn"/>.</summary>
internal sealed record SessionBranchParams(int Turn, List<SavedMessageDto> Messages);

/// <summary>`session/branch` answer: the new branch (host history already rebuilt from it) plus the
/// localized confirmation bubble — the adapter has no access to the shared .resx.</summary>
internal sealed record SessionBranchResult(
    string Name, string Parent, int ForkTurn, List<SavedMessageDto> Messages, string Message, int NextTurnTokens = 0);

/// <summary>`chat/rewindPlan` and `chat/rewind` — going back to the question at <paramref name="MessageIndex"/> of the
/// adapter's transcript (its saved form, one message per item).</summary>
/// <param name="ConversationOnly">`chat/rewind`: cut the conversation and leave every file as it is.</param>
/// <param name="Question">The question the person clicked, as saved: a transcript whose index points elsewhere is refused.</param>
/// <param name="ExpectedRuns">`chat/rewind`: the runs the card showed (<see cref="RewindCardDto.RunIds"/>); others now
/// follow the question, and the card is drawn again instead of undoing what it did not show.</param>
internal sealed record RewindParams(int MessageIndex, List<SavedMessageDto> Messages, bool ConversationOnly = false,
                                    string? Question = null, List<string>? ExpectedRuns = null);

internal sealed record RewindLineDto(string Path, string Name, string Kind, string Label);

/// <summary>A question's "Go back here" card (<see cref="Services.Presentation.RewindCardModel"/>), its words in the
/// person's language.</summary>
internal sealed record RewindCardDto(
    int MessageIndex, string Question, string Title, string Body, List<RewindLineDto> Files, string Note, bool Refused,
    List<string> RunIds, string Go, string Cancel, string ConversationOnly, string ConversationOnlyHint, string ReviewRuns,
    string ReviewRunsHint)
{
    public static RewindCardDto Of(Services.Presentation.RewindCardModel m, int messageIndex) => new(
        messageIndex, m.Question, m.Title, m.Body,
        [.. m.Files.Select(f => new RewindLineDto(f.Path, f.Name,
            char.ToLowerInvariant(f.Kind.ToString()[0]) + f.Kind.ToString()[1..], f.Label))],
        m.Note, m.Refused, [.. m.RunIds], Localization.Strings.RewindGo, Localization.Strings.RewindCancel,
        Localization.Strings.RewindConversationOnly, Localization.Strings.RewindConversationOnlyHint,
        Localization.Strings.RewindReviewRuns, Localization.Strings.RewindReviewRunsHint);
}

/// <summary>`chat/rewind` answer: refused (<paramref name="Card"/> says why, nothing changed), stopped half-way
/// (<paramref name="Message"/> only), or back at the question — <paramref name="Branch"/> is the conversation cut before
/// it and <paramref name="Question"/> goes back in the message box.</summary>
internal sealed record RewindResult(RewindCardDto? Card = null, SessionBranchResult? Branch = null, string? Question = null,
                                    string? Message = null);

/// <summary>`session/branchCommand` — <c>/branch [args]</c> decided on the adapter's displayed transcript.</summary>
internal sealed record SessionBranchCommandParams(string? Args, List<SavedMessageDto>? Messages);

/// <summary>`session/branchCommand` answer: exactly one outcome. <paramref name="Message"/> is the bubble
/// to show — for a switch, the confirmation to show once the load succeeded.</summary>
internal sealed record SessionBranchCommandResult(string? Message = null, int? ForkTurn = null, string? SwitchTo = null);

/// <summary>`session/title` — text to summarise; empty = the session's first user message.</summary>
internal sealed record SessionTitleParams(string? Text = null);

/// <summary>`session/title` answer: the bare title plus the timestamped save file name.</summary>
internal sealed record SessionTitleResult(string Title, string FileName);

// ── Settings schema (`settings/schema`) ──────────────────────────────────────

/// <summary>One choice of a select field. Texts are product names — never localized. <c>Description</c>: the sentence
/// under its name when drawn as a card.</summary>
internal sealed record SettingsOptionDto(string Value, string Text, string? Description = null);

/// <summary>An editable setting: <paramref name="Label"/>/<paramref name="Hint"/> are resource
/// names the adapter resolves against `settings/strings`.</summary>
/// <param name="DefaultValue">Factory value of a numeric field, so the panel can honour "clearing the box restores
/// the default" - the affordance the Visual Studio window applies and this one ignored.</param>
/// <param name="Inverted">A check box showing the opposite of the stored boolean.</param>
/// <param name="OpensFold">A value other than <paramref name="DefaultValue"/> opens the page's advanced sections.</param>
/// <param name="HintNotOllama">Resource name of the hint shown instead when the selected server is not Ollama.</param>
/// <param name="Editor">The structured editor of a list setting: pinnedFiles | mcpServers | approvalRules |
/// nameValue.</param>
/// <param name="ZeroIsEmpty">A numeric box whose 0 means "not set": shown empty.</param>
/// <param name="EmptyChoice">Resource name of what an optional model list shows for its empty value ("Same as
/// chat").</param>
/// <param name="Columns">Resource names of a nameValue table's column headers.</param>
/// <param name="Min">Bounds of a numeric box: a value outside them is named and not saved, as in the VS window.</param>
/// <param name="EmbeddingModels">A model box that lists the embedding models, not the chat models.</param>
/// <param name="ShowWhen">The field shows only while another field has a value (<c>key=value</c>).</param>
/// <param name="Server">The role whose server the field belongs to: its Test button and its model list use that server.</param>
internal sealed record SettingsFieldDto(
    string Key, string Kind, string Label, string? Hint, string? Unit, string? Button,
    List<SettingsOptionDto>? Options,
    string? DefaultValue = null,
    bool Inverted = false,
    bool OpensFold = false,
    string? HintNotOllama = null,
    string? Editor = null,
    bool ZeroIsEmpty = false,
    string? EmptyChoice = null,
    List<string>? Columns = null,
    int? Min = null,
    int? Max = null,
    bool EmbeddingModels = false,
    string? ShowWhen = null,
    string? Server = null);

/// <summary>A group of fields under a title (empty = no heading).</summary>
internal sealed record SettingsSectionDto(
    string Title, List<SettingsFieldDto> Fields,
    string? Description, string? Gate, bool Collapsible, string? Note, bool Grid, string? Widget);

/// <summary>One page of the settings window.</summary>
internal sealed record SettingsTabDto(
    string Key, string Title, string Description, List<SettingsSectionDto> Sections, string? AdvancedToggle);

/// <summary>`settings/schema` answer: the pages, in the order of the side navigation.</summary>
internal sealed record SettingsSchemaDto(List<SettingsTabDto> Tabs);

// ── Settings: structured editors of the Tools page ──────────────────────────────

/// <summary>One MCP server card. <c>State</c>: connected | signIn | failed | off | notStarted. <c>OutsideTheList</c>:
/// declared by the repository or passed by the editor — shown, signed in to, never edited.</summary>
internal sealed record McpCardDto(
    string Name, string Transport, string Target, bool Enabled,
    string State, string StatusText, string? Cause, int ToolCount, bool OutsideTheList);

/// <summary>`mcp/cards` (and `mcp/retry`, `mcp/authorize`) answer. <c>Error</c>: why a sign-in failed.</summary>
internal sealed record McpCardsDto(string Summary, List<McpCardDto> Cards, string? Error);

internal sealed record McpServerNameParams(string Name);

internal sealed record PermissionsTableParams(string? Rules);

internal sealed record PlanModeParams(bool Enabled);

/// <summary>One approval rule. <c>Source</c>: team | machine. <c>Status</c>: inForce | ignoredAllow | unreadable.</summary>
internal sealed record ApprovalRuleRowDto(
    string Source, bool? Allow, string Tool, string Pattern, string Status,
    string EffectText, string FromText, string? NoteText, int MachineLine);

internal sealed record ApprovalRuleTableDto(bool TeamUnusable, List<ApprovalRuleRowDto> Rows);

internal sealed record NewRuleParams(bool Allow, string? Tool, string? Pattern);
