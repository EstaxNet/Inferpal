using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Models;

namespace Inferpal.Services.Inference;

/// <summary>
/// LM Studio provider. Inherits the OpenAI-compatible chat/embeddings path
/// (<see cref="OpenAiCompatibleClient"/>) and recovers the features a generic OpenAI server can't do
/// by calling LM Studio's <b>native</b> REST API (<c>/api/v1/*</c>, since LM Studio 0.4.0): listing
/// models with their loaded state and context length, loading/unloading/downloading models, and
/// (client-side) Fill-in-the-Middle ghost text.
/// </summary>
/// <remarks>
/// LM Studio's native models endpoint exists in two incompatible shapes — the 0.4.0+ <c>/api/v1/models</c>
/// (list under <c>"models"</c>, id is <c>"key"</c>, loaded state via <c>"loaded_instances"</c>, on-disk
/// <c>"size_bytes"</c>) and the legacy <c>/api/v0/models</c> (list under <c>"data"</c>, id is <c>"id"</c>,
/// <c>"state"</c>). <see cref="GetNativeModelsAsync"/> tries v1 first and falls back to v0, normalizing both.
/// The one figure neither exposes is the live per-model VRAM byte count (size_bytes is the on-disk weight,
/// not resident VRAM), so running-model entries carry <c>SizeVram = 0</c> and the header badge shows the
/// name without a GB figure. The manual VRAM budget (<c>/hardware &lt;gb&gt;</c>) and local auto-seed are unaffected.
/// </remarks>
internal sealed class LmStudioClient : OpenAiCompatibleClient
{
    public LmStudioClient(InferpalConfig config) : base(config) { }

    /// <inheritdoc/>
    // VRAM monitoring is "true" in the sense of loaded-model awareness (state), though byte sizes
    // are unavailable; the report/badge degrade to names-only rather than being hidden. KeepAlive is
    // false: the OpenAI chat wire LM Studio inherits carries no per-request keep_alive hint.
    public override ProviderCapabilities Capabilities => ProviderCapabilities.LmStudio;

