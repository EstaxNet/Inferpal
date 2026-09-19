using Inferpal.Config;
using Inferpal.Models;

namespace Inferpal.Services.Hardware;

/// <summary>
/// Background service that periodically polls <c>GET /api/ps</c> to monitor which Ollama
/// models are currently loaded in VRAM and how much GPU memory they consume.
/// </summary>
/// <remarks>
/// <para>
/// The actual model auto-unload mechanism is handled by passing a <c>keep_alive</c> header
/// in every <c>/api/chat</c> and <c>/api/generate</c> request (see
/// <see cref="OllamaClient.ComputeKeepAlive"/>). Ollama evicts models autonomously once the
/// timer expires — no explicit unload call is needed in the normal case.
/// </para>
/// <para>
/// This service exists for <em>monitoring</em>: it makes the current VRAM usage visible in
/// the tool-window header so the developer knows whether their GPU memory is occupied.
/// </para>
/// Poll interval: 60 seconds (first tick after 30 s so the window shows data quickly).
/// </remarks>
internal sealed class ModelLifetimeService : IDisposable
{
    private readonly IInferenceProvider _client;
    private readonly InferpalConfig _config;
    private readonly Timer             _timer;
    // volatile: read by thread-pool timer ticks after Dispose runs on another thread.
    private volatile bool              _disposed;

    // ── Public state ──────────────────────────────────────────────────────────

    /// <summary>
    /// Most-recent snapshot of models currently loaded in Ollama (may be stale by up to 60 s).
    /// Empty list when Ollama is unreachable or no models are loaded.
    /// </summary>
    // volatile: written on thread-pool (RefreshAsync), read on the UI thread.
    // Reference assignment is atomic in .NET, but volatile ensures the store-fence so
    // the new list reference is visible to readers without a full memory barrier.
    private volatile IReadOnlyList<RunningModelInfo> _currentModels = [];
    public IReadOnlyList<RunningModelInfo> CurrentModels => _currentModels;

    /// <summary>
    /// Raised on a thread-pool thread whenever the model list is refreshed.
    /// Subscribers must marshal to their own synchronization context as needed.
    /// </summary>
    public event Action<IReadOnlyList<RunningModelInfo>>? ModelsRefreshed;

    public ModelLifetimeService(IInferenceProvider client, InferpalConfig config)
    {
        _client = client;
        _config = config;

        // First tick after 30 s so the window shows VRAM info shortly after opening.
        _timer = new Timer(OnTick, null,
            dueTime:  TimeSpan.FromSeconds(30),
            period:   TimeSpan.FromSeconds(60));
    }

    // ── Timer callback ────────────────────────────────────────────────────────

    // The timer fires a synchronous callback; we dispatch async work via Task.Run
    // to avoid "async void" which would crash the process on unhandled exceptions.
    private void OnTick(object? _)
    {
        if (_disposed) return;
        _ = Task.Run(RefreshAsync);
    }

    /// <summary>One poll: reads the state, then publishes it — whatever it is.</summary>
    /// <remarks>
    /// ⚠ <b>Publishing only on success makes the badge lie.</b> A failed poll that leaves
    /// <c>_currentModels</c> untouched and raises no event keeps the header asserting that models
    /// occupy the GPU long after they do not. The commonest path is not even a failure: switching
    /// the backend to an OpenAI-compatible server turns <c>VramMonitoring</c> off, and an early
    /// return leaves the previous Ollama models on screen for good. Empty is not "nothing is
    /// loaded", it is "I cannot say" — and that is the only honest badge, for the same reason a
    /// wrong number costs more than a silence everywhere else in this product.
    /// </remarks>
    internal async Task RefreshAsync()
    {
        if (_disposed) return;

        var models = await PollAsync().ConfigureAwait(false);
        if (_disposed) return;

        _currentModels = models;
        // Outside the poll's try: a subscriber that throws is its own failure, not the backend's.
        try { ModelsRefreshed?.Invoke(models); }
        catch (Exception ex) { Diagnostics.Swallow("ModelLifetimeService.Notify", ex); }
    }

    /// <summary>What is loaded right now, or an empty list when this backend cannot say.</summary>
    private async Task<IReadOnlyList<RunningModelInfo>> PollAsync()
    {
        // Backends without VRAM monitoring (OpenAI-compatible) expose no running-model endpoint.
        if (!_client.Capabilities.VramMonitoring) return [];
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            return await _client.GetRunningModelsAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return []; }
        catch (Exception ex)
        {
            // ⚠ Diagnostics, not Debug.WriteLine: the Release build strips the latter, so the only
            // explanation for an empty badge did not exist in the published product — and
            // /diagnostics is the channel the user opens precisely to find out.
            Diagnostics.Swallow("ModelLifetimeService.Poll", ex);
            return [];
        }
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Dispose();
    }
}
