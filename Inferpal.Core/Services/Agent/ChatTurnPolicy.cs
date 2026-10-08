using System.Text;
using Inferpal.Localization;
using Inferpal.Models;

namespace Inferpal.Services.Agent;

/// <summary>
/// Editor-agnostic view of a chat attachment (label + full content) — the only two
/// facts <see cref="ChatTurnPolicy.BuildHistoryText"/> needs. The tool-window VM maps
/// its observable <c>AttachmentItem</c> chips to this before calling.
/// </summary>
internal sealed record AttachmentContent(string Label, string Content);

/// <summary>
/// What the final-render pass of a chat turn should display, in fallback order:
/// the live-streamed bubble, the stored final response, a tool-execution summary,
/// or the absolute "empty response" fallback.
/// </summary>
internal enum FinalAnswerKind
{
    StreamedAnswer,
    FinalText,
    ToolSummary,
    EmptyFallback,
}

/// <summary>
/// Pure decision/formatting logic extracted from the tool-window VM's send pipeline
/// (<c>SendCoreAsync</c>): the empty-bubble triple guard, the final-answer fallback
/// chain, tool-bubble previews, the context-enriched history text, the multi-file
/// recap inputs, and the prompt-history rules. The VM keeps UI state, message
/// insertion, theming, and the LLM calls.
/// </summary>
internal static class ChatTurnPolicy
{
    /// <summary>
    /// The time budget of a chat turn: the "quick" one for a code action sent through the chat (explain, review), the
    /// "normal" one for a question — with tools or without. One decision for both front-ends.
    /// </summary>
    /// <remarks>
    /// ⚠ Decided here, never at the call site: chosen by each front-end, the same question asked with tools off waits
    /// 120 s for a slow or cold model in one editor and 300 s in the other — and the settings call explain a quick task.
    /// </remarks>
    public static TaskComplexity TurnComplexity(bool codeAction) =>
        codeAction ? TaskComplexity.Quick : TaskComplexity.Normal;

    /// <summary>
    /// Triple guard against visually empty assistant bubbles (see bug history): content
    /// is "visibly empty" when it parses to no markdown blocks, contains no printable
    /// character once &lt;think&gt; tags are stripped (whitespace runs, zero-width
    /// spaces, BOM…), or parses only to thematic-break separators ("---" renders as an
    /// invisible 1-px line). Matches <c>ChatMessageItem.ParseMarkdown</c>'s outcome:
    /// <c>!HasBlocks || !HasPrintableText(stripped) || Blocks.All(separator)</c>.
    /// </summary>
    public static bool IsVisiblyEmpty(string? content)
    {
        if (!MarkdownParser.HasPrintableText(MarkdownParser.StripThinkTags(content)))
            return true;
        var blocks = MarkdownParser.Parse(content ?? string.Empty);
        return blocks.Count == 0 || blocks.All(b => b.Type == "separator");
    }

    /// <summary>
    /// The line shown AFTER an answer that is not a task carried to its end — empty when it is. One
    /// reader for both front-ends: the Visual Studio window and the host each recopied the choice.
    /// </summary>
    /// <remarks>
    /// ⚠ A cut answer is a separate fact from how the run ended: a synthesis written at the iteration
    /// limit can itself stop at the length limit, and the reader must learn both — the text is
    /// incomplete AND the task was not carried through.
    /// </remarks>
    /// <param name="answerRepeating">The cut answer was stopped because the model was repeating itself: "increase the
    /// context length" is then the one remedy that does not help, and the notice names the ones that do.</param>
    public static string EndNotice(bool reachedIterationLimit, bool loopDetected, bool answerCut,
                                   bool editsWithoutEffect = false, bool answerRepeating = false,
                                   bool lastCheckFailed = false)
    {
        var notices = new List<string>(4);
        if (reachedIterationLimit) notices.Add(Strings.AgentEndedAtIterationLimit);
        else if (loopDetected)     notices.Add(Strings.AgentEndedOnRepeat);
        if (answerCut)             notices.Add(answerRepeating ? Strings.AnswerStoppedRepeating : Strings.AnswerCutAtLimit);
        if (editsWithoutEffect)    notices.Add(Strings.AgentEditsNotApplied);
        if (lastCheckFailed)       notices.Add(Strings.AgentLastCheckFailed);
        return string.Join("\n\n", notices);
    }

