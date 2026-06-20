using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Models;

namespace Inferpal.Services;

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
                var id = pendingIds.Count > 0 ? pendingIds.Dequeue() : $"call_{counter++}";
                mapped.Add(new OpenAiRequestMessage("tool", m.Content ?? string.Empty, ToolCallId: id));
            }
            else
            {
                mapped.Add(new OpenAiRequestMessage(m.Role, m.Content ?? string.Empty));
            }
        }
        return mapped;
    }

    // ── Proactive context-fit guard ────────────────────────────────────────────

    /// <summary>
    /// The context window (in tokens) the server currently has <em>loaded</em> for
    /// <paramref name="model"/>, or <c>null</c> when unknown. Generic OpenAI-compatible servers expose
    /// no such figure (default); <see cref="LmStudioClient"/> overrides it from the native API so an
    /// over-budget request can be rejected before the (expensive) call. Note: this is the loaded n_ctx,
    /// not the model's <em>max</em> capability — a model can be loaded well below what it supports.
    /// </summary>
    private protected virtual Task<int?> GetLoadedContextLengthAsync(string model, CancellationToken ct)
        => Task.FromResult<int?>(null);

    /// <summary>Rough request size in tokens (~4 chars/token, matching the project's estimator),
    /// counting message content, any assistant tool-call payloads, <em>and</em> the tool schemas —
    /// the agent's ~30 tool definitions add several thousand tokens and are exactly what tips a
    /// request over a modestly-sized loaded context, so they must be included.</summary>
    internal static int EstimateRequestTokens(List<ChatMessageDto> messages, List<ToolDefinition>? defs)
    {
        var chars = 0;
        foreach (var m in messages)
        {
            chars += m.Content?.Length ?? 0;
            if (m.ToolCalls is { Count: > 0 } calls)
                foreach (var c in calls)
                    chars += c.Function.Name.Length + c.Function.Arguments.GetRawText().Length;
        }
        if (defs is { Count: > 0 })
        {
            try   { chars += JsonSerializer.Serialize(defs).Length; }
            catch { chars += defs.Count * 400; } // fallback: rough per-tool budget if serialization fails
        }
        return chars / 4;
    }

    /// <summary>Returns a user-facing overflow message when <paramref name="estimateTokens"/> already
    /// exceeds the model's loaded context window, else <c>null</c>. A known, positive
    /// <paramref name="loadedContext"/> is required — an unknown window never blocks a request.</summary>
    internal static string? CheckContextFit(int estimateTokens, int? loadedContext)
        => loadedContext is > 0 && estimateTokens > loadedContext
            ? Strings.MsgContextWontFit(estimateTokens, loadedContext.Value)
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

        var defs    = tools.Definitions.Count > 0 ? tools.Definitions.ToList() : null;

        // Proactive context-fit guard. A model loaded with a smaller context than the request needs
        // doesn't fail cleanly: it eval's the whole (oversized) prompt — minutes on a no-cache model —
        // then aborts mid-stream with a context-overflow error, which trips the orchestrator's
        // stall-retry into re-sending the same doomed request. When the server tells us the n_ctx the
        // model is actually loaded with (LM Studio native API), catch the mismatch up front and fail
        // fast with the concrete numbers. Skipped for generic OpenAI servers (loaded context unknown →
        // null), and only fires when the prompt estimate alone already overflows, so it can't
        // false-positive a request that would have fit. Ollama is a separate class, unaffected.
        var loadedCtx = await GetLoadedContextLengthAsync(model, ct);
        if (CheckContextFit(EstimateRequestTokens(messages, defs), loadedCtx) is { } overflowMsg)
            throw new AgentHttpException(overflowMsg, isTimeout: false);

        var request = new OpenAiChatRequest(
            model,
            MapMessages(messages),
            defs,
            Stream: true,
            StreamOptions: new OpenAiStreamOptions(IncludeUsage: true),
            ToolChoice: defs is not null ? toolChoice : null);

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
        // Accumulate streamed tool-call fragments by index (name + arguments arrive piecewise).
        var toolAcc        = new SortedDictionary<int, (string Name, System.Text.StringBuilder Args)>();
        int tokensUsed = 0, promptTokens = 0;

        using var bodyCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bodyCts.CancelAfter(deadline);

        try
        {
            using var stream = await http.Content.ReadAsStreamAsync(bodyCts.Token);
            using var reader = new System.IO.StreamReader(stream);

            string? line;
            while ((line = await reader.ReadLineAsync(bodyCts.Token)) is not null)
            {
                bodyCts.CancelAfter(deadline); // re-arm: a chunk arrived, push the deadline back
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (!line.StartsWith("data:", StringComparison.Ordinal))
                {
                    // Most non-data lines are SSE comments/keep-alives — but a server can also emit a
                    // bare JSON error object here (no "data:" prefix) when it aborts after the 200 headers.
                    if (line.TrimStart().StartsWith('{') && TryExtractError(ParseErrorElement(line)) is { } bareError)
                    {
                        RecordFailure();
                        throw new AgentHttpException(MapServerError(bareError, base_), isTimeout: false);
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
                    RecordFailure();
                    throw new AgentHttpException(MapServerError(serverError, base_), isTimeout: false);
                }

                if (chunk?.Usage is { } usage)
                {
                    promptTokens = usage.PromptTokens ?? promptTokens;
                    tokensUsed   = usage.TotalTokens
                                   ?? ((usage.PromptTokens ?? 0) + (usage.CompletionTokens ?? 0));
                }

                var delta = chunk?.Choices is { Count: > 0 } ch ? ch[0].Delta : null;
                if (delta is null) continue;

                // Servers disagree on the reasoning field name: vLLM/DeepSeek use "reasoning_content",
                // Ollama's OpenAI endpoint uses "reasoning". Surface whichever is present.
                var reasoning = !string.IsNullOrEmpty(delta.ReasoningContent) ? delta.ReasoningContent : delta.Reasoning;
                if (!string.IsNullOrEmpty(reasoning))
                {
                    reasoningBuilder.Append(reasoning);
                    onThinking?.Invoke(reasoning);
                }

                if (!string.IsNullOrEmpty(delta.Content))
                {
                    contentBuilder.Append(delta.Content);
                    onToken?.Invoke(delta.Content);
                }

                if (delta.ToolCalls is { Count: > 0 } tcs)
                {
                    foreach (var tc in tcs)
                    {
                        if (!toolAcc.TryGetValue(tc.Index, out var slot))
                            slot = (string.Empty, new System.Text.StringBuilder());
                        if (!string.IsNullOrEmpty(tc.Function?.Name))
                            slot.Name = tc.Function!.Name!;
                        if (!string.IsNullOrEmpty(tc.Function?.Arguments))
                            slot.Args.Append(tc.Function!.Arguments);
                        toolAcc[tc.Index] = slot;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            RecordFailure();
            throw new AgentHttpException(Strings.MsgTimeout(base_), isTimeout: true);
        }
        catch (AgentHttpException) { throw; } // an in-stream server error we already mapped — keep its message
        catch (Exception ex)
        {
            RecordFailure();
            throw new AgentHttpException(Strings.MsgUnreachable(base_) + "\n" + ex.Message, isTimeout: false);
        }

        var toolCalls   = BuildToolCalls(toolAcc);
        var contentText = contentBuilder.ToString();
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
            var (inlineCalls, cleaned) = InlineToolCallParser.TryParse(contentText);
            if (inlineCalls is { Count: > 0 })
                return new ChatTurnResult(cleaned, inlineCalls, tokensUsed, promptTokens);
        }

        // Last-resort: a reasoning model under tool_choice:"required" (e.g. Qwen3 on LM Studio) can
        // emit its <tool_call><function=…> call as text in the reasoning channel, leaving the content
        // channel empty (or with only non-printable scaffolding) and the structured tool_calls empty —
        // which would otherwise dead-end as an empty response. Only probe reasoning when the turn has
        // no printable content, so normal reasoning prose is never mistaken for a call (the structured
        // path always wins when present).
        if (toolCalls is null && !contentPrintable && reasoningBuilder.Length > 0)
        {
            var (reasoningCalls, _) = InlineToolCallParser.TryParse(reasoningBuilder.ToString());
            if (reasoningCalls is { Count: > 0 })
                return new ChatTurnResult(string.Empty, reasoningCalls, tokensUsed, promptTokens);

            // No tool call either: a reasoning model (e.g. Qwen3 on LM Studio) can route its whole
            // turn — final answer included — into the reasoning channel, leaving content empty. Without
            // this the answer is dropped: the UI streamed it as a live "💭" thinking preview, but the
            // turn returns "" → an empty answer bubble. Surface the reasoning text as the answer so the
            // user keeps what they already saw, rather than dead-ending on an empty turn.
            return new ChatTurnResult(reasoningBuilder.ToString().Trim(), null, tokensUsed, promptTokens);
        }

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

        return new ChatTurnResult(contentText, toolCalls, tokensUsed, promptTokens);
    }

    /// <summary>Turns the accumulated streamed fragments into structured tool calls (arguments parsed as JSON).</summary>
    internal static List<ToolCallDto>? BuildToolCalls(
        SortedDictionary<int, (string Name, System.Text.StringBuilder Args)> acc)
    {
        if (acc.Count == 0) return null;
        var calls = new List<ToolCallDto>(acc.Count);
        foreach (var (_, slot) in acc)
        {
            if (string.IsNullOrEmpty(slot.Name)) continue;
            JsonElement args;
            var raw = slot.Args.Length > 0 ? slot.Args.ToString() : "{}";
            try   { args = JsonDocument.Parse(raw).RootElement.Clone(); }
            catch (JsonException) { args = JsonDocument.Parse("{}").RootElement.Clone(); }
            calls.Add(new ToolCallDto(new ToolCallFunction(slot.Name, args)));
        }
        return calls.Count > 0 ? calls : null;
    }

    // ── Server-error surfacing ─────────────────────────────────────────────────

    /// <summary>
    /// Extracts a human message from an SSE <c>error</c> element. Servers disagree on the shape:
    /// a bare string (<c>"error":"…"</c>) or an object (<c>"error":{"message":"…"}</c>); returns
    /// <c>null</c> when no error is present so the normal streaming path continues.
    /// </summary>
    /// <summary>Parses a raw JSON line and returns its <c>error</c> element, or <c>default</c>
    /// (Undefined) when the line isn't a JSON object or has no <c>error</c> member.</summary>
    internal static JsonElement ParseErrorElement(string jsonLine)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonLine);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("error", out var err)
                ? err.Clone()
                : default;
        }
        catch (JsonException) { return default; }
    }

    internal static string? TryExtractError(JsonElement error)
    {
        if (error.ValueKind == JsonValueKind.String) return error.GetString();
        if (error.ValueKind == JsonValueKind.Object)
            return error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString()
                : error.ToString();
        return null;
    }

    /// <summary>
    /// Turns a raw server error into a user-facing message. A context-overflow error (the request,
    /// inflated by the agent's tool definitions, no longer fits the model's loaded context window) is
    /// the common LM Studio failure, so it gets an actionable hint; everything else is surfaced verbatim.
    /// </summary>
    internal static string MapServerError(string serverError, string url)
    {
        var lower = serverError.ToLowerInvariant();
        var isContextOverflow =
            lower.Contains("context size") || lower.Contains("context length") ||
            lower.Contains("n_ctx") || lower.Contains("exceeds the available context");
        return isContextOverflow
            ? Strings.MsgContextOverflow(serverError)
            : Strings.MsgServerError(url, serverError);
    }

    // ── Embeddings ─────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public override async Task<float[]?> GetEmbeddingAsync(string text, string model, CancellationToken ct)
    {
        var base_ = BaseV1;
        if (string.IsNullOrWhiteSpace(base_) || IsEmbeddingInCooldown()) return null;

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
            http.EnsureSuccessStatusCode();

            var result = await http.Content.ReadFromJsonAsync<OpenAiEmbeddingResponse>(_jsonOpts, sendCts.Token);
            var emb    = result?.Data is { Count: > 0 } d ? d[0].Embedding : null;
            if (emb is { Length: > 0 })
            {
                RecordEmbeddingSuccess();
                return emb;
            }
            RecordEmbeddingFailure();
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch
        {
            RecordEmbeddingFailure();
            return null;
        }
    }

    // ── Connection / model listing ─────────────────────────────────────────────

    /// <inheritdoc/>
    public override async Task<bool> CheckConnectionAsync(string url, CancellationToken ct)
    {
        if (IsInCooldown()) return false;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{V1(url)}/models");
            AddAuth(req);
            using var response = await _http.SendAsync(req, cts.Token);
            if (response.IsSuccessStatusCode) { RecordSuccess(); return true; }
            RecordFailure();
            return false;
        }
        catch { RecordFailure(); return false; }
    }

    /// <inheritdoc/>
    public override async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct, string? url = null)
    {
        try
        {
            var result = await GetModelsAsync(V1(url ?? _config.BaseUrl), ct);
            return result?.Data?.Select(m => m.Id).ToList() ?? [];
        }
        catch { return []; }
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
        catch { return []; }
    }

    private async Task<OpenAiModelsResponse?> GetModelsAsync(string v1Base, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{v1Base}/models");
        AddAuth(req);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<OpenAiModelsResponse>(_jsonOpts, ct);
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