    /// <summary>
    /// Host root (no API suffix), tolerating a <c>/v1</c> suffix — taken from
    /// <paramref name="url"/> when a caller forces one, otherwise from the configuration.
    /// </summary>
    /// <remarks>
    /// ⚠ The <c>url</c> parameter of <c>ListModelsAsync</c> / <c>ListInstalledModelsAsync</c> was
    /// honoured by the two other providers (<c>url ?? _config.BaseUrl</c>) and <b>silently
    /// ignored</b> here: the native probe queried the CONFIGURED server while the
    /// OpenAI-compatible fallback queried the one it was handed. No caller paid for it today —
    /// the one that passes a URL rebuilds a client whose config already carries it — but it is a
    /// trap: the obvious gesture would have listed one server's models while presenting them as
    /// another's.
    /// </remarks>
    private string HostRootFor(string? url)
    {
        var b = (url ?? _config.BaseUrl ?? string.Empty).Trim().TrimEnd('/');
        if (b.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            b = b[..^3].TrimEnd('/');
        return b;
    }

    private string HostRoot => HostRootFor(null);


    /// <summary>Native REST API root: <c>{host}/api/v1</c> (LM Studio 0.4.0+).</summary>
    private string NativeBase => HostRoot + "/api/v1";

    /// <summary>Legacy native REST API root: <c>{host}/api/v0</c> (pre-0.4.0).</summary>
    private string LegacyBase => HostRoot + "/api/v0";

    /// <summary>Normalized native model entry, shape-agnostic between the v1 and v0 APIs.
    /// <paramref name="MaxContextLength"/> is the model's capability; <paramref name="LoadedContextLength"/>
    /// (only meaningful while <paramref name="Loaded"/>) is the n_ctx the running instance was loaded with —
    /// the window a request actually has to fit into.</summary>
    private readonly record struct NativeModel(
        string Id, bool Loaded, int? LoadedContextLength, int? MaxContextLength, long SizeBytes);

    // Queries the native models endpoint, tolerating both LM Studio shapes: the 0.4.0+ v1 payload
    // ({"models":[{"key",...}]}) first, then the legacy v0 payload ({"data":[{"id",...}]}). Whichever
    // returns a non-empty list wins, so a server that only speaks one of the two still populates the
    // model selectors (the bug this guards against: an empty/partial list despite installed models).
    private async Task<List<NativeModel>> GetNativeModelsAsync(CancellationToken ct, int timeoutSeconds = 5, string? url = null)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var nativeBase = HostRootFor(url) + "/api/v1";
        var legacyBase = HostRootFor(url) + "/api/v0";

        using (var req = new HttpRequestMessage(HttpMethod.Get, $"{nativeBase}/models"))
        {
            AddAuth(req);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (resp.IsSuccessStatusCode)
            {
                var v1 = await resp.Content.ReadFromJsonAsync<LmStudioV1ModelsResponse>(_jsonOpts, cts.Token);
                if (v1?.Models is { Count: > 0 } models)
                    return models
                        .Where(m => !string.IsNullOrEmpty(m.Key))
                        .Select(m => new NativeModel(
                            m.Key!, m.LoadedInstances is { Count: > 0 },
                            LoadedContextFromInstances(m.LoadedInstances), m.MaxContextLength, m.SizeBytes ?? 0))
                        .ToList();
            }
        }

        using (var req = new HttpRequestMessage(HttpMethod.Get, $"{legacyBase}/models"))
        {
            AddAuth(req);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (resp.IsSuccessStatusCode)
            {
                var v0 = await resp.Content.ReadFromJsonAsync<LmStudioModelsResponse>(_jsonOpts, cts.Token);
                if (v0?.Data is { Count: > 0 } models)
                    return models
                        .Where(m => !string.IsNullOrEmpty(m.Id))
                        .Select(m => new NativeModel(
                            m.Id, string.Equals(m.State, "loaded", StringComparison.OrdinalIgnoreCase),
                            m.LoadedContextLength, m.MaxContextLength, 0))
                        .ToList();
            }
        }

        return [];
    }

