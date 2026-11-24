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
/// not resident VRAM), so running-model entries carry <see cref="RunningModelInfo.VramNotReported"/> and
/// every reader prints no figure: the header badge shows the name, <c>/hardware</c> and <c>/models</c> say
/// the backend does not report it (a 0 there would read as "runs on the CPU"). The manual VRAM budget (<c>/hardware &lt;gb&gt;</c>) and local auto-seed are unaffected.
/// </remarks>
internal sealed class LmStudioClient : OpenAiCompatibleClient
{
    public LmStudioClient(InferpalConfig config, ServerRole role = ServerRole.Chat) : base(config, role) { }

    /// <inheritdoc/>
    // VRAM monitoring is "true" in the sense of loaded-model awareness (state), though byte sizes
    // are unavailable; the report/badge degrade to names-only rather than being hidden. KeepAlive is
    // false: the OpenAI chat wire LM Studio inherits carries no per-request keep_alive hint.
    public override ProviderCapabilities Capabilities => ProviderCapabilities.LmStudio;

    /// <inheritdoc/>
    private protected override bool SendsExtendedSampling => true;

    /// <summary>
    /// Host root (no API suffix), tolerating a <c>/v1</c> suffix — taken from
    /// <paramref name="url"/> when a caller forces one, otherwise from the configuration.
    /// </summary>
    /// <remarks>
    /// ⚠ The <c>url</c> parameter of <c>ListModelsAsync</c> / <c>ListInstalledModelsAsync</c> is
    /// honoured here as in the two other providers (<c>url ?? ServerUrl</c>). Ignored, the
    /// native probe queries the CONFIGURED server while the OpenAI-compatible fallback queries the
    /// one it was handed — one server's models presented as another's.
    /// </remarks>
    private string HostRootFor(string? url) => ProviderProbe.RootOf(UrlOr(url));

    private string HostRoot => HostRootFor(null);


    /// <summary>Native REST API root: <c>{host}/api/v1</c> (LM Studio 0.4.0+).</summary>
    private string NativeBase => HostRoot + "/api/v1";

