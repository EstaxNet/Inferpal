using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services.Tools;

namespace Inferpal.Services;

/// <summary>
/// Backend-agnostic foundation shared by every <see cref="IInferenceProvider"/> implementation
/// (Ollama and OpenAI-compatible). Holds the cross-cutting concerns that do not depend on the
/// wire format: the shared <see cref="HttpClient"/>, the chat + embedding circuit breakers, the
/// per-task timeout policy, the streaming POST helper, and the whole agentic loop
/// (<see cref="RunAgentAsync"/>, which only ever calls the abstract <see cref="SendChatAsync"/>).
/// </summary>
/// <remarks>
/// Wire-format-specific operations are <c>abstract</c> (chat, embeddings, model listing, connection
/// check, FIM). Ollama-only operations have safe <c>virtual</c> no-op defaults so an OpenAI-compatible
/// backend never has to implement endpoints its server doesn't expose.
/// </remarks>
internal abstract class InferenceProviderBase : IInferenceProvider
{
    protected static readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };

    protected static readonly JsonSerializerOptions _jsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    protected readonly InferpalConfig _config;

    protected InferenceProviderBase(InferpalConfig config) => _config = config;

    /// <inheritdoc/>
    public abstract ProviderCapabilities Capabilities { get; }

    // ── Chat circuit breaker ───────────────────────────────────────────────────
    // 5 consecutive failures → 5-minute cooldown; all calls short-circuit until
    // the cooldown expires or the user clicks Retry (which calls ResetCircuit).
    private const  int          MaxConsecutiveFailures = 5;
    private static readonly TimeSpan CooldownDuration = TimeSpan.FromMinutes(5);

    private int  _consecutiveFailures = 0;
    private long _cooldownUntilTicks  = 0; // UTC ticks; 0 = no cooldown

    protected bool IsInCooldown()  => DateTime.UtcNow.Ticks < Interlocked.Read(ref _cooldownUntilTicks);
    protected void RecordSuccess() => Interlocked.Exchange(ref _consecutiveFailures, 0);

    protected void RecordFailure()
    {
        if (Interlocked.Increment(ref _consecutiveFailures) >= MaxConsecutiveFailures)
        {
            Interlocked.Exchange(ref _cooldownUntilTicks, (DateTime.UtcNow + CooldownDuration).Ticks);
            Interlocked.Exchange(ref _consecutiveFailures, 0);
        }
    }

    /// <summary>Resets the chat circuit breaker immediately (e.g. on manual Retry).</summary>
    public void ResetCircuit()
    {
        Interlocked.Exchange(ref _consecutiveFailures, 0);
        Interlocked.Exchange(ref _cooldownUntilTicks,  0);
    }

    // ── Embedding circuit breaker ──────────────────────────────────────────────
    // Dedicated breaker for embedding calls, fully independent of the chat breaker:
    // embedding model failures do NOT affect chat availability.
    // 3 consecutive failures → 2-minute cooldown (faster recovery than chat).
    private const  int          EmbMaxConsecutiveFailures = 3;
    private static readonly TimeSpan EmbCooldownDuration = TimeSpan.FromMinutes(2);

    private int  _embConsecutiveFailures = 0;
    private long _embCooldownUntilTicks  = 0; // UTC ticks; 0 = no cooldown

    protected bool IsEmbeddingInCooldown()  => DateTime.UtcNow.Ticks < Interlocked.Read(ref _embCooldownUntilTicks);
    protected void RecordEmbeddingSuccess() => Interlocked.Exchange(ref _embConsecutiveFailures, 0);

    protected void RecordEmbeddingFailure()
    {
        if (Interlocked.Increment(ref _embConsecutiveFailures) >= EmbMaxConsecutiveFailures)
        {
            Interlocked.Exchange(ref _embCooldownUntilTicks, (DateTime.UtcNow + EmbCooldownDuration).Ticks);
            Interlocked.Exchange(ref _embConsecutiveFailures, 0);
        }
    }

    /// <summary>
    /// <c>true</c> when the embedding circuit breaker is open (cooldown in effect).
    /// The RAG index will fall back to keyword search during this period.
    /// </summary>
    public bool IsEmbeddingCircuitOpen => IsEmbeddingInCooldown();

    /// <summary><c>true</c> when the chat circuit breaker is open (cooldown in effect).</summary>
    internal bool IsChatCircuitOpen => IsInCooldown();

    // ── Task timeout helper ───────────────────────────────────────────────────
    protected int TimeoutFor(TaskComplexity c) => c switch
    {
        TaskComplexity.Quick  => Math.Max(10, _config.QuickTimeoutSeconds),
        TaskComplexity.Normal => Math.Max(10, _config.NormalTimeoutSeconds),
        TaskComplexity.Deep   => Math.Max(10, _config.DeepTimeoutSeconds),
        _                     => Math.Max(10, _config.NormalTimeoutSeconds),
    };

    // ── Conversation normalization (shared by all wire formats) ────────────────

    /// <summary>
    /// Normalizes a conversation so strict chat templates accept it. Two transforms, both
    /// wire-agnostic (a cleaner history also improves KV-cache reuse on the tolerant backends):
    /// <list type="number">
    /// <item><b>Coalesce same-role plain turns.</b> The agent orchestration emits two consecutive
    /// <c>user</c> turns (workspace context + question, then the planning prompt) and can chain other
    /// same-role turns. Ollama's Go templates tolerate this, but the Jinja template Mistral / Devstral
    /// embed (rendered by LM Studio) rejects it with "conversation roles must alternate user and
    /// assistant roles except for tool calls and results". Merging joins the bodies with a blank line.</item>
    /// <item><b>Fold a user turn that follows a tool result into that result.</b> The same strict
    /// templates require the turn after tool results to be the assistant reply (or generation) — a
    /// <c>tool → user</c> transition is rejected with the same error. The agent loop injects an
    /// "OBSERVER" <c>user</c> nudge right after the tool output, so fold its text into the preceding
    /// tool message: the history stays valid (…<c>assistant(tool_calls) → tool(result + nudge) →
    /// [generate]</c>) without dropping the steering text.</item>
    /// </list>
    /// Tool messages and assistant turns carrying <c>tool_calls</c> are never merged with each other:
    /// they are structurally distinct and templates treat them as part of the surrounding turn.
    /// </summary>
    internal static List<ChatMessageDto> CoalesceConsecutiveRoles(List<ChatMessageDto> messages)
    {
        static bool IsPlain(ChatMessageDto m) =>
            m.Role != "tool" && (m.ToolCalls is null || m.ToolCalls.Count == 0);

        static string JoinBodies(string? a, string? b) =>
            string.Join("\n\n", new[] { a, b }.Where(c => !string.IsNullOrEmpty(c)));

        var result = new List<ChatMessageDto>(messages.Count);
        foreach (var m in messages)
        {
            if (result.Count > 0 && IsPlain(m))
            {
                var prev = result[^1];
                // Merge adjacent same-role plain turns (user→user, assistant→assistant).
                if (prev.Role == m.Role && IsPlain(prev))
                {
                    result[^1] = prev with { Content = JoinBodies(prev.Content, m.Content) };
                    continue;
                }
                // Fold a user nudge that follows a tool result into that result (tool→user is illegal
                // under strict templates; assistant→user is valid alternation and is left untouched).
                if (m.Role == "user" && prev.Role == "tool")
                {
                    result[^1] = prev with { Content = JoinBodies(prev.Content, m.Content) };
                    continue;
                }
            }
            result.Add(m);
        }
        return result;
    }

    // ── Streaming POST helper ──────────────────────────────────────────────────
    // Sends a POST and returns as soon as the response HEADERS are read, so the body can be
    // consumed incrementally while the server generates. PostAsJsonAsync (ResponseContentRead)
    // buffers the entire body before returning, defeating streaming and applying the request
    // timeout to the whole generation instead of the time-to-first-byte.
    protected static async Task<HttpResponseMessage> PostForStreamingAsync<T>(
        string url, T body, CancellationToken ct, IReadOnlyDictionary<string, string>? headers = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body, options: _jsonOpts),
        };
        if (headers is not null)
            foreach (var kv in headers) request.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        try
        {
            response.EnsureSuccessStatusCode();
            return response;
        }
        catch
        {
            response.Dispose(); // release the connection on HTTP errors
            throw;
        }
    }

    // ── Wire-format-specific operations (each backend implements these) ─────────

    /// <inheritdoc/>
    public abstract Task<ChatTurnResult> SendChatAsync(
        string model,
        List<ChatMessageDto> messages,
        IToolRegistry tools,
        Action<string>? onToken,
        CancellationToken ct,
        TaskComplexity complexity = TaskComplexity.Normal,
        string? toolChoice = null,
        Action<string>? onThinking = null);

    /// <inheritdoc/>
    public abstract Task<float[]?> GetEmbeddingAsync(string text, string model, CancellationToken ct);

    /// <inheritdoc/>
    public abstract Task<bool> CheckConnectionAsync(string url, CancellationToken ct);

    /// <inheritdoc/>
    public abstract Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct, string? url = null);

    /// <inheritdoc/>
    public abstract Task<IReadOnlyList<InstalledModelInfo>> ListInstalledModelsAsync(CancellationToken ct, string? url = null);

    /// <inheritdoc/>
    public abstract Task StreamFimAsync(
        string prefix,
        string suffix,
        int maxTokens,
        double temperature,
        Action<string> onToken,
        CancellationToken ct,
        string? model = null);

    // ── Ollama-only operations (safe no-op defaults; Ollama overrides them) ─────

    /// <inheritdoc/>
    public virtual Task<IReadOnlyList<RunningModelInfo>> GetRunningModelsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<RunningModelInfo>>([]);

    /// <inheritdoc/>
    public virtual Task UnloadModelAsync(string model, CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc/>
    public virtual Task<ModelArchInfo?> ShowModelAsync(string model, CancellationToken ct) =>
        Task.FromResult<ModelArchInfo?>(null);

    /// <inheritdoc/>
    public virtual Task<bool> PullModelAsync(string model, Action<string> onStatus, CancellationToken ct) =>
        Task.FromResult(false);

    /// <inheritdoc/>
    public virtual Task<bool> DeleteModelAsync(string model, CancellationToken ct) => Task.FromResult(false);

    // ── Agentic loop (backend-agnostic: only calls SendChatAsync) ──────────────

    /// <summary>
    /// Runs the basic agentic loop: one <see cref="SendChatAsync"/> turn at a time, executing any
    /// returned tool calls, until the model answers without tools or the configured iteration cap
    /// (<see cref="InferpalConfig.AgentMaxIterations"/>) is reached. Tool results are size-capped
    /// and the oldest are elided when the running context nears num_ctx (shared policies with
    /// <see cref="AgentOrchestrator"/> — this path just skips the PLAN phase and LLM summarisation).
    /// </summary>
    /// <param name="model">Model name (e.g. <c>"llama3.1"</c>).</param>
    /// <param name="history">Full conversation history including the new user message.</param>
    /// <param name="tools">Registry of tools to expose to the model.</param>
    /// <param name="onStep">Called with a status string on each major step (thinking, calling tool).</param>
    /// <param name="onToken">Called with each streamed token for real-time display; <c>null</c> to disable.</param>
    /// <returns>
    /// An <see cref="AgentResult"/> with the final response text, all tool executions,
    /// the updated conversation history, and token counts.
    /// </returns>
    public async Task<AgentResult> RunAgentAsync(
        string model,
        List<ChatMessageDto> history,
        IToolRegistry tools,
        Action<string> onStep,
        Action<string>? onToken,
        CancellationToken ct,
        TaskComplexity complexity = TaskComplexity.Normal,
        Action<ToolExecution>? onToolExecuted = null,
        Action<string>? onThinking = null)
    {
        var base_ = _config.BaseUrl.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(base_))
            return new AgentResult(Strings.MsgNoUrl, [], history);

        if (IsInCooldown())
            return new AgentResult(Strings.MsgCircuitOpen, [], history);

        // Claim the shared GPU for the whole run: background indexing pauses (GpuScheduler) and the
        // in-devenv ghost-text yields (ChatBusySignal) for the duration, across every agent turn and
        // tool call. Disposed on every exit path. Covers ALL callers (chat, commit, title, synthesis,
        // plan, code actions) — replacing the chat VM's manual BeginInteractive/EndInteractive bracket.
        using var gpuLease = GpuScheduler.AcquireChatLease();

        var messages          = new List<ChatMessageDto>(history);
        var executions        = new List<ToolExecution>();
        // Per-batch repeat counts for loop detection (see AgentLoopPolicy).
        var sigCounts         = new Dictionary<string, int>();
        int totalTokens       = 0;
        int lastPromptEval    = 0;

        // Honor the user's configured cap; 0/unset falls back to the shared default
        // (same semantics as AgentOrchestrator — never unlimited).
        var maxIterations = _config.AgentMaxIterations > 0
            ? _config.AgentMaxIterations
            : AgentOrchestrator.DefaultMaxIterations;

        // The caller's history (system prompt + conversation + the user's task) is the
        // anchored head — intra-run elision never touches it (see CompactRunContext).
        var anchorCount = messages.Count;

        for (int i = 0; i < maxIterations; i++)
        {
            ct.ThrowIfCancellationRequested();

            // Keep the running context under num_ctx: deterministically elide the oldest tool
            // results so the model never silently truncates the head (system prompt + task).
            // No LLM summary on this basic path — that extra call is the orchestrator's.
            AgentOrchestrator.CompactRunContext(messages, anchorCount, _config.ContextWindowSize);

            onStep(Strings.StatusThinking);

            // ── One model turn — SendChatAsync owns HTTP, streaming, timeout policy,
            //    circuit breaker, and inline tool-call recovery.
            ChatTurnResult turn;
            try
            {
                turn = await SendChatAsync(model, messages, tools, onToken, ct, complexity, null, onThinking);
            }
            catch (OperationCanceledException) { throw; } // propagate user-initiated cancel
            catch (AgentHttpException ex)
            {
                return new AgentResult(ex.Message, executions, messages, totalTokens, lastPromptEval);
            }

            totalTokens   += turn.TokensUsed;
            lastPromptEval = turn.PromptTokens;

            messages.Add(new ChatMessageDto("assistant", turn.TextContent, turn.ToolCalls));

            if (turn.ToolCalls is { Count: > 0 } calls)
            {
                // Detect infinite loops: a repeated tool-call batch → abort (AgentLoopPolicy
                // tolerates more repeats for read-only batches). When work was already done,
                // return an empty response so the UI shows its "✓ Done — <tools>" summary
                // rather than an alarming "loop detected" message.
                if (AgentLoopPolicy.IsLoop(sigCounts, calls))
                {
                    var loopMsg = executions.Count > 0 ? string.Empty : Strings.MsgLoopDetected;
                    return new AgentResult(loopMsg, executions, messages, totalTokens, lastPromptEval);
                }

                // A batch of independent, GPU-free, prompt-free read tools runs concurrently
                // (same policy as the orchestrator); everything else stays sequential. Read-only
                // tools never produce a diff, so the parallel path records DiffInfo = null.
                if (AgentOrchestrator.ShouldRunParallel(calls))
                {
                    onStep(Strings.StatusCallingTool(string.Join(", ", calls.Select(c => c.Function.Name).Distinct())));
                    var results = await Task.WhenAll(
                        calls.Select(c => tools.ExecuteAsync(c.Function.Name, c.Function.Arguments, ct)));
                    for (int idx = 0; idx < calls.Count; idx++)
                    {
                        var toolName  = calls[idx].Function.Name;
                        var result    = results[idx];
                        var hasErrors = toolName == GetDiagnosticsTool.ToolName && GetDiagnosticsTool.OutputHasErrors(result);
                        var exec      = new ToolExecution(toolName, calls[idx].Function.Arguments.ToString(), result, hasErrors, null);
                        executions.Add(exec);
                        onToolExecuted?.Invoke(exec);
                        messages.Add(new ChatMessageDto("tool", AgentOrchestrator.CapForContext(result)));
                    }
                }
                else foreach (var call in calls)
                {
                    ct.ThrowIfCancellationRequested();
                    var toolName = call.Function.Name;
                    onStep(Strings.StatusCallingTool(toolName));

                    var result    = await tools.ExecuteAsync(toolName, call.Function.Arguments, ct);
                    var diff      = tools.ConsumeDiff();
                    var hasErrors = toolName == GetDiagnosticsTool.ToolName && GetDiagnosticsTool.OutputHasErrors(result);
                    var exec      = new ToolExecution(toolName, call.Function.Arguments.ToString(), result, hasErrors, diff);
                    executions.Add(exec);
                    onToolExecuted?.Invoke(exec);   // render the tool bubble live (full output)
                    // Size-capped copy for the model context; the UI bubble keeps the full output.
                    messages.Add(new ChatMessageDto("tool", AgentOrchestrator.CapForContext(result)));
                }
            }
            else
            {
                return new AgentResult(turn.TextContent, executions, messages, totalTokens, lastPromptEval);
            }
        }

        return new AgentResult(Strings.MsgIterationLimit, executions, messages, totalTokens, lastPromptEval);
    }
}