    /// <summary>The tools that write a file — every one backs the file up first, so a write that lands counts in the run.</summary>
    /// <remarks>⚠ A property, held by <c>EditsWithoutEffectTests</c>: every tool whose code calls
    /// <c>BackUpBeforeChangeAsync</c> is here. Kept as a list of the three content edits, it missed
    /// <c>rename_symbol</c>, whose default dry run writes nothing — and a model answered "renamed everywhere" with no
    /// notice under it.</remarks>
    private static readonly HashSet<string> FileEditTools = new(StringComparer.Ordinal)
        { "write_file", "apply_diff", "apply_edits", "rename_symbol", "delete_file", "restore_file", "update_memory" };

    /// <summary>Whether <paramref name="toolName"/> is one that writes a file.</summary>
    internal static bool IsFileEdit(string toolName) => FileEditTools.Contains(toolName);

    /// <summary>
    /// Whether the run tried to edit a file and changed none — read from what happened, never from the answer.
    /// </summary>
    /// <param name="filesChangedInRun"><see cref="Execution.FileHistoryService.CurrentRunFileCount"/>: every write
    /// that lands is entered in the run, so 0 means none did; <c>null</c> (no run) says nothing.</param>
    /// <remarks>
    /// ⚠ A small model whose write was refused (not read first, declined at the prompt, old_content not found) goes on
    /// to answer "the page has been updated" — the answer is the part the user reads, and it is false. Whether it
    /// CLAIMS a change is a question about its wording in ten languages; whether a change LANDED is a fact of the run.
    /// A background task (<c>/task</c>) does not ask: its edits are proposals by construction.
    /// </remarks>
    public static bool EditsWithoutEffect(IEnumerable<ToolExecution> executions, int? filesChangedInRun) =>
        filesChangedInRun == 0 && executions.Any(e => FileEditTools.Contains(e.Name));

    /// <summary>
    /// Whether a turn that CHANGED files ended on a failing test or build check — read from the tool's own verdict and
    /// from the run's writes, never from the answer.
    /// </summary>
    /// <remarks>
    /// ⚠ A model answers "all tests pass" right after a run that said "✗ BUILD FAILED" (measured: gpt-oss, and a
    /// failing test under "the bug is fixed" with Llama 3.1 and Qwen3 Coder). Only the last check counts: a red run the
    /// model fixed and ran again green says nothing. And only a turn that changed files: asked "does it compile? do not
    /// fix anything", a model that reports the error has done the task — the notice would contradict a true answer
    /// (measured, Devstral). The verdict readers are <c>/tdd</c>'s, <c>get_diagnostics</c>'s and Smart Fix's.
    /// </remarks>
    /// <param name="filesChangedInRun"><see cref="Execution.FileHistoryService.CurrentRunFileCount"/>; <c>null</c> (no
    /// run) or 0 says nothing.</param>
    public static bool LastCheckFailed(IEnumerable<ToolExecution> executions, int? filesChangedInRun) =>
        filesChangedInRun is > 0 && LastCheck(executions)?.Failed == true;

    /// <summary>What a check of a turn checked.</summary>
    internal enum CheckKind { Build, Tests }

    /// <summary>A check's kind and verdict: <c>true</c> failed, <c>false</c> passed, <c>null</c> nothing proven.</summary>
    internal readonly record struct CheckVerdict(CheckKind Kind, bool? Failed);

