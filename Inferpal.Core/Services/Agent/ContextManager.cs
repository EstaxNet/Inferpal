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
/// <param name="ShownSummary">The summary as the person may read it: <paramref name="Summary"/> without the notes written
/// for the model about what it lacks (the person reads them in <paramref name="Notice"/>).</param>
/// <param name="Window">
/// The window, in tokens, the conversation was measured against (<see cref="ContextManager.EffectiveWindowAsync"/>)
/// — what the context gauges must show too, or they announce room a smaller loaded window does not have.
/// </param>
/// <param name="WindowRead">The server said the window it loaded the model with; <c>false</c> when <paramref name="Window"/> is
/// the configured one standing in (<see cref="ContextManager.MeasureWindowAsync"/>).</param>
internal sealed record ContextDecision(
    ContextOutcome Outcome, CompactionPlan Plan, string? Summary, string Notice, int Window = 0,
    bool SummaryIncomplete = false, string? ShownSummary = null, bool WindowRead = true)
{
    /// <summary>The turn sends only the tools used most (<see cref="Execution.SmallWindowToolRegistry"/>): the window is too
    /// small for every definition. Both front-ends wrap the turn's registry by it, and say it once.</summary>
    public bool ToolsCut { get; init; }

    /// <summary>The tool definitions the turn was planned for: every one, or the kept ones when <see cref="ToolsCut"/> —
    /// what the head-share notice weighs.</summary>
    public int ToolTokens { get; init; }

    /// <summary>The server refused to load the chat model at the window it needs (pari 127): said as a warning by both
    /// front-ends, again only once the refusal is no longer remembered (<c>LmStudioClient.RefusalLife</c>); <c>null</c>
    /// otherwise.</summary>
    public string? WindowNotice { get; init; }

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
    /// <param name="lastPromptTokens">What the conversation weighs — never the tool definitions (see
    /// <paramref name="toolTokens"/>); 0 = not measured yet, nothing to decide.</param>
    /// <param name="toolTokens">The tool definitions the turn about to go will carry (<see cref="NextTurnToolTokens"/>).
    /// ⚠ Not optional in effect: every turn with tools sends them, and they weigh about half of an 8 192-token window,
    /// so a decision without them compacts only after the backend has already refused the request.</param>
    /// <param name="keptToolTokens">What the turn carries instead when its window is too small for every definition
    /// (<see cref="NextTurnKeptToolTokens"/>), asked only then; <c>null</c> = a turn whose tools are never cut.</param>
    internal static async Task<ContextDecision> PrepareAsync(
        IReadOnlyList<ChatMessageDto> history,
        InferpalConfig                config,
        IInferenceProvider            client,
        int                           lastPromptTokens,
        Action<string>?               onStep,
        CancellationToken             ct,
        string?                       model = null,
        int                           toolTokens = 0,
        Func<int>?                    keptToolTokens = null)
    {
        // Only a turn that carries tools: the lever exists because their definitions fill a small window.
        var windowNotice = toolTokens > 0 ? await EnsureWindowAsync(config, client, model, onStep, ct).ConfigureAwait(false) : null;
        var (window, read) = await MeasureWindowAsync(config, client, model, ct).ConfigureAwait(false);
        // ⚠ Decided on the window just measured, before the plan: what the turn sends is what the plan makes room for —
        // planned for every definition, the conversation would be compacted for tools never sent.
        var toolsCut = keptToolTokens is not null
                       && Execution.SmallWindowToolRegistry.Cuts(CutWindow(window, read, config.Provider), toolTokens);
        if (toolsCut) toolTokens = keptToolTokens!();
        // Every way out carries what was measured and decided: a decision without them makes neither front-end cut.
        ContextDecision Done(ContextDecision d) =>
            d with { WindowRead = read, WindowNotice = windowNotice, ToolsCut = toolsCut, ToolTokens = toolTokens };
        // ⚠ The question about to go is already in the history — with its attachments, the auto-context and the
        // workspace block — and not in lastPromptTokens, measured after the previous turn. Decided on that figure alone,
        // a large attachment sent near the trigger goes out over the window: Ollama drops the head of the conversation
        // in silence, LM Studio refuses a request that compaction would have made fit.
        var measured = lastPromptTokens == 0 ? 0 : Math.Max(lastPromptTokens, AgentOrchestrator.EstimateTokens(history));
        var plan   = HistoryCompaction.Decide(
            history, window, NextTurnLoad(measured, toolTokens),
            config.ContextWindowKeepTurns, config.KvCacheAnchorMessages, config.CompactionEnabled, toolTokens);

        if (plan.Action == CompactionAction.None)
            return Done(new ContextDecision(ContextOutcome.None, plan, null, string.Empty, window));

        // The person reads the conversation's messages, never the head the range holds (a marker, a summary); a plan that
        // replaces only the summary held (over the window) says so.
        var own = HistoryCompaction.ConversationMessages(history, plan);
        if (plan.Action == CompactionAction.Truncate)
            return Done(new ContextDecision(ContextOutcome.Truncated, plan, null,
                                       own > 0 ? Strings.MsgContextTruncated(own, plan.KeepTurns)
                                               : Strings.MsgContextTruncatedSummaryOnly(plan.KeepTurns), window));

        onStep?.Invoke(Strings.StatusCompacting);

        var (summary, cut, omitted, failure) =
            await SummarizeAsync(history, plan, config, client, onStep, ct).ConfigureAwait(false);

        // ⚠ No summary: we truncate, and we SAY so — that is a degraded result, not the one that
        // was asked for. The two look alike on the history and not at all to the user. And the
        // sentence names the cause: "timed out" sends the user to compactionTimeoutSeconds, which
        // does nothing for a server that refused the request (a misspelled utilityModel fails every
        // compaction the same way).
        if (string.IsNullOrEmpty(summary))
            return Done(new ContextDecision(ContextOutcome.CompactionFellBack, plan, null,
                                       failure ?? Strings.MsgContextCompactionEmpty, window));

        // ⚠ An incomplete summary is KEPT — dropping it would lose the turns entirely, and the window is fullest
        // exactly when compaction runs — but it is said to both readers: read as whole, the model denies what was said
        // in the part it misses, and the user reads "compacted". Two causes, two sentences: the summary stopped at the
        // length limit, or the oldest turns never reached the summarizer. And messages dropped before the summarised turns
        // stay said: the marker that held them is summarised away.
        // What the request left out, as the conversation's messages and the summary held: a marker left out is said by
        // the note of what was dropped before (once), and alone it makes no partial summary.
        var lost    = omitted > 0 ? HistoryCompaction.Omitted(history, plan, omitted) : (Messages: 0, Summary: false);
        var partial = lost.Messages > 0 || lost.Summary;
        var shown = summary;
        var kept  = summary
                  + (partial ? HistoryCompaction.PartialSummaryMarker(lost.Messages, lost.Summary) : string.Empty)
                  + (cut ? HistoryCompaction.CutSummaryMarker : string.Empty)
                  + (HistoryCompaction.AlreadyDropped(history, plan) is { } gone
                        ? HistoryCompaction.DroppedBeforeSummaryMarker(gone.Messages, gone.EarlierPart) : string.Empty);

        // ⚠ A summary is not bounded by anything but the model: asked to keep "every key fact", a model can write as much
        // as it was given, and each compaction then summarizes the previous summary too. Kept, it leaves the
        // conversation over the trigger — the next request is refused, larger at every turn. Truncation at least fits.
        // Measured as the model keeps it, its notes included.
        if (!FitsAfterSummary(history, plan, kept, toolTokens, window))
            return Done(new ContextDecision(ContextOutcome.CompactionFellBack, plan, null,
                                       Strings.MsgContextSummaryTooLong, window));

        var notice = (own > 0 ? Strings.MsgContextCompacted(own, plan.KeepTurns) : Strings.MsgContextCompactedSummaryOnly(plan.KeepTurns))
                   + (plan.KvAnchor > 0 ? Strings.MsgKvCacheAnchorNote(plan.KvAnchor) : string.Empty);
        if (lost.Messages > 0) notice += "\n\n" + Strings.MsgContextSummaryPartial(lost.Messages, own);
        else if (lost.Summary) notice += "\n\n" + Strings.MsgContextSummaryEarlierLost;
        if (cut)     notice += "\n\n" + Strings.MsgContextSummaryCut;
        return Done(new ContextDecision(ContextOutcome.Compacted, plan, kept, notice, window,
                                   SummaryIncomplete: cut || partial, ShownSummary: shown));
    }

    /// <summary>
    /// What the next turn sends, in tokens: the conversation plus the tool definitions it carries. Read by the pre-send
    /// check, the Visual Studio gauge and the X-Ray — one sum, so none of them announces room the request does not have.
    /// </summary>
    /// <remarks>
    /// The conversation measure never includes the tool definitions: after a turn with tools, both front-ends replace the
    /// server's figure (inflated by the run's own transcript) by an estimate of the history alone. 0 stays 0 — nothing
    /// measured yet is not "the tools alone".
    /// </remarks>
    internal static int NextTurnLoad(int conversationTokens, int toolTokens) =>
        conversationTokens == 0 ? 0 : conversationTokens + toolTokens;

    /// <summary>False when <paramref name="summary"/>, in place of the planned turns, is what keeps the conversation over
    /// the compaction trigger (tool definitions of the next turn included) — truncation would be back under it. When
    /// neither is, the kept turns are the excess, not the summary: it stays. No window known: nothing to fit.</summary>
    internal static bool FitsAfterSummary(
        IReadOnlyList<ChatMessageDto> history, CompactionPlan plan, string summary, int toolTokens, int window)
    {
        if (window <= 0) return true;
        var trigger = window * 8 / 10;
        var summarized = history.ToList();
        HistoryCompaction.ApplySummary(summarized, plan, summary);
        if (AgentOrchestrator.EstimateTokens(summarized) + toolTokens <= trigger) return true;
        var truncated = history.ToList();
        HistoryCompaction.ApplyTruncation(truncated, plan);
        return AgentOrchestrator.EstimateTokens(truncated) + toolTokens > trigger;
    }

    /// <summary>The share of the window above which the system prompt and the tool definitions are said to crowd the turn.</summary>
    internal const int HeadSharePercent = 50;

    /// <summary>
    /// The notice a turn shows when what every request of it carries before the conversation — the system message and the
    /// tool definitions — takes more than <see cref="HeadSharePercent"/> % of the window it is measured against; <c>null</c>
    /// under it, with no known window, or when <paramref name="said"/> — the caller's session — already holds the same root,
    /// model, window and tools: said again when one of them changes, never at each /clear. The remedy is the one that works
    /// on the person's server.
    /// </summary>
    /// <param name="said">What this session said already; the key is added when the notice is returned.</param>
    /// <param name="windowRead">The server said the window it loaded the model with (<see cref="ContextDecision.WindowRead"/>).</param>
    /// <remarks>⚠ After the window is measured AND the prompt rebuilt for it: measured before, it reads a prompt budgeted
    /// for another window. It weighs the tools the turn was planned for (<see cref="ContextDecision.ToolTokens"/>): after
    /// a small window's cut, the kept ones, whose own notice says what was left out.
    /// ⚠ Only Ollama loads the model at the configured window with every request (num_ctx); LM Studio does when Inferpal
    /// loads it (<see cref="EnsureWindowAsync"/>, for a model loaded too small). Elsewhere a window the server did not say
    /// is the setting standing in — a model not loaded yet is then loaded at the server's own size, often
    /// far larger — and the notice would describe a window the model never has, with a remedy that changes nothing: nothing
    /// is said until the window is read (the next question, once the model is loaded). A server that never says its
    /// window is never told it: there the setting is only a guess.</remarks>
    internal static string? HeadShareNotice(string? root, string model, string provider, int window, int configuredWindow,
                                            int systemTokens, int toolTokens, ISet<string> said, bool windowRead = true)
    {
        if (window <= 0) return null;
        if (!windowRead && InferenceProviderFactory.KindOf(provider) != InferenceProviderFactory.Ollama) return null;
        var head = systemTokens + toolTokens;
        var share = (int)Math.Round(head * 100.0 / window);
        // Over the whole window the request is refused, and the refusal says why (MsgContextWontFit): one sentence, not two.
        if (share <= HeadSharePercent || head > window) return null;
        if (!said.Add($"{root}|{model}|{window}|{toolTokens > 0}")) return null;

        return Strings.MsgHeadShare(share, window, window - head, LargerWindowRemedy(provider, window, configuredWindow),
                                    toolTokens >= systemTokens ? Strings.ContextPartTools(toolTokens) : Strings.ContextPartSystem(systemTokens));
    }

    /// <summary>
    /// The tool definitions the next chat turn carries, in tokens: none with tools off, the read-only subset in plan
    /// mode, the mode's tools in a mode the person picked, every offered tool otherwise (MCP and custom tools included) —
    /// the registry each front-end then sends.
    /// </summary>
    /// <param name="window">The window the tools are cut for (<see cref="CutWindow"/>): when the definitions take more
    /// than half of it, the tools the turn then sends (<see cref="Execution.SmallWindowToolRegistry"/>); 0 = every one.</param>
    /// <param name="named">The tools the conversation's questions named: a cut keeps them.</param>
    internal static int NextTurnToolTokens(IToolRegistry tools, bool toolsEnabled, bool planMode, Governance.ChatMode? mode = null,
                                           int window = 0, IReadOnlyCollection<string>? named = null)
    {
        if (!toolsEnabled) return 0;
        // Weighed once when nothing is cut: the gauge asks while an answer streams.
        var turn = NextTurnTools(tools, planMode, mode);
        var all = RequestSize.ToolTokens(turn.Definitions);
        return Execution.SmallWindowToolRegistry.Cuts(window, all)
            ? RequestSize.ToolTokens(new Execution.SmallWindowToolRegistry(turn, window, named).Definitions)
            : all;
    }

    /// <summary>The tool definitions the next turn sends if its window is too small for every one: the tools used most and
    /// the ones the conversation's questions <paramref name="named"/> (<see cref="Execution.SmallWindowToolRegistry"/>) —
    /// what <see cref="PrepareAsync"/> plans for when it cuts.</summary>
    internal static int NextTurnKeptToolTokens(IToolRegistry tools, bool toolsEnabled, bool planMode, Governance.ChatMode? mode,
                                               IReadOnlyCollection<string> named) =>
        !toolsEnabled ? 0
        : RequestSize.ToolTokens(new Execution.SmallWindowToolRegistry(NextTurnTools(tools, planMode, mode), 0, named).Definitions);

    /// <summary>
    /// What a turn that sends only the tools used most says, once per workspace, model, window and mode
    /// (<paramref name="said"/>): which ones it leaves out and how to have them back. <c>null</c> when already said.
    /// </summary>
    /// <param name="modeKey">The mode the tools were cut in (plan, a custom mode, none): another mode leaves other tools
    /// out, and says so. Never the count left out — a question that names a tool would bring the notice back.</param>
    /// <param name="provider">The server kind, and <paramref name="configuredWindow"/> the setting: where a larger window
    /// is made (<see cref="LargerWindowRemedy"/>).</param>
    /// <param name="configuredWindow">The window the settings ask for.</param>
    internal static string? ToolsCutNotice(string? root, string model, int window, IToolRegistry turnTools,
                                           IReadOnlyCollection<string> named, string modeKey, string provider, int configuredWindow,
                                           ISet<string> said)
    {
        var (count, text) = ToolsCutText(new Execution.SmallWindowToolRegistry(turnTools, window, named).LeftOut(turnTools), window,
                                         LargerWindowRemedy(provider, window, configuredWindow));
        // The mode is in the key: another mode at the same window leaves other tools out, and says so; the server too, whose
        // remedy is another.
        return count == 0 || !said.Add($"{CutNoticeKey}{root}|{model}|{provider}|{window}|{modeKey}") ? null : text;
    }

    private const string CutNoticeKey = "cut|";

    /// <summary>Forgets the cut notices <paramref name="said"/> holds: a new conversation is told again what a small window
    /// leaves out, and how to keep a tool.</summary>
    internal static void ForgetCutNotices(HashSet<string> said) =>
        said.RemoveWhere(k => k.StartsWith(CutNoticeKey, StringComparison.Ordinal));

    /// <summary>The key a cut notice is said once under for this mode (<see cref="ToolsCutNotice"/>).</summary>
    internal static string ToolsCutModeKey(bool planMode, Governance.ChatMode? mode) =>
        planMode ? "\u0001plan" : mode?.Name ?? string.Empty;

    /// <summary>The sentence that names the tools <paramref name="left"/> out, and how many: a question's, said once and
    /// naming every one (the person keeps one by writing its name), or a command run's (<paramref name="forRun"/>), a
    /// status line with no question, naming the first few.</summary>
    private static (int Count, string Text) ToolsCutText(IReadOnlyList<string> left, int window, string remedy, bool forRun = false)
    {
        const int shownOnALine = 6;
        var names = !forRun || left.Count <= shownOnALine
            ? string.Join(", ", left)
            : string.Join(", ", left.Take(shownOnALine)) + ", …";
        return (left.Count, left.Count == 0 ? string.Empty
                            : forRun ? Strings.MsgToolsCutSmallWindowRun(window, left.Count, names, remedy)
                            : Strings.MsgToolsCutSmallWindow(window, left.Count, names, remedy));
    }

    /// <summary>Where a larger window is made, by server: Ollama (the card's memory, <c>/hardware</c>), LM Studio (the
    /// settings, or LM Studio itself when it loaded the model smaller), any other server (its own setting).</summary>
    internal static string LargerWindowRemedy(string provider, int window, int configuredWindow) =>
        InferenceProviderFactory.KindOf(provider) switch
    {
        InferenceProviderFactory.Ollama   => Strings.HeadShareRemedyOllama,
        // ⚠ LM Studio loads a model at a window of its own: when that is the limit, a larger setting changes nothing.
        InferenceProviderFactory.LmStudio => window < configuredWindow ? Strings.HeadShareRemedyLmStudioLoaded(window)
                                                                       : Strings.HeadShareRemedyLmStudio,
        _                                 => Strings.HeadShareRemedyServer,
    };

    /// <summary>The registry the next turn sends when its tools are on: Plan's read-only one, or the held mode's. One
    /// answer for what the gauge measures and for what the question's hints may name (<c>ToolHints.OfferedBy</c>).</summary>
    internal static IToolRegistry NextTurnTools(IToolRegistry tools, bool planMode, Governance.ChatMode? mode) =>
        planMode ? new PlanModeToolRegistry(tools) : Governance.ChatModes.ToolsFor(tools, mode, null);

    /// <summary>
    /// The MCP tools the next chat turn leaves out until <c>tool_search</c> loads them, by server: none with tools off
    /// or in plan mode (which sends no MCP tool at all), the registry's waiting ones otherwise.
    /// </summary>
    internal static IReadOnlyList<(string Server, int Tools)> NextTurnToolsOnDemand(ToolRegistry tools, bool toolsEnabled, bool planMode,
                                                                                   Governance.ChatMode? mode = null) =>
        !toolsEnabled || planMode ? []
        : mode?.Tools is { } kept ? [.. tools.ToolsOnDemand.Where(e => kept.MayUseServer(e.Server))]
        : tools.ToolsOnDemand;

    /// <summary>
    /// The tools the next chat turn does not send because they are turned off, by the name they are turned off by: none
    /// with tools off for the conversation (nothing is sent at all), in plan mode only those plan mode would have sent
    /// (the same request <see cref="NextTurnToolTokens"/> weighs), the registry's otherwise.
    /// </summary>
    internal static IReadOnlyList<string> NextTurnTurnedOff(ToolRegistry tools, bool toolsEnabled, bool planMode,
                                                          Governance.ChatMode? mode = null) =>
        !toolsEnabled ? []
        : planMode ? [.. tools.TurnedOffKeys().Where(PlanModeToolRegistry.IsAllowed)]
        : mode?.Tools is { } kept ? [.. tools.TurnedOffKeys().Where(kept.AllowsKey)]
        : tools.TurnedOffKeys();

    /// <summary>The window the chat model should have: the person's, or with the automatic window at least the smallest
    /// where the tools and the files of a real question fit (<see cref="Inference.ContextWindowChoice.SmallestWorthwhile"/>).</summary>
    internal static int TargetWindow(InferpalConfig config) =>
        TargetWindow(config.ContextWindowSize, Inference.ContextWindowChoice.IsAutomatic(config));

    /// <summary><see cref="TargetWindow(InferpalConfig)"/> once the window and whether it is automatic are known.</summary>
    internal static int TargetWindow(int configured, bool automatic) =>
        automatic ? Math.Max(configured, Inference.ContextWindowChoice.SmallestWorthwhile) : configured;

    /// <summary>
    /// Has the server load the chat model at <see cref="TargetWindow(InferpalConfig)"/> where it lets a client choose (LM Studio): a model it
    /// loaded smaller — 8 192 by default for many — leaves no room once Inferpal's tools are in (pari 127). The sentence to
    /// say when the server refused, else <c>null</c>.
    /// </summary>
    /// <remarks>Before the window is measured, so the measure reads the window the model has now. A failure to ask is no
    /// refusal: the turn goes on with the window the server has.</remarks>
    private static async Task<string?> EnsureWindowAsync(
        InferpalConfig config, IInferenceProvider client, string? model, Action<string>? onStep, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        var target = TargetWindow(config);
        try
        {
            return await client.EnsureWindowAsync(model, target, onStep, ct).ConfigureAwait(false) is { Refusal: { } why }
                ? Strings.MsgModelWindowNotLoaded(model, target, why)
                : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Diagnostics.Swallow("ContextManager.EnsureWindow", ex);
            return null;
        }
    }

    /// <summary>
    /// The window the conversation is measured against: the configured one, or the one the server
    /// really loaded <paramref name="model"/> with when that is SMALLER — or when none is configured. When the configured
    /// one is automatic (<see cref="Inference.ContextWindowChoice.IsAutomatic(int, int)"/>), a LARGER loaded one too, up
    /// to <see cref="Inference.ContextWindowChoice.MeasuredCeiling"/>.
    /// </summary>
    /// <remarks>
    /// ⚠ LM Studio, vLLM and llama-server load a model with a window of their own choosing and say which
    /// (llama-server's default is 4 096 tokens). Measured against the
    /// configured window alone, a conversation between the two was refused on every request while
    /// compaction waited for a threshold it could never reach — stuck until the user cleared it. A larger
    /// loaded window never raises a window the person set: that is their budget. An automatic one — the factory window,
    /// or the one the product sized for Ollama — is nobody's budget: kept, it compacts a model LM Studio loaded at 32 K,
    /// and cuts its prompt budgets, as if it had 8 K. It rises no higher than the product sizes a window for Ollama (the
    /// largest the battery measures): a server loading 128 K would otherwise grow every budget the window scales.
    /// Ollama reports no loaded window, so its num_ctx stays the setting.
    /// ⚠ A window of 0 means "the model's own default", and a server that says which window it loaded has just
    /// said what that default is: answered 0 here, compaction was switched off while the client still refused
    /// every request past the loaded window — the stuck conversation again, by the other door. 0 stays 0 only
    /// when no one can say (Ollama, which reports nothing).
    /// </remarks>
    internal static async Task<int> EffectiveWindowAsync(
        InferpalConfig config, IInferenceProvider client, string? model, CancellationToken ct)
        => (await MeasureWindowAsync(config, client, model, ct).ConfigureAwait(false)).Window;

    /// <summary><see cref="EffectiveWindowAsync"/>, and whether the server said the window it loaded the model with
    /// (<c>Read</c>): <c>false</c> when the configured window stands in for one nobody could read.</summary>
    internal static async Task<(int Window, bool Read)> MeasureWindowAsync(
        InferpalConfig config, IInferenceProvider client, string? model, CancellationToken ct)
    {
        var configured = config.ContextWindowSize;
        if (string.IsNullOrWhiteSpace(model)) return (configured, false);

        int? loaded = null;
        try { loaded = await client.GetLoadedContextWindowAsync(model, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { Diagnostics.Swallow("ContextManager.EffectiveWindow", ex); }

        if (loaded is not > 0) return (configured, false);
        if (configured <= 0 || loaded < configured) return (loaded.Value, true);
        return (Inference.ContextWindowChoice.IsAutomatic(configured, config.ContextWindowSizeAuto)
            ? Math.Max(configured, Math.Min(loaded.Value, Inference.ContextWindowChoice.MeasuredCeiling))
            : configured, true);
    }

    /// <summary>
    /// The window a tool cut is decided on (<see cref="Execution.SmallWindowToolRegistry"/>): one the server reported, or
    /// Ollama's setting, which every request carries as <c>num_ctx</c>; 0 otherwise. A setting that only stands in for a
    /// window nobody could read never cuts the tools of a model that may well have far more.
    /// </summary>
    internal static int CutWindow(int window, bool read, string provider) =>
        read || InferenceProviderFactory.KindOf(provider) == InferenceProviderFactory.Ollama ? window : 0;

    /// <summary>
    /// The registry an agent run sends: <paramref name="tools"/>, or the tools used most when every definition would take
    /// more than half the window (<see cref="CutWindow"/>). The funnel of both agent
    /// loops, so the run of a command (<c>/task</c>, <c>/tdd</c>, <c>/fix-build</c>) fits where a question does, and says
    /// what it leaves out on the run's status line; a question's registry, decided with its plan, cut or not
    /// (<see cref="Execution.SmallWindowToolRegistry.MarkDecided"/>), is left as it is.
    /// </summary>
    internal static async Task<IToolRegistry> ToolsForWindowAsync(
        InferpalConfig config, IInferenceProvider? client, string model, IToolRegistry tools,
        Action<string>? onStep, CancellationToken ct)
    {
        // A question decided its tools with its plan (and kept the ones it named): its runs send exactly that.
        if (tools.Definitions.Count == 0 || Execution.SmallWindowToolRegistry.IsDecided(tools)) return tools;
        var (window, read) = client is null ? (config.ContextWindowSize, false)
                                            : await MeasureWindowAsync(config, client, model, ct).ConfigureAwait(false);
        var at = CutWindow(window, read, config.Provider);
        // ⚠ No question here: a command's prompt quotes code and build output, where tool names abound, and the tools a
        // command works with are kept ones.
        var sent = Execution.SmallWindowToolRegistry.For(tools, at, null);
        if (sent is Execution.SmallWindowToolRegistry cut
            && ToolsCutText(cut.LeftOut(tools), at, LargerWindowRemedy(config.Provider, at, config.ContextWindowSize), forRun: true)
               is { Count: > 0 } said)
            onStep?.Invoke(said.Text);
        return sent;
    }

    /// <summary>The summarising call, with its own deadline. <c>Text</c> null on any failure, with <c>Failure</c> the
    /// sentence that names it (null when the model merely answered nothing); <c>Cut</c> when the reply stopped at the
    /// model's length limit; <c>Omitted</c> the oldest messages that did not fit in the request. <paramref name="onStep"/>
    /// is where the summary says how far it has come (<see cref="SummaryFuse"/>).</summary>
    private static async Task<(string? Text, bool Cut, int Omitted, string? Failure)> SummarizeAsync(
        IReadOnlyList<ChatMessageDto> history, CompactionPlan plan,
        InferpalConfig config, IInferenceProvider client, Action<string>? onStep, CancellationToken ct)
    {
        // The time the line shows counts from "Compacting context…", said just before: the utility model and its window
        // are resolved before the fuse.
        var compacting = System.Diagnostics.Stopwatch.StartNew();
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
            // The fuse now that the size to READ is known (restarts the countdown), rearmed by every streamed chunk.
            var fuse = new SummaryFuse(cts, config.CompactionTimeoutSeconds, onStep, () => compacting.Elapsed);
            fuse.Arm(request, PrefillRates.Shared.Estimate(config.BaseUrl, model, RequestSize.Of(request.Messages, null).Total));

            // ⚠ SendChatAsync, never RunAgentAsync: the agent loop reports a network failure as its
            // FinalResponse, and the error text would become the "summary" that replaces the turns.
            var turn = await client.SendChatAsync(
                model, request.Messages, EmptyToolRegistry.Instance, fuse.Progress, ReasoningEffort.Off, cts.Token,
                onThinking: fuse.Progress).ConfigureAwait(false);

            // ⚠ Stopped because the model kept repeating itself, the reply is a loop: kept, it would replace the turns
            // it was asked to summarize — and a model shown a loop resumes it. Unlike a reply cut at the length limit,
            // which is a summary missing its end.
            if (turn.StoppedRepeating) return (null, false, 0, Strings.MsgContextCompactionRepeating);
            // ⚠ No summary was written, only reasoning — kept, the model's draft ("We need to summarize…") would replace
            // the turns. The cause is named: the reasoning used the context, so a model that does not reason first, or a
            // larger context, is what works — not "the model returned an empty summary".
            if (turn.AnswerIsReasoning) return (null, false, 0, Strings.MsgContextCompactionOnlyReasoning);

            return (MarkdownParser.StripThinkTags(turn.TextContent).Trim(), turn.CutAtLimit, request.Omitted, null);
        }
        // A cancellation by the USER propagates; the fuse's own is a fallback, not an error.
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return (null, false, 0, Strings.MsgContextCompactionFallback); }
        catch (AgentHttpException ex)
        {
            // The client's message is already the user-facing cause (refusal, unreachable server, open breaker) — and
            // a request still being read when its wait ran out says so: the fuse's message would name the writing time.
            Diagnostics.Swallow("ContextManager.Summarize", ex);
            return (null, false, 0, ex.IsTimeout && !ex.PrefillOverran ? Strings.MsgContextCompactionFallback
                                                                       : Strings.MsgContextCompactionFailed(ex.Message));
        }
        catch (Exception ex)
        {
            Diagnostics.Swallow("ContextManager.Summarize", ex);
            return (null, false, 0, Strings.MsgContextCompactionFailed(Diagnostics.RootMessage(ex)));
        }
    }
}
