using Inferpal.Models;

namespace Inferpal.Services;

/// <summary>
/// Backend-agnostic inference provider: every operation the extension needs from a local model
/// server, regardless of whether it speaks the Ollama REST API or an OpenAI-compatible one
/// (LM Studio, llama.cpp server, vLLM, Jan, LiteLLM…). Resolved once at startup by
/// <see cref="InferenceProviderFactory"/> from <c>config.Provider</c> and injected everywhere the
/// old concrete <see cref="OllamaClient"/> used to be.
/// </summary>
/// <remarks>
/// Extends <see cref="IOllamaChatClient"/> so the <see cref="AgentOrchestrator"/> keeps depending on
/// the narrow <c>SendChatAsync</c> seam (its unit tests are unaffected). Backend features that not
/// every server exposes (VRAM monitoring, model pull/delete, FIM) are advertised through
/// <see cref="Capabilities"/> and degrade to safe no-ops when unsupported.
/// </remarks>
internal interface IInferenceProvider : IOllamaChatClient
{
    /// <summary>What this backend can do beyond plain chat — used to gate Ollama-only UI/features.</summary>
    ProviderCapabilities Capabilities { get; }

    /// <inheritdoc cref="OllamaClient.RunAgentAsync"/>
    Task<AgentResult> RunAgentAsync(
        string model,
        List<ChatMessageDto> history,
        IToolRegistry tools,
        Action<string> onStep,
        Action<string>? onToken,
        CancellationToken ct,
        TaskComplexity complexity = TaskComplexity.Normal,
        Action<ToolExecution>? onToolExecuted = null,
        Action<string>? onThinking = null);

    /// <inheritdoc cref="OllamaClient.GetEmbeddingAsync"/>
    Task<float[]?> GetEmbeddingAsync(string text, string model, CancellationToken ct);

    /// <inheritdoc cref="OllamaClient.CheckConnectionAsync"/>
    Task<bool> CheckConnectionAsync(string url, CancellationToken ct);

    /// <inheritdoc cref="OllamaClient.ListModelsAsync"/>
    Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct, string? url = null);

    /// <inheritdoc cref="OllamaClient.ListInstalledModelsAsync"/>
    Task<IReadOnlyList<InstalledModelInfo>> ListInstalledModelsAsync(CancellationToken ct, string? url = null);

    /// <inheritdoc cref="OllamaClient.StreamFimAsync"/>
    Task StreamFimAsync(
        string prefix,
        string suffix,
        int maxTokens,
        double temperature,
        Action<string> onToken,
        CancellationToken ct,
        string? model = null);

    /// <summary>Resets the chat circuit breaker immediately (e.g. on manual Retry).</summary>
    void ResetCircuit();

    /// <summary><c>true</c> when the embedding circuit breaker is open (cooldown in effect).</summary>
    bool IsEmbeddingCircuitOpen { get; }

    // ── Ollama-only capabilities (no-op / empty on backends that don't support them) ──────

    /// <inheritdoc cref="OllamaClient.GetRunningModelsAsync"/>
    Task<IReadOnlyList<RunningModelInfo>> GetRunningModelsAsync(CancellationToken ct);

    /// <inheritdoc cref="OllamaClient.UnloadModelAsync"/>
    Task UnloadModelAsync(string model, CancellationToken ct);

    /// <inheritdoc cref="OllamaClient.ShowModelAsync"/>
    Task<ModelArchInfo?> ShowModelAsync(string model, CancellationToken ct);

    /// <inheritdoc cref="OllamaClient.PullModelAsync"/>
    Task<bool> PullModelAsync(string model, Action<string> onStatus, CancellationToken ct);

    /// <inheritdoc cref="OllamaClient.DeleteModelAsync"/>
    Task<bool> DeleteModelAsync(string model, CancellationToken ct);
}

/// <summary>
/// Optional backend features. The hardware-aware moat (VRAM badge, <c>/hardware</c> auto-seed,
/// <c>num_ctx</c> sizing) and model management lean on Ollama-only endpoints; an OpenAI-compatible
/// server exposes none of them, so the UI consults these flags to hide/disable what won't work.
/// </summary>
/// <param name="ModelManagement"><c>/api/pull</c> + <c>/api/delete</c> are available.</param>
/// <param name="VramMonitoring"><c>/api/ps</c> (running models / VRAM) + <c>/api/show</c> (arch) are available.</param>
/// <param name="Fim">Fill-in-the-Middle completions (ghost text) are supported.</param>
/// <param name="KeepAlive">A per-request <c>keep_alive</c> idle-unload hint is honored.</param>
internal record ProviderCapabilities(
    bool ModelManagement,
    bool VramMonitoring,
    bool Fim,
    bool KeepAlive)
{
    /// <summary>Full Ollama feature set.</summary>
    public static readonly ProviderCapabilities Ollama = new(true, true, true, true);

    /// <summary>
    /// LM Studio (native <c>/api/v1/*</c>): model management (load/unload/download) + loaded-state
    /// VRAM awareness + client-side FIM. <b>No</b> per-request <c>keep_alive</c>, though — its chat is
    /// the OpenAI <c>/v1/chat/completions</c> wire, which has no such field (context and idle-unload
    /// are configured at model load), so the keep_alive-driven auto-unload setting is hidden for it.
    /// </summary>
    public static readonly ProviderCapabilities LmStudio = new(true, true, true, false);

    /// <summary>Generic OpenAI-compatible server (llama.cpp, vLLM, Jan…): chat + embeddings only in v1.</summary>
    public static readonly ProviderCapabilities OpenAiCompatible = new(false, false, false, false);
}