    /// <summary>
    /// The last check of a turn — a build (<c>get_diagnostics</c>), the tests (<c>run_tests</c>), the build an edit's
    /// Smart Fix ran, or a test run or build through the shell —, or <c>null</c> when nothing checked.
    /// </summary>
    /// <remarks>
    /// ⚠ One reader for the end-of-turn notice and the run's result bar: read twice, the notice counted a test run
    /// through the shell and the bar did not — "the last test or build of this turn failed" printed beside
    /// "✓ build passed". ⚠ Four checks, not two: an edit's Smart Fix note is a build too (a turn that ENDED on its
    /// compilation errors said nothing), and the model running pytest by hand is a check like any other, judged on
    /// the exit code, never on words.
    /// </remarks>
    internal static CheckVerdict? LastCheck(IEnumerable<ToolExecution> executions)
    {
        var last = executions.LastOrDefault(e => e.Name is "run_tests" or "get_diagnostics"
                                              || (IsFileEdit(e.Name) && CodeActions.SmartFixValidator.ReadVerdict(e.Output) is not null)
                                              || (e.Name == "run_command" && CheckCommand.Failed(e.Input, e.Output) is not null));
        return last switch
        {
            null => null,
            { Name: "get_diagnostics" } => new CheckVerdict(CheckKind.Build, Tools.GetDiagnosticsTool.ReadVerdict(last.Output) switch
            {
                Tools.GetDiagnosticsTool.BuildVerdict.Errors => true,
                Tools.GetDiagnosticsTool.BuildVerdict.Clean  => false,
                _                                            => null,
            }),
            { Name: "run_tests" } => new CheckVerdict(CheckKind.Tests,
                Commands.TddCommandHandler.TestsFailed(last.Output) ? true
                : Commands.TddCommandHandler.TestsPassed(last.Output) ? false
                : null),
            { Name: "run_command" } => new CheckVerdict(
                CheckCommand.RunsTests(CheckCommand.CommandOf(last.Input)) ? CheckKind.Tests : CheckKind.Build,
                CheckCommand.Failed(last.Input, last.Output)),
            _ => new CheckVerdict(CheckKind.Build, CodeActions.SmartFixValidator.ReadVerdict(last.Output)),
        };
    }

    /// <summary>
    /// Picks what the final render pass should show. <paramref name="finalResponse"/> is
    /// the agent's stored final response (may still contain &lt;think&gt; tags).
    /// </summary>
    public static FinalAnswerKind DecideFinalAnswer(
        bool streamingBubbleVisible, string? finalResponse, int executionCount)
    {
        if (streamingBubbleVisible)            return FinalAnswerKind.StreamedAnswer;
        if (!IsVisiblyEmpty(finalResponse))    return FinalAnswerKind.FinalText;
        if (executionCount > 0)                return FinalAnswerKind.ToolSummary;
        return FinalAnswerKind.EmptyFallback;
    }

    /// <summary>
    /// What stands in for the answer when the model wrote none but tools ran (<see cref="FinalAnswerKind.ToolSummary"/>):
    /// "✓ Done — &lt;tools&gt;" only for a turn that carried its task to its end, "Tools called — &lt;tools&gt;" when
    /// <paramref name="endNotice"/> follows it.
    /// </summary>
    /// <remarks>
    /// ⚠ The basic loop answers EMPTY when it stops a model that repeats its tool calls after work was done, so the
    /// summary is exactly what a stopped turn shows — and the end notice under it says "the agent was stopped". A check
    /// mark above that sentence is the product contradicting itself, and the check mark is what the eye reads first.
    /// Every notice counts, not only the loop: edits that did not land or a failing last check are not "done" either.
    /// </remarks>
    public static string ToolSummaryAnswer(IEnumerable<ToolExecution> executions, string? endNotice) =>
        string.IsNullOrEmpty(endNotice)
            ? Strings.MsgAgentDone(BuildToolSummary(executions))
            : Strings.MsgAgentToolsCalled(BuildToolSummary(executions));

    /// <summary>"read_file, write_file ×3" — tool names grouped with a ×count when repeated.</summary>
    public static string BuildToolSummary(IEnumerable<ToolExecution> executions) =>
        string.Join(", ", executions
            .GroupBy(e => e.Name)
            .Select(g => g.Count() == 1 ? g.Key : $"{g.Key} \xd7{g.Count()}"));

    /// <summary>Truncates a tool output for its result bubble (full output stays in the history).</summary>
    public static string BuildToolPreview(string output, int maxLength = 500) =>
        output.Length > maxLength ? output[..maxLength] + Strings.MsgTruncated : output;

    /// <summary>The heading the user's message carries when the product injected blocks ahead of it.</summary>
    public const string UserRequestHeader = "## User request";

