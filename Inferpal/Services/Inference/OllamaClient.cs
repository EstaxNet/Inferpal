using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services.Tools;

namespace Inferpal.Services.Inference;

/// <summary>
/// <see cref="IInferenceProvider"/> backed by the native Ollama REST API (<c>/api/chat</c>,
/// <c>/api/embeddings</c>, <c>/api/generate</c>, <c>/api/tags</c>, <c>/api/show</c>, <c>/api/ps</c>,
/// <c>/api/pull</c>, <c>/api/delete</c>) with NDJSON streaming and the full hardware-aware feature set.
/// </summary>
/// <remarks>
/// Cross-cutting concerns (circuit breakers, per-task timeout, the agentic loop) live in
/// <see cref="InferenceProviderBase"/>; this class only implements the Ollama wire format.
/// Streaming requests read the response with
/// <see cref="System.Net.Http.HttpCompletionOption.ResponseHeadersRead"/>: the per-task deadline
/// (Quick/Normal/Deep) bounds time-to-first-byte and then acts as an inactivity timeout between
/// chunks — total generation time is unbounded as long as tokens keep flowing.
/// </remarks>
internal class OllamaClient : InferenceProviderBase
{
    public OllamaClient(InferpalConfig config) : base(config) { }

    /// <inheritdoc/>
    public override ProviderCapabilities Capabilities => ProviderCapabilities.Ollama;

    // ── Model keep-alive helper ────────────────────────────────────────────────
    // Returns the keep_alive string to embed in every /api/chat and /api/generate
    // request so Ollama unloads the model after the configured idle timeout.
    // Returns null when auto-unload is disabled (Ollama uses its own default, 5 min).
    private string? ComputeKeepAlive() =>
        _config.ModelAutoUnloadEnabled
            ? $"{Math.Max(1, _config.ModelIdleTimeoutMinutes)}m"
            : null;

    // ── Context window helper ──────────────────────────────────────────────────
    // Returns the per-request options to send with every /api/chat call. When the
    // user has set a context window size, num_ctx is forwarded so Ollama caps the
    // KV-cache (keeping the model in VRAM); 0 means "let Ollama use the model default".
    private ChatOptions? ComputeOptions() =>
        _config.ContextWindowSize > 0
            ? new ChatOptions(NumCtx: _config.ContextWindowSize)
            : null;

