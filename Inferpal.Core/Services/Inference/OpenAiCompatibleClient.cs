using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Models;

namespace Inferpal.Services.Inference;

/// <summary>
/// <see cref="IInferenceProvider"/> backed by an OpenAI-compatible server — LM Studio, llama.cpp
/// server, vLLM, Jan, LiteLLM, etc. Speaks <c>/v1/chat/completions</c> (SSE streaming),
/// <c>/v1/embeddings</c>, and <c>/v1/models</c>.
/// </summary>
/// <remarks>
/// In v1 it advertises <see cref="ProviderCapabilities.OpenAiCompatible"/> (chat + embeddings only):
/// these servers expose no VRAM/running-model endpoint, no model pull/delete, and no reliable FIM,
/// so those operations inherit the safe no-op defaults from <see cref="InferenceProviderBase"/> and
/// the dependent UI is gated off via <see cref="Capabilities"/>.
/// </remarks>
internal class OpenAiCompatibleClient : InferenceProviderBase
{
    public OpenAiCompatibleClient(InferpalConfig config) : base(config) { }

    /// <inheritdoc/>
    public override ProviderCapabilities Capabilities => ProviderCapabilities.OpenAiCompatible;

    // ── URL / auth helpers ─────────────────────────────────────────────────────

    /// <summary>Normalizes a base URL to its <c>/v1</c> root (LM Studio default: http://localhost:1234).</summary>
    internal static string V1(string raw)
    {
        var b = (raw ?? string.Empty).Trim().TrimEnd('/');
        if (b.Length == 0) return b;
        return b.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? b : b + "/v1";
    }

    private protected string BaseV1 => V1(_config.BaseUrl);

    /// <summary>Bearer auth headers for generic OpenAI-compatible servers; empty for LM Studio (no key).</summary>
    private protected IReadOnlyDictionary<string, string>? AuthHeaders()
    {
        var key = _config.ApiKey?.Trim();
        return string.IsNullOrEmpty(key)
            ? null
            : new Dictionary<string, string> { ["Authorization"] = $"Bearer {key}" };
    }

    private protected void AddAuth(HttpRequestMessage req)
    {
        var headers = AuthHeaders();
        if (headers is not null)
            foreach (var kv in headers) req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
    }

    // ── Message mapping (internal ChatMessageDto → OpenAI wire shape) ───────────
    // OpenAI requires: each assistant tool_call carries an id + type; arguments are a JSON *string*;
    // each tool result carries the matching tool_call_id. The agent loop emits tool results in the
    // same order as the calls, so ids are correlated positionally via a per-batch queue.
    internal static List<OpenAiRequestMessage> MapMessages(List<ChatMessageDto> messages)
    {
        messages = CoalesceConsecutiveRoles(messages);

        var mapped     = new List<OpenAiRequestMessage>(messages.Count);
        var pendingIds = new Queue<string>();
        int counter    = 0;

        foreach (var m in messages)
        {
            if (m.Role == "assistant" && m.ToolCalls is { Count: > 0 } calls)
            {
                pendingIds.Clear();
                var wireCalls = new List<OpenAiToolCall>(calls.Count);
                foreach (var c in calls)
                {
                    var id = $"call_{counter++}";
                    pendingIds.Enqueue(id);
                    wireCalls.Add(new OpenAiToolCall(
                        id,
                        new OpenAiFnCall(c.Function.Name, c.Function.Arguments.GetRawText())));
                }
                // content must be null (not "") when only tool_calls are present.
                var content = string.IsNullOrEmpty(m.Content) ? null : m.Content;
                mapped.Add(new OpenAiRequestMessage("assistant", content, wireCalls));
            }
            else if (m.Role == "tool")
            {
                // No pending call ⇒ this result is orphaned (its assistant parent is gone). Inventing
                // an id here produced a tool_call_id no assistant message declares, which
                // OpenAI-compatible servers reject with a 400 — killing the run.
                // ⚠ Dropping is a LAST-RESORT net, not a strategy: it is lossy, and silent to the
                // user. Callers that rewrite history are boundary-safe by construction
                // (ToolTranscript, ToolBlockBoundary), so reaching this line means a new caller
                // broke that invariant — the Diagnostics entry is how it gets found.
                if (pendingIds.Count == 0)
                {
                    Diagnostics.Record("OpenAiCompatible", "Dropped an orphaned tool result (no matching tool_call).");
                    continue;
                }
                mapped.Add(new OpenAiRequestMessage("tool", m.Content ?? string.Empty, ToolCallId: pendingIds.Dequeue()));
            }
            else
            {
                mapped.Add(new OpenAiRequestMessage(m.Role, m.Content ?? string.Empty));
            }
        }
        return mapped;
    }

    /// <summary>
    /// Whether the server reads the sampling fields the OpenAI API does not define (<c>top_k</c>, <c>min_p</c>,
    /// <c>repeat_penalty</c>). A generic OpenAI-compatible server gets the standard ones only: a strict one refuses a
    /// request that carries a field it does not know.
    /// </summary>
    private protected virtual bool SendsExtendedSampling => false;

    // ── Proactive context-fit guard ────────────────────────────────────────────