    /// <summary>
    /// The message the model receives: the blocks the product injected unasked (RAG auto-context, workspace
    /// context), then the user's message — attachments included — under its own heading. Unchanged when nothing
    /// was injected.
    /// </summary>
    /// <remarks>
    /// ⚠ Every injected block carries a heading ("## Relevant code", "## Workspace context (auto-injected…)") and
    /// the user's words did not: a one-line request after them reads as one more line of context, and an agent
    /// asked to remember a fact planned an analysis of the workspace. Structural, like the headings beside it: not
    /// localized.
    /// </remarks>
    public static string WithInjectedContext(string message, params string?[] blocks)
    {
        var injected = blocks.Where(b => !string.IsNullOrWhiteSpace(b)).ToList();
        return injected.Count == 0
            ? message
            : string.Join("\n\n", injected) + "\n\n" + UserRequestHeader + "\n\n" + message;
    }

    /// <summary>
    /// Builds the context-enriched history message sent to the model (not shown in the
    /// chat bubble): each attachment as a labelled fenced block, then the user's text.
    /// </summary>
    public static string BuildHistoryText(string userText, IReadOnlyList<AttachmentContent> attachments)
    {
        if (attachments.Count == 0) return userText;
        var sb = new StringBuilder();
        foreach (var att in attachments)
        {
            sb.AppendLine($"[Attached: {att.Label}]");
            sb.AppendLine("```");
            sb.AppendLine(att.Content);
            sb.AppendLine("```");
            sb.AppendLine();
        }
        sb.Append(userText);
        return sb.ToString();
    }

    /// <summary>
    /// The chat bubble for the same turn: the user's text, plus one line naming what was sent with
    /// it. Unchanged when nothing was attached.
    /// </summary>
    /// <remarks>
    /// ⚠ The chips are cleared on send, so a bubble that keeps only the typed text leaves nothing —
    /// on screen, in the exported conversation or in the session file — saying that a file, a
    /// selection or a <c>@diff</c> went with the question. A reloaded session then hands the model
    /// "explain this" <b>without</b> what "this" referred to (the attached content lives only in
    /// the API history, never persisted), and regeneration replays the turn having lost its
    /// attachments without saying so.
    /// The content itself is deliberately not saved — a <c>@clipboard</c> or a <c>@diff</c> is the
    /// snapshot of a moment that has passed, and an attached file can be read again. The
    /// <b>label</b> is enough to stop lying.
    ///
    /// ⚠ On the VS Code side, an <c>@mention</c> stays written in the question's text, but a chip
    /// ("+" menu, selection, clipboard, problems) is not: the adapter names it under the question
    /// with the same sentence (<c>chatViewProvider.nameAttachmentsInQuestion</c>).
    /// </remarks>
    public static string BuildBubbleText(string userText, IReadOnlyList<string> attachmentLabels)
    {
        if (attachmentLabels.Count == 0) return userText;
        var recap = Strings.MsgAttachedRecap(string.Join(" · ", attachmentLabels));
        return string.IsNullOrEmpty(userText) ? recap : userText + "\n\n" + recap;
    }

    /// <summary>
    /// The inverse of <see cref="BuildBubbleText"/>: the question, and the names of what went with it — so the chat
    /// draws them as chips under the question while the saved text keeps the sentence.
    /// </summary>
    /// <remarks>A recap written in another language (a session saved before a language change) is not recognised:
    /// the bubble then shows its text whole, sentence included — never a name cut out of it.</remarks>
    public static (string Text, IReadOnlyList<string> Attachments) SplitBubbleText(string bubble)
    {
        var prefix = Strings.MsgAttachedRecap("\0").Split('\0')[0];
        if (prefix.Length == 0) return (bubble, []);
        var at = bubble.LastIndexOf(prefix, StringComparison.Ordinal);
        if (at < 0 || bubble.IndexOf('\n', at) >= 0) return (bubble, []);
        if (at > 0 && !bubble[..at].EndsWith("\n\n", StringComparison.Ordinal)) return (bubble, []);
        var names = bubble[(at + prefix.Length)..].Split(" · ", StringSplitOptions.RemoveEmptyEntries);
        return names.Length == 0 ? (bubble, []) : (bubble[..at].TrimEnd('\n'), names);
    }

