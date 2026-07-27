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
/// Unlike the basic <see cref="OllamaClient.RunAgentAsync"/> loop (which is purely reactive),
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

    private readonly IOllamaChatClient _client;
    private readonly InferpalConfig _config;

    public AgentOrchestrator(IOllamaChatClient client, InferpalConfig config)
    {
        _client = client;
        _config = config;
    }

    // Upper bound on the size of a single tool result fed back into the agent context.
    // ~8000 chars ≈ 2000 tokens. Large outputs (fetch_url of a whole page — its own max_chars
    // goes up to 50 000 — web_search, search_codebase) can exceed the model's num_ctx on their
    // own. Compaction runs only *between* user turns (SendCoreAsync), never inside this Plan→Act
    // →Observe loop, so an oversized result makes Ollama silently truncate the request and drop
    // the head (system prompt + task + plan), derailing the agent. The full output is still shown
    // in the UI bubble (onToolExecuted is invoked with the un-capped result).
    // Stable identity of a tool call (name + raw arguments) used to deduplicate repeated calls
    // within a single agent run. Uses the arguments' raw JSON text: models re-issue byte-identical
    // calls when looping, so exact-text matching is sufficient and avoids false merges.
    internal static string ToolCallKey(string toolName, System.Text.Json.JsonElement args) =>
        toolName + "\u0001" + args.GetRawText();

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

    // Fallback iteration cap when AgentMaxIterations is 0/unset. There is deliberately
    // no "unlimited" mode: an uncapped agent loop on a single shared local GPU is a
    // footgun (and the settings UI documents 0 as "use the default").
    internal const int DefaultMaxIterations = 20;

    internal static string CapForContext(string result) =>
        result.Length <= MaxToolResultCharsInContext
            ? result
            : result[..MaxToolResultCharsInContext] +
              $"\n\n[... truncated to {MaxToolResultCharsInContext} characters out of {result.Length} to stay within the context window]";

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
    internal static int EstimateTokens(IEnumerable<ChatMessageDto> messages)
    {
        var chars = 0;
        foreach (var m in messages)
            chars += m.Content?.Length ?? 0;
        return chars / 4;
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
    internal static void CompactRunContext(List<ChatMessageDto> messages, int anchorCount, int budget)
    {
        if (budget <= 0) return;
        if (EstimateTokens(messages) <= budget * 8 / 10) return;   // under 80% — nothing to do

        var target          = budget * 7 / 10;                     // compact down to ~70%
        var lastCompactable = messages.Count - KeepRecentMessages; // keep the recent tail verbatim

        for (int i = anchorCount; i < lastCompactable; i++)
        {
            if (EstimateTokens(messages) <= target) break;
            var m = messages[i];
            if (m.Role != "tool" || m.Content == ElidedToolResult) continue;
            messages[i] = m with { Content = ElidedToolResult };
        }
    }

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
        bool alreadySummarized, Action<string> onStep, CancellationToken ct)
    {
        var budget = _config.ContextWindowSize;
        if (budget <= 0) return alreadySummarized;
        if (EstimateTokens(messages) <= budget * 8 / 10) return alreadySummarized;

        if (!alreadySummarized && _config.CompactionEnabled)
        {
            onStep(Strings.StatusCompacting);
            if (await TrySummarizeOldTurnsAsync(messages, anchorCount, model, ct))
                return true;   // summary replaced the old range — done for this overflow
        }

        // No summary (disabled, too little to summarize, or it failed/timed out) → elide.
        CompactRunContext(messages, anchorCount, budget);
        return true;
    }

    /// <summary>
    /// Summarises the old middle turns (between the anchored head and the recent tail) with one LLM
    /// call and replaces them in place with a single summary message pair. Returns <c>false</c> —
    /// leaving <paramref name="messages"/> untouched — when there is too little to summarise or the
    /// call fails/times out, so the caller can fall back to elision. Re-throws on user cancellation.
    /// </summary>
    private async Task<bool> TrySummarizeOldTurnsAsync(
        List<ChatMessageDto> messages, int anchorCount, string model, CancellationToken ct)
    {
        var rangeLen = messages.Count - KeepRecentMessages - anchorCount;
        if (rangeLen < 2) return false;   // not enough old turns to be worth a round-trip

        var sb = new System.Text.StringBuilder();
        for (int i = anchorCount; i < anchorCount + rangeLen; i++)
        {
            var m = messages[i];
            if (string.IsNullOrEmpty(m.Content)) continue;
            var label = m.Role switch
            {
                "user"      => "User",
                "assistant" => "Assistant",
                "tool"      => "Tool",
                _           => m.Role,
            };
            sb.Append(label).Append(": ").AppendLine(m.Content);
            sb.AppendLine();
        }
        if (sb.Length == 0) return false;

        var summarizeMessages = new List<ChatMessageDto>
        {
            messages[0],   // system prompt anchor
            new("user", Strings.CompactionSummarizePrompt(sb.ToString())),
        };

        string summary;
        try
        {
            var timeoutSec = Math.Max(10, _config.CompactionTimeoutSeconds);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));

            var turn = await _client.SendChatAsync(
                model, summarizeMessages, EmptyToolRegistry.Instance, null, cts.Token, TaskComplexity.Quick);
            summary = MarkdownParser.StripThinkTags(turn.TextContent);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; } // user cancelled
        catch (OperationCanceledException) { return false; }                            // summary timed out
        catch { return false; }                                                         // network/other → elide

        if (string.IsNullOrWhiteSpace(summary)) return false;

        // Replace the summarised range with a single [summary] pair.
        messages.RemoveRange(anchorCount, rangeLen);
        messages.Insert(anchorCount, new ChatMessageDto("assistant", summary));
        messages.Insert(anchorCount, new ChatMessageDto("user", "[Summary of this run's earlier context]"));
        return true;
    }

    /// <summary>
    /// Final synthesis pass. When the Act-Observe loop ends without a printable answer but tools did
    /// run (web_search, fetch_url, search_codebase…), the gathered results are sitting unused: the
    /// model looped (re-issued an identical call → loop detection), stalled think-only on its final
    /// turn, or hit the iteration cap before writing its reply. One last no-tools call turns those
    /// results into the actual answer the user asked for, instead of the bare "✓ Done — &lt;tools&gt;"
    /// tool summary the UI falls back to for an empty response. Streams via <paramref name="onToken"/>
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
    private async Task<string> SynthesizeFinalAnswerAsync(
        string model, List<ChatMessageDto> messages, int anchorCount,
        IReadOnlyList<ToolExecution> executions, string userTask, string fallback,
        Action<string>? onToken, Action? onStreamReset, Action<string> onStep, CancellationToken ct,
        Action<string>? onThinking = null)
    {
        // Clear any think-only / partial tokens from the stalled final ACT so they don't pollute
        // the synthesised answer that streams next.
        onStreamReset?.Invoke();
        onStep(Strings.StatusAgentSynthesizing);

        // Head = system prompt + original conversation, WITHOUT the 3-message plan trio appended at
        // the tail of the anchored head (AgentPlanPrompt / plan JSON / AgentExecutePlan). tool_calls
        // are stripped so nothing references a tool while the registry is empty.
        var synth   = new List<ChatMessageDto>();
        var headEnd = Math.Max(1, anchorCount - 3);
        for (int i = 0; i < headEnd && i < messages.Count; i++)
            synth.Add(messages[i].ToolCalls is null ? messages[i] : messages[i] with { ToolCalls = null });

        // The gathered tool results + the synthesis instruction, in a single user turn so "those
        // results" in the prompt resolves to the digest directly above it. Bounded to ~60% of the
        // context window so the synthesis request can't overflow num_ctx and lose its head.
        var budgetChars = _config.ContextWindowSize > 0
            ? Math.Max(8000, _config.ContextWindowSize * 4 * 6 / 10)
            : 24000;
        var digest = BuildToolDigest(executions, budgetChars);
        var prompt = digest.Length > 0
            ? digest + "\n\n" + Strings.AgentSynthesizePrompt(TaskSnippet(userTask))
            : Strings.AgentSynthesizePrompt(TaskSnippet(userTask));
        synth.Add(new ChatMessageDto("user", prompt));

        try
        {
            var turn   = await _client.SendChatAsync(
                model, synth, EmptyToolRegistry.Instance, onToken, ct, TaskComplexity.Normal, onThinking: onThinking);
            var answer = MarkdownParser.StripThinkTags(turn.TextContent);
            return MarkdownParser.HasPrintableText(answer) && !LooksLikeToolRefusal(answer)
                ? turn.TextContent
                : fallback;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return fallback; }   // synthesis failed/timed out → keep the existing fallback
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
        Action<string>?            onThinking = null)
    {
        // Claim the shared GPU for the whole run, like InferenceProviderBase.RunAgentAsync does for
        // the basic loop: this path calls SendChatAsync directly, so without its own lease the
        // background embedding loops keep the GPU busy and the cross-process FIM never yields
        // during an orchestrated (Agent mode) run. Re-entrant, so nesting under RunAgentAsync is safe.
        using var gpuLease = GpuScheduler.AcquireChatLease();

        // Early-exit checks (circuit breaker, URL) are handled inside SendChatAsync.
        var messages    = new List<ChatMessageDto>(history);
        // The user's current request (last user turn) — quoted in the synthesis prompt so the
        // final answer targets it, not an earlier question from the conversation.
        var userTask    = string.Empty;
        for (int i = history.Count - 1; i >= 0; i--)
            if (history[i].Role == "user") { userTask = history[i].Content ?? string.Empty; break; }
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

        // NOTE: we deliberately do NOT re-inject the tool catalogue as prose into the system prompt.
        // The full tools:[] JSON schema is already sent on every ACT turn, and the orchestrator
        // backstops a model that narrates instead of calling (tool_choice:"required" on the first
        // ACT + the one-shot nudge below). A prose duplicate only inflated every (cache-missing,
        // multi-turn) reprocess by hundreds of tokens — most painful on large multimodal models that
        // can't reuse the KV cache — for no functional gain. The PLAN phase decomposes the task into
        // steps, not tool selections, so it does not need the catalogue either.

        // ── Phase 1: PLAN ────────────────────────────────────────────────────
        onStep(Strings.StatusAgentPlanning);

        // Build a separate message list for the planning call: we append the
        // plan-request as a new user message so the original user turn is preserved.
        var planMessages = new List<ChatMessageDto>(messages)
        {
            new ChatMessageDto("user", Strings.AgentPlanPrompt),
        };

        ChatTurnResult planTurn;
        try
        {
            // No tools during planning — we want a pure JSON response.  Quick timeout: plans are short.
            planTurn = await _client.SendChatAsync(model, planMessages, EmptyToolRegistry.Instance, null, ct, TaskComplexity.Quick, onThinking: onThinking);
        }
        catch (OperationCanceledException) { throw; }
        catch (AgentHttpException ex)
        {
            return OrchestratorResult.Error(ex.Message, messages);
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
        messages.Add(new ChatMessageDto("user",      Strings.AgentPlanPrompt));
        messages.Add(new ChatMessageDto("assistant", planTurn.TextContent));
        messages.Add(new ChatMessageDto("user",      Strings.AgentExecutePlan));

        // Everything added so far — system prompt (+ tool descriptions), the original conversation
        // history, and the plan trio — is the anchored head. Intra-run compaction (CompactRunContext)
        // never touches messages below this index, so the model keeps its task framing and plan even
        // when the accumulating tool results would otherwise push the request past num_ctx and make
        // Ollama silently truncate from the front.
        var anchorCount = messages.Count;

        // ── Phase 2: Act-Observe loop ────────────────────────────────────────
        int stepIdx = 0;

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
            runSummarized = await CompactRunContextAsync(messages, anchorCount, model, runSummarized, onStep, ct);

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
                var toolChoice = (iteration == 0 || actRetries > 0) ? "required" : null;
                turn = await _client.SendChatAsync(model, messages, tools, onToken, ct, TaskComplexity.Normal, toolChoice, onThinking);
            }
            catch (OperationCanceledException) { throw; }
            catch (AgentHttpException ex)
            {
                return new OrchestratorResult(
                    ex.Message, plan, executions, messages,
                    totalTokens, lastPromptTokens, false, false);
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
                    // If real work was already done, treat the repeat as a graceful finish rather
                    // than an alarming error: mark remaining steps done and synthesise the final
                    // answer from the gathered tool results (the model looped instead of writing it).
                    // Only surface the loop message when nothing was accomplished — the genuinely
                    // stuck case.
                    foreach (var step in plan.Steps)
                        if (step.Status is AgentStepStatus.Pending or AgentStepStatus.Active)
                        {
                            step.Status = AgentStepStatus.Done;
                            onStepUpdate?.Invoke(plan.Steps.IndexOf(step), AgentStepStatus.Done);
                        }

                    var loopMsg = executions.Count > 0
                        ? await SynthesizeFinalAnswerAsync(
                            model, messages, anchorCount, executions, userTask, string.Empty, onToken, onStreamReset, onStep, ct, onThinking)
                        : Strings.MsgLoopDetected;
                    return new OrchestratorResult(
                        loopMsg, plan, executions, messages,
                        totalTokens, lastPromptTokens, true, false);
                }

                // ── Execute tools ─────────────────────────────────────────────
                var iterExecs = new List<ToolExecution>();

                // A batch made only of independent, GPU-free, prompt-free read tools runs
                // concurrently — a real speedup when the model explores several files in one turn.
                // The dedup-cache / diff plumbing only matters for cacheable or mutating tools, which
                // are never parallel-safe, so the parallel path skips it (results recorded in order).
                if (ShouldRunParallel(calls))
                {
                    onStep(Strings.StatusCallingTool(string.Join(", ", calls.Select(c => c.Function.Name).Distinct())));
                    var results = await Task.WhenAll(
                        calls.Select(c => tools.ExecuteAsync(c.Function.Name, c.Function.Arguments, ct)));
                    for (int i = 0; i < calls.Count; i++)
                    {
                        var toolName  = calls[i].Function.Name;
                        var result    = results[i];
                        var hasErrors = toolName == GetDiagnosticsTool.ToolName
                                        && GetDiagnosticsTool.OutputHasErrors(result);
                        var exec = new ToolExecution(toolName, calls[i].Function.Arguments.ToString(), result, hasErrors, null);
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
                    string? cached = null;
                    var fromCache = IsCacheable(toolName) && toolCache.TryGetValue(cacheKey, out cached);

                    string    result;
                    DiffInfo? diff;
                    if (fromCache)
                    {
                        result = cached!;
                        diff   = null;
                    }
                    else
                    {
                        result = await tools.ExecuteAsync(toolName, call.Function.Arguments, ct);
                        diff   = tools.ConsumeDiff();
                        if (IsCacheable(toolName))
                            toolCache[cacheKey] = result;
                    }

                    var hasErrors = toolName == GetDiagnosticsTool.ToolName
                                    && GetDiagnosticsTool.OutputHasErrors(result);

                    var exec = new ToolExecution(toolName, call.Function.Arguments.ToString(), result, hasErrors, diff);
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

                // ── Mark step done, advance ────────────────────────────────────
                if (stepIdx < plan.Steps.Count)
                {
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
                answerRequested = remaining == 0;
                var observeMsg = answerRequested
                    ? Strings.AgentObservePromptComplete(iteration + 1, maxIter, toolNames, TaskSnippet(userTask))
                    : Strings.AgentObservePrompt(iteration + 1, maxIter, toolNames, remaining);
                messages.Add(new ChatMessageDto("user", observeMsg));

                // Signal the UI to clear the streaming bubble so that think-only tokens
                // from this tool-calling act do not pollute the final response bubble.
                onStreamReset?.Invoke();
            }
            else
            {
                // ── No tool calls ─────────────────────────────────────────────
                var visible = MarkdownParser.HasPrintableText(MarkdownParser.StripThinkTags(turn.TextContent));

                // (1) Empty / think-only stall: the model returned nothing worth showing and
                //     has done no work yet. The A/B test (qwen3.6:27b) showed the same ACT call
                //     succeeds ~3/3 — this failure is a stochastic stall, not a context problem.
                //     Retry the very same (reliable) ACT call a bounded number of times rather
                //     than ending on a silent "no response" bubble. We drop the empty turn we
                //     just appended so it doesn't pollute the retried context.
                if (!visible && executions.Count == 0 && tools.Definitions.Count > 0
                    && actRetries < MaxActRetries)
                {
                    actRetries++;
                    messages.Remove(assistantMsg);
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
                    messages.Add(new ChatMessageDto("user", Strings.AgentNudgeToolCall));
                    // Clear the streaming bubble — the nudge response will start fresh.
                    onStreamReset?.Invoke();
                    continue;
                }

                // ── Genuine final response ────────────────────────────────────
                // Mark all remaining steps as done.
                foreach (var step in plan.Steps)
                    if (step.Status is AgentStepStatus.Pending or AgentStepStatus.Active)
                    {
                        step.Status = AgentStepStatus.Done;
                        onStepUpdate?.Invoke(plan.Steps.IndexOf(step), AgentStepStatus.Done);
                    }

                // The model stopped calling tools but gave no usable answer even though tools ran this
                // turn — either no printable text (think-only / empty) OR a degenerate "I don't have
                // the tools" refusal (tool-oriented models choke on the prose-now turn). Synthesise the
                // answer from the gathered results rather than ending on an empty bubble / a refusal.
                var finalText  = turn.TextContent;
                var degenerate = LooksLikeToolRefusal(turn.TextContent);
                if ((!visible || degenerate) && executions.Count > 0)
                    finalText = await SynthesizeFinalAnswerAsync(
                        model, messages, anchorCount, executions, userTask,
                        degenerate ? string.Empty : finalText,   // never keep a refusal as the fallback
                        onToken, onStreamReset, onStep, ct, onThinking);

                return new OrchestratorResult(
                    finalText,
                    plan, executions, messages,
                    totalTokens, lastPromptTokens, false, false);
            }
        }

        // Hit the iteration cap. If tools ran, synthesise a final answer from what was gathered so
        // the user gets a real reply instead of only the "iteration limit" notice.
        var capFinal = executions.Count > 0
            ? await SynthesizeFinalAnswerAsync(
                model, messages, anchorCount, executions, userTask, Strings.MsgIterationLimit, onToken, onStreamReset, onStep, ct, onThinking)
            : Strings.MsgIterationLimit;
        return new OrchestratorResult(
            capFinal, plan, executions, messages,
            totalTokens, lastPromptTokens, false, true);
    }
}
