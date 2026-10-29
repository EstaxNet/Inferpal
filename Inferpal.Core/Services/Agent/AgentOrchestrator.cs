using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services.Tools;

namespace Inferpal.Services.Agent;

/// <summary>
/// Autonomous agent orchestrator implementing a Plan → (Act + Observe) × N loop.
/// </summary>
/// <remarks>
/// <para>
/// Unlike the basic <see cref="InferenceProviderBase.RunAgentAsync"/> loop (which is purely reactive),
/// this orchestrator adds explicit structure before and between tool-execution rounds:
/// </para>
/// <list type="number">
///   <item>
///     <term>PLAN</term>
///     <description>
///       Calls the model once (no tools) to produce a numbered JSON plan.
///       The plan is injected into the conversation and displayed live in the UI.
///     </description>
///   </item>
///   <item>
///     <term>ACT</term>
///     <description>
///       Calls the model with the full tool registry; executes any returned tool calls.
///     </description>
///   </item>
///   <item>
///     <term>OBSERVE</term>
///     <description>
///       After each tool-execution batch, injects a structured user message summarising
///       what ran and prompting the model to continue — without an extra LLM round-trip.
///     </description>
///   </item>
/// </list>
/// <para>
/// The loop runs for at most <see cref="InferpalConfig.AgentMaxIterations"/> iterations.
/// Loop detection aborts early when the exact same tool-call signature repeats.
/// </para>
/// </remarks>
internal sealed class AgentOrchestrator
{

    private readonly IChatTurnClient _client;
    private readonly InferpalConfig _config;

    public AgentOrchestrator(IChatTurnClient client, InferpalConfig config)
    {
        _client = client;
        _config = config;
    }

    // Stable identity of a tool call (name + raw arguments) used to deduplicate repeated calls
    // within a single agent run. Uses the arguments' raw JSON text: models re-issue byte-identical
    // calls when looping, so exact-text matching is sufficient and avoids false merges.
    internal static string ToolCallKey(string toolName, System.Text.Json.JsonElement args) =>
        toolName + "\u0001" + args.GetRawText();

    /// <summary>
    /// Upper bound, in characters (about 2 000 tokens), of one tool result fed back into the run's context. The UI
    /// still shows the whole output: <c>onToolExecuted</c> receives it uncut.
    /// </summary>
    /// <remarks>
    /// ⚠ One large result — a web page, a search report, a chatty command — can exceed the model's window on its own,
    /// and intra-run elision cannot shrink it: <see cref="CompactRunContext"/> keeps the most recent act/observe cycle
    /// verbatim. Uncapped, the backend refuses the request or drops its head (system prompt, task, plan) and the agent
    /// derails. <see cref="CapForContext"/> applies the bound; a tool that can return more offers a way to resume under
    /// it (<c>read_file</c>'s <c>start_line</c>, <c>fetch_url</c>'s <c>start_char</c>).
    /// </remarks>
    internal const int MaxToolResultCharsInContext = 8000;