    // Digs the loaded context window out of a v1 entry's loaded_instances. LM Studio nests the
    // running instance's n_ctx under loaded_instances[].config.context_length; tolerate a couple of
    // plausible flatter shapes too (context_length / loaded_context_length directly on the instance),
    // since the native payload is only partially documented. First match wins; null when absent.
    internal static int? LoadedContextFromInstances(List<JsonElement>? instances)
    {
        if (instances is not { Count: > 0 }) return null;
        foreach (var inst in instances)
        {
            if (inst.ValueKind != JsonValueKind.Object) continue;
            if (inst.TryGetProperty("config", out var cfg) && cfg.ValueKind == JsonValueKind.Object
                && TryReadCtx(cfg, "context_length", out var nested)) return nested;
            if (TryReadCtx(inst, "context_length", out var flat)) return flat;
            if (TryReadCtx(inst, "loaded_context_length", out var loaded)) return loaded;
        }
        return null;

        static bool TryReadCtx(JsonElement obj, string name, out int value)
        {
            value = 0;
            return obj.TryGetProperty(name, out var v)
                && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out value) && value > 0;
        }
    }

    // The loaded context window changes only on (un)load, so cache it briefly to avoid a native-API
    // round-trip on every agent iteration. Keyed by model so a mid-session model switch re-probes.
    private readonly object _ctxLock = new();
    private (string Model, int? Ctx, DateTime At) _ctxCache;

    /// <inheritdoc/>
    private protected override async Task<int?> GetLoadedContextLengthAsync(string model, CancellationToken ct)
    {
        lock (_ctxLock)
            if (_ctxCache.Model == model && DateTime.UtcNow - _ctxCache.At < TimeSpan.FromSeconds(30))
                return _ctxCache.Ctx;

        int? ctx;
        try
        {
            var models = await GetNativeModelsAsync(ct);
            // Prefer the loaded instance of this model; the loaded n_ctx is only meaningful when loaded.
            var match = models.FirstOrDefault(m => m.Loaded && IdMatches(m.Id, model));
            ctx = match.Id is not null ? match.LoadedContextLength : null;
        }
        catch { ctx = null; } // never let a probe failure block the actual request

        lock (_ctxLock) _ctxCache = (model, ctx, DateTime.UtcNow);
        return ctx;
    }

    // LM Studio model keys can carry a quantization/variant suffix (e.g. "qwen/qwen3-27b" vs the
    // wire id "qwen/qwen3-27b@q4"): match exactly, or one being the other plus an "@variant".
    // ⚠ Never a bare prefix: "qwen/qwen3-4b" would read the n_ctx of a loaded
    // "qwen/qwen3-4b-thinking-2507" and the context guard would refuse a request that fits.
    private static bool IdMatches(string entryId, string requested)
        => string.Equals(entryId, requested, StringComparison.OrdinalIgnoreCase)
           || entryId.StartsWith(requested + "@", StringComparison.OrdinalIgnoreCase)
           || requested.StartsWith(entryId + "@", StringComparison.OrdinalIgnoreCase);

    // ── Model listing / loaded state (native /api/v1 or /api/v0 /models) ───────

    /// <inheritdoc/>
    public override async Task<IReadOnlyList<RunningModelInfo>> GetRunningModelsAsync(CancellationToken ct)
    {
        try
        {
            var models = await GetNativeModelsAsync(ct);
            // Resident VRAM bytes aren't exposed (size_bytes is the on-disk weight) → 0; no expiry timestamp.
            return models
                .Where(m => m.Loaded)
                .Select(m => new RunningModelInfo(m.Id, 0, string.Empty))
                .ToList();
        }
        catch { return []; }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// ⚠ Two surfaces, and the connection badge does not query this one.
    /// <see cref="CheckConnectionAsync"/> is inherited from <see cref="OpenAiCompatibleClient"/> and probes
    /// <c>{base}/v1/models</c> — the surface the chat actually talks; the listing below comes from the
    /// NATIVE API <c>{base}/api/v1|v0/models</c>, the only one carrying loaded state and size. A server
    /// that serves only the OpenAI-compatible surface — a reverse proxy routing just <c>/v1</c>, the
    /// most common shape of an LM Studio exposed on a domain — therefore answered "connected" with ZERO
    /// models. Measured 2026-09-03 against a stand-in server serving only <c>/v1/models</c>: badge
    /// <c>true</c>, empty list; control arm in <c>openai-compatible</c> on the same server, one model. And
    /// on the UI side an empty list does not read as a failure: the picker puts the configured model back
    /// and shows one entry, exactly like a backend serving a single model.
    ///
    /// Hence the fallback to the OpenAI-compatible surface when the native one answers nothing. It cannot
    /// lie the other way: what it lists is what the chat can actually talk to.
    /// </remarks>
    public override async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct, string? url = null)
    {
        try
        {
            var native = (await GetNativeModelsAsync(ct, url: url)).Select(m => m.Id).ToList();
            if (native.Count > 0) return native;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Diagnostics.Swallow("LmStudioClient.ListModels(native)", ex);
        }
        return await base.ListModelsAsync(ct, url);
    }

    /// <inheritdoc/>
    /// <remarks>Same fallback as <see cref="ListModelsAsync"/>, and for the same measurement: without it a
    /// server serving only the OpenAI-compatible surface returns an empty installed list, so VRAM
    /// estimation and <c>/hardware</c> go silent on a reachable backend. The fallback surface exposes no
    /// size (0), which degrades the estimate — but zero models removes it.</remarks>
    public override async Task<IReadOnlyList<InstalledModelInfo>> ListInstalledModelsAsync(CancellationToken ct, string? url = null)
    {
        try
        {
            // v1 reports on-disk size_bytes (improves VRAM estimation); v0 has none → 0.
            var native = (await GetNativeModelsAsync(ct, url: url)).Select(m => new InstalledModelInfo(m.Id, m.SizeBytes)).ToList();
            if (native.Count > 0) return native;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Diagnostics.Swallow("LmStudioClient.ListInstalledModels(native)", ex);
        }
        return await base.ListInstalledModelsAsync(ct, url);
    }

    // ── Model management (native load / unload / download) ─────────────────────
    // Request-body shapes confirmed by runtime probe against LM Studio:
    //   load     POST /api/v1/models/load     { "model": "<id>" }        → { instance_id, status, … }
    //   download POST /api/v1/models/download  { "model": "<id>" }        → { job_id, status, … } (asynchronous job)
    //   unload   POST /api/v1/models/unload    { "instance_id": "<id>" }  → { instance_id }   (NOT "model"!)
    // For a single loaded instance the instance_id equals the model key (loaded_instances[].id).

    /// <inheritdoc/>
    public override async Task UnloadModelAsync(string model, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            // Unload keys off the loaded *instance* id, not the model id — sending { model } is rejected
            // with HTTP 400 "Missing required field 'instance_id'". For one instance the two coincide.
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{NativeBase}/models/unload")
            {
                Content = JsonContent.Create(new { instance_id = model }, options: _jsonOpts),
            };
            AddAuth(req);
            using var resp = await _http.SendAsync(req, cts.Token);
            // A refused unload leaves the model in VRAM: say so instead of assuming it worked.
            if (!resp.IsSuccessStatusCode)
                Diagnostics.Record("LmStudioClient.UnloadModel",
                    $"Unloading \"{model}\" was refused: HTTP {(int)resp.StatusCode}.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Diagnostics.Swallow("LmStudioClient.UnloadModel", ex); }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <c>POST /api/v1/models/download</c> answers at once with a job; the download itself is followed on
    /// <c>GET /api/v1/models/download/status/{job_id}</c> until it completes or fails (LM Studio REST API
    /// reference). Taking the POST answer as the outcome announced a model as downloaded while it was
    /// still downloading — or after the download had failed. A server that answers without a job keeps
    /// its 2xx as the signal.
    /// </remarks>
    public override async Task<bool> PullModelAsync(string model, Action<string> onStatus, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{NativeBase}/models/download")
            {
                Content = JsonContent.Create(new { model }, options: _jsonOpts),
            };
            AddAuth(req);
            using var resp = await _http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
            {
                onStatus(TryExtractError(ParseErrorElement(body)) ?? $"HTTP {(int)resp.StatusCode}");
                return false;
            }

            var job = ReadDownloadJob(body);
            if (job.Status is null) return true;   // no job in the answer: the 2xx is all there is

            while (true)
            {
                if (!string.IsNullOrEmpty(job.Message)) onStatus(job.Message);
                if (job.Status is "completed" or "already_downloaded") return true;
                if (job.Status is "failed") return false;
                if (job.JobId is null) return true;   // a job that cannot be followed: nothing better than the 2xx

                // "downloading" or "paused": the user's Stop is the way out of a paused job.
                await Task.Delay(DownloadPollInterval, ct);
                using var poll = new HttpRequestMessage(HttpMethod.Get,
                    $"{NativeBase}/models/download/status/{Uri.EscapeDataString(job.JobId)}");
                AddAuth(poll);
                using var pollResp = await _http.SendAsync(poll, ct);
                var pollBody = await pollResp.Content.ReadAsStringAsync(ct);
                if (!pollResp.IsSuccessStatusCode)
                {
                    onStatus(TryExtractError(ParseErrorElement(pollBody)) ?? $"HTTP {(int)pollResp.StatusCode}");
                    return false;
                }

                var next = ReadDownloadJob(pollBody);
                job = next with { JobId = next.JobId ?? job.JobId };
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Diagnostics.Swallow("LmStudioClient.PullModel", ex);
            return false;
        }
    }

    /// <summary>How often a download job is polled.</summary>
    private static readonly TimeSpan DownloadPollInterval = TimeSpan.FromSeconds(1);

    /// <summary>A download job as the native API reports it; <see cref="Status"/> is null when the
    /// answer carries no job.</summary>
    private readonly record struct DownloadJob(string? JobId, string? Status, string? Message);

    private static DownloadJob ReadDownloadJob(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return default;

            string? Str(string name) =>
                root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            long? Num(string name) =>
                root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)
                    ? n : null;

            var status  = Str("status");
            var message = status;
            if (status == "downloading" && Num("downloaded_bytes") is { } done && Num("total_size_bytes") is { } total
                && total > 0)
                message += $" ({done * 100 / total}%)";
            return new DownloadJob(Str("job_id"), status, message);
        }
        catch (JsonException) { return default; }
    }

    // ── Ghost text (client-side FIM via /v1/completions) ───────────────────────

    /// <inheritdoc/>
    public override async Task StreamFimAsync(
        string prefix,
        string suffix,
        int maxTokens,
        double temperature,
        Action<string> onToken,
        CancellationToken ct,
        string? model = null)
    {
        var base_ = BaseV1;
        if (string.IsNullOrWhiteSpace(base_) || IsInCooldown()) return;

        // Yield the shared GPU to an in-flight chat/agent request: a delayed, now-stale ghost-text
        // suggestion is worse than none. FIM resumes once the chat turn ends. In-process lease
        // first (exact, no I/O), cross-process marker second (other editors, one GPU).
        if (GpuScheduler.ShouldFimYield()) return;

        var m    = string.IsNullOrEmpty(model) ? _config.DefaultModel : model;
        var spec = FimTemplate.Build(m, prefix, suffix);
        var request = new OpenAiCompletionRequest(m, spec.Prompt, maxTokens, temperature, Stream: true, Stop: spec.Stop);

        var deadline = TimeSpan.FromSeconds(60);

        HttpResponseMessage http;
        try
        {
            using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            sendCts.CancelAfter(deadline);
            http = await PostForStreamingAsync($"{base_}/completions", request, sendCts.Token, AuthHeaders());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { RecordFailure(); return; }
        catch { RecordFailure(); return; }

        using var response = http;
        using var bodyCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bodyCts.CancelAfter(deadline);

        try
        {
            using var stream = await http.Content.ReadAsStreamAsync(bodyCts.Token);
            using var reader = new System.IO.StreamReader(stream);

            string? line;
            while ((line = await reader.ReadLineAsync(bodyCts.Token)) is not null)
            {
                bodyCts.CancelAfter(deadline);
                if (string.IsNullOrWhiteSpace(line)) continue;

                // Other non-data lines are SSE comments or keep-alives, but a server that aborts after
                // the 200 headers can also send a bare JSON error — as the chat loop already knows.
                var payload = line.StartsWith("data:", StringComparison.Ordinal) ? line.AsSpan(5).Trim().ToString()
                            : line.TrimStart().StartsWith('{')                   ? line
                            : null;
                if (payload is null) continue;
                if (payload == "[DONE]") break;

                // A failure inside the 200 response (a model that does not do completions) is a
                // failure: counted as a success, the breaker never opened and every pause in typing
                // sent the same failing request again.
                if (TryExtractError(ParseErrorElement(payload)) is { } serverError)
                {
                    RecordFailure();
                    Diagnostics.Record("Fim", "The server reported an error inside the stream: " + serverError);
                    return;
                }

                OpenAiCompletionChunk? chunk;
                try   { chunk = JsonSerializer.Deserialize<OpenAiCompletionChunk>(payload); }
                catch (JsonException) { continue; }

                var text = chunk?.Choices is { Count: > 0 } ch ? ch[0].Text : null;
                if (!string.IsNullOrEmpty(text)) onToken(text);
            }
            // Success is a stream that ended cleanly, not headers that arrived.
            RecordSuccess();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return; }
        catch { return; }
    }
}
