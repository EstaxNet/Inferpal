using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services.Execution;
using Inferpal.Services.Inference;

namespace Inferpal.Services.Agent;

/// <summary>What the pre-send context check ended up doing.</summary>
internal enum ContextOutcome
{
    /// <summary>Under budget — nothing to do, nothing to say.</summary>
    None,

    /// <summary>Old turns dropped outright (compaction off, or nothing worth summarising).</summary>
    Truncated,

    /// <summary>Compaction was attempted and failed or timed out — truncated instead.</summary>
    CompactionFellBack,

    /// <summary>Old turns replaced by an LLM-written summary.</summary>
    Compacted,
}

/// <summary>
/// The decision <b>and</b> the summary, ready for the caller to apply to its own history.
/// </summary>
/// <param name="Summary">Non-null only for <see cref="ContextOutcome.Compacted"/>.</param>
/// <param name="Notice">What to show the user, or empty for <see cref="ContextOutcome.None"/>.</param>
/// <param name="Window">
/// The window, in tokens, the conversation was measured against (<see cref="ContextManager.EffectiveWindowAsync"/>)
/// — what the context gauges must show too, or they announce room a smaller loaded window does not have.
/// </param>
internal sealed record ContextDecision(
    ContextOutcome Outcome, CompactionPlan Plan, string? Summary, string Notice, int Window = 0,
    bool SummaryIncomplete = false)
{
    /// <summary>
    /// The conversation lost turns with nothing — or only part of a summary — put in their place: a degraded
    /// result, not the one that was asked for.
    /// </summary>
    /// <remarks>
    /// ⚠ The rule lives here because BOTH front-ends read it: a successful compaction is a
    /// collapsible tool bubble, the fallbacks are plain warnings — the conversation lost turns,
    /// and that is read in plain text. Rendered the same way, the degraded result is the one that
    /// looks routine. A summary that covers only part of the dropped turns is one of them
    /// (<see cref="SummaryIncomplete"/>: cut at the length limit, or written from the newest part only).
    /// </remarks>
    public bool IsDegraded =>
        Outcome is ContextOutcome.Truncated or ContextOutcome.CompactionFellBack || SummaryIncomplete;
}

/// <summary>
/// The pre-send context check, shared by both front-ends.
/// </summary>
/// <remarks>
/// ⚠ Shared, and not a copy per front-end: where this check is missing the history is <b>never</b>
/// bounded — it grows past the model's <c>num_ctx</c>, and the backend then drops the head of the
/// conversation, system prompt included, without a word. An assistant that "forgets".
///
/// It is also what makes <c>compactionEnabled</c>, <c>contextWindowKeepTurns</c> and
/// <c>compactionTimeoutSeconds</c> mean something in both editors: a settings panel must not offer
/// a control that does nothing on its side.
///
/// <para><b>What is here and what stays with the caller.</b> Here: the decision
/// (<see cref="HistoryCompaction"/>), the summarising model call, and its fuse. With the caller:
/// <i>applying</i> it to the history, and the rendering. That is not timidity — the VS window must
/// mutate its history on its own context, or it replaces it under the turn loop that reads it
/// (convention rule 6).</para>
/// </remarks>
internal static class ContextManager
{
    /// <summary>
    /// Decides what to do and, when compaction applies, obtains the summary.
    /// Never throws: a failed or timed-out summary degrades to
    /// <see cref="ContextOutcome.CompactionFellBack"/>.
    /// </summary>
    internal static async Task<ContextDecision> PrepareAsync(
        IReadOnlyList<ChatMessageDto> history,
        InferpalConfig                config,
        IInferenceProvider            client,
        int                           lastPromptTokens,
        Action<string>?               onStep,
        CancellationToken             ct,
        string?                       model = null)
    {
        var window = await EffectiveWindowAsync(config, client, model, ct).ConfigureAwait(false);
        var plan   = HistoryCompaction.Decide(
            history, window, lastPromptTokens,
            config.ContextWindowKeepTurns, config.KvCacheAnchorMessages, config.CompactionEnabled);

        if (plan.Action == CompactionAction.None)
            return new ContextDecision(ContextOutcome.None, plan, null, string.Empty, window);

        if (plan.Action == CompactionAction.Truncate)
            return new ContextDecision(ContextOutcome.Truncated, plan, null,
                                       Strings.MsgContextTruncated(plan.Count, plan.KeepTurns), window);

        onStep?.Invoke(Strings.StatusCompacting);

        var (summary, cut, omitted) = await SummarizeAsync(history, plan, config, client, ct).ConfigureAwait(false);

        // ⚠ The fuse blew: we truncate, and we SAY so — that is a degraded result, not the one that
        // was asked for. The two look alike on the history and not at all to the user.
        if (string.IsNullOrEmpty(summary))
            return new ContextDecision(ContextOutcome.CompactionFellBack, plan, null,
                                       Strings.MsgContextCompactionFallback, window);

        var notice = Strings.MsgContextCompacted(plan.Count, plan.KeepTurns)
                   + (plan.KvAnchor > 0 ? Strings.MsgKvCacheAnchorNote(plan.KvAnchor) : string.Empty);
        // ⚠ An incomplete summary is KEPT — dropping it would lose the turns entirely, and the window is fullest
        // exactly when compaction runs — but it is said to both readers: read as whole, the model denies what was said
        // in the part it misses, and the user reads "compacted". Two causes, two sentences: the summary stopped at the
        // length limit, or the oldest turns never reached the summarizer.
        if (omitted > 0)
        {
            summary += HistoryCompaction.PartialSummaryMarker(omitted);
            notice  += "\n\n" + Strings.MsgContextSummaryPartial(omitted, plan.Count);
        }
        if (cut)
        {
            summary += HistoryCompaction.CutSummaryMarker;
            notice  += "\n\n" + Strings.MsgContextSummaryCut;
        }
        return new ContextDecision(ContextOutcome.Compacted, plan, summary, notice, window,
                                   SummaryIncomplete: cut || omitted > 0);
    }