    /// <summary>
    /// The context window (in tokens) the server currently has <em>loaded</em> for
    /// <paramref name="model"/>, or <c>null</c> when unknown — so an over-budget request can be rejected
    /// before the (expensive) call, and compaction and the gauges measure against the window that refuses.
    /// Note: this is the loaded n_ctx, not the model's <em>max</em> capability — a model can be loaded well
    /// below what it supports. <see cref="LmStudioClient"/> reads its native API instead.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two servers say it, each on its own endpoint: vLLM puts <c>max_model_len</c> on the model's entry of
    /// <c>/v1/models</c>; llama-server puts the slot's <c>n_ctx</c> under <c>default_generation_settings</c> of
    /// <c>/props</c> — the window ONE request must fit into (the context split across the slots). llama-server
    /// loads 4 096 tokens by default: without this, the configured window was the only one compaction knew, and
    /// past the loaded one every question was refused while compaction waited for a limit it never reached.
    /// </para>
    /// <para>
    /// ⚠ Never <c>meta.n_ctx_train</c> (llama-server's <c>/v1/models</c>): that is what the model was trained on,
    /// not what was loaded. And a <c>0</c> is "unknown" — the router of a multi-model llama-server answers it.
    /// </para>
    /// </remarks>
    private protected virtual async Task<int?> GetLoadedContextLengthAsync(string model, CancellationToken ct)
    {
        lock (_loadedWindowLock)
            if (_loadedWindowCache.Model == model && DateTime.UtcNow - _loadedWindowCache.At < LoadedWindowCacheLife)
                return _loadedWindowCache.Window;

        int? window;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(LoadedWindowProbeBudget);
            window = await ServedModelLengthAsync(model, cts.Token) ?? await SlotContextLengthAsync(cts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Never let the probe block the actual request: unknown is the answer every other server gives.
            Diagnostics.RecordOnce("OpenAiCompatible.LoadedWindow",
                $"The loaded context window could not be read ({ex.GetType().Name}: {ex.Message}); the configured one is used.",
                ex.GetType().FullName ?? "error");
            window = null;
        }

        lock (_loadedWindowLock) _loadedWindowCache = (model, window, DateTime.UtcNow);
        return window;
    }

    // The loaded window changes only on (un)load: cached briefly per model, so the probe does not run on every agent
    // iteration — and a server that has neither endpoint costs two GETs per half-minute, not two per request.
    private static readonly TimeSpan LoadedWindowCacheLife = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Budget of the whole probe (both endpoints). Short, because it runs ahead of a request the user is waiting for
    /// and unknown is a safe answer. ⚠ Per instance, so a test can widen it: on a loaded runner that compiles and
    /// runs two test series at once, 5 s is not 5 s, and a probe cut short reads as "the server did not say".
    /// </summary>
    internal TimeSpan LoadedWindowProbeBudget { get; set; } = TimeSpan.FromSeconds(5);
    private readonly object _loadedWindowLock = new();
    private (string Model, int? Window, DateTime At) _loadedWindowCache;

    // vLLM: `/v1/models` → data[] { id, max_model_len }.
    private async Task<int?> ServedModelLengthAsync(string model, CancellationToken ct)
    {
        using var doc = await GetJsonAsync($"{BaseV1}/models", ct);
        if (doc?.RootElement.ValueKind != JsonValueKind.Object
            || !doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var entry in data.EnumerateArray())
            if (entry.ValueKind == JsonValueKind.Object
                && entry.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                && string.Equals(id.GetString(), model, StringComparison.OrdinalIgnoreCase))
                return PositiveInt(entry, "max_model_len");
        return null;
    }

    // llama-server: `/props` → default_generation_settings.n_ctx (the slot's window). At the host root, not under /v1.
    private async Task<int?> SlotContextLengthAsync(CancellationToken ct)
    {
        var root = BaseV1.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? BaseV1[..^3] : BaseV1;
        using var doc = await GetJsonAsync($"{root}/props", ct);
        return doc?.RootElement.ValueKind == JsonValueKind.Object
               && doc.RootElement.TryGetProperty("default_generation_settings", out var settings)
               && settings.ValueKind == JsonValueKind.Object
            ? PositiveInt(settings, "n_ctx")
            : null;
    }