    /// <summary>
    /// Makes a single <c>POST /api/chat</c> call with streaming and returns the model's response.
    /// Does <b>not</b> execute tool calls — the caller is responsible for dispatching them.
    /// </summary>
    /// <param name="model">Ollama model name.</param>
    /// <param name="messages">Full conversation history for this turn.</param>
    /// <param name="tools">
    /// Tool definitions to expose; pass <see cref="EmptyToolRegistry.Instance"/> to disable tool calling.
    /// </param>
    /// <param name="onToken">Invoked with each streamed text token; <c>null</c> to suppress live streaming.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A <see cref="ChatTurnResult"/> with accumulated text, any tool calls, and token counts.
    /// </returns>
    /// <exception cref="AgentHttpException">Thrown on network error or timeout.</exception>
    /// <exception cref="OperationCanceledException">Re-thrown when the user cancels.</exception>
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
        var base_ = _config.BaseUrl.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(base_))
            throw new AgentHttpException(Strings.MsgNoUrl, isTimeout: false);
        if (IsInCooldown())
            throw new AgentHttpException(Strings.MsgCircuitOpen, isTimeout: false);

        var defs    = tools.Definitions.Count > 0 ? tools.Definitions.ToList() : null;
        // tool_choice is only meaningful when tools are exposed; drop it otherwise.
        var effectiveToolChoice = defs is not null ? toolChoice : null;
        // Merge consecutive same-role turns. Ollama's Go templates tolerate them, but it keeps the
        // history clean (better KV-cache reuse) and guards any model whose template is strict.
        var request = new ChatRequest(model, CoalesceConsecutiveRoles(messages), defs, Stream: true, KeepAlive: ComputeKeepAlive(), Options: ComputeOptions(), ToolChoice: effectiveToolChoice);

        // The complexity deadline bounds time-to-first-byte (connection, queue, model load,
        // prompt eval) and then acts as an INACTIVITY timeout between streamed chunks —
        // it no longer caps the total generation time, only a silent/hung server.
        var deadline = TimeSpan.FromSeconds(TimeoutFor(complexity));

        HttpResponseMessage http;
        try
        {
            using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            sendCts.CancelAfter(deadline);
            http = await PostForStreamingAsync($"{base_}/api/chat", request, sendCts.Token);
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

        using var response = http; // ensure the HttpResponseMessage is disposed on exit

        var contentBuilder = new System.Text.StringBuilder();
        List<ToolCallDto>? toolCalls = null;
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

                ChatResponse? chunk;
                try   { chunk = JsonSerializer.Deserialize<ChatResponse>(line); }
                catch (JsonException) { continue; } // skip malformed lines (partial TCP, model crash)
                if (chunk is null) continue;

                if (chunk.Message is not null)
                {
                    if (chunk.Message.ToolCalls is { Count: > 0 } tc)
                        toolCalls = tc;
                    // Reasoning models (magistral, deepseek-r1…) stream their chain-of-thought in a
                    // separate `thinking` field and only start emitting `content` once reasoning is done.
                    // Surface it live so the UI shows the model is working instead of a blank bubble.
                    var think = chunk.Message.Thinking;
                    if (!string.IsNullOrEmpty(think))
                        onThinking?.Invoke(think);
                    var token = chunk.Message.Content;
                    if (!string.IsNullOrEmpty(token))
                    {
                        contentBuilder.Append(token);
                        onToken?.Invoke(token);
                    }
                }

                if (chunk.Done)
                {
                    promptTokens = chunk.PromptEvalCount ?? 0;
                    tokensUsed   = promptTokens + (chunk.EvalCount ?? 0);
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            // No chunk for `deadline` — the server hung mid-stream.
            RecordFailure();
            throw new AgentHttpException(Strings.MsgTimeout(base_), isTimeout: true);
        }
        catch (Exception ex)
        {
            // Mid-stream network failure (connection reset, Ollama crash…).
            RecordFailure();
            throw new AgentHttpException(Strings.MsgUnreachable(base_) + "\n" + ex.Message, isTimeout: false);
        }

        // Some models emit their tool call as plain-text JSON in the content instead of in the
        // structured tool_calls field. Recover it so the orchestrator executes the tool rather
        // than printing the raw JSON as the final answer (and stopping).
        if ((toolCalls is null || toolCalls.Count == 0) && contentBuilder.Length > 0)
        {
            var (inlineCalls, cleaned) = InlineToolCallParser.TryParse(contentBuilder.ToString());
            if (inlineCalls is { Count: > 0 })
                return new ChatTurnResult(cleaned, inlineCalls, tokensUsed, promptTokens);
        }

        return new ChatTurnResult(contentBuilder.ToString(), toolCalls, tokensUsed, promptTokens);
    }

    /// <summary>Pings <c>/api/tags</c> with a 5-second timeout to verify Ollama is reachable.</summary>
    public override async Task<bool> CheckConnectionAsync(string url, CancellationToken ct)
    {
        if (IsInCooldown()) return false;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            var response = await _http.GetAsync($"{url.TrimEnd('/')}/api/tags", cts.Token);
            if (response.IsSuccessStatusCode) { RecordSuccess(); return true; }
            RecordFailure();
            return false;
        }
        catch { RecordFailure(); return false; }
    }

    /// <summary>
    /// Calls <c>POST /api/embeddings</c> and returns the embedding vector for <paramref name="text"/>.
    /// Returns <c>null</c> on any failure (model not found, timeout, Ollama unreachable, etc.)
    /// so callers can fall back gracefully without propagating exceptions.
    /// </summary>
    /// <param name="text">Text to embed (typically a code chunk of up to ~1 000 tokens).</param>
    /// <param name="model">Embedding model name (e.g. <c>"nomic-embed-text"</c>).</param>
    public override async Task<float[]?> GetEmbeddingAsync(string text, string model, CancellationToken ct)
    {
        var base_ = _config.BaseUrl.TrimEnd('/');
        // Use the dedicated embedding circuit breaker (independent of chat breaker).
        if (string.IsNullOrWhiteSpace(base_) || IsEmbeddingInCooldown()) return null;

        try
        {
            using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            sendCts.CancelAfter(TimeSpan.FromSeconds(30));

            // Ollama /api/embeddings uses "prompt" as the text field (legacy API)
            var requestBody = new { model, prompt = text };
            var http = await _http.PostAsJsonAsync(
                $"{base_}/api/embeddings", requestBody, _jsonOpts, sendCts.Token);
            http.EnsureSuccessStatusCode();

            using var stream = await http.Content.ReadAsStreamAsync(sendCts.Token);
            using var doc    = await JsonDocument.ParseAsync(stream, cancellationToken: sendCts.Token);

            if (doc.RootElement.TryGetProperty("embedding", out var embEl) &&
                embEl.ValueKind == JsonValueKind.Array)
            {
                var arr = new float[embEl.GetArrayLength()];
                int i   = 0;
                foreach (var el in embEl.EnumerateArray())
                    arr[i++] = el.GetSingle();
                RecordEmbeddingSuccess();
                return arr;
            }
            // Unexpected response shape — count as a soft failure
            RecordEmbeddingFailure();
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch
        {
            // Network error, HTTP error, JSON parse error, 30-s inner timeout, etc.
            // Record failure against the embedding breaker only — do NOT touch the chat breaker.
            RecordEmbeddingFailure();
            return null;
        }
    }

    // ── VRAM / running-model monitoring ───────────────────────────────────────

    /// <summary>
    /// Returns the list of models currently loaded in Ollama's memory via <c>GET /api/ps</c>.
    /// Each entry includes the model name, VRAM usage in bytes, and expiry timestamp.
    /// Returns an empty list on any failure (Ollama unreachable, HTTP error, etc.).
    /// </summary>
    public override async Task<IReadOnlyList<RunningModelInfo>> GetRunningModelsAsync(CancellationToken ct)
    {
        try
        {
            var base_ = _config.BaseUrl.TrimEnd('/');
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            var result = await _http.GetFromJsonAsync<RunningModelsResponse>(
                $"{base_}/api/ps", cts.Token);
            return result?.Models ?? [];
        }
        catch { return []; }
    }

    /// <summary>
    /// Forces Ollama to immediately unload <paramref name="model"/> from VRAM by posting
    /// a <c>/api/generate</c> request with <c>keep_alive: 0</c>.
    /// Silently ignores all errors (model already unloaded, Ollama unreachable, etc.).
    /// </summary>
    public override async Task UnloadModelAsync(string model, CancellationToken ct)
    {
        try
        {
            var base_ = _config.BaseUrl.TrimEnd('/');
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            // keep_alive=0 tells Ollama to evict the model immediately after this no-op request.
            var body = new { model, keep_alive = 0 };
            await _http.PostAsJsonAsync($"{base_}/api/generate", body, _jsonOpts, cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Diagnostics.Swallow("OllamaClient.UnloadModel", ex); }
    }

    /// <summary>Returns the names of all models available at the given Ollama URL.</summary>
    public override async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct, string? url = null)
    {
        try
        {
            var base_  = (url ?? _config.BaseUrl).TrimEnd('/');
            var result = await _http.GetFromJsonAsync<ModelListResponse>($"{base_}/api/tags", ct);
            return result?.Models?.Select(m => m.Name).ToList() ?? [];
        }
        catch { return []; }
    }

    /// <summary>Returns all installed models with their on-disk size (for VRAM footprint estimation).</summary>
    public override async Task<IReadOnlyList<InstalledModelInfo>> ListInstalledModelsAsync(CancellationToken ct, string? url = null)
    {
        try
        {
            var base_  = (url ?? _config.BaseUrl).TrimEnd('/');
            var result = await _http.GetFromJsonAsync<ModelListResponse>($"{base_}/api/tags", ct);
            return result?.Models?.Select(m => new InstalledModelInfo(m.Name, m.Size)).ToList() ?? [];
        }
        catch { return []; }
    }

    /// <summary>
    /// Fetches a model's architecture metadata (<c>/api/show</c> → <c>model_info</c>) needed to size
    /// the KV-cache. Returns <c>null</c> when Ollama is unreachable or the metadata is incomplete.
    /// </summary>
    public override async Task<ModelArchInfo?> ShowModelAsync(string model, CancellationToken ct)
    {
        try
        {
            var base_ = _config.BaseUrl.TrimEnd('/');
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(8));
            using var resp = await _http.PostAsJsonAsync($"{base_}/api/show", new { model }, _jsonOpts, cts.Token);
            if (!resp.IsSuccessStatusCode) return null;
            var show = await resp.Content.ReadFromJsonAsync<ShowModelResponse>(_jsonOpts, cts.Token);
            return show?.ModelInfo is { } info ? ModelCatalog.ParseArch(info) : null;
        }
        catch { return null; }
    }

    /// <summary>Pulls (downloads) a model from the Ollama registry. Streams progress via <paramref name="onStatus"/>.</summary>
    public override async Task<bool> PullModelAsync(string model, Action<string> onStatus, CancellationToken ct)
    {
        try
        {
            var base_ = _config.BaseUrl.TrimEnd('/');
            var body  = new { name = model, stream = true };
            // Headers-read so download progress streams live instead of arriving all at
            // once when the pull completes (see PostForStreamingAsync).
            using var response = await PostForStreamingAsync($"{base_}/api/pull", body, ct);

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader       = new System.IO.StreamReader(stream);

            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc    = System.Text.Json.JsonDocument.Parse(line);
                    var status       = doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() : null;
                    var completed    = doc.RootElement.TryGetProperty("completed", out var c) ? (long?)c.GetInt64() : null;
                    var total        = doc.RootElement.TryGetProperty("total",     out var t) ? (long?)t.GetInt64() : null;
                    var msg = status ?? string.Empty;
                    if (completed.HasValue && total.HasValue && total > 0)
                        msg += $" ({completed * 100 / total}%)";
                    if (!string.IsNullOrEmpty(msg)) onStatus(msg);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Diagnostics.Swallow("OllamaClient.PullStatusParse", ex); }
            }
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }

    /// <summary>Deletes a model from Ollama's local store.</summary>
    public override async Task<bool> DeleteModelAsync(string model, CancellationToken ct)
    {
        try
        {
            var base_ = _config.BaseUrl.TrimEnd('/');
            using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Delete, $"{base_}/api/delete")
            {
                Content = System.Net.Http.Json.JsonContent.Create(new { name = model }, options: _jsonOpts)
            };
            using var response = await _http.SendAsync(req, ct);
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    /// <summary>
    /// Streams a Fill-in-the-Middle completion via <c>POST /api/generate</c>.
    /// Ollama 0.5+ applies the model's native FIM template when <c>suffix</c> is provided.
    /// Calls <paramref name="onToken"/> on the calling thread for each token chunk.
    /// </summary>
    public override async Task StreamFimAsync(
        string         prefix,
        string         suffix,
        int            maxTokens,
        double         temperature,
        Action<string> onToken,
        CancellationToken ct,
        string?        model = null)
    {
        var base_ = _config.BaseUrl.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(base_) || IsInCooldown()) return;

        // Yield the shared GPU to an in-flight chat/agent request (cross-process signal): a delayed,
        // now-stale ghost-text suggestion is worse than none. FIM resumes once the chat turn ends.
        if (ChatBusySignal.IsBusy()) return;

        var request = new GenerateRequest(
            Model:     string.IsNullOrEmpty(model) ? _config.DefaultModel : model,
            Prompt:    prefix,
            Suffix:    suffix,
            Stream:    true,
            Options:   new GenerateOptions(temperature, maxTokens, Stop: ["\n\n\n"]),
            KeepAlive: ComputeKeepAlive());

        // 60 s bounds time-to-first-byte, then chunk inactivity (see SendChatAsync).
        var deadline = TimeSpan.FromSeconds(60);

        HttpResponseMessage http;
        try
        {
            using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            sendCts.CancelAfter(deadline);
            http = await PostForStreamingAsync($"{base_}/api/generate", request, sendCts.Token);
            RecordSuccess();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { RecordFailure(); return; } // internal 60-s timeout expired
        catch { RecordFailure(); return; }

        using var response = http; // ensure the HttpResponseMessage is disposed on exit
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

                var chunk = JsonSerializer.Deserialize<GenerateResponse>(line, _jsonOpts);
                if (chunk is null) continue;
                if (!string.IsNullOrEmpty(chunk.Response)) onToken(chunk.Response);
                if (chunk.Done) break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return; } // stream hung — ghost text simply stops
        catch { return; }                              // mid-stream network failure
    }
}

/// <summary>Result of a completed agentic loop run.</summary>
internal record AgentResult(
    /// <summary>Final text response from the model (may be empty if the loop was cancelled).</summary>
    string               FinalResponse,
    /// <summary>All tool calls executed during the loop, in order.</summary>
    List<ToolExecution>  Executions,
    /// <summary>Full conversation history including all tool turns, ready for the next call.</summary>
    List<ChatMessageDto> UpdatedHistory,
    int                  TokensUsed   = 0,
    int                  PromptTokens = 0);

/// <summary>A single tool invocation within an agentic loop run.</summary>
internal record ToolExecution(
    string    Name,
    string    Input,
    string    Output,
    /// <summary><c>true</c> when the tool is <c>get_diagnostics</c> and the output contains build errors — triggers the "Fix with AI" button.</summary>
    bool      HasErrors = false,
    /// <summary>Line-level diff produced by write_file / apply_diff, or <c>null</c> for all other tools.</summary>
    DiffInfo? Diff      = null);