    /// <summary>Normalized native model entry, shape-agnostic between the v1 and v0 APIs.
    /// <paramref name="MaxContextLength"/> is the model's capability; <paramref name="LoadedContextLength"/>
    /// (only meaningful while <paramref name="Loaded"/>) is the n_ctx the running instance was loaded with —
    /// the window a request actually has to fit into. <paramref name="ReasoningOptions"/> are the reasoning settings the
    /// server publishes for the model (<c>off</c>, <c>low</c>, <c>on</c>…), <c>null</c> when it publishes none;
    /// <paramref name="TrainedForToolUse"/> whether it publishes the model as trained for tool use, <c>null</c> when it does
    /// not say.</summary>
    private readonly record struct NativeModel(
        string Id, bool Loaded, int? LoadedContextLength, int? MaxContextLength, long SizeBytes,
        IReadOnlyList<string>? ReasoningOptions = null, bool? TrainedForToolUse = null, bool? Vision = null);

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
                            LoadedContextFromInstances(m.LoadedInstances), m.MaxContextLength, m.SizeBytes ?? 0,
                            ReasoningOptionsOf(m.Capabilities), TrainedForToolUseOf(m.Capabilities), VisionOf(m.Capabilities)))
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

    /// <summary>The reasoning settings a v1 entry publishes (<c>capabilities.reasoning.allowed_options</c>), or <c>null</c>.</summary>
    internal static IReadOnlyList<string>? ReasoningOptionsOf(JsonElement? capabilities)
    {
        if (capabilities is not { ValueKind: JsonValueKind.Object } c
            || !c.TryGetProperty("reasoning", out var reasoning) || reasoning.ValueKind != JsonValueKind.Object
            || !reasoning.TryGetProperty("allowed_options", out var options) || options.ValueKind != JsonValueKind.Array)
            return null;
        return [.. options.EnumerateArray().Where(o => o.ValueKind == JsonValueKind.String).Select(o => o.GetString()!)];
    }

    /// <summary>Whether a v1 entry publishes the model as reading images (<c>capabilities.vision</c>), <c>null</c> when it does
    /// not say.</summary>
    internal static bool? VisionOf(JsonElement? capabilities) =>
        capabilities is { ValueKind: JsonValueKind.Object } c && c.TryGetProperty("vision", out var flag)
        && flag.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? flag.GetBoolean()
            : null;

    /// <summary>Whether a v1 entry publishes the model as trained for tool use (<c>capabilities.trained_for_tool_use</c>),
    /// or <c>null</c> when it publishes nothing.</summary>
    internal static bool? TrainedForToolUseOf(JsonElement? capabilities) =>
        capabilities is { ValueKind: JsonValueKind.Object } c && c.TryGetProperty("trained_for_tool_use", out var flag)
        && flag.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? flag.GetBoolean()
            : null;

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
    private (string Model, int? Ctx, DateTime Until) _ctxCache;
    /// <summary>How long "listed, not loaded yet" is kept: long enough for one request's two reads, short enough to read
    /// the window the server loads the model with at the next one. Per instance, so a test can shorten it.</summary>
    internal TimeSpan NotLoadedLife { get; set; } = TimeSpan.FromSeconds(5);

    /// <inheritdoc/>
    private protected override async Task<int?> GetLoadedContextLengthAsync(string model, CancellationToken ct)
    {
        DropCachesIfReloaded();
        lock (_ctxLock)
            if (_ctxCache.Model == model && DateTime.UtcNow < _ctxCache.Until)
                return _ctxCache.Ctx;

        int? ctx;
        try
        {
            // Read fresh, never from the shared snapshot: a model loaded since the last read would read as unknown.
            var models = await NativeSnapshotAsync(ct, fresh: true);
            // Prefer the loaded instance of this model; the loaded n_ctx is only meaningful when loaded.
            var match = models.FirstOrDefault(m => m.Loaded && IdMatches(m.Id, model));
            ctx = match.Id is not null ? match.LoadedContextLength : null;
            // ⚠ Listed but not loaded yet is an answer for a few seconds only: the next request loads it, at a window of
            // the server's choosing. Kept 30 s, it leaves a run measured against the configured window while the server
            // has loaded a smaller one. Never a window remembered from an earlier load: a reload by request takes the
            // model's default settings, and a remembered window too small refuses every request here, before the one
            // that would load the model is ever sent.
            if (match.Id is null && models.Any(m => IdMatches(m.Id, model)))
            {
                lock (_ctxLock) _ctxCache = (model, null, DateTime.UtcNow + NotLoadedLife);
                return null;
            }
        }
        // ⚠ The caller's Stop is a cancellation, never "the server did not say": caught, it was cached as unknown for the
        // next questions, which then took the configured window for the loaded one.
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Never let a probe failure block the actual request: unknown, said once.
            Diagnostics.RecordOnce("LmStudio.LoadedWindow",
                $"The loaded context window could not be read ({ex.GetType().Name}: {ex.Message}); the configured one is used.",
                ex.GetType().FullName ?? "error");
            ctx = null;
        }

        lock (_ctxLock) _ctxCache = (model, ctx, DateTime.UtcNow + LoadedWindowCacheLife);
        return ctx;
    }

    // One read of the native list serves the requests a turn makes of it — the loaded window reads it fresh and keeps it,
    // the reasoning settings take what it kept. A read that failed is kept too: a native API that hangs costs its budget
    // once, not once per question asked of it — the title's whole budget otherwise goes on probes.
    private readonly object _nativeLock = new();
    private (List<NativeModel>? Models, Exception? Failure, DateTime At)? _nativeSnapshot;

    /// <summary>The native model list — read now when <paramref name="fresh"/>, else as last read within 30 s.</summary>
    private async Task<List<NativeModel>> NativeSnapshotAsync(CancellationToken ct, bool fresh = false)
    {
        DropCachesIfReloaded();
        if (!fresh)
            lock (_nativeLock)
                if (_nativeSnapshot is { } snapshot && DateTime.UtcNow - snapshot.At < LoadedWindowCacheLife)
                    return snapshot.Models ?? throw snapshot.Failure!;
        try
        {
            var models = await GetNativeModelsAsync(ct);
            lock (_nativeLock) _nativeSnapshot = (models, null, DateTime.UtcNow);
            return models;
        }
        // The caller's Stop is not an answer of the server: never kept.
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            lock (_nativeLock) _nativeSnapshot = (null, ex, DateTime.UtcNow);
            throw;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// ⚠ A level only to a model whose native entry publishes it among its reasoning settings — <c>off</c> sent as
    /// <c>"none"</c>, <c>low</c>, <c>medium</c>, <c>high</c> as themselves. To one that does not publish it, the field is
    /// not that effort: GLM 4.7 Flash, which publishes none, reasons six times longer sent <c>"none"</c> and answers
    /// nothing sent <c>"low"</c>. And <c>/v1</c> refuses the native names <c>off</c> and <c>on</c> with HTTP 400: it takes
    /// <c>none</c>, <c>minimal</c>, <c>low</c>, <c>medium</c>, <c>high</c>, <c>xhigh</c>.
    /// </remarks>
    private protected override async Task<string?> ReasoningEffortFieldAsync(string model, ReasoningEffort effort, CancellationToken ct)
    {
        var (published, value) = effort switch
        {
            ReasoningEffort.Off    => ("off", "none"),
            ReasoningEffort.Low    => ("low", "low"),
            ReasoningEffort.Medium => ("medium", "medium"),
            ReasoningEffort.High   => ("high", "high"),
            _                      => ((string?)null, (string?)null),
        };
        if (published is null) return null;

        // Read from the native snapshot, cached briefly: a question and its title ask two models in turn.
        IReadOnlyList<string> options;
        try
        {
            var models = await NativeSnapshotAsync(ct);
            var entry  = models.FirstOrDefault(m => IdMatches(m.Id, model));
            options    = entry.ReasoningOptions ?? [];
            // ⚠ Two causes, two keys: an empty list and a read that fails are told apart, and each note is forgotten when
            // its own cause passes — one key would let whichever came first silence the other.
            if (models.Count == 0)
                // A server that lists nothing natively (a /v1-only proxy) does not say what its models take: nothing sent.
                Diagnostics.RecordOnce("LmStudio.ReasoningSettings",
                    $"LM Studio's native model list at {HostRoot} is empty or absent (/api/v1/models); no reasoning effort is sent.",
                    "empty:" + HostRoot);
            else
                Diagnostics.Forget("LmStudio.ReasoningSettings", "empty:" + HostRoot);
            // Read again: the next time it cannot be is said again.
            Diagnostics.Forget("LmStudio.ReasoningSettings", "unreadable:" + HostRoot);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Not known is not "it can": nothing is sent, said once.
            Diagnostics.RecordOnce("LmStudio.ReasoningSettings",
                $"The reasoning settings of the models at {HostRoot} could not be read ({ex.GetType().Name}: {ex.Message}); no reasoning "
                + "effort is sent.", "unreadable:" + HostRoot);
            options = [];
        }

        return options.Contains(published, StringComparer.OrdinalIgnoreCase) ? value : null;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// ⚠ A model LM Studio publishes as not trained for tool use has no call format the server reads back: forced, it
    /// switches to LM Studio's own <c>[TOOL_REQUEST]</c> form inside its reasoning, which nothing reads, while unforced it
    /// keeps the form it writes in the chat, which Inferpal reads. Not known — no native list, no flag, a list that cannot
    /// be read — keeps the call forced.
    /// </remarks>
    private protected override async Task<bool> ForcesToolCallAsync(string model, CancellationToken ct)
    {
        try
        {
            var entry = (await NativeSnapshotAsync(ct)).FirstOrDefault(m => IdMatches(m.Id, model));
            return entry.TrainedForToolUse != false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Diagnostics.Swallow("LmStudio.ForcesToolCall", ex);
            return true;
        }
    }

    // LM Studio model keys can carry a quantization/variant suffix (e.g. "qwen/qwen3-27b" vs the
    // wire id "qwen/qwen3-27b@q4"): match exactly, or one being the other plus an "@variant".
    // ⚠ Never a bare prefix: "qwen/qwen3-4b" would read the n_ctx of a loaded
    // "qwen/qwen3-4b-thinking-2507" and the context guard would refuse a request that fits.
    private static bool IdMatches(string entryId, string requested)
        => string.Equals(entryId, requested, StringComparison.OrdinalIgnoreCase)
           || entryId.StartsWith(requested + "@", StringComparison.OrdinalIgnoreCase)
           || requested.StartsWith(entryId + "@", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc/>
    /// <remarks>⚠ Asked on the legacy surface: <c>/api/v1</c> lists an instance from the start of a load, and only the
    /// <c>state</c> of <c>/api/v0</c> tells <c>loading</c> from <c>loaded</c>. The model is matched by its key and variant
    /// (<see cref="IdMatches"/>).</remarks>
    internal override Func<CancellationToken, Task<bool?>>? LoadedProbe(string model) => async ct =>
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, HostRootFor(null) + "/api/v0/models");
        AddAuth(req);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        var v0 = await resp.Content.ReadFromJsonAsync<LmStudioModelsResponse>(_jsonOpts, ct).ConfigureAwait(false);
        // Every variant of the model: one of them in memory is the model in memory, whichever is listed first.
        var entries = v0?.Data?.Where(m => !string.IsNullOrEmpty(m.Id) && IdMatches(m.Id, model)).ToList() ?? [];
        return entries.Count == 0 ? null : entries.Any(m => string.Equals(m.State, "loaded", StringComparison.OrdinalIgnoreCase));
    };

    // ── Model listing / loaded state (native /api/v1 or /api/v0 /models) ───────

    /// <inheritdoc/>
    public override async Task<IReadOnlyList<RunningModelInfo>> GetRunningModelsAsync(CancellationToken ct) =>
        await ReadRunningModelsAsync(ct) ?? [];

    /// <inheritdoc/>
    /// <remarks>⚠ The loaded state lives on the NATIVE surface only: a server that answers nothing there — a proxy that
    /// routes only <c>/v1</c> — has not said that nothing is loaded.</remarks>
    public override async Task<IReadOnlyList<RunningModelInfo>?> ReadRunningModelsAsync(CancellationToken ct)
    {
        try
        {
            var models = await GetNativeModelsAsync(ct);
            if (models.Count == 0) return null;
            // Resident VRAM bytes aren't exposed (size_bytes is the on-disk weight); no expiry timestamp.
            return models
                .Where(m => m.Loaded)
                .Select(m => new RunningModelInfo(m.Id, RunningModelInfo.VramNotReported, string.Empty))
                .ToList();
        }
        catch { return null; }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// ⚠ Two surfaces, and the connection badge does not query this one.
    /// <see cref="OpenAiCompatibleClient.CheckConnectionAsync"/> is inherited from <see cref="OpenAiCompatibleClient"/> and probes
    /// <c>{base}/v1/models</c> — the surface the chat actually talks; the listing below comes from the
    /// NATIVE API <c>{base}/api/v1|v0/models</c>, the only one carrying loaded state and size. A server
    /// that serves only the OpenAI-compatible surface — a reverse proxy routing just <c>/v1</c>, the
    /// most common shape of an LM Studio exposed on a domain — therefore answers "connected" with ZERO
    /// models, while the same server in <c>openai-compatible</c> mode lists them. And on the UI side an
    /// empty list does not read as a failure: the picker puts the configured model back and shows one
    /// entry, exactly like a backend serving a single model.
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
            var native = (await GetNativeModelsAsync(ct, url: url)).Select(m => new InstalledModelInfo(m.Id, m.SizeBytes, SeesImages: m.Vision)).ToList();
            if (native.Count > 0) return native;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Diagnostics.Swallow("LmStudioClient.ListInstalledModels(native)", ex);
        }
        return await base.ListInstalledModelsAsync(ct, url);
    }

    // ── Model management (native load / unload / download) ─────────────────────
    // Request-body shapes, as LM Studio's native API expects them:
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

    // ── The window a model is loaded with (pari 127) ────────────────────────────────

    /// <summary>A model Inferpal loaded through the native API: its name and the instance the server named.</summary>
    /// <param name="Model">The model Inferpal loaded.</param>
    /// <param name="Instance">The instance the server named it.</param>
    /// <param name="Window">The window Inferpal loaded it at; <c>0</c> when not known (a record written before it was kept).</param>
    internal sealed record OwnedLoad(string Model, string Instance, int Window = 0);

    /// <summary>
    /// The model each server has loaded by Inferpal's hand, shared by every Inferpal process of the account (both editors,
    /// the agent clients) and kept across their restarts.
    /// </summary>
    /// <remarks>⚠ LM Studio never evicts a model loaded through its API, and refuses a <c>ttl</c> on such a load: a model
    /// remembered by one process alone stays in memory once that process ends, and the next one loads another beside it.</remarks>
    internal static readonly AppDataJsonFile<Dictionary<string, OwnedLoad>> OwnedFile = new("lmstudio-loaded.json", "LmStudioLoaded");

    /// <summary>The loads a server refused, by server, model and window, and until when they are not tried again: memory
    /// freed since may let the next one through, a question every turn would not.</summary>
    private static readonly Dictionary<string, DateTime> RefusedLoads = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The servers without the native load (LM Studio before 0.4): never asked again.</summary>
    private static readonly HashSet<string> NoNativeLoad = new(StringComparer.OrdinalIgnoreCase);

    private static readonly object RefusedGate = new();

    /// <summary>One model prepared at a time in this process: two turns at once would both unload and both load.</summary>
    private static readonly SemaphoreSlim EnsureGate = new(1, 1);

    /// <summary>How long a load is given: a large model read from a slow disk takes minutes.</summary>
    internal static TimeSpan LoadBudget { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How long a refused load is not tried again.</summary>
    internal static TimeSpan RefusalLife { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Whether a question is being answered anywhere on the account, a person's card included: no model is
    /// unloaded or loaded under it. A test replaces it.</summary>
    internal static Func<bool> AnsweringAnywhere { get; set; } =
        () => GpuScheduler.IsTurnInFlight || ChatBusySignal.IsTurnInFlightElsewhere();

    /// <summary>Bumped by every load or unload this process makes: the windows and lists every client of it cached are
    /// stale then, not only the caller's.</summary>
    private static int _reloads;
    private int _reloadsSeen;

    private enum LoadOutcome { Loaded, Refused, NoNativeLoad, Unfinished }

    /// <inheritdoc/>
    /// <remarks>
    /// <para>⚠ Only a model loaded too small is reloaded. A model LM Studio loads by itself, on a request, keeps the window
    /// LM Studio gives it — often larger than any target — and stays one LM Studio evicts when another is asked for: a model
    /// not loaded yet is therefore loaded first as any request loads it, then measured. A loaded model that does not say its
    /// window, and a model the server does not list (said by <see cref="ModelCatalog"/>), are not judged. The target is
    /// never above the model's own maximum.</para>
    /// <para>⚠ A model loaded through the API is never evicted by LM Studio when another is asked for: the one Inferpal
    /// loaded (<see cref="OwnedFile"/>) is unloaded before another is prepared, and forgotten only once the server no longer
    /// lists it — never two chat models from Inferpal. Recorded before the load is sent: a load cut by Stop or by its budget
    /// may still finish on the server. The too-small instance replaced is the same model, larger.</para>
    /// <para>⚠ Nothing is unloaded or loaded while a question is answered anywhere on the account (another thread of an
    /// agent client, the other editor, a card waiting for a person): its model would go from under it.</para>
    /// </remarks>
    public override async Task<WindowLoad?> EnsureWindowAsync(string model, int target, Action<string>? onStep, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(model) || target <= 0 || AnsweringAnywhere()) return null;
        var server = HostRoot;
        lock (RefusedGate)
            if (NoNativeLoad.Contains(server)) return null;
        await EnsureGate.WaitAsync(ct);
        try
        {
            // The GPU is the chat's while a model loads: indexing and completions wait, and the machine stays awake.
            using var lease = GpuScheduler.AcquireChatLease();
            var owned = new Dictionary<string, OwnedLoad>(OwnedFile.Load(new()), StringComparer.OrdinalIgnoreCase);
            if (owned.TryGetValue(server, out var mine) && !IdMatches(mine.Model, model))
            {
                // ⚠ Gone by another hand (unloaded in LM Studio, or from the loaded-models card), or loaded again by the person
                // at another window under the same id: no longer Inferpal's, forgotten — never unloaded from under them.
                var before = await LoadedNowAsync(mine.Model, ct);
                var stillOurs = !before.Read
                                || before.Instance is not null && (mine.Window <= 0 || before.Window is not { } w || w == mine.Window);
                if (stillOurs)
                {
                    await UnloadModelAsync(mine.Instance, ct);
                    ForgetLoadedWindow();
                    // Forgotten only once gone: an unload refused or cut leaves it Inferpal's, and nothing is loaded beside it.
                    var gone = await LoadedNowAsync(mine.Model, ct);
                    if (!gone.Read || gone.Instance is not null) return null;
                }
                owned.Remove(server);
                OwnedFile.Save(owned);
            }
            var key = $"{server}|{model}|{target}";
            lock (RefusedGate)
                if (RefusedLoads.TryGetValue(key, out var until) && DateTime.UtcNow < until) return null;

            // A server whose native list cannot be read is not judged: nothing is loaded on a guess.
            var now = await LoadedNowAsync(model, ct);
            if (!now.Read || !now.Listed) return null;
            if (now.Max is > 0 and var max && max < target) target = max;
            if (now.Instance is null)
            {
                onStep?.Invoke(Localization.Strings.WaitLoadingModel + "…");
                await WarmUpAsync(model, ct);
                now = await LoadedNowAsync(model, ct);
                if (!now.Read) return null;
            }
            if (now.Instance is not null && now.Window is null) return null;
            if (now.Window >= target) return null;

            onStep?.Invoke(Localization.Strings.StatusLoadingModelWindow(model, target));
            if (now.Instance is { } instance) await UnloadModelAsync(instance, ct);
            owned[server] = new OwnedLoad(model, model, target);
            OwnedFile.Save(owned);
            var (loaded, outcome, why) = await LoadAsync(model, target, ct);
            ForgetLoadedWindow();
            switch (outcome)
            {
                case LoadOutcome.Loaded:
                    owned[server] = new OwnedLoad(model, loaded ?? model, target);
                    OwnedFile.Save(owned);
                    return new WindowLoad(target, null);
                case LoadOutcome.Unfinished:
                    // Still Inferpal's: the server may finish the load, and the next turn reads the window it has.
                    Diagnostics.Record("LmStudioClient.EnsureWindow", $"Loading \"{model}\" at {target} tokens did not finish: {why}.");
                    return null;
            }
            owned.Remove(server);
            OwnedFile.Save(owned);
            lock (RefusedGate)
            {
                if (outcome == LoadOutcome.NoNativeLoad) NoNativeLoad.Add(server);
                else RefusedLoads[key] = DateTime.UtcNow + RefusalLife;
            }
            // Unloaded for nothing: loaded again LM Studio's way, so the turn is measured against the window it has.
            await WarmUpAsync(model, ct);
            ForgetLoadedWindow();
            return outcome == LoadOutcome.Refused ? new WindowLoad(0, why) : null;
        }
        finally
        {
            EnsureGate.Release();
        }
    }

    /// <summary>Whether the native list was read and lists <paramref name="model"/>, the instance it has loaded (<c>null</c>:
    /// not loaded), the window that instance says (<c>null</c>: not said) and the model's maximum. A window read is kept
    /// as the measure's.</summary>
    private async Task<(bool Read, bool Listed, string? Instance, int? Window, int? Max)> LoadedNowAsync(string model, CancellationToken ct)
    {
        try
        {
            DropCachesIfReloaded();
            var models = await NativeSnapshotAsync(ct, fresh: true);
            if (models.Count == 0) return (false, false, null, null, null);
            var listed = models.Where(m => IdMatches(m.Id, model)).ToList();
            if (listed.Count == 0) return (true, false, null, null, null);
            var max = listed.Select(m => m.MaxContextLength).FirstOrDefault(m => m is > 0);
            var match = listed.FirstOrDefault(m => m.Loaded);
            if (match.Id is null) return (true, true, null, null, max);
            if (match.LoadedContextLength is { } window)
                lock (_ctxLock) _ctxCache = (model, window, DateTime.UtcNow + LoadedWindowCacheLife);
            // ⚠ The model's key, which LM Studio names its single instance after: a variant asked without its suffix is
            // unloaded by the key the server lists, never by the name asked.
            return (true, true, match.Id, match.LoadedContextLength, max);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Diagnostics.Swallow("LmStudioClient.LoadedNow", ex);
            return (false, false, null, null, null);
        }
    }

    /// <summary>One token asked of <paramref name="model"/>: LM Studio loads it as it loads it for any request.</summary>
    private async Task WarmUpAsync(string model, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(LoadBudget);
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{HostRoot}/v1/chat/completions")
            {
                Content = JsonContent.Create(new { model, messages = new[] { new { role = "user", content = "." } }, max_tokens = 1, stream = false },
                                             options: _jsonOpts),
            };
            AddAuth(req);
            using var resp = await _http.SendAsync(req, cts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { Diagnostics.Swallow("LmStudioClient.WarmUp", ex); }
    }

    /// <summary>Loads <paramref name="model"/> at <paramref name="window"/> tokens through the native API: the instance the
    /// server names when it loaded; refused, with the server's words; no native load at all (404); or unfinished (the
    /// budget, the connection), when the server may still be loading it.</summary>
    private async Task<(string? Instance, LoadOutcome Outcome, string? Why)> LoadAsync(string model, int window, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(LoadBudget);
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{NativeBase}/models/load")
            {
                Content = JsonContent.Create(new { model, context_length = window }, options: _jsonOpts),
            };
            AddAuth(req);
            using var resp = await _http.SendAsync(req, cts.Token);
            var body = await resp.Content.ReadAsStringAsync(cts.Token);
            if (resp.IsSuccessStatusCode) return (InstanceOf(body), LoadOutcome.Loaded, null);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return (null, LoadOutcome.NoNativeLoad, null);
            return (null, LoadOutcome.Refused, $"HTTP {(int)resp.StatusCode}" + (ErrorMessageOf(body) is { Length: > 0 } said ? ": " + said : ""));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            return (null, LoadOutcome.Unfinished, $"it did not finish within {LoadBudget.TotalMinutes:0} minutes");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return (null, LoadOutcome.Unfinished, Diagnostics.RootMessage(ex));
        }
    }

    /// <summary>The <c>instance_id</c> of a load's answer, <c>null</c> when it names none.</summary>
    private static string? InstanceOf(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("instance_id", out var id)
                   && id.ValueKind == JsonValueKind.String && id.GetString() is { Length: > 0 } named ? named : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>What a refused request is said as: the server's <c>error</c> message, else its status with the text it
    /// answered, cut short — a download refused as a load is, never its status alone when the server said why.</summary>
    private static string RefusalOf(System.Net.HttpStatusCode status, string body) =>
        TryExtractError(ParseErrorElement(body)) is { Length: > 0 } said ? SafeTruncate.Truncate(said, 300)
        : body.Trim().Length > 0 ? $"HTTP {(int)status}: {SafeTruncate.Truncate(body.Trim(), 300)}"
        : $"HTTP {(int)status}";

    /// <summary>The <c>error.message</c> of a server's answer, else its text cut short.</summary>
    private static string ErrorMessageOf(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out var e))
            {
                if (e.ValueKind == JsonValueKind.String) return SafeTruncate.Truncate(e.GetString() ?? "", 300);
                if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                    return SafeTruncate.Truncate(m.GetString() ?? "", 300);
            }
        }
        catch (JsonException) { /* not JSON: the text itself */ }
        return SafeTruncate.Truncate(body.Trim(), 300);
    }

    /// <summary>Forgets every window and list read before a load or an unload — this client's now, every other client's of
    /// this process at its next read.</summary>
    private void ForgetLoadedWindow()
    {
        Interlocked.Increment(ref _reloads);
        DropCachesIfReloaded();
    }

    /// <summary>Drops this client's cached window and list when a load or an unload happened since it read them.</summary>
    private void DropCachesIfReloaded()
    {
        var reloads = Volatile.Read(ref _reloads);
        if (reloads == _reloadsSeen) return;
        lock (_ctxLock) _ctxCache = default;
        lock (_nativeLock) _nativeSnapshot = null;
        _reloadsSeen = reloads;
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
                onStatus(RefusalOf(resp.StatusCode, body));
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
                    onStatus(RefusalOf(pollResp.StatusCode, pollBody));
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
        BeginFimAttempt();
        if (string.IsNullOrWhiteSpace(base_) || IsFimInCooldown()) return;

        // Yield the shared GPU to an in-flight chat/agent request: a delayed, now-stale ghost-text
        // suggestion is worse than none. FIM resumes once the chat turn ends. In-process lease
        // first (exact, no I/O), cross-process marker second (other editors, one GPU).
        if (FimYieldsToChat) return;

        var m    = FimModelFor(model);
        if (m is null) return;
        var spec = FimTemplate.Build(m, prefix, suffix);
        var request = new OpenAiCompletionRequest(m, spec.Prompt, maxTokens, temperature, Stream: true, Stop: FimCompletion.Stops(suffix, spec.Stop));

        await StreamInlineAsync($"{base_}/completions", request, m, CompletionText, onToken, ct);
    }
}
