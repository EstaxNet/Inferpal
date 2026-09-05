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
internal sealed record ContextDecision(
    ContextOutcome Outcome, CompactionPlan Plan, string? Summary, string Notice);

/// <summary>
/// The pre-send context check, shared by both front-ends.
/// </summary>
/// <remarks>
/// ⚠ It lived <b>entirely in the Visual Studio window</b>. Measured consequence: on the VS Code
/// side the history was <b>never</b> bounded — it grew until it went past the model's
/// <c>num_ctx</c>, and it was then the backend that dropped the head of the conversation, system
/// prompt included, without a word. An assistant that "forgets".
///
/// And the VS Code settings panel offered <c>compactionEnabled</c>, <c>contextWindowKeepTurns</c>
/// and <c>compactionTimeoutSeconds</c> — three controls that did <b>nothing</b> there. A setting
/// rendered as functional with no effect is the class of this whole series.
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
        CancellationToken             ct)
    {
        var plan = HistoryCompaction.Decide(
            history, config.ContextWindowSize, lastPromptTokens,
            config.ContextWindowKeepTurns, config.KvCacheAnchorMessages, config.CompactionEnabled);

        if (plan.Action == CompactionAction.None)
            return new ContextDecision(ContextOutcome.None, plan, null, string.Empty);

        if (plan.Action == CompactionAction.Truncate)
            return new ContextDecision(ContextOutcome.Truncated, plan, null,
                                       Strings.MsgContextTruncated(plan.Count, plan.KeepTurns));

        onStep?.Invoke(Strings.StatusCompacting);

        var summary = await SummarizeAsync(history, plan, config, client, ct).ConfigureAwait(false);

        // ⚠ The fuse blew: we truncate, and we SAY so — that is a degraded result, not the one that
        // was asked for. The two look alike on the history and not at all to the user.
        if (string.IsNullOrEmpty(summary))
            return new ContextDecision(ContextOutcome.CompactionFellBack, plan, null,
                                       Strings.MsgContextCompactionFallback);

        var note = plan.KvAnchor > 0 ? Strings.MsgKvCacheAnchorNote(plan.KvAnchor) : string.Empty;
        return new ContextDecision(ContextOutcome.Compacted, plan, summary,
                                   Strings.MsgContextCompacted(plan.Count, plan.KeepTurns) + note);
    }

    /// <summary>The summarising call, with its own deadline. <c>null</c> on any failure.</summary>
    private static async Task<string?> SummarizeAsync(
        IReadOnlyList<ChatMessageDto> history, CompactionPlan plan,
        InferpalConfig config, IInferenceProvider client, CancellationToken ct)
    {
        try
        {
            var toCompact = HistoryCompaction.SliceToCompact(history, plan);
            var request   = HistoryCompaction.BuildSummarizeRequest(history, toCompact);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(10, config.CompactionTimeoutSeconds)));

            var result = await client.RunAgentAsync(
                model:   await ModelRouter.ResolveUtilityAsync(config, client, cts.Token).ConfigureAwait(false),
                history: request,
                tools:   EmptyToolRegistry.Instance,
                onStep:  _ => { },
                onToken: null,
                ct:      cts.Token).ConfigureAwait(false);

            return result.FinalResponse?.Trim();
        }
        // A cancellation by the USER propagates; the fuse's own is a fallback, not an error.
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            Diagnostics.Swallow("ContextManager.Summarize", ex);
            return null;
        }
    }
}