    // Tools whose result cannot change within a single run, so a byte-identical repeat may be
    // served from the intra-run cache. Deliberately NOT the same set as AgentLoopPolicy's
    // read-only tools: read_file / run_tests / get_diagnostics are read-only but their results
    // DO change after an intervening write_file or apply_diff — serving a cached copy there
    // feeds the model stale state in the very edit → verify cycle the loop policy tolerates.
    // (search_codebase qualifies because background indexing is paused during interactive runs.)
    private static readonly HashSet<string> CacheableTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "fetch_url", "web_search", "search_docs", "search_codebase",
    };

    /// <summary>Whether a repeat of <paramref name="toolName"/> may be served from the intra-run cache.</summary>
    internal static bool IsCacheable(string toolName) => CacheableTools.Contains(toolName);

    /// <summary>
    /// Whether a cacheable tool's result may be served to a repeat: an answer. ⚠ A failure ("Error: …", a timeout), the
    /// person's refusal of the card, or a search engine that refused is not what the next try gets — served from the
    /// cache, the person who says "fetch it now" gets "Cancelled by user." again, with no card.
    /// </summary>
    internal static bool IsAnAnswer(string result) =>
        !result.StartsWith("Error:", StringComparison.Ordinal)
        && result != ToolFailure.CancelledByUser
        && result != Strings.WebSearchRefused;

    /// <summary>The plan request, followed by the names of the tools the run can call.</summary>
    /// <remarks>
    /// ⚠ The plan's JSON asks for a "tool" per step, and the planning call carries no tools: without the names a
    /// model plans tools it cannot see — <c>search_code</c>, <c>list_directory</c> — and the ACT phase, which reads
    /// its own plan, calls them. Names only: the schemas travel with every ACT request. The line is structural,
    /// like the tool names themselves, so it is not localized.
    /// </remarks>
    internal static string PlanPrompt(IToolRegistry tools) =>
        tools.Definitions.Count == 0
            ? ModelPrompts.AgentPlanPrompt
            : ModelPrompts.AgentPlanPrompt + "\nTools you can call (use these names for \"tool\"): "
              + string.Join(", ", tools.Definitions.Select(d => d.Function.Name)) + "."
              + (tools.Definitions.Any(d => d.Function.Name == "rename_symbol") ? "\n" + ModelPrompts.AgentPlanRename : string.Empty);

    // Read-only tools safe to run concurrently within one batch: pure filesystem reads, no GPU work,
    // no approval prompt, no VS UI-thread affinity. Deliberately EXCLUDES search_codebase/search_docs
    // (GPU embeddings — must stay serialized on the single shared GPU, see GpuScheduler), the
    // approval-gated fetch_url/web_search (would pop concurrent modal prompts), and the VS-context
    // get_* tools (extensibility calls are not guaranteed thread-safe).
    private static readonly HashSet<string> ParallelSafeTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "read_file", "list_files", "search_in_files",
    };

    /// <summary>
    /// Whether a tool-call batch may run concurrently: more than one call and every call is a
    /// parallel-safe read-only tool. Mixed/mutating batches stay sequential.
    /// </summary>
    internal static bool ShouldRunParallel(IReadOnlyList<ToolCallDto> calls) =>
        calls.Count > 1 && calls.All(c => ParallelSafeTools.Contains(c.Function.Name));

    /// <summary>
    /// Executes one model-requested call, turning any failure into a message the model can act on — or refuses it,
    /// unexecuted, when its arguments never parsed (<see cref="ToolCallFunction.UnparsedArguments"/>): running it with
    /// default arguments turns a truncated <c>run_tests {"filter":"Foo…</c> into the whole suite. Shared by this
    /// orchestrator and the basic loop in <c>InferenceProviderBase</c>.
    /// </summary>
    /// <param name="replyCut">The reply carrying the call stopped at the length limit
    /// (<see cref="ChatTurnResult.CutAtLimit"/>). ⚠ Required, not defaulted: it decides what the refusal
    /// tells the model. "Resend with complete JSON" is the one remedy that cannot work on a cut reply —
    /// the same call is cut at the same point, and a long <c>write_file</c> near a full window is
    /// refused round after round.</param>
    /// <remarks>
    /// <para>
    /// Tools validate their arguments by <b>throwing</b> — <c>PathSanitizer</c> raises
    /// <see cref="ArgumentException"/> on a missing path, for one. Unguarded, a single malformed
    /// call (routine from a small local model that omits a required argument) escapes the agent
    /// loop and kills the whole turn, breaking the contract that a run never throws except on
    /// cancellation.
    /// </para>
    /// <para>
    /// Handing the error back as the tool's result is also what actually works: the model reads
    /// "path is required" and retries correctly, exactly as it does with the <c>"Error: …"</c>
    /// strings tools already return for the cases they validate by hand.
    /// </para>
    /// </remarks>
    internal static Task<string> ExecuteToolSafeAsync(
        IToolRegistry tools, ToolCallFunction call, bool replyCut, CancellationToken ct)
    {
        // ⚠ The judgement lives HERE and not in the providers: `arguments` is deserialized as it
        // comes, so a payload where it is neither an object, nor absent, nor `null` travelled through
        // the whole product with nobody judging it — and `ToolArgs`, which never throws by contract,
        // then returned the DEFAULT value of every argument. A `run_tests` whose filter arrived as a
        // string became the whole suite.
        // A tool turned off first: "send the call again, well formed" would cost an iteration refused anyway.
        if (tools.TurnedOffRefusal(call.Name) is { } turnedOff) return Task.FromResult(turnedOff);
        // ⚠ Before the cut: a reply stopped because its call kept repeating is cut too, and "split the work" is not the
        // remedy — the model has to write the call once.
        if (call.BrokenShape is { } broken)
        {
            Diagnostics.Record("Agent", $"Refused a '{call.Name}' call: its arguments stopped being readable ({broken}).");
            return Task.FromResult(
                $"Error: the arguments of this '{call.Name}' call stopped being something the tool can read — {broken} — "
                + "so the reply was stopped there and the call was NOT executed. The usual cause is a quote inside a string "
                + "value that was not escaped: send the call again with every quote inside a string written \\\" (the "
                + "quote of a C# $\\\"…\\\" string included).");
        }
        if (call.StoppedRepeating)
        {
            Diagnostics.Record("Agent", $"Refused a '{call.Name}' call: its arguments kept repeating the same text and were stopped.");
            return Task.FromResult(
                $"Error: the arguments of this '{call.Name}' call kept repeating the same text, so the reply was stopped "
                + "and the call was NOT executed. Send the call again, complete and written only once — inside a JSON "
                + "string, every quote of the code is escaped (\\\").");
        }

        call = ToolCallArguments.Judge(call);

        if (call.UnparsedArguments is not { } raw)
        {
            // An MCP tool's schema is a third party's, and may declare a free-form object whose keys are text.
            if (Mcp.McpTool.IsMcpName(call.Name) || ToolCallArguments.StrayKey(call.Arguments) is not { } stray)
                return ExecuteToolSafeAsync(tools, call.Name, call.Arguments, ct);
            Diagnostics.Record("Agent", $"Refused a '{call.Name}' call: a field name was code, cut by an unescaped quote.");
            var interpolated = stray.Before?.EndsWith('$') == true
                ? " The code there is a C# interpolated string: inside JSON it is written $\\\"…\\\", the $ included."
                : string.Empty;
            return Task.FromResult(
                $"Error: the arguments of this '{call.Name}' call have a field named \"{SafeTruncate.Truncate(stray.Key, 80)}\": "
                + "a quote inside a string value was not escaped, so that value was cut short there and the rest became a "
                + "field name. The call was NOT executed. Send it again with every quote inside a string written \\\"."
                + interpolated);
        }

        Diagnostics.Record("Agent", $"Refused a '{call.Name}' call: its arguments were not a JSON object"
                                    + (replyCut ? " (the reply stopped at the length limit)." : "."));
        return Task.FromResult(replyCut
            ? $"Error: your reply stopped at the length limit before the arguments of this '{call.Name}' call were "
              + "complete, so it was NOT executed. Sending the same call again would be cut at the same point: split "
              + "the work into smaller calls — for a long file, write a first part, then add the rest in later edits."
            : $"Error: the arguments of this '{call.Name}' call are not a valid JSON object, so it was NOT executed "
              + $"(received: {SafeTruncate.Truncate(raw, 300)}). The output may have been cut off — resend the "
              + "call with complete JSON arguments matching the tool's schema.");
    }

    internal static async Task<string> ExecuteToolSafeAsync(
        IToolRegistry tools, string name, System.Text.Json.JsonElement args, CancellationToken ct)
    {
        // ⚠ Here, the funnel of the model's calls, and never in the registry's ExecuteAsync: a tool turned off is not
        // offered, but a model calls a name it remembers — and a gesture of the user that runs the tool itself must keep
        // working (turning get_diagnostics off to lighten requests leaves @problems as it was). Not traced: the refusal is
        // the call's result, in the transcript — traced once per call, a model that retries would fill the diagnostics ring.
        if (tools.TurnedOffRefusal(name) is { } turnedOff) return turnedOff;
        try
        {
            // An empty result says nothing — not even "nothing": `cd` in run_command prints nothing, and the model
            // cannot tell that from a call that did not happen. Said here, at the funnel, for every tool.
            var result = await tools.ExecuteAsync(name, args, ct);
            return string.IsNullOrWhiteSpace(result) ? "(no output)" : result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }   // the user's Stop
        catch (OperationCanceledException ex)
        {
            // A cancellation nobody asked for is the tool's own deadline (an HttpClient timeout, an
            // MCP call budget): an error for the model — rethrown, it stopped the whole run.
            Diagnostics.Swallow($"Agent.Tool({name})", ex);
            return $"Error: the '{name}' call timed out before it finished. "
                 + "Retry with a narrower request, or continue without it.";
        }
        catch (Exception ex)
        {
            Diagnostics.Swallow($"Agent.Tool({name})", ex);
            return Execution.ToolFailure.Describe(name, ex);
        }
    }

    // Fallback iteration cap when AgentMaxIterations is 0/unset. There is deliberately
    // no "unlimited" mode: an uncapped agent loop on a single shared local GPU is a
    // footgun (and the settings UI documents 0 as "use the default").
    internal const int DefaultMaxIterations = 20;

    /// <summary>Of what a capped result keeps, the share taken from its END.</summary>
    internal const int TailCharsInContext = MaxToolResultCharsInContext / 4;

    /// <summary>
    /// A tool result as it enters the context: whole when it fits, otherwise its beginning AND its
    /// end, the cut said in the middle.
    /// </summary>
    /// <remarks>
    /// ⚠ The head alone dropped the verdict. A command's outcome is at its end — <c>ShellSession</c>
    /// appends <c>[stderr]</c> and <c>[exit code N]</c> after the output, a build or a test run ends on
    /// its summary — so a chatty command that failed reached the model as eight thousand characters of
    /// progress lines, with neither the error nor the exit code. The head stays the larger part: a
    /// report's own notes are written at its top.
    /// </remarks>
    internal static string CapForContext(string result)
    {
        if (result.Length <= MaxToolResultCharsInContext) return result;

        var head      = SafeTruncate.Truncate(result, MaxToolResultCharsInContext - TailCharsInContext);
        var tailStart = result.Length - TailCharsInContext;
        if (char.IsLowSurrogate(result[tailStart])) tailStart++;   // never start on half a pair
        var tail = result[tailStart..];
        var cut  = result.Length - head.Length - tail.Length;

        return head
             + $"\n\n[... truncated: {cut} of {result.Length} characters cut from the middle to stay within the "
             + "context window — the beginning and the end are shown ...]\n\n"
             + tail;
    }

    /// <summary>
    /// The plan reply as the anchored head keeps it: the model's own words when they are a complete reply, bounded
    /// like a tool result; the plan the run follows, as JSON, when the reply was cut.
    /// </summary>
    /// <remarks>
    /// ⚠ The anchored head is never compacted, so whatever model output it holds is sent with every request of the
    /// run. A reasoning model that loops while planning has its reasoning surfaced as the reply (its content is empty),
    /// stopped by the client after tens of thousands of characters — kept whole, every later request of the run
    /// overflows the window, and nothing can shrink it. A cut reply is a fragment, a looping one a loop, and a model
    /// shown its own loop resumes it: the plan the orchestrator parsed — or its fallback — stands in for it.
    /// A complete reply stays verbatim, and only a long one is capped (head and tail: a plan written after its
    /// reasoning is at the end).
    /// </remarks>
    internal static string AnchoredPlanReply(ChatTurnResult planTurn, AgentPlan plan) =>
        CapForContext(planTurn.CutAtLimit ? plan.ToJson() : planTurn.TextContent);

    // Short quote of the user's current request, embedded in the synthesis prompt so the model
    // anchors on THIS question instead of an earlier turn it answered before (on a long multi-turn
    // context, "my original request" resolves to the FIRST question and the model replays its old
    // answer). Tail-truncated: injected context (workspace, mentions) is prepended to the user
    // turn, so the actual question sits at the end.
    internal static string TaskSnippet(string task) =>
        task.Length <= 400 ? task : "…" + task[^400..];

    // Number of trailing messages kept verbatim by CompactRunContext (the most recent act/observe
    // cycle), and the placeholder that replaces an elided older tool result.
    private const int    KeepRecentMessages = 5;
    private const string ElidedToolResult   =
        "[Older tool result elided to stay within the context window.]";

    // Rough BPE estimate (~4 chars/token), matching the project's chunk-size estimation.
    // Tool-call arguments count too: a write_file/apply_diff turn carries the whole file in the
    // assistant's tool_calls, not in Content — ignoring them meant a run writing several large
    // files never crosses the compaction threshold and the backend silently truncates the head
    // (system prompt + plan), the exact failure compaction exists to prevent. Same formula as
    // OpenAiCompatibleClient.EstimateRequestTokens.
    internal static int EstimateTokens(IEnumerable<ChatMessageDto> messages) => EstimateChars(messages) / 4;

    /// <summary>
    /// The same estimate over the <b>exchange only</b>: the leading system message is left out.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>For a reader that already accounts for the system prompt separately</b> — the Context
    /// X-Ray, which breaks it into layers and then adds a "history" figure on top. Handed
    /// <see cref="EstimateTokens"/> of the whole history, it counted the prompt twice: a brand-new
    /// conversation reported a history as heavy as the entire prompt, and the budget line
    /// over-reported by exactly the part X-Ray exists to display. The header gauge keeps
    /// <see cref="EstimateTokens"/> — it measures what is sent, prompt included — and the two
    /// figures now agree on the same conversation.
    /// <para>
    /// Only the FIRST message, and only when it is a system one: a history restored without a
    /// prompt must keep its first turn, and a system turn inserted mid-run is part of the exchange.
    /// </para>
    /// </remarks>
    internal static int EstimateConversationTokens(IReadOnlyList<ChatMessageDto> messages) =>
        messages.Count > 0 && messages[0].Role == "system"
            ? EstimateTokens(messages.Skip(1))
            : EstimateTokens(messages);

    /// <summary>The characters <see cref="EstimateTokens"/> counts — content plus tool-call payloads.
    /// Shared with <c>OpenAiCompatibleClient.EstimateRequestTokens</c>.</summary>
    internal static int EstimateChars(IEnumerable<ChatMessageDto> messages)
    {
        var chars = 0;
        foreach (var m in messages)
        {
            chars += m.Content?.Length ?? 0;
            if (m.ToolCalls is { Count: > 0 } calls)
                foreach (var c in calls)
                    chars += c.Function.Name.Length + c.Function.Arguments.GetRawText().Length;
        }
        return chars;
    }

    /// <summary>
    /// Intra-run context compaction. When the estimated size of <paramref name="messages"/> nears
    /// the configured num_ctx, replaces the content of the <em>oldest</em> tool results — those
    /// between the anchored head (<paramref name="anchorCount"/>) and the last
    /// <see cref="KeepRecentMessages"/> messages — with a short placeholder. This frees tokens while
    /// preserving the message structure, the system prompt + task + plan, and the most recent
    /// results. The full elided output is still recoverable via the dedup cache if the model
    /// re-requests the same call. No-op when no context window is configured (num_ctx = model default).
    /// </summary>
    /// <param name="toolTokens">The tool definitions every request of the run carries (<see cref="RequestSize.ToolTokens"/>).
    /// ⚠ Counted like the conversation, as the check between turns counts them: they weigh about half of an 8 192-token
    /// window, so a run measured on its messages alone elided only once the backend had already refused the request
    /// (LM Studio) or cut its head (Ollama).</param>
    internal static void CompactRunContext(List<ChatMessageDto> messages, int anchorCount, int budget, int toolTokens = 0)
    {
        if (budget <= 0) return;
        // Counted once, then kept current as results are elided: re-estimating the whole list for
        // every message examined was quadratic on a long run.
        var chars = EstimateChars(messages);
        if (chars / 4 + toolTokens <= budget * 8 / 10) return;     // under 80% — nothing to do

        var target          = budget * 7 / 10;                     // compact down to ~70%
        var lastCompactable = messages.Count - KeepRecentMessages; // keep the recent tail verbatim

        for (int i = anchorCount; i < lastCompactable; i++)
        {
            if (chars / 4 + toolTokens <= target) break;
            var m = messages[i];
            if (m.Role != "tool" || m.Content == ElidedToolResult) continue;
            chars -= (m.Content?.Length ?? 0) - ElidedToolResult.Length;
            messages[i] = m with { Content = ElidedToolResult };
        }
    }

    /// <summary>The window this run is measured against: <see cref="ContextManager.EffectiveWindowAsync"/>
    /// when the client can report a loaded window, else the configured one.</summary>
    private Task<int> RunWindowAsync(string model, CancellationToken ct) =>
        _client is IInferenceProvider provider
            ? ContextManager.EffectiveWindowAsync(_config, provider, model, ct)
            : Task.FromResult(_config.ContextWindowSize);

    /// <summary>
    /// Intra-run compaction entry point. When the running estimate nears num_ctx, the <em>first</em>
    /// overflow of the run is handled by a single LLM summary of the old turns (when compaction is
    /// enabled); every later overflow falls back to instant <see cref="CompactRunContext"/> elision.
    /// This bounds the cost to one extra LLM call per run — important on a single shared GPU, where
    /// repeated summary calls would contend with the agent's own inference. Returns the updated
    /// "already summarized this run" flag.
    /// </summary>
    internal async Task<bool> CompactRunContextAsync(
        List<ChatMessageDto> messages, int anchorCount, string model,
        bool alreadySummarized, Action<string> onStep, CancellationToken ct, int toolTokens = 0)
    {
        // ⚠ The window the model is really loaded with when the server reports a smaller one: measured
        // against the configured window alone, a run was refused mid-way while elision waited.
        var budget = await RunWindowAsync(model, ct);
        if (budget <= 0) return alreadySummarized;
        if (EstimateTokens(messages) + toolTokens <= budget * 8 / 10) return alreadySummarized;

        // ⚠ A summary that cannot bring the run under the target is not asked for. When the anchored head and the tool
        // definitions alone fill the 70 % target — the default 8K window with the built-in tools — the summary is a whole
        // extra inference call, rewrites the context, and elision follows anyway.
        var cannotFit = EstimateTokens(messages.Take(anchorCount)) + toolTokens >= budget * 7 / 10;

        if (!alreadySummarized && _config.CompactionEnabled && !cannotFit)
        {
            switch (await TrySummarizeOldTurnsAsync(messages, anchorCount, model, onStep, ct))
            {
                case RunSummaryOutcome.Summarized:
                    // A summary can still leave the run over budget (a large recent tail): elide on top.
                    CompactRunContext(messages, anchorCount, budget, toolTokens);
                    return true;
                case RunSummaryOutcome.NothingToSummarize:
                    // ⚠ Not spent: a later overflow, with more old turns, is worth the one summary.
                    CompactRunContext(messages, anchorCount, budget, toolTokens);
                    return false;
            }
        }

        // No summary (disabled, already spent, or it failed/timed out) → elide.
        CompactRunContext(messages, anchorCount, budget, toolTokens);
        return true;
    }

    private enum RunSummaryOutcome { Summarized, NothingToSummarize, Failed }

    /// <summary>
    /// Summarises the old middle turns (between the anchored head and the recent tail) with one LLM
    /// call and replaces them in place with a single summary message pair. Leaves
    /// <paramref name="messages"/> untouched when there is too little to summarise or the call
    /// fails/times out, so the caller can fall back to elision. Re-throws on user cancellation.
    /// </summary>
    private async Task<RunSummaryOutcome> TrySummarizeOldTurnsAsync(
        List<ChatMessageDto> messages, int anchorCount, string model, Action<string> onStep, CancellationToken ct)
    {
        // Snap both ends to a tool-block boundary: removing half of an assistant(tool_calls)+tool
        // group leaves a structurally invalid history that OpenAI-compatible backends reject
        // outright (Ollama silently tolerates it, which is why this went unnoticed).
        var start = ToolBlockBoundary.SnapStart(messages, anchorCount, floor: 1);
        var end   = ToolBlockBoundary.SnapEnd(messages, start, messages.Count - KeepRecentMessages);

        var rangeLen = end - start;
        var range    = messages.Skip(start).Take(Math.Max(0, rangeLen)).ToList();
        // Not enough old turns to be worth a round-trip.
        if (rangeLen < 2 || range.All(m => string.IsNullOrEmpty(m.Content)))
            return RunSummaryOutcome.NothingToSummarize;

        onStep(Strings.StatusCompacting);

        string summary;
        bool   cut, repeating, reasoningOnly;
        int    omitted;
        try
        {
            var timeoutSec = Math.Max(10, _config.CompactionTimeoutSeconds);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));

            // Model Router: the summary is an auxiliary task — a small utility model (explicit or
            // auto-picked from the /bench recommendation when already warm) answers in seconds
            // where the agent model would burn its own GPU time. Its request is bounded by ITS window.
            var utility = await ModelRouter.ResolveUtilityAsync(_config, _client, cts.Token);
            var window  = await RunWindowAsync(utility, cts.Token);
            var request = HistoryCompaction.BuildSummarizeRequest(range, HistoryCompaction.SummaryInputBudgetChars(window));
            // The fuse now that the size to READ is known (restarts the countdown), rearmed by every streamed chunk.
            var fuse = new SummaryFuse(cts, _config.CompactionTimeoutSeconds);
            fuse.Arm(request, PrefillRates.Shared.Estimate(_config.BaseUrl, utility, RequestSize.Of(request.Messages, null).Total));

            var turn = await _client.SendChatAsync(
                utility, request.Messages, EmptyToolRegistry.Instance, fuse.Progress, ReasoningEffort.Off, cts.Token,
                TaskComplexity.Quick, onThinking: fuse.Progress);
            summary   = MarkdownParser.StripThinkTags(turn.TextContent);
            cut       = turn.CutAtLimit;
            repeating = turn.StoppedRepeating;
            reasoningOnly = turn.AnswerIsReasoning;
            omitted   = request.Omitted;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; } // user cancelled
        catch (OperationCanceledException) { return RunSummaryOutcome.Failed; }         // summary timed out
        catch (Exception ex)                                                            // network/other → elide
        {
            Diagnostics.Swallow("Agent.RunSummary", ex);
            return RunSummaryOutcome.Failed;
        }

        if (string.IsNullOrWhiteSpace(summary)) return RunSummaryOutcome.Failed;
        // A reply that is only the model's reasoning is a draft, not a summary: in place of the run's earlier context
        // it would hold "We need to summarize…". Elision instead.
        if (reasoningOnly)
        {
            Diagnostics.Record("Agent.RunSummary", "The summary of the run's earlier context came back as reasoning only, "
                + "with no summary written: the old turns were elided instead.");
            return RunSummaryOutcome.Failed;
        }
        // ⚠ A summary stopped because the model kept repeating itself is a loop, not a summary: in place of the turns
        // it would hold the loop, and a model shown a loop resumes it. Elision instead.
        if (repeating)
        {
            Diagnostics.Record("Agent.RunSummary", "The summary of the run's earlier context kept repeating the same "
                + "passage and was stopped: the old turns were elided instead.");
            return RunSummaryOutcome.Failed;
        }
        // Kept, but said: read as whole, an incomplete summary makes the model redo — or deny — the work it misses.
        if (omitted > 0) summary += HistoryCompaction.PartialSummaryMarker(omitted);
        if (cut)         summary += HistoryCompaction.CutSummaryMarker;

        // Replace the summarised range with a single [summary] pair — followed by the user's notes it held, word for word:
        // paraphrased by the summary, or left out of it, a correction is no longer the one the user gave.
        var notes = range.Where(m => m.IsUserNote).ToList();
        messages.RemoveRange(start, rangeLen);
        messages.InsertRange(start,
        [
            new ChatMessageDto("user", "[Summary of this run's earlier context]") { IsScaffolding = true },
            new ChatMessageDto("assistant", summary),
            .. notes,
        ]);
        return RunSummaryOutcome.Summarized;
    }

    /// <summary>
    /// Final synthesis pass. When the Act-Observe loop ends without a printable answer but tools did
    /// run (web_search, fetch_url, search_codebase…), the gathered results are sitting unused: the
    /// model looped (re-issued an identical call → loop detection), stalled think-only on its final
    /// turn, or hit the iteration cap before writing its reply. One last no-tools call turns those
    /// results into the actual answer the user asked for, instead of the bare tool summary the UI
    /// falls back to for an empty response (<see cref="ChatTurnPolicy.ToolSummaryAnswer"/>). Streams via <paramref name="onToken"/>
    /// so the answer appears live in the bubble. Returns <paramref name="fallback"/> unchanged on
    /// failure / empty / refusal output so the existing fallback behaviour still applies. Re-throws on
    /// user cancellation.
    /// </summary>
    /// <remarks>
    /// The synthesis context is rebuilt CLEAN rather than reusing the act-loop messages: those are
    /// saturated with "do NOT write text — call a tool" imperatives (AgentExecutePlan / observe /
    /// nudge) and assistant turns carrying tool_calls. Sent to a tools=[] call, that scaffolding makes
    /// tool-oriented models (devstral/Mistral) refuse with "I don't have the tools needed" instead of
    /// writing the answer. We keep only the system prompt, the original conversation (without the plan
    /// trio), a size-bounded digest of the gathered tool results, and the synthesis instruction — and
    /// strip any tool_calls so the history is consistent with the empty registry. A degenerate refusal
    /// that slips through anyway is caught (<see cref="LooksLikeToolRefusal"/>) and falls back.
    /// </remarks>
    /// <returns>The answer, whether it stopped at the model's length limit, and whether it was stopped repeating itself.</returns>
    private async Task<(string Answer, bool Cut, bool Repeating)> SynthesizeFinalAnswerAsync(
        string model, List<ChatMessageDto> messages, int anchorCount,
        IReadOnlyList<ToolExecution> executions, string userTask, string fallback,
        Action<string>? onToken, Action? onStreamReset, Action<string> onStep, ReasoningEffort effort, CancellationToken ct,
        Action<string>? onThinking = null)
    {
        var (answer, synthesized, cut, repeating) = await TrySynthesizeAsync(
            model, messages, anchorCount, executions, userTask, fallback, onToken, onStreamReset, onStep, effort, ct, onThinking);

        // The synthesized answer is what the user read: it must also be what the model reads next
        // turn (the host keeps this history), not the empty or refused turn it replaced.
        if (synthesized)
        {
            if (messages.Count > 0 && messages[^1] is { Role: "assistant" } last && last.ToolCalls is not { Count: > 0 })
                messages[^1] = last with { Content = answer };
            else
                messages.Add(new ChatMessageDto("assistant", answer));
        }
        return (answer, cut, repeating);
    }

    private async Task<(string Answer, bool Synthesized, bool Cut, bool Repeating)> TrySynthesizeAsync(
        string model, List<ChatMessageDto> messages, int anchorCount,
        IReadOnlyList<ToolExecution> executions, string userTask, string fallback,
        Action<string>? onToken, Action? onStreamReset, Action<string> onStep, ReasoningEffort effort, CancellationToken ct,
        Action<string>? onThinking)
    {
        // Clear any think-only / partial tokens from the stalled final ACT so they don't pollute
        // the synthesised answer that streams next.
        onStreamReset?.Invoke();
        onStep(Strings.StatusAgentSynthesizing);

        // Head = system prompt + original conversation, WITHOUT the 3-message plan trio appended at
        // the tail of the anchored head (AgentPlanPrompt / plan JSON / AgentExecutePlan). Nothing
        // may reference a tool while the registry is empty — so the whole head is flattened, calls
        // AND answers. ⚠ Stripping only the tool_calls (what this did) left the tool results behind
        // without the call they answered: an orphan that OpenAI-compatible backends never see,
        // MapMessages dropping it to keep the request valid. Earlier turns' findings vanished from
        // the synthesis on those backends — silently, and only there. See ToolTranscript.
        var headEnd = Math.Max(1, anchorCount - 3);
        var synth   = ToolTranscript.Flatten(messages.Take(Math.Min(headEnd, messages.Count)));

        // The gathered tool results + the synthesis instruction, in a single user turn so "those
        // results" in the prompt resolves to the digest directly above it, bounded by what the window
        // leaves after the head (SynthesisDigestBudget).
        var window      = await RunWindowAsync(model, ct);
        var instruction = ModelPrompts.AgentSynthesizePrompt(userTask);
        var digest = BuildToolDigest(executions, SynthesisDigestBudget(window, EstimateChars(synth), instruction.Length));
        var prompt = digest.Length > 0
            ? digest + "\n\n" + instruction
            : instruction;
        synth.Add(new ChatMessageDto("user", prompt));

        try
        {
            var turn   = await _client.SendChatAsync(
                model, synth, EmptyToolRegistry.Instance, onToken, effort, ct, TaskComplexity.Normal, onThinking: onThinking,
                onWaiting: ModelWaitScope.OrStep(onStep, Strings.StatusAgentSynthesizing));
            var answer = MarkdownParser.StripThinkTags(turn.TextContent);
            // ⚠ A synthesis the client stopped because it kept repeating is cut AND repeating: carried as a cut alone,
            // the end notice advised a longer context — the one remedy that does nothing for a loop.
            return MarkdownParser.HasPrintableText(answer) && !LooksLikeToolRefusal(answer) && !LooksLikePlanEcho(answer)
                ? (turn.TextContent, true, turn.CutAtLimit, turn.StoppedRepeating)
                : (fallback, false, false, false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)   // synthesis failed/timed out → keep the existing fallback
        {
            Diagnostics.Swallow("Agent.Synthesis", ex);
            return (fallback, false, false, false);
        }
    }

    /// <summary>
    /// The characters the tool digest of a synthesis may take: what the window (in tokens) leaves after the head sent
    /// with it, the instruction and a quarter kept for the answer — never more than 60 % of the window.
    /// </summary>
    /// <remarks>
    /// ⚠ The head is the system prompt and the whole conversation: a budget taken from the window alone lets a long
    /// one plus a full digest go past the window — refused by LM Studio, cut at its head by Ollama — and the synthesis
    /// falls back without a word, on exactly the runs that gathered the most.
    /// </remarks>
    internal static int SynthesisDigestBudget(int window, int headChars, int instructionChars)
    {
        if (window <= 0) return 24000;
        var windowChars = window * 4;
        return Math.Clamp(windowChars - windowChars / 4 - headChars - instructionChars, 0, windowChars * 6 / 10);
    }

    /// <summary>
    /// Builds a compact, size-bounded digest of the tool results gathered during a run, for the
    /// synthesis prompt. Each result is capped (<see cref="CapForContext"/>); when the combined size
    /// would exceed <paramref name="budgetChars"/> the OLDEST results are dropped (the most recent are
    /// usually the relevant ones). The most recent result is always kept even if it alone exceeds the
    /// budget. Returns the empty string when no tools ran.
    /// </summary>
    internal static string BuildToolDigest(IReadOnlyList<ToolExecution> executions, int budgetChars)
    {
        if (executions.Count == 0) return string.Empty;
        var blocks = new List<string>(executions.Count);
        var used   = 0;
        for (int i = executions.Count - 1; i >= 0; i--)
        {
            var block = "### " + executions[i].Name + "\n" + CapForContext(executions[i].Output) + "\n";
            if (blocks.Count > 0 && used + block.Length > budgetChars) break;
            blocks.Insert(0, block);
            used += block.Length;
        }
        // ⚠ The oldest results left out are SAID: this digest is the synthesis model's only record of the run, and
        // read as the whole of it, the final answer — the one the user reads and the history keeps — could deny the
        // early work ("no file was modified" over a run whose first calls wrote two).
        var dropped = executions.Count - blocks.Count;
        if (dropped > 0)
            blocks.Insert(0, $"[{dropped} earlier tool result(s) of this run did not fit and are not shown: "
                             + $"{ChatTurnPolicy.BuildToolSummary(executions.Take(dropped))}. They happened — account for "
                             + "them in the answer.]\n");
        return string.Join("\n", blocks);
    }

    // Degenerate non-answers some tool-oriented models emit when asked to synthesise prose after a
    // tool run ("I don't have the tools needed…"). Detected so the caller can fall back to the tool
    // summary instead of surfacing a refusal as the final answer. Narrow by design — must not match a
    // legitimate answer that merely mentions tools.
    private static readonly string[] ToolRefusalMarkers =
    {
        "don't have the tools",            "do not have the tools",
        "don't have access to the tools",  "do not have access to the tools",
        "don't have the necessary tools",  "don't have the required tools",
        "currently don't have the tools",
    };

    /// <summary>
    /// Whether <paramref name="answer"/> is a degenerate "I lack the tools" refusal rather than a real
    /// answer. Only fires on short replies so a long answer that happens to mention tools is never
    /// suppressed.
    /// </summary>
    internal static bool LooksLikeToolRefusal(string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer)) return false;
        var stripped = MarkdownParser.StripThinkTags(answer);
        if (stripped.Length > 400) return false;   // real answers are longer than a one-line refusal
        var lower = stripped.ToLowerInvariant();
        foreach (var m in ToolRefusalMarkers)
            if (lower.Contains(m)) return true;
        return false;
    }

    /// <summary>
    /// Closes the steps a run leaves open when it ends — on a repeat or on the final answer. A step whose tool WRITES is
    /// done only with a write of its own: one the run made that no earlier write step claimed; otherwise it is skipped.
    /// </summary>
    /// <remarks>
    /// ⚠ Closed done whatever happened, a plan whose edits were all refused read "all done" right above the notice that
    /// no file was changed — the rule "a write step is done only when a file was written" held where a step advances,
    /// not where the run sweeps the rest. <paramref name="unclaimedWrites"/> null = a registry that counts no writes:
    /// nothing can tell, so every step closes done.
    /// </remarks>
    internal static void CloseRemainingSteps(AgentPlan plan, int? unclaimedWrites, Action<int, AgentStepStatus>? onStepUpdate)
    {
        var spare = unclaimedWrites ?? 0;
        for (var i = 0; i < plan.Steps.Count; i++)
        {
            var step = plan.Steps[i];
            if (step.Status is not (AgentStepStatus.Pending or AgentStepStatus.Active)) continue;
            var status = AgentStepStatus.Done;
            if (unclaimedWrites is not null && WritesFiles(step))
            {
                if (spare > 0) spare--;
                else status = AgentStepStatus.Skipped;
            }
            step.Status = status;
            onStepUpdate?.Invoke(i, status);
        }
    }

    /// <summary>Whether the tool a step announces writes files (the same reading as the step's advance).</summary>
    private static bool WritesFiles(AgentPlanStep step) => step.ToolHint is { } hint && ChatTurnPolicy.IsFileEdit(hint.Trim());

    /// <summary>
    /// Whether <paramref name="answer"/> is, in its WHOLE, a new JSON plan (<c>{"goal":…,"steps":[…]}</c>, optionally
    /// fenced) rather than a step carried out or an answer.
    /// </summary>
    /// <remarks>
    /// ⚠ The plan the model wrote first stays in the thread as its own message, and small models imitate it: after a
    /// tool result they write another plan instead of the next call, and taken as the final answer it ends the run —
    /// the task half done and the user reading JSON. Narrow by design: prose around the object, a plan without a step
    /// or a broken object is left alone, since a legitimate answer may show a JSON example.
    /// </remarks>
    internal static bool LooksLikePlanEcho(string? answer)
    {
        var s = MarkdownParser.StripThinkTags(answer ?? string.Empty).Trim();
        if (s.StartsWith("```", StringComparison.Ordinal))
        {
            var nl = s.IndexOf('\n');
            s = nl < 0 ? string.Empty : s[(nl + 1)..];
            if (s.TrimEnd().EndsWith("```", StringComparison.Ordinal)) s = s.TrimEnd()[..^3];
            s = s.Trim();
        }
        return s.StartsWith('{') && s.EndsWith('}') && s.Contains("\"goal\"", StringComparison.Ordinal)
            && AgentPlan.TryParse(s) is not null;
    }

    // ── Public entry point ────────────────────────────────────────────────────

    /// <summary>
    /// Runs the full Plan → Act → Observe loop.
    /// </summary>
    /// <param name="model">Ollama model name.</param>
    /// <param name="history">
    /// Conversation history including the user's current message as the last entry.
    /// </param>
    /// <param name="tools">Tool registry; pass <see cref="EmptyToolRegistry.Instance"/> to run without tools.</param>
    /// <param name="onStep">UI status callback (e.g. "Planning…", "Calling read_file…").</param>
    /// <param name="onToken">Live-streaming token callback for the final response; <c>null</c> to disable.</param>
    /// <param name="onPlanReady">
    /// Called once the plan has been parsed from the model's planning response,
    /// before any tool calls are made. Use to insert a plan bubble in the chat UI.
    /// </param>
    /// <param name="onStepUpdate">
    /// Called each time a plan step changes status (index, new status).
    /// Use to update the live plan bubble in the chat UI.
    /// </param>
    /// <param name="onToolExecuted">
    /// Called immediately after each individual tool finishes executing, so the UI can render the
    /// tool-result bubble live instead of waiting for the whole loop to complete.
    /// </param>
    /// <param name="onStreamReset">
    /// Called after each tool-execution batch completes, before the next ACT begins.
    /// Use to clear any accumulated streaming tokens from the current bubble so only the
    /// final act's response is shown (avoids think-only intermediate tokens leaking into
    /// the final bubble).
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="steering">The notes the user types while the run works, read before each action (<see cref="RunSteering"/>);
    /// <c>null</c> for a run nobody steers.</param>
    public async Task<OrchestratorResult> RunAsync(
        string                     model,
        List<ChatMessageDto>       history,
        IToolRegistry              tools,
        Action<string>             onStep,
        Action<string>?            onToken,
        Action<AgentPlan>?         onPlanReady,
        Action<int, AgentStepStatus>? onStepUpdate,
        Action<ToolExecution>?     onToolExecuted,
        Action?                    onStreamReset,
        CancellationToken          ct,
        Action<string>?            onThinking = null,
        RunSteering?               steering = null)
    {
        // Claim the shared GPU for the whole run, like InferenceProviderBase.RunAgentAsync does for
        // the basic loop: this path calls SendChatAsync directly, so without its own lease the
        // background embedding loops keep the GPU busy and the cross-process FIM never yields
        // during an orchestrated (Agent mode) run. Re-entrant, so nesting under RunAgentAsync is safe.
        using var gpuLease = GpuScheduler.AcquireChatLease();
        // One effort for every call of the run — plan, actions, synthesis: a model that carries the effort in its system
        // message (gpt-oss, Gemma 4) rewrites the head of its prompt when it changes, and the server reads the run again.
        var runEffort = ReasoningEffortSetting.Agent(_config);

        // Early-exit checks (circuit breaker, URL) are handled inside SendChatAsync.
        var messages    = new List<ChatMessageDto>(history);
        // The user's current request (last user turn) — quoted in the synthesis prompt so the
        // final answer targets it, not an earlier question from the conversation.
        var userTask    = string.Empty;
        for (int i = history.Count - 1; i >= 0; i--)
            if (history[i].Role == "user") { userTask = history[i].Content ?? string.Empty; break; }
        // The request as the prompts quote it (TaskSnippet), and later with the notes read during the run — each cut on
        // its own: one 400-character tail of "request + notes" lost the request behind two long notes.
        var askedTask   = userTask;
        userTask        = TaskSnippet(askedTask);
        var executions  = new List<ToolExecution>();
        // Per-batch repeat counts for loop detection (see AgentLoopPolicy).
        var sigCounts   = new Dictionary<string, int>();
        // Intra-run tool-result cache (toolName+args → result): a context-truncated model often
        // re-requests a tool call it already made this run (identical web_search, re-fetching an
        // already-fetched URL). Reusing the cached result avoids the redundant network/work and
        // the wasted tokens. Restricted to idempotent tools — see CacheableTools / ToolCallKey.
        var toolCache   = new Dictionary<string, string>(StringComparer.Ordinal);
        int totalTokens = 0, lastPromptTokens = 0;
        // Set to true after the one-shot nudge so we never inject it twice.
        bool nudgedOnce = false;
        // Set once the plan is exhausted and the observe-complete prompt asked the model to
        // answer in prose: from then on a visible no-tool-call turn IS the requested final
        // answer, and nudging it back towards tools would erase a perfectly good reply.
        bool answerRequested = false;
        // Spent after the first intra-run summary so we never make more than one summary LLM call
        // per run (costly on a single shared GPU); later overflows fall back to deterministic elision.
        bool runSummarized = false;
        // Counts bounded ACT retries after an empty / think-only stall (see below).
        int actRetries = 0;
        const int MaxActRetries = 2;
        // A plan written instead of a step (LooksLikePlanEcho) is sent back at most this many times per run.
        const int MaxPlanEchoNudges = 2;
        int planEchoNudges = 0;
        // A plan exhausted on an edit that broke the build asks for the fix at most this many times per run (see buildFixAsked).
        const int MaxBuildFixRounds = 2;
        int buildFixRounds = 0;
        // Set by a stall-retry: forces a tool call on the RETRIED act only. ⚠ Deriving it from
        // actRetries > 0 kept every later act forced for the rest of the run once one stall happened,
        // and the model could no longer finish in prose.
        bool retryForcesTool = false;

        // NOTE: we deliberately do NOT re-inject the tool catalogue as prose into the system prompt.
        // The full tools:[] JSON schema is already sent on every ACT turn, and the orchestrator
        // backstops a model that narrates instead of calling (tool_choice:"required" on the first
        // ACT + the one-shot nudge below). A prose duplicate only inflated every (cache-missing,
        // multi-turn) reprocess by hundreds of tokens — most painful on large multimodal models that
        // can't reuse the KV cache — for no functional gain. The PLAN phase gets the tool NAMES only
        // (PlanPrompt): its JSON asks for a "tool" per step.

        // ── Phase 1: PLAN ────────────────────────────────────────────────────
        onStep(Strings.StatusAgentPlanning);

        // Build a separate message list for the planning call: we append the
        // plan-request as a new user message so the original user turn is preserved.
        var planPrompt   = PlanPrompt(tools);
        var planMessages = new List<ChatMessageDto>(messages)
        {
            new ChatMessageDto("user", planPrompt),
        };

        ChatTurnResult planTurn;
        try
        {
            // No tools during planning — we want a pure JSON response.  Quick timeout: plans are short.
            // While the server reads the prompt the line says so, then gives the step back.
            planTurn = await _client.SendChatAsync(model, planMessages, EmptyToolRegistry.Instance, null, runEffort, ct, TaskComplexity.Quick,
                                                   onThinking: onThinking, onWaiting: ModelWaitScope.OrStep(onStep, Strings.StatusAgentPlanning));
        }
        catch (OperationCanceledException) { throw; }
        catch (AgentHttpException ex)
        {
            return OrchestratorResult.Error(ex.Message, messages, ex.PrefillOverran);
        }

        totalTokens      += planTurn.TokensUsed;
        lastPromptTokens  = planTurn.PromptTokens;

        // Parse JSON plan; fall back to a single-step "execute" plan if parsing fails.
        var plan = AgentPlan.TryParse(planTurn.TextContent)
                ?? new AgentPlan
                   {
                       Goal  = Strings.AgentPlanFallbackGoal,
                       Steps = [new AgentPlanStep { Index = 1, Description = Strings.AgentPlanFallbackStep }],
                   };

        onPlanReady?.Invoke(plan);

        // Inject the plan into the main conversation so the model can refer to it.
        // We also add the AgentPlanPrompt as the user message that triggered the plan —
        // without it the model sees its own JSON with no visible context, which confuses
        // small models into narrating instead of calling tools in the ACT phase.
        messages.Add(new ChatMessageDto("user",      planPrompt) { IsScaffolding = true });
        messages.Add(new ChatMessageDto("assistant", AnchoredPlanReply(planTurn, plan)));
        messages.Add(new ChatMessageDto("user",      ModelPrompts.AgentExecutePlan) { IsScaffolding = true });

        // Everything added so far — system prompt (+ tool descriptions), the original conversation
        // history, and the plan trio — is the anchored head. Intra-run compaction (CompactRunContext)
        // never touches messages below this index, so the model keeps its task framing and plan even
        // when the accumulating tool results would otherwise push the request past num_ctx and make
        // Ollama silently truncate from the front.
        var anchorCount = messages.Count;

        // ── Phase 2: Act-Observe loop ────────────────────────────────────────
        int stepIdx = 0;
        // The last answer-now OBSERVE, and the generic one that stands in for it when a note arrives after it — found by its
        // text: a summary of the run's middle shifts the messages under it.
        (string Complete, string Generic)? answerNowObserve = null;
        // The writes no finished write step has claimed yet: what lets a step the run closes at its end count as done
        // only when a file was written for it (CloseRemainingSteps).
        int unclaimedWrites = 0;

        // 0/unset falls back to the default cap — never unlimited (see DefaultMaxIterations).
        var maxIter = _config.AgentMaxIterations > 0 ? _config.AgentMaxIterations : DefaultMaxIterations;
        for (int iteration = 0; iteration < maxIter; iteration++)
        {
            ct.ThrowIfCancellationRequested();

            // Mark the current plan step as active.
            if (stepIdx < plan.Steps.Count)
            {
                plan.Steps[stepIdx].Status = AgentStepStatus.Active;
                onStepUpdate?.Invoke(stepIdx, AgentStepStatus.Active);
            }

            // Keep the running context under num_ctx so Ollama never truncates the head. First
            // overflow → one LLM summary of the old turns; later overflows → deterministic elision.
            runSummarized = await CompactRunContextAsync(messages, anchorCount, model, runSummarized, onStep, ct,
                                                         RequestSize.ToolTokens(tools.Definitions));

            // ── The user's notes, the last text read before acting ───────────
            // ⚠ A note restarts the loop count: told "not that file", the model sends the call it was refused again with
            // other arguments — or the same, once the note is lifted — and a mutating batch repeated once ends the run.
            // ⚠ And the request the final answer quotes carries the notes: the synthesis rebuilds its context without them.
            if (steering is not null && steering.AddTo(messages, executions.Count) > 0)
            {
                sigCounts.Clear();
                // And "fetch it now" after a refused card means the call, not its cached refusal.
                toolCache.Clear();
                // ⚠ A note that came after the answer-now prompt was chosen (during a summary, a stream reset) was read under
                // "WITHOUT calling any more tools": that prompt becomes the generic one, which leaves them open.
                if (answerNowObserve is { } observe
                    && messages.FindLastIndex(m => m.IsScaffolding && m.Content == observe.Complete) is var at and >= 0)
                    messages[at] = messages[at] with { Content = observe.Generic };
                answerNowObserve = null;
                userTask = ChatTurnPolicy.WithUserNotes(TaskSnippet(askedTask), steering.Applied.Select(n => TaskSnippet(n.Text)));
            }

            // ── ACT ──────────────────────────────────────────────────────────
            onStep(Strings.StatusThinking);

            ChatTurnResult turn;
            try
            {
                // Force a tool call on the first ACT iteration and on every stall-retry: small
                // models (Qwen, Llama) tend to narrate the plan in prose instead of emitting
                // structured tool_calls. tool_choice:"required" asks Ollama to commit to a call.
                // (Older Ollama builds ignore the field harmlessly; the bounded retry below is the
                // real mitigation.) Other iterations stay on "auto" so the model can still finish.
                var toolChoice = (iteration == 0 || retryForcesTool) ? "required" : null;
                retryForcesTool = false;
                turn = await _client.SendChatAsync(model, messages, tools, onToken, runEffort, ct, TaskComplexity.Normal, toolChoice, onThinking,
                                                   ModelWaitScope.OrStep(onStep, Strings.StatusThinking));
            }
            catch (OperationCanceledException) { throw; }
            catch (AgentHttpException ex)
            {
                return new OrchestratorResult(
                    ex.Message, plan, executions, messages,
                    totalTokens, lastPromptTokens, false, false, Failed: true, WaitRanOut: ex.PrefillOverran);
            }

            totalTokens      += turn.TokensUsed;
            lastPromptTokens  = turn.PromptTokens;

            // Append the assistant message (may contain tool calls).
            var assistantMsg = new ChatMessageDto("assistant", turn.TextContent, turn.ToolCalls);
            messages.Add(assistantMsg);

            if (turn.ToolCalls is { Count: > 0 } calls)
            {
                // ── Loop detection ────────────────────────────────────────────
                // Read-only batches tolerate more repeats than mutating ones (AgentLoopPolicy).
                if (AgentLoopPolicy.IsLoop(sigCounts, calls))
                {
                    // The repeated batch is never executed: drop the assistant turn that asked for
                    // it, or the history ends on calls nobody answered — the host keeps this history,
                    // and OpenAI-compatible servers refuse every later request.
                    messages.RemoveAt(messages.Count - 1);

                    // If real work was already done, treat the repeat as a graceful finish rather
                    // than an alarming error: close the remaining steps and synthesise the final
                    // answer from the gathered tool results (the model looped instead of writing it).
                    // Only surface the loop message when nothing was accomplished — the genuinely
                    // stuck case.
                    CloseRemainingSteps(plan, tools.WritesInRun is null ? null : unclaimedWrites, onStepUpdate);

                    var (loopMsg, loopCut, loopRepeating) = executions.Count > 0
                        ? await SynthesizeFinalAnswerAsync(
                            model, messages, anchorCount, executions, userTask, string.Empty, onToken, onStreamReset, onStep,
                            runEffort, ct, onThinking)
                        : (Strings.MsgLoopDetected, false, false);
                    return new OrchestratorResult(
                        loopMsg, plan, executions, messages,
                        totalTokens, lastPromptTokens, true, false, loopCut, AnswerRepeating: loopRepeating);
                }

                // ── Execute tools ─────────────────────────────────────────────
                var iterExecs = new List<ToolExecution>();
                // What the run had written before these calls — to know whether an edit among them wrote anything.
                var writesBefore = tools.WritesInRun;

                // A batch made only of independent, GPU-free, prompt-free read tools runs
                // concurrently — a real speedup when the model explores several files in one turn.
                // The dedup-cache / diff plumbing only matters for cacheable or mutating tools, which
                // are never parallel-safe, so the parallel path skips it (results recorded in order).
                if (ShouldRunParallel(calls))
                {
                    onStep(Strings.StatusCallingTool(string.Join(", ", calls.Select(c => c.Function.Name).Distinct())));
                    var results = await Task.WhenAll(
                        calls.Select(c => ExecuteToolSafeAsync(tools, c.Function, turn.CutAtLimit, ct)));
                    for (int i = 0; i < calls.Count; i++)
                    {
                        var toolName  = calls[i].Function.Name;
                        var result    = results[i];
                        var hasErrors = toolName == GetDiagnosticsTool.ToolName
                                        && GetDiagnosticsTool.OutputHasErrors(result);
                        var exec = new ToolExecution(toolName, calls[i].Function.WrittenArguments, result, hasErrors, null);
                        iterExecs.Add(exec);
                        executions.Add(exec);
                        onToolExecuted?.Invoke(exec);
                        messages.Add(new ChatMessageDto("tool", CapForContext(result)));
                    }
                }
                else foreach (var call in calls)
                {
                    ct.ThrowIfCancellationRequested();
                    var toolName = call.Function.Name;
                    onStep(Strings.StatusCallingTool(toolName));

                    // ── Intra-run deduplication (idempotent tools only) ──────────
                    // If this exact (tool, args) already ran this run AND the tool's result
                    // cannot have changed since (see CacheableTools), reuse it instead of
                    // re-executing. DiffInfo is intentionally null for a reuse: no new mutation
                    // happened, so we must not re-trigger restore/diff UI for it.
                    var cacheKey  = ToolCallKey(toolName, call.Function.Arguments);
                    // A call whose arguments never parsed, or that was stopped while it streamed, is refused: never
                    // cached nor served from cache — its refusal tells the model to send it again.
                    var cacheable = IsCacheable(toolName) && call.Function.UnparsedArguments is null
                                    && call.Function.BrokenShape is null && !call.Function.StoppedRepeating;
                    string? cached = null;
                    var fromCache = cacheable && toolCache.TryGetValue(cacheKey, out cached);

                    string    result;
                    DiffInfo? diff;
                    if (fromCache)
                    {
                        result = cached!;
                        diff   = null;
                    }
                    else
                    {
                        result = await ExecuteToolSafeAsync(tools, call.Function, turn.CutAtLimit, ct);
                        diff   = tools.ConsumeDiff();
                        if (cacheable && IsAnAnswer(result))
                            toolCache[cacheKey] = result;
                    }

                    var hasErrors = toolName == GetDiagnosticsTool.ToolName
                                    && GetDiagnosticsTool.OutputHasErrors(result);

                    var exec = new ToolExecution(toolName, call.Function.WrittenArguments, result, hasErrors, diff);
                    iterExecs.Add(exec);
                    executions.Add(exec);
                    onToolExecuted?.Invoke(exec);   // render the tool bubble live (full, un-capped output)

                    // Feed back a size-capped copy (see CapForContext). On a reuse, prepend a marker
                    // so the model recognises the repeat and stops re-issuing the same call.
                    var forContext = fromCache
                        ? "[Result already obtained earlier in this run — reused without a new tool call.]\n\n" + result
                        : result;
                    messages.Add(new ChatMessageDto("tool", CapForContext(forContext)));
                }

                AgentLoopPolicy.Forgive(sigCounts, calls, iterExecs.Select(e => e.Output));

                // ⚠ An edit that wrote nothing is not a step done: an apply_edits refused whole, an old_content not
                // found, a write declined. Telling the model its plan is complete then — "answer now, without
                // calling any more tools" — is what made it end the run, claiming a change no file received.
                if (writesBefore is { } counted && tools.WritesInRun is { } writesNow) unclaimedWrites += writesNow - counted;
                var editChangedNothing = writesBefore is { } before && tools.WritesInRun == before
                                         && iterExecs.Any(e => ChatTurnPolicy.IsFileEdit(e.Name));

                // Whether the build is broken as the RUN's last check left it — an edit's Smart Fix note, get_diagnostics, a
                // build through the shell. ⚠ Read from the run, never from this round alone: asked to fix the build, a
                // model reads the lines the errors name first — the prompt tells it to —, that round has no edit, and the
                // plan read "complete": the answer-now prompt ended the run on code that no longer built.
                var buildBroken = !editChangedNothing && buildFixRounds < MaxBuildFixRounds
                                  && ChatTurnPolicy.LastCheck(executions) is { Kind: ChatTurnPolicy.CheckKind.Build, Failed: true };

                // ── Mark step done, advance ────────────────────────────────────
                // ⚠ A step whose expected tool WRITES is done only when a file was written. Advanced per tool call, five
                // reads ticked off "fix the bug" as well, the plan read complete, and the answer-now prompt ("WITHOUT
                // calling any more tools") made the model describe the fix instead of applying it.
                var stepAwaitsWrite = stepIdx < plan.Steps.Count && WritesFiles(plan.Steps[stepIdx])
                                      && writesBefore is { } wrote && tools.WritesInRun == wrote;
                if (stepIdx < plan.Steps.Count && !editChangedNothing && !stepAwaitsWrite)
                {
                    if (WritesFiles(plan.Steps[stepIdx]) && unclaimedWrites > 0) unclaimedWrites--;
                    plan.Steps[stepIdx].Status = AgentStepStatus.Done;
                    onStepUpdate?.Invoke(stepIdx, AgentStepStatus.Done);
                    stepIdx++;
                }

                // ── OBSERVE: inject structured observation ─────────────────────
                // Instead of an extra LLM round-trip, we inject a user message that
                // summarises what happened and prompts the model to continue.
                // The model will ACT again on the next loop iteration.
                onStep(Strings.StatusAgentObserving);
                var toolNames  = string.Join(", ", iterExecs.Select(e => e.Name).Distinct());
                var remaining  = plan.Steps.Count(s =>
                    s.Status is AgentStepStatus.Pending or AgentStepStatus.Active);
                // Once the plan is exhausted, the generic observe prompt ("call the tool for the
                // next step") makes the model invent an extra call whose result then displaces
                // the real answer — switch to the answer-now variant anchored on the user task.
                // ⚠ A plan exhausted on an edit that BROKE THE BUILD does not get the answer-now prompt: Smart Fix listed
                // the compilation errors, the prompt then said "WITHOUT calling any more tools", and the run ended on code
                // that no longer compiled — on the bench, every run that ended there failed. Only there: mid-plan,
                // a build broken between two edits of one change (a type created in its new file before it leaves the old
                // one) is the expected state, and asked to fix it, models deleted the file they had just created
                // (measured: hard-move-class 6 of 8 → 3 of 8). Bounded: errors that predate the run would otherwise hold
                // the run open until the iteration limit.
                // The bound counts repair ATTEMPTS — rounds that edited and still left the build broken —, never a round
                // spent reading what to fix.
                var buildFixAsked = remaining == 0 && buildBroken;
                if (buildFixAsked && iterExecs.Any(e => ChatTurnPolicy.IsFileEdit(e.Name))) buildFixRounds++;
                // ⚠ A prose answer after the repair prompt IS an answer ("these errors were there before my change"):
                // counted as a narration, it was told to "call the first tool from your plan".
                answerRequested = remaining == 0 && !editChangedNothing;
                // ⚠ A note waiting for the next action gets the generic prompt, which leaves the tools open: after "write the
                // answer WITHOUT calling any more tools", a note that asks for one more change was answered in prose. The
                // answer is still requested — a prose reply to "stop, just tell me what you changed" IS the answer, never a
                // narration to send back.
                var noteWaiting = steering?.HasPending == true;
                var observeMsg = editChangedNothing
                    ? ModelPrompts.AgentObservePromptEditUnchanged(iteration + 1, maxIter, toolNames)
                    : buildFixAsked
                    ? ModelPrompts.AgentObservePromptBuildBroken(iteration + 1, maxIter, toolNames)
                    : answerRequested && !noteWaiting
                        ? ModelPrompts.AgentObservePromptComplete(iteration + 1, maxIter, toolNames, userTask)
                        : ModelPrompts.AgentObservePrompt(iteration + 1, maxIter, toolNames, remaining);
                answerNowObserve = answerRequested && !noteWaiting && !editChangedNothing && !buildFixAsked
                    ? (observeMsg, ModelPrompts.AgentObservePrompt(iteration + 1, maxIter, toolNames, remaining))
                    : null;
                messages.Add(new ChatMessageDto("user", observeMsg) { IsScaffolding = true });

                // Signal the UI to clear the streaming bubble so that think-only tokens
                // from this tool-calling act do not pollute the final response bubble.
                onStreamReset?.Invoke();
            }
            else
            {
                // ── No tool calls ─────────────────────────────────────────────
                var visible = MarkdownParser.HasPrintableText(MarkdownParser.StripThinkTags(turn.TextContent));

                // (1) Empty / think-only stall: the model returned nothing worth showing and has
                //     done no work yet. The same ACT call usually succeeds on retry — this is a
                //     stochastic stall, not a context problem — so retry it a bounded number of
                //     times rather than ending on a silent "no response" bubble. The empty turn
                //     just appended is dropped so it does not pollute the retried context.
                if (!visible && executions.Count == 0 && tools.Definitions.Count > 0
                    && actRetries < MaxActRetries)
                {
                    actRetries++;
                    retryForcesTool = true;
                    // RemoveAt, NOT Remove(assistantMsg): ChatMessageDto is a record, so Remove
                    // uses VALUE equality and strips the FIRST equal occurrence — after a plan
                    // fallback also produced an empty assistant, a double stall corrupted the plan
                    // trio instead of dropping this turn. The empty turn
                    // was appended just above; it is by construction the last element.
                    messages.RemoveAt(messages.Count - 1);
                    onStreamReset?.Invoke();
                    continue;
                }

                // (1b) The model wrote a new plan instead of the next call — or of the answer. Not an answer: it is sent
                //      back, whether or not the plan was declared complete (the nudge offers both ways out).
                if (visible && tools.Definitions.Count > 0 && planEchoNudges < MaxPlanEchoNudges
                    && LooksLikePlanEcho(turn.TextContent))
                {
                    planEchoNudges++;
                    messages.Add(new ChatMessageDto("user", ModelPrompts.AgentPlanEchoNudge) { IsScaffolding = true });
                    onStreamReset?.Invoke();
                    continue;
                }

                // (2) The model narrated its intentions in prose instead of calling a tool.
                //     Inject one firm nudge and retry; we never nudge more than once. Never
                //     nudge after the observe-complete prompt asked for a prose answer: the
                //     visible text is that answer, not a narration stall.
                if (!nudgedOnce && !answerRequested && tools.Definitions.Count > 0 && visible)
                {
                    nudgedOnce = true;
                    // assistantMsg is already appended to messages above.
                    messages.Add(new ChatMessageDto("user", ModelPrompts.AgentNudgeToolCall) { IsScaffolding = true });
                    // Clear the streaming bubble — the nudge response will start fresh.
                    onStreamReset?.Invoke();
                    continue;
                }

                // ── Genuine final response ────────────────────────────────────
                CloseRemainingSteps(plan, tools.WritesInRun is null ? null : unclaimedWrites, onStepUpdate);

                // The model stopped calling tools but gave no usable answer even though tools ran this
                // turn — either no printable text (think-only / empty) OR a degenerate "I don't have
                // the tools" refusal (tool-oriented models choke on the prose-now turn) OR one more plan
                // after the nudges ran out. Synthesise the answer from the gathered results rather than
                // ending on an empty bubble / a refusal / JSON.
                var finalText      = turn.TextContent;
                var finalCut       = turn.CutAtLimit;
                var finalRepeating = turn.StoppedRepeating;
                var degenerate     = LooksLikeToolRefusal(turn.TextContent) || LooksLikePlanEcho(turn.TextContent);
                if ((!visible || degenerate) && executions.Count > 0)
                {
                    // The synthesis replaced the answer: its own cut and repeat are what the end notice reads.
                    (finalText, finalCut, finalRepeating) = await SynthesizeFinalAnswerAsync(
                        model, messages, anchorCount, executions, userTask,
                        degenerate ? string.Empty : finalText,   // never keep a refusal or a plan as the fallback
                        onToken, onStreamReset, onStep, runEffort, ct, onThinking);
                }

                return new OrchestratorResult(
                    finalText,
                    plan, executions, messages,
                    totalTokens, lastPromptTokens, false, false, finalCut, AnswerRepeating: finalRepeating);
            }
        }

        // Hit the iteration cap. If tools ran, synthesise a final answer from what was gathered so
        // the user gets a real reply instead of only the "iteration limit" notice.
        var (capFinal, capCut, capRepeating) = executions.Count > 0
            ? await SynthesizeFinalAnswerAsync(
                model, messages, anchorCount, executions, userTask, Strings.MsgIterationLimit(maxIter), onToken, onStreamReset, onStep,
                runEffort, ct, onThinking)
            : (Strings.MsgIterationLimit(maxIter), false, false);
        return new OrchestratorResult(
            capFinal, plan, executions, messages,
            totalTokens, lastPromptTokens, false, true, capCut, AnswerRepeating: capRepeating);
    }
}
