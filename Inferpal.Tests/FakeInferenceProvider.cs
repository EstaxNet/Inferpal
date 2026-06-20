using Inferpal.Models;
using Inferpal.Services;

namespace Inferpal.Tests;

/// <summary>
/// Reusable test double for <see cref="IInferenceProvider"/>. Every member is backed by a settable
/// value or delegate with a safe default (empty collections / success / no-op), and the mutating
/// calls (<c>delete</c>, <c>pull</c>, <c>unload</c>) are recorded for assertions. Lets command
/// handlers and VM logic that depend on the provider be unit-tested without a live backend.
/// </summary>
internal sealed class FakeInferenceProvider : IInferenceProvider
{
    // ── Scriptable state ────────────────────────────────────────────────────────
    public ProviderCapabilities Capabilities { get; set; } = ProviderCapabilities.Ollama;
    public List<string>             ModelNames { get; set; } = [];
    public List<RunningModelInfo>   Running    { get; set; } = [];
    public List<InstalledModelInfo> Installed  { get; set; } = [];
    public ChatTurnResult           ChatResult { get; set; } = new(string.Empty, null, 0, 0);
    public float[]?                 Embedding  { get; set; }
    public bool                     ConnectionOk { get; set; } = true;
    public bool                     IsEmbeddingCircuitOpen { get; set; }

    /// <summary>Result of <see cref="DeleteModelAsync"/> (keyed by model name).</summary>
    public Func<string, bool>          OnDelete { get; set; } = _ => true;
    /// <summary>Result of <see cref="PullModelAsync"/> (keyed by model name).</summary>
    public Func<string, bool>          OnPull   { get; set; } = _ => true;
    /// <summary>Architecture returned by <see cref="ShowModelAsync"/> (keyed by model name).</summary>
    public Func<string, ModelArchInfo?> OnShow  { get; set; } = _ => null;

    // ── Call recording ──────────────────────────────────────────────────────────
    public List<string> Deleted  { get; } = [];
    public List<string> Pulled   { get; } = [];
    public List<string> Unloaded { get; } = [];

    // ── IOllamaChatClient ───────────────────────────────────────────────────────
    public Task<ChatTurnResult> SendChatAsync(
        string model, List<ChatMessageDto> messages, IToolRegistry tools, Action<string>? onToken,
        CancellationToken ct, TaskComplexity complexity = TaskComplexity.Normal,
        string? toolChoice = null, Action<string>? onThinking = null) => Task.FromResult(ChatResult);

    // ── IInferenceProvider ──────────────────────────────────────────────────────
    public Task<AgentResult> RunAgentAsync(
        string model, List<ChatMessageDto> history, IToolRegistry tools, Action<string> onStep,
        Action<string>? onToken, CancellationToken ct, TaskComplexity complexity = TaskComplexity.Normal,
        Action<ToolExecution>? onToolExecuted = null, Action<string>? onThinking = null) =>
        Task.FromResult(new AgentResult(ChatResult.TextContent, [], history));

    public Task<float[]?> GetEmbeddingAsync(string text, string model, CancellationToken ct) =>
        Task.FromResult(Embedding);

    public Task<bool> CheckConnectionAsync(string url, CancellationToken ct) => Task.FromResult(ConnectionOk);

    public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct, string? url = null) =>
        Task.FromResult<IReadOnlyList<string>>(ModelNames);

    public Task<IReadOnlyList<InstalledModelInfo>> ListInstalledModelsAsync(CancellationToken ct, string? url = null) =>
        Task.FromResult<IReadOnlyList<InstalledModelInfo>>(Installed);

    public Task StreamFimAsync(string prefix, string suffix, int maxTokens, double temperature,
        Action<string> onToken, CancellationToken ct, string? model = null) => Task.CompletedTask;

    public void ResetCircuit() { }

    public Task<IReadOnlyList<RunningModelInfo>> GetRunningModelsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<RunningModelInfo>>(Running);

    public Task UnloadModelAsync(string model, CancellationToken ct)
    {
        Unloaded.Add(model);
        return Task.CompletedTask;
    }

    public Task<ModelArchInfo?> ShowModelAsync(string model, CancellationToken ct) =>
        Task.FromResult(OnShow(model));

    public Task<bool> PullModelAsync(string model, Action<string> onStatus, CancellationToken ct)
    {
        Pulled.Add(model);
        return Task.FromResult(OnPull(model));
    }

    public Task<bool> DeleteModelAsync(string model, CancellationToken ct)
    {
        Deleted.Add(model);
        return Task.FromResult(OnDelete(model));
    }
}