    /// <summary>
    /// The window the conversation is measured against: the configured one, or the one the server
    /// really loaded <paramref name="model"/> with when that is SMALLER.
    /// </summary>
    /// <remarks>
    /// ⚠ LM Studio, vLLM and llama-server load a model with a window of their own choosing and say which
    /// (llama-server's default is 4 096 tokens). Measured against the
    /// configured window alone, a conversation between the two was refused on every request while
    /// compaction waited for a threshold it could never reach — stuck until the user cleared it. A larger
    /// loaded window never raises the configured one: that is the user's budget (and Ollama's num_ctx).
    /// </remarks>
    internal static async Task<int> EffectiveWindowAsync(
        InferpalConfig config, IInferenceProvider client, string? model, CancellationToken ct)
    {
        var configured = config.ContextWindowSize;
        if (configured <= 0 || string.IsNullOrWhiteSpace(model)) return configured;

        int? loaded = null;
        try { loaded = await client.GetLoadedContextWindowAsync(model, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { Diagnostics.Swallow("ContextManager.EffectiveWindow", ex); }

        return loaded is > 0 && loaded < configured ? loaded.Value : configured;
    }

    /// <summary>The summarising call, with its own deadline. <c>null</c> on any failure; <c>Cut</c> when the reply
    /// stopped at the model's length limit; <c>Omitted</c> the oldest messages that did not fit in the request.</summary>
    private static async Task<(string? Text, bool Cut, int Omitted)> SummarizeAsync(
        IReadOnlyList<ChatMessageDto> history, CompactionPlan plan,
        InferpalConfig config, IInferenceProvider client, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(10, config.CompactionTimeoutSeconds)));

            // The request is bounded by the SUMMARIZER's window: the utility model is not the chat model, and a server
            // may have loaded it with a smaller one.
            var model   = await ModelRouter.ResolveUtilityAsync(config, client, cts.Token).ConfigureAwait(false);
            var window  = await EffectiveWindowAsync(config, client, model, cts.Token).ConfigureAwait(false);
            var request = HistoryCompaction.BuildSummarizeRequest(
                HistoryCompaction.SliceToCompact(history, plan), HistoryCompaction.SummaryInputBudgetChars(window));

            // ⚠ SendChatAsync, never RunAgentAsync: the agent loop reports a network failure as its
            // FinalResponse, and the error text would become the "summary" that replaces the turns.
            var turn = await client.SendChatAsync(
                model, request.Messages, EmptyToolRegistry.Instance, onToken: null, cts.Token).ConfigureAwait(false);

            return (MarkdownParser.StripThinkTags(turn.TextContent).Trim(), turn.CutAtLimit, request.Omitted);
        }
        // A cancellation by the USER propagates; the fuse's own is a fallback, not an error.
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return (null, false, 0); }
        catch (Exception ex)
        {
            Diagnostics.Swallow("ContextManager.Summarize", ex);
            return (null, false, 0);
        }
    }
}