    /// <summary>
    /// The assistant text persisted in the durable history: the bubble actually shown to
    /// the user when there is one, the stored final response otherwise — think tags
    /// stripped in both cases. Returns an empty string when nothing is worth persisting.
    /// </summary>
    public static string ChoosePersistedAnswer(string? shownBubbleContent, string? finalResponse)
    {
        var answer = MarkdownParser.StripThinkTags(shownBubbleContent ?? finalResponse);
        return string.IsNullOrWhiteSpace(answer) ? string.Empty : answer;
    }

    /// <summary>
    /// What the durable history keeps as the answer of an agent or tool turn that did not fail — one decision for both
    /// front-ends: the answer when the model wrote one, the tool-summary line when it only called tools, nothing for an
    /// empty response.
    /// </summary>
    /// <remarks>
    /// ⚠ Decided twice, the two front-ends disagreed on the two text-less endings. Tools called without a word: Visual
    /// Studio kept the "✓ Done — write_file ×2" line, the VS Code host kept nothing — the next question then followed
    /// the previous one with no answer between them, the clients merge consecutive user messages, and the model read
    /// both tasks as one request (and redid the first). An empty response: the host kept nothing, Visual Studio kept the
    /// "empty response from model X" diagnostic as what the model had said — a notice is never an answer.
    /// </remarks>
    public static string PersistedAnswer(FinalAnswerKind kind, string? shownBubbleContent, string? finalResponse,
                                         IReadOnlyList<ToolExecution> executions, string? endNotice) => kind switch
    {
        FinalAnswerKind.ToolSummary   => ToolSummaryAnswer(executions, endNotice),
        FinalAnswerKind.EmptyFallback => string.Empty,
        _                             => ChoosePersistedAnswer(shownBubbleContent, finalResponse),
    };

    /// <summary>
    /// Appends a prompt to the recall history (no duplicate at the top, oldest evicted
    /// past <paramref name="max"/>). Returns <c>true</c> when the list changed and
    /// should be saved.
    /// </summary>
    public static bool AppendPromptHistory(List<string> history, string prompt, int max)
    {
        if (history.Count > 0 && history[^1] == prompt) return false;
        history.Add(prompt);
        if (history.Count > max)
            history.RemoveAt(0);
        return true;
    }

    /// <summary>Single-line preview: capped at <paramref name="max"/> chars (ellipsis added), newlines flattened.</summary>
    public static string OneLinePreview(string text, int max)
    {
        var capped = text.Length > max ? text[..max] + "…" : text;
        return capped.Replace('\n', ' ');
    }

    /// <summary>
    /// Stable key of a prompt-history entry for <c>/phistory use</c>: FNV-1a over the text, 8 hex
    /// digits. Identical prompts share a key, which is harmless: they fill the same text.
    /// </summary>
    public static string PromptKey(string prompt)
    {
        var hash = 2166136261u;
        foreach (var c in prompt)
            hash = unchecked((hash ^ c) * 16777619u);
        return hash.ToString("x8");
    }

    /// <summary>
    /// The <c>/phistory</c> listing: entries matching <paramref name="term"/> (all when
    /// <c>null</c>), most recent first, each with its 1-based index and a ready-to-type
    /// <c>/phistory use n</c>. Returns <c>null</c> when nothing matches (the VM shows
    /// the notice).
    /// </summary>
    public static string? FormatPromptHistory(IReadOnlyList<string> history, string? term)
    {
        var matches = history
            .Select((p, i) => (Idx: i + 1, Text: p))
            .Where(x => term is null || x.Text.Contains(term, StringComparison.OrdinalIgnoreCase))
            .Reverse()     // most recent first
            .ToList();
        if (matches.Count == 0) return null;

        var sb = new StringBuilder((term is null ? Strings.PHistoryListHeader : Strings.PHistoryListHeaderTerm(term)) + "\n\n");
        foreach (var (idx, text) in matches)
            // By content key, not position: the list is capped, so each new prompt evicts the oldest
            // entry and shifts every number.
            sb.AppendLine($"**#{idx}** {OneLinePreview(text, 80)}  `/phistory use {PromptKey(text)}`");
        return sb.ToString().TrimEnd();
    }
}
