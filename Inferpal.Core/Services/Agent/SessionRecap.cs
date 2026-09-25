using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Models;

namespace Inferpal.Services.Agent;

/// <summary>What a session recap produced: the recap, or why there is none.</summary>
/// <param name="Recap">The text to fold into the system prompt and show, or <c>null</c>.</param>
/// <param name="Failure">The run's error when it failed; <c>null</c> when the model merely answered nothing.</param>
internal sealed record SessionRecapResult(string? Recap, string? Failure);

/// <summary>
/// The OODA session recap written every <c>oodaTurnThreshold</c> turns, shared by both front-ends: the request, its
/// budget, the call and the reading of the reply live here; folding the recap into the system prompt and showing it
/// stay with each front-end.
/// </summary>
/// <remarks>
/// ⚠ The request is bounded by the window of the model that writes the recap — the utility model, which a server
/// loads with a window of its own (LM Studio chooses one per model). Sent whole, the history refused there (no recap,
/// ever) and was cut at its head by Ollama: the chat's system prompt first, which carries the previous recap, so the
/// "Goal" section was rewritten from the recent turns alone. Hence no chat system prompt (pinned files, rules and
/// memory are not the conversation), the previous recap carried explicitly, and the transcript kept from the newest
/// turn back (<see cref="HistoryCompaction.BoundedTranscript"/>).
/// ⚠ The request ENDS on <see cref="Strings.OodaSummarizePrompt"/>, alone in a user message after a system message:
/// strict chat templates refuse two user messages in a row.
/// </remarks>
internal static class SessionRecap
{
    /// <summary>Writes the recap of <paramref name="history"/>. Never throws but on cancellation.</summary>
    public static async Task<SessionRecapResult> WriteAsync(
        IReadOnlyList<ChatMessageDto> history, string? previousRecap,
        InferpalConfig config, IInferenceProvider client, CancellationToken ct)
    {
        var model   = await ModelRouter.ResolveUtilityAsync(config, client, ct).ConfigureAwait(false);
        var window  = await ContextManager.EffectiveWindowAsync(config, client, model, ct).ConfigureAwait(false);
        var request = BuildRequest(history, previousRecap, HistoryCompaction.SummaryInputBudgetChars(window));

        var result = await client.RunAgentAsync(
            model:   model,
            history: request.Messages,
            tools:   EmptyToolRegistry.Instance,
            onStep:  _ => { },
            onToken: null,
            ct:      ct).ConfigureAwait(false);

        // ⚠ A failed run returns its error as the reply — and the recap joins the system prompt of every following
        // question.
        if (result.Failed) return new SessionRecapResult(null, result.FinalResponse);

        // The basic loop returns the reply whole: the reasoning must not be folded into every following system prompt.
        var recap = MarkdownParser.StripThinkTags(result.FinalResponse);
        if (string.IsNullOrWhiteSpace(recap)) return new SessionRecapResult(null, null);
        if (request.Omitted > 0) recap += HistoryCompaction.PartialSummaryMarker(request.Omitted);
        if (result.AnswerCut)    recap += HistoryCompaction.CutSummaryMarker;
        return new SessionRecapResult(recap, null);
    }

    /// <summary>
    /// The recap request: a system message carrying the previous recap and the conversation (its own system prompt
    /// left out) within <paramref name="budgetChars"/>, then the recap instruction.
    /// </summary>
    internal static SummarizeRequest BuildRequest(
        IReadOnlyList<ChatMessageDto> history, string? previousRecap, int budgetChars)
    {
        var turns = history.Count > 0 && history[0].Role == "system" ? history.Skip(1).ToList() : history.ToList();

        // Model-facing and structural, like the transcript's own markers: not localized.
        var head = string.IsNullOrWhiteSpace(previousRecap)
            ? "The conversation to recap:\n\n"
            : $"The recap written earlier in this conversation:\n\n{previousRecap.Trim()}\n\nThe conversation to recap:\n\n";
        var (transcript, omitted) = HistoryCompaction.BoundedTranscript(
            turns, budgetChars == int.MaxValue ? budgetChars : Math.Max(0, budgetChars - head.Length));

        return new SummarizeRequest(
        [
            new ChatMessageDto("system", head + transcript),
            new ChatMessageDto("user",   Strings.OodaSummarizePrompt),
        ], omitted);
    }
}
