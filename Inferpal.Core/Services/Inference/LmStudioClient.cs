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

    /// <summary>Host root (no API suffix), tolerating a <c>/v1</c> suffix on BaseUrl.</summary>
    private string HostRoot
    {
        get
        {
            var b = (_config.BaseUrl ?? string.Empty).Trim().TrimEnd('/');
            if (b.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
                b = b[..^3].TrimEnd('/');
            return b;
        }
    }

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
    private async Task<List<NativeModel>> GetNativeModelsAsync(CancellationToken ct, int timeoutSeconds = 5)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        using (var req = new HttpRequestMessage(HttpMethod.Get, $"{NativeBase}/models"))
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

        using (var req = new HttpRequestMessage(HttpMethod.Get, $"{LegacyBase}/models"))
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
    // wire id "qwen/qwen3-27b@q4"); match exactly, then tolerate one being a prefix of the other.
    private static bool IdMatches(string entryId, string requested)
        => string.Equals(entryId, requested, StringComparison.OrdinalIgnoreCase)
           || entryId.StartsWith(requested, StringComparison.OrdinalIgnoreCase)
           || requested.StartsWith(entryId, StringComparison.OrdinalIgnoreCase);

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
    public override async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct, string? url = null)
    {
        try   { return (await GetNativeModelsAsync(ct)).Select(m => m.Id).ToList(); }
        catch { return []; }
    }

    /// <inheritdoc/>
    public override async Task<IReadOnlyList<InstalledModelInfo>> ListInstalledModelsAsync(CancellationToken ct, string? url = null)
    {
        try
        {
            // v1 reports on-disk size_bytes (improves VRAM estimation); v0 has none → 0.
            return (await GetNativeModelsAsync(ct)).Select(m => new InstalledModelInfo(m.Id, m.SizeBytes)).ToList();
        }
        catch { return []; }
    }

    // ── Model management (native load / unload / download) ─────────────────────
    // Request-body shapes confirmed by runtime probe against LM Studio (2026-06-14):
    //   load     POST /api/v1/models/load     { "model": "<id>" }        → { instance_id, status, … }
    //   download POST /api/v1/models/download  { "model": "<id>" }        → JSON (no streamed progress)
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
            await _http.SendAsync(req, cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Diagnostics.Swallow("LmStudioClient.UnloadModel", ex); }
    }

    /// <inheritdoc/>
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

            // The native download is request/response JSON, and v1 exposes no confirmed progress-polling
            // endpoint (runtime probe 2026-06-14: /models/download/status → 404), so we rely on the POST
            // response alone: an error object means failure; otherwise surface any status it carries and
            // report success. No phantom poll loop that would falsely claim instant completion.
            if (!resp.IsSuccessStatusCode)
            {
                onStatus(TryExtractError(ParseErrorElement(body)) ?? $"HTTP {(int)resp.StatusCode}");
                return false;
            }

            try
            {
                using var doc = JsonDocument.Parse(body);
                if (TryReadDownloadStatus(doc.RootElement, model, out var message, out _)
                    && !string.IsNullOrEmpty(message))
                    onStatus(message);
            }
            catch (JsonException) { /* opaque body — the 2xx is the signal */ }
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }

    // Defensive parse of the (under-documented) download status shape: surfaces a human status string
    // and whether the download has finished. Tolerates several plausible field layouts (top-level
    // object or a { "downloads": [ … ] } list).
    private static bool TryReadDownloadStatus(JsonElement root, string model, out string message, out bool finished)
    {
        message  = string.Empty;
        finished = false;

        // Accept either a top-level object or a { "downloads": [ … ] } list.
        var entry = root;
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("downloads", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            entry = arr.EnumerateArray().FirstOrDefault(
                e => e.TryGetProperty("model", out var mm) && mm.GetString() == model);
            if (entry.ValueKind == JsonValueKind.Undefined) return false;
        }

        if (entry.ValueKind != JsonValueKind.Object) return false;

        var status = entry.TryGetProperty("status", out var s) ? s.GetString() : null;
        double? progress = entry.TryGetProperty("progress", out var p) && p.ValueKind == JsonValueKind.Number
            ? p.GetDouble() : null;

        message = status ?? string.Empty;
        if (progress is { } pr) message += $" ({pr * 100:0}%)";
        finished = (status is "completed" or "done" or "finished") || progress >= 1.0;
        return true;
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

        // Yield the shared GPU to an in-flight chat/agent request (cross-process signal): a delayed,
        // now-stale ghost-text suggestion is worse than none.
        if (ChatBusySignal.IsBusy()) return;

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
            RecordSuccess();
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
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

                var payload = line.AsSpan(5).Trim().ToString();
                if (payload == "[DONE]") break;

                OpenAiCompletionChunk? chunk;
                try   { chunk = JsonSerializer.Deserialize<OpenAiCompletionChunk>(payload); }
                catch (JsonException) { continue; }

                var text = chunk?.Choices is { Count: > 0 } ch ? ch[0].Text : null;
                if (!string.IsNullOrEmpty(text)) onToken(text);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return; }
        catch { return; }
    }
}