    private async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        AddAuth(req);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode) return null;
        await using var body = await resp.Content.ReadAsStreamAsync(ct);
        try { return await JsonDocument.ParseAsync(body, cancellationToken: ct); }
        catch (JsonException) { return null; }   // not JSON: a server that simply does not have this endpoint
    }

    private static int? PositiveInt(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) && n > 0
            ? n
            : null;

    /// <inheritdoc/>
    public override Task<int?> GetLoadedContextWindowAsync(string model, CancellationToken ct)
        => GetLoadedContextLengthAsync(model, ct);

    /// <summary>Rough request size in tokens (~4 chars/token, matching the project's estimator),
    /// counting message content, any assistant tool-call payloads, <em>and</em> the tool schemas —
    /// the agent's ~30 tool definitions add several thousand tokens and are exactly what tips a
    /// request over a modestly-sized loaded context, so they must be included.</summary>
    internal static int EstimateRequestTokens(List<ChatMessageDto> messages, List<ToolDefinition>? defs)
        => RequestSize.Of(messages, defs).Total;

    /// <summary>Returns a user-facing overflow message when <paramref name="size"/> already exceeds the
    /// model's loaded context window, else <c>null</c>. A known, positive <paramref name="loadedContext"/>
    /// is required — an unknown window never blocks a request. The message lists what the request is
    /// made of, largest part first (<see cref="RequestSize.Breakdown"/>).</summary>
    internal static string? CheckContextFit(RequestSize size, int? loadedContext)
        => loadedContext is > 0 && size.Total > loadedContext
            ? Strings.MsgContextWontFit(size.Total, loadedContext.Value, size.Breakdown())
            : null;

    // ── Chat (SSE streaming) ───────────────────────────────────────────────────

    /// <inheritdoc/>
    public override async Task<ChatTurnResult> SendChatAsync(
        string model,
        List<ChatMessageDto> messages,
        IToolRegistry tools,
        Action<string>? onToken,
        CancellationToken ct,
        TaskComplexity complexity = TaskComplexity.Normal,
        string? toolChoice = null,
        Action<string>? onThinking = null)
    {
        var base_ = BaseV1;
        if (string.IsNullOrWhiteSpace(base_))
            throw new AgentHttpException(Strings.MsgNoUrl, isTimeout: false);
        if (IsInCooldown())
            throw new AgentHttpException(Strings.MsgCircuitOpen, isTimeout: false);

        // Direct callers (code actions, inline edit, /check…) hold the GPU exactly like an agent run:
        // indexing pauses and ghost-text yields. Re-entrant inside RunAgentAsync's own lease.
        using var gpuLease = GpuScheduler.AcquireChatLease();

        var defs    = tools.Definitions.Count > 0 ? tools.Definitions.ToList() : null;

        // Proactive context-fit guard. A model loaded with a smaller context than the request needs
        // doesn't fail cleanly: it eval's the whole (oversized) prompt — minutes on a no-cache model —
        // then aborts mid-stream with a context-overflow error, which trips the orchestrator's
        // stall-retry into re-sending the same doomed request. When the server tells us the n_ctx the
        // model is actually loaded with (LM Studio's native API, vLLM's /v1/models, llama-server's
        // /props), catch the mismatch up front and fail fast with the concrete numbers. Skipped when the
        // server does not say (null), and only fires when the prompt estimate alone already overflows, so it can't
        // false-positive a request that would have fit. Ollama is a separate class, unaffected.
        var loadedCtx = await GetLoadedContextLengthAsync(model, ct);
        var size      = RequestSize.Of(messages, defs);
        if (loadedCtx is > 0 && CheckContextFit(size, loadedCtx) is { } overflowMsg)
            throw new AgentHttpException(overflowMsg, isTimeout: false);
        // The window the server serves this request in: the loaded one when it says, the configured one otherwise.
        var window   = loadedCtx is > 0 ? loadedCtx.Value : _config.ContextWindowSize;
        var maxChars = OutputBound.MaxChars(window, size.Total);

        // The sampling the model's vendor recommends, when the user leaves that on (ModelProfiles).
        var sampling = ModelProfiles.SamplingFor(model, _config);

        // A model whose chat template the server cannot render with tools gets them in its system prompt (PromptedTools).
        var promptedKey = base_ + "|" + model;
        var prompted    = defs is not null && PromptedTools.Models.ContainsKey(promptedKey);
        // ⚠ The refusal comes through any of the three doors a server refuses by — an HTTP status, an error event in
        // the stream (LM Studio answers 200 and streams "event: error"), a bare error object — and nothing has been
        // generated yet in any of them, so each one retries.
        bool FallsBackToPromptedTools(string error)
        {
            if (defs is null || prompted || !PromptedTools.IsToolRefusal(error)) return false;
            // The server cannot write this model's prompt with tools in it — its chat template fails on them, or has
            // none (Ollama's /v1). Asked again with the tools in the system prompt, for this request and every later one.
            PromptedTools.Models.TryAdd(promptedKey, 0);
            Diagnostics.Record("OpenAiCompatible.PromptedTools",
                $"{base_} refused tools for \"{model}\" (its chat template cannot carry them): its tools are described "
                + "in the system prompt instead, for the rest of this session.");
            return true;
        }
        var request = new OpenAiChatRequest(
            model,
            prompted ? PromptedTools.Rewrite(MapMessages(messages), defs!) : MapMessages(messages),
            prompted ? null : defs,
            Stream: true,
            StreamOptions: new OpenAiStreamOptions(IncludeUsage: true),
            ToolChoice: defs is not null && !prompted ? toolChoice : null,
            Stop: prompted ? PromptedTools.ResponseMarkers : null,
            Temperature: sampling?.Temperature,
            TopP:        sampling?.TopP,
            TopK:        SendsExtendedSampling ? sampling?.TopK : null,
            MinP:        SendsExtendedSampling ? sampling?.MinP : null,
            RepeatPenalty: SendsExtendedSampling ? sampling?.RepeatPenalty : null);

        var deadline = TimeSpan.FromSeconds(TimeoutFor(complexity));

        HttpResponseMessage http;
        try
        {
            using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            sendCts.CancelAfter(deadline);
            http = await PostForStreamingAsync($"{base_}/chat/completions", request, sendCts.Token, AuthHeaders());
            RecordSuccess();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            RecordFailure();
            throw new AgentHttpException(Strings.MsgTimeout(base_), isTimeout: true);
        }
        catch (HttpRequestException ex) when (ex.Message.StartsWith("HTTP ", StringComparison.Ordinal)
                                              && FallsBackToPromptedTools(ex.Message))
        {
            return await SendChatAsync(model, messages, tools, onToken, ct, complexity, toolChoice, onThinking);
        }
        catch (HttpRequestException ex) when (ex.Message.StartsWith("HTTP ", StringComparison.Ordinal))
        {
            // The server ANSWERED — with a refusal (4xx/5xx body carried by PostForStreamingAsync).
            // "Cannot reach … check the URL" sends the user to verify a URL that is fine: say
            // what the server said instead.
            RecordRefusal(ex);
            throw new AgentHttpException(MapServerError(ex.Message, base_, () => RequestSize.Of(messages, defs)), isTimeout: false);
        }
        catch (Exception ex)
        {
            RecordFailure();
            throw new AgentHttpException(Strings.MsgUnreachable(base_) + "\n" + ex.Message, isTimeout: false);
        }

        using var response = http;

        var contentBuilder   = new System.Text.StringBuilder();
        // Reasoning text is normally only previewed live, but a forced reasoning model can emit its
        // whole tool call into this channel (see the recovery fallback below) — so keep a copy.
        var reasoningBuilder = new System.Text.StringBuilder();
        // Accumulate streamed tool-call fragments (name + arguments arrive piecewise).
        var toolAcc        = new ToolCallAccumulator();
        int tokensUsed = 0, promptTokens = 0;
        // What the stream contained: the one thing missing to diagnose an empty turn.
        var chunkCount   = 0;
        string? finishReason = null;
        // What the response has streamed so far, every channel counted; past OutputBound the client stops reading.
        long received = 0;
        var  bounded  = false;
        // A call written twice in the reasoning: the model has decided and is looping — stop at the repeat.
        var  loop     = new RepeatedCallDetector();
        var  repeated = false;
        // Text going round in circles, in either channel: stopped, and said to be (StoppedRepeating).
        var  reasoningLoop = new TextLoopDetector();
        var  contentLoop   = new TextLoopDetector();
        var  looping       = false;
        // A structured call whose arguments repeat their own JSON: the third channel a model loops in.
        var  argumentsLoop = new ArgumentsLoopDetector();
        var  argsLooping   = false;
        // A structured call whose arguments can no longer become what its tool reads: stopped there, not minutes later.
        var  schemas       = new Dictionary<string, JsonElement?>(StringComparer.Ordinal);
        var  argsShape     = new ArgumentsShapeWatcher(name =>
            schemas.TryGetValue(name, out var known) ? known : schemas[name] = SchemaOf(defs, name));
        string? brokenShape = null;
        // A model whose addressed messages the server streams as content (Muse Glimmer): reasoning split from answer.
        var  envelope      = new ChannelEnvelope();

        using var bodyCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bodyCts.CancelAfter(deadline);
        // Every call's arguments are closed: the end of the turn comes at once, so the wait for it is short.
        var closedQuiet = false;

        try
        {
            using var stream = await http.Content.ReadAsStreamAsync(bodyCts.Token);
            using var reader = new System.IO.StreamReader(stream);

            string? line;
            while ((line = await reader.ReadLineAsync(bodyCts.Token)) is not null)
            {
                // Re-arm: a chunk arrived, push the deadline back — a short one once every call's arguments are closed.
                closedQuiet = argsShape.AllClosed;
                bodyCts.CancelAfter(closedQuiet ? ClosedArgumentsSilence : deadline);
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (!line.StartsWith("data:", StringComparison.Ordinal))
                {
                    // Most non-data lines are SSE comments/keep-alives — but a server can also emit a
                    // bare JSON error object here (no "data:" prefix) when it aborts after the 200 headers.
                    if (line.TrimStart().StartsWith('{') && TryExtractError(ParseErrorElement(line)) is { } bareError)
                    {
                        if (chunkCount == 0 && FallsBackToPromptedTools(bareError))
                            return await SendChatAsync(model, messages, tools, onToken, ct, complexity, toolChoice, onThinking);
                        RecordFailure();
                        throw new AgentHttpException(MapServerError(bareError, base_, () => RequestSize.Of(messages, defs)), isTimeout: false);
                    }
                    continue;
                }

                var payload = line.AsSpan(5).Trim().ToString();
                if (payload == "[DONE]") break;

                OpenAiStreamChunk? chunk;
                try   { chunk = JsonSerializer.Deserialize<OpenAiStreamChunk>(payload); }
                catch (JsonException) { continue; } // skip malformed chunks

                // A server can inject an error mid-stream after the 200 headers (LM Studio / llama.cpp
                // do this for context overflow: "request (N tokens) exceeds the available context size").
                // Surface it as a hard failure instead of letting the turn fall through as empty — an
                // empty turn would trip the orchestrator's stall-retry and re-issue the same oversized
                // request in a tight loop, ending on a blank bubble with no explanation for the user.
                if (TryExtractError(chunk?.Error ?? default) is { } serverError)
                {
                    if (chunkCount == 0 && FallsBackToPromptedTools(serverError))
                        return await SendChatAsync(model, messages, tools, onToken, ct, complexity, toolChoice, onThinking);
                    RecordFailure();
                    throw new AgentHttpException(MapServerError(serverError, base_, () => RequestSize.Of(messages, defs)), isTimeout: false);
                }

                if (chunk?.Usage is { } usage)
                {
                    promptTokens = usage.PromptTokens ?? promptTokens;
                    tokensUsed   = usage.TotalTokens
                                   ?? ((usage.PromptTokens ?? 0) + (usage.CompletionTokens ?? 0));
                }

                chunkCount++;
                if (chunk?.Choices is { Count: > 0 } fin && fin[0].FinishReason is { Length: > 0 } fr)
                    finishReason = fr;

                var delta = chunk?.Choices is { Count: > 0 } ch ? ch[0].Delta : null;
                if (delta is null) continue;

                // Servers disagree on the reasoning field name: vLLM/DeepSeek use "reasoning_content",
                // Ollama's OpenAI endpoint uses "reasoning". Surface whichever is present.
                var reasoning = !string.IsNullOrEmpty(delta.ReasoningContent) ? delta.ReasoningContent : delta.Reasoning;
                if (!string.IsNullOrEmpty(reasoning))
                {
                    reasoningBuilder.Append(reasoning);
                    onThinking?.Invoke(reasoning);
                    received += reasoning.Length;
                    // Only while nothing else has come: a structured call or printable content is a model that
                    // got out of its reasoning, and the recovery below would not read the reasoning anyway.
                    if (toolAcc.IsEmpty && contentBuilder.Length == 0 && loop.Repeats(reasoningBuilder))
                    {
                        repeated = true;
                        Diagnostics.Record("OpenAiCompatibleClient.SendChat",
                            $"Response from \"{model}\" stopped by Inferpal after {received} characters of reasoning: it "
                            + "had written the same tool call twice there. The first call is run.");
                        break;
                    }
                    if (reasoningLoop.Repeats(reasoning))
                    {
                        looping = true;
                        Diagnostics.Record("OpenAiCompatibleClient.SendChat",
                            $"Response from \"{model}\" stopped by Inferpal after {received} characters: its reasoning was "
                            + "repeating the same passage. Treated as incomplete.");
                        break;
                    }
                }

                if (!string.IsNullOrEmpty(delta.Content))
                {
                    var (answerPart, thoughtPart) = envelope.Push(delta.Content);
                    if (thoughtPart.Length > 0)
                    {
                        reasoningBuilder.Append(thoughtPart);
                        onThinking?.Invoke(thoughtPart);
                    }
                    if (answerPart.Length > 0)
                    {
                        contentBuilder.Append(answerPart);
                        onToken?.Invoke(answerPart);
                        // Tools in the prompt: a model that opens the tool's response is waiting for it — whatever
                        // follows is invented. Stops here, for a server that ignored the request's stop.
                        if (prompted && PromptedTools.ResponseStart(contentBuilder, answerPart.Length) is var at and >= 0)
                        {
                            contentBuilder.Length = at;
                            finishReason = "stop";
                            break;
                        }
                    }
                    received += delta.Content.Length;
                    if (contentLoop.Repeats(delta.Content))
                    {
                        looping = true;
                        Diagnostics.Record("OpenAiCompatibleClient.SendChat",
                            $"Response from \"{model}\" stopped by Inferpal after {received} characters: its answer was "
                            + "repeating the same passage. Treated as incomplete.");
                        break;
                    }
                }

                if (delta.ToolCalls is { Count: > 0 } tcs)
                {
                    foreach (var tc in tcs)
                    {
                        toolAcc.Add(tc.Index, tc.Id, tc.Function?.Name, tc.Function?.Arguments);
                        received += tc.Function?.Arguments?.Length ?? 0;
                        argsLooping |= argumentsLoop.Repeats(tc.Function?.Arguments ?? string.Empty);
                        brokenShape ??= argsShape.Breaks(tc.Index, tc.Function?.Name, tc.Function?.Arguments);
                    }
                    if (brokenShape is not null)
                    {
                        Diagnostics.Record("OpenAiCompatibleClient.SendChat",
                            $"Response from \"{model}\" stopped by Inferpal after {received} characters: the arguments of "
                            + $"its tool call can no longer be read by the tool ({brokenShape}). The call is refused, not run.");
                        break;
                    }
                    if (argsLooping)
                    {
                        looping = true;
                        Diagnostics.Record("OpenAiCompatibleClient.SendChat",
                            $"Response from \"{model}\" stopped by Inferpal after {received} characters: the arguments of "
                            + "its tool call kept repeating the same text. The call is refused, not run.");
                        break;
                    }
                }

                // Leaving the loop disposes the stream: the connection closes, and the server stops generating.
                if (received > maxChars)
                {
                    bounded = true;
                    Diagnostics.Record("OpenAiCompatibleClient.SendChat", OutputBound.Note(model, received, window, size.Total));
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) when (closedQuiet)
        {
            // Not a dead server: the calls were complete and the model went on writing what the server keeps to itself.
            brokenShape = ArgumentsShapeWatcher.ClosedThenSilent(ClosedArgumentsSilence);
            Diagnostics.Record("OpenAiCompatibleClient.SendChat",
                $"Response from \"{model}\" stopped by Inferpal: the arguments of its tool call were complete, then the "
                + $"server sent nothing for {ClosedArgumentsSilence.TotalSeconds:0} s. The call is refused, not run.");
        }
        catch (OperationCanceledException)
        {
            RecordFailure();
            throw new AgentHttpException(Strings.MsgTimeout(base_), isTimeout: true);
        }
        catch (AgentHttpException) { throw; } // an in-stream server error we already mapped — keep its message
        catch (HttpRequestException ex) when (ex.Message.StartsWith("HTTP ", StringComparison.Ordinal))
        {
            // The server ANSWERED — with a refusal (4xx/5xx body carried by PostForStreamingAsync).
            // "Cannot reach … check the URL" sends the user to verify a URL that is fine: say
            // what the server said instead.
            RecordRefusal(ex);
            throw new AgentHttpException(MapServerError(ex.Message, base_, () => RequestSize.Of(messages, defs)), isTimeout: false);
        }
        catch (Exception ex)
        {
            // The stream broke after the server had answered: it was reached, so not "cannot reach… check the URL".
            RecordFailure();
            throw new AgentHttpException(Strings.MsgStreamDropped(base_) + "\n" + ex.Message, isTimeout: false);
        }

        // What the envelope held back — a marker the end of the stream cut in two — is released now.
        var (lastAnswer, lastThought) = envelope.Flush();
        if (lastThought.Length > 0)
        {
            reasoningBuilder.Append(lastThought);
            onThinking?.Invoke(lastThought);
        }
        if (lastAnswer.Length > 0)
        {
            contentBuilder.Append(lastAnswer);
            onToken?.Invoke(lastAnswer);
        }

        var toolCalls   = toolAcc.Build();
        // Stopped mid-call: whatever its arguments parse into, it is not the call the model meant (the funnel refuses it).
        if (argsLooping && toolCalls is not null)
            toolCalls = toolCalls.Select(c => c with { Function = c.Function with { StoppedRepeating = true } }).ToList();
        if (brokenShape is not null && toolCalls is not null)
            toolCalls = toolCalls.Select(c => c with { Function = c.Function with { BrokenShape = brokenShape } }).ToList();
        var contentText = contentBuilder.ToString();
        // The answer stopped at the length limit, not where the model meant to end: a caller that turns it
        // into an edit must not apply it (CodeActionPipeline.Finish).
        var cut         = finishReason == "length" || bounded || looping;
        // A reasoning model routes its real turn (tool call or final answer) into the reasoning
        // channel and can leak only stray, non-printable bytes into the content channel — qwen3.6 on
        // LM Studio prefixes every turn with "\n\n". Gate the reasoning-recovery on *printable*
        // content rather than raw length: a "\n\n"-only content (Length > 0 but nothing to show) must
        // not suppress recovery, or the answer is stranded in the live "💭" status preview while the
        // chat bubble is discarded as empty.
        var contentPrintable = MarkdownParser.HasPrintableText(MarkdownParser.StripThinkTags(contentText));

        // Fallback: some models emit the tool call as plain-text JSON in the content (see OllamaClient).
        if (toolCalls is null && contentBuilder.Length > 0)
        {
            var known = new HashSet<string>(tools.Definitions.Select(d => d.Function.Name), StringComparer.Ordinal);
            var (inlineCalls, cleaned) = InlineToolCallParser.FromContent(contentText, known);
            if (inlineCalls is { Count: > 0 })
                return new ChatTurnResult(cleaned, inlineCalls, tokensUsed, promptTokens, cut, StoppedRepeating: looping);
        }

        // Last-resort: a reasoning model under tool_choice:"required" (e.g. Qwen3 on LM Studio) can
        // emit its <tool_call><function=…> call as text in the reasoning channel, leaving the content
        // channel empty (or with only non-printable scaffolding) and the structured tool_calls empty —
        // which would otherwise dead-end as an empty response. Only probe reasoning when the turn has
        // no printable content, so normal reasoning prose is never mistaken for a call (the structured
        // path always wins when present). Without a call there, the final answer itself can be in the
        // reasoning channel, and is promoted rather than dropped — the UI already streamed it as "💭".
        if (toolCalls is null && !contentPrintable && reasoningBuilder.Length > 0)
            return ReasoningOnlyTurn.Recover(reasoningBuilder.ToString(), tokensUsed, promptTokens, cut, looping,
                                             stoppedEarly: bounded || repeated || looping);

        // Empty turn under tool_choice:"required": some models/runtimes (e.g. devstral/Mistral on
        // LM Studio) return *nothing at all* — no content, no structured tool_calls, no reasoning —
        // when a call is forced, because the forcing grammar conflicts with how the model emits
        // calls. The agent loop's stall-retry would re-issue the same forced request and reproduce
        // the empty turn, so relax the constraint to the server default ("auto") and retry once
        // here, letting the model answer or call a tool freely. This is scoped to the
        // OpenAI-compatible / LM Studio wire on purpose — the Ollama client (a separate class) keeps
        // its forced-retry behaviour untouched. One-shot: the retry passes a non-"required" choice,
        // so this branch can never re-enter.
        if (toolChoice == "required" && defs is not null
            && toolCalls is null && !contentPrintable && reasoningBuilder.Length == 0)
        {
            return await SendChatAsync(model, messages, tools, onToken, ct, complexity, "auto", onThinking);
        }

        // ⚠ A turn that returns NOTHING — no text, no tool call, no reasoning, and no error — was
        // the only failure in this file leaving no trace at all: everything else throws an
        // AgentHttpException that carries its cause to the screen. The user sees "the model
        // returned no response", and nobody — not even us — can know what the server had sent.
        // Reported from a machine on the network: measuring the very server it accused showed it
        // perfectly healthy (model list, chat, streaming, 28 tools, forced tool_choice), and the
        // investigation stopped there for want of this trace. It exists now: every empty turn
        // records what was observed, readable with /diagnostics.
        if (toolCalls is null && !contentPrintable && reasoningBuilder.Length == 0)
        {
            Diagnostics.Swallow(
                $"Empty turn — model \"{model}\", server {base_}, tool_choice \"{toolChoice ?? "(none)"}\", "
                + $"{chunkCount} SSE chunk(s) received, finish_reason \"{finishReason ?? "(none)"}\", "
                + $"{contentText.Length} char(s) of non-printable content, "
                + $"{(defs?.Count ?? 0)} tool(s) declared, {promptTokens} prompt token(s)",
                new InvalidOperationException("no content, no tool call, no server error"));
        }

        return new ChatTurnResult(contentText, toolCalls, tokensUsed, promptTokens, cut, StoppedRepeating: looping);
    }

    /// <summary>The parameters schema the request declared for tool <paramref name="name"/>, as JSON; <c>null</c> when
    /// it declared none — a call to a tool nobody offered is the funnel's to refuse.</summary>
    private static JsonElement? SchemaOf(List<ToolDefinition>? defs, string name) =>
        defs?.FirstOrDefault(d => d.Function.Name == name) is { } def
            ? JsonSerializer.SerializeToElement(def.Function.Parameters)
            : null;

    /// <summary>Turns the accumulated streamed fragments into structured tool calls (arguments parsed as JSON).</summary>
    internal static List<ToolCallDto>? BuildToolCalls(
        SortedDictionary<int, (string Name, System.Text.StringBuilder Args)> acc)
    {
        if (acc.Count == 0) return null;
        var calls = new List<ToolCallDto>(acc.Count);
        foreach (var (_, slot) in acc)
        {
            // ⚠ A slot that never received a name cannot be executed — but dropping it in SILENCE
            // is the model asking for a tool and nothing happening, with no trace anywhere. When it
            // was the only call, `Build` then returns null and the turn is read as a plain text
            // answer. Same repair as the MCP sibling: a fragment we cannot turn into a call is
            // NAMED rather than dropped quietly.
            // ⚠ Said once: a gateway that does this does it on every response, and one entry per
            // response is the noise `RecordOnce` exists to prevent.
            if (string.IsNullOrEmpty(slot.Name))
            {
                Diagnostics.RecordOnce(
                    "OpenAiCompatible.ToolCalls",
                    "A streamed tool call carried arguments but never a name, so it was dropped: "
                    + "the model asked for a tool and nothing ran. The backend's stream is malformed.",
                    "unnamed-slot");
                continue;
            }
            calls.Add(new ToolCallDto(ToolCallArguments.Parse(slot.Name, slot.Args.ToString())));
        }
        return calls.Count > 0 ? calls : null;
    }

    /// <summary>Assembles streamed tool-call fragments into calls.</summary>
    /// <remarks>Fragments are keyed by <c>index</c>, but ⚠ a server that sends several COMPLETE calls
    /// on one index with distinct ids (a gateway that omits <c>index</c>, which then reads 0) must not
    /// merge them — the names overwrote each other and the arguments concatenated into
    /// <c>{…}{…}</c>. A fragment whose id differs from the one already held on its index opens a new
    /// call; a fragment without an id continues the current one.</remarks>
    internal sealed class ToolCallAccumulator
    {
        private readonly SortedDictionary<int, (string Name, System.Text.StringBuilder Args)> _slots = new();
        private readonly Dictionary<int, int>    _slotByIndex = new();
        private readonly Dictionary<int, string> _idBySlot    = new();
        private readonly Dictionary<int, int>    _indexBySlot = new();

        /// <summary>No structured call has started streaming yet.</summary>
        public bool IsEmpty => _slots.Count == 0;

        public void Add(int index, string? id, string? name, string? arguments)
        {
            var opensNewCall = !_slotByIndex.TryGetValue(index, out var key)
                || (!string.IsNullOrEmpty(id) && _idBySlot.TryGetValue(key, out var held) && held != id);
            if (opensNewCall)
            {
                key = _slots.Count;
                _slotByIndex[index] = key;
                _indexBySlot[key]   = index;
                _slots[key] = (string.Empty, new System.Text.StringBuilder());
            }
            if (!string.IsNullOrEmpty(id)) _idBySlot.TryAdd(key, id);

            var slot = _slots[key];
            if (!string.IsNullOrEmpty(name)) slot.Name = name;
            if (!string.IsNullOrEmpty(arguments)) slot.Args.Append(arguments);
            _slots[key] = slot;
        }

        /// <summary>
        /// The calls in the order the SERVER numbered them, not the order the fragments arrived.
        /// </summary>
        /// <remarks>
        /// ⚠ <c>_slots</c> is keyed by arrival rank, so a <c>SortedDictionary</c> over it sorts by
        /// arrival and by nothing else — the structure announced an ordering it did not deliver.
        /// Deltas may arrive out of order, and the batch order is not cosmetic: the orchestrator
        /// runs a read-only batch in parallel but a MUTATING one sequentially, in list order, so two
        /// edits to one file would land the wrong way round. Ordered by <c>index</c> first, then by
        /// arrival — which keeps the several-complete-calls-on-one-index case in its own order.
        /// </remarks>
        public List<ToolCallDto>? Build()
        {
            var ordered = new SortedDictionary<int, (string Name, System.Text.StringBuilder Args)>();
            var rank    = 0;
            foreach (var key in _slots.Keys
                         .OrderBy(k => _indexBySlot.TryGetValue(k, out var i) ? i : int.MaxValue)
                         .ThenBy(k => k))
                ordered[rank++] = _slots[key];
            return BuildToolCalls(ordered);
        }
    }

    /// <summary>How long the stream may stay silent once every call's arguments are closed
    /// (<see cref="ArgumentsShapeWatcher.AllClosed"/>) before the call is refused as one the model wrote past.</summary>
    internal TimeSpan ClosedArgumentsSilence { get; init; } = TimeSpan.FromSeconds(20);

    // ── Embeddings ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The waits before re-sending an embedding the server refused with a 5xx — one per retry.
    /// </summary>
    /// <remarks>
    /// ⚠ LM Studio, loading a model on demand, answers a SECOND concurrent request at once with HTTP 500 while the
    /// first waits for the load (about three seconds): the indexing pass, the re-indexing of a saved file and a query
    /// can all be in flight, and three quick 500s open the embedding breaker — an index built while the embedding model
    /// is not yet loaded comes out without a single vector. A 5xx is retried; a 4xx (a model the server does not have)
    /// never is, waiting does not change it.
    /// </remarks>
    internal TimeSpan[] EmbeddingRetryDelays { get; init; } =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)];

    /// <inheritdoc/>
    public override async Task<float[]?> GetEmbeddingAsync(string text, string model, CancellationToken ct)
    {
        var base_ = BaseV1;
        if (string.IsNullOrWhiteSpace(base_) || IsEmbeddingInCooldown()) return null;

        for (var attempt = 0; ; attempt++)
        {
            var (emb, cause, retryable) = await SendEmbeddingAsync(base_, text, model, ct);
            if (emb is not null)
            {
                RecordEmbeddingSuccess();
                NoteEmbeddingRecovered(model);
                return emb;
            }
            if (retryable && attempt < EmbeddingRetryDelays.Length)
            {
                await Task.Delay(EmbeddingRetryDelays[attempt], ct);
                continue;
            }
            RecordEmbeddingFailure();
            NoteEmbeddingFailure(model, cause);
            return null;
        }
    }

    /// <summary>One embedding request: the vector, or why there is none and whether waiting can change it.</summary>
    private async Task<(float[]? Embedding, string Cause, bool Retryable)> SendEmbeddingAsync(
        string base_, string text, string model, CancellationToken ct)
    {
        try
        {
            using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            sendCts.CancelAfter(TimeSpan.FromSeconds(30));

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{base_}/embeddings")
            {
                Content = JsonContent.Create(new OpenAiEmbeddingRequest(model, text), options: _jsonOpts),
            };
            AddAuth(req);
            using var http = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, sendCts.Token);
            if (!http.IsSuccessStatusCode)
                return (null, $"HTTP {(int)http.StatusCode}", (int)http.StatusCode >= 500);

            var result = await http.Content.ReadFromJsonAsync<OpenAiEmbeddingResponse>(_jsonOpts, sendCts.Token);
            var emb    = result?.Data is { Count: > 0 } d ? d[0].Embedding : null;
            return emb is { Length: > 0 } ? (emb, string.Empty, false) : (null, "the response held no vector", false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return (null, "no answer within 30 s", false); }
        catch (Exception ex) { return (null, Diagnostics.RootMessage(ex), false); }
    }

    // ── Connection / model listing ─────────────────────────────────────────────

    /// <inheritdoc/>
    public override async Task<bool> CheckConnectionAsync(string url, CancellationToken ct)
    {
        // The probe TRAVERSES the cooldown instead of short-circuiting on it: returning false
        // while the breaker was open meant the one call able to notice the server coming back was
        // itself blocked — the connection indicator stayed red for the full 5 minutes after a
        // recovery. A successful probe closes the circuit on the spot.
        //
        // And the status concludes nothing on its own: the body must carry "data", the property
        // that signs the OpenAI-compatible surface (see ConfirmsBackendPayload). A server — or a
        // reverse proxy — that returns 200 on every route would otherwise give a green badge for
        // any configured backend.
        var endpoint = $"{V1(url)}/models";
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            using var req = new HttpRequestMessage(HttpMethod.Get, endpoint);
            AddAuth(req);
            using var response = await _http.SendAsync(req, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                RecordCheckRefusal(response.StatusCode, response.ReasonPhrase);
                return false;
            }
            ClearCheckRefusal();
            var body = await response.Content.ReadAsStringAsync(cts.Token);
            if (ConfirmsBackendPayload(endpoint, body, "data", "OpenAiCompatible.CheckConnection", _config.Provider))
            {
                ResetCircuit();
                return true;
            }
            RecordFailure();
            return false;
        }
        // The caller gave up — not a server failure, so it must not push the breaker toward open.
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return false; }
        catch { ClearCheckRefusal(); RecordFailure(); return false; }
    }

    /// <inheritdoc/>
    public override async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct, string? url = null)
    {
        try
        {
            var result = await GetModelsAsync(V1(url ?? _config.BaseUrl), ct);
            return result?.Data?.Select(m => m.Id).ToList() ?? [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Diagnostics.Swallow("OpenAiCompatible.ListModels", ex);
            return [];
        }
    }

    /// <inheritdoc/>
    public override async Task<IReadOnlyList<InstalledModelInfo>> ListInstalledModelsAsync(CancellationToken ct, string? url = null)
    {
        try
        {
            // OpenAI-compatible /v1/models exposes no on-disk size — report 0 (VRAM estimation degrades).
            var result = await GetModelsAsync(V1(url ?? _config.BaseUrl), ct);
            return result?.Data?.Select(m => new InstalledModelInfo(m.Id, 0)).ToList() ?? [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Diagnostics.Swallow("OpenAiCompatible.ListInstalledModels", ex);
            return [];
        }
    }

    private async Task<OpenAiModelsResponse?> GetModelsAsync(string v1Base, CancellationToken ct)
    {
        // Bounded: the shared HttpClient's timeout is infinite, and a server that accepts TCP but
        // never answers froze every model dropdown behind this call.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{v1Base}/models");
        AddAuth(req);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<OpenAiModelsResponse>(_jsonOpts, cts.Token);
    }

    // ── FIM ────────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    /// <remarks>No-op in v1: Fill-in-the-Middle support varies too much across OpenAI-compatible
    /// servers, so ghost-text completions stay disabled (see <see cref="ProviderCapabilities.Fim"/>).</remarks>
    public override Task StreamFimAsync(
        string prefix,
        string suffix,
        int maxTokens,
        double temperature,
        Action<string> onToken,
        CancellationToken ct,
        string? model = null) => Task.CompletedTask;
}
