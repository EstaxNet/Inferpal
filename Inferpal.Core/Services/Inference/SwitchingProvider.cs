using Inferpal.Config;
using Inferpal.Models;

namespace Inferpal.Services.Inference;

/// <summary>
/// The inference provider an editor holds for its whole life, whose inside is rebuilt when the configured KIND of server
/// changes (Ollama, LM Studio, OpenAI-compatible): every object handed the provider at startup follows, without a restart.
/// </summary>
/// <remarks>
/// ⚠ Every member delegates EXPLICITLY, the interface's default-bodied ones included: left to their default body, they
/// would answer without reaching the inside — completions never paused, the reasoning effort dropped. Held by
/// <c>SwitchingProviderTests.EveryMember_ReachesTheInside</c>.
/// ⚠ <see cref="Follow"/> is called only when no turn runs: the agent loop asks the provider again on every call, so a
/// swap under a turn would send the rest of that turn to the new server.
/// </remarks>
internal sealed class SwitchingProvider : IInferenceProvider
{
    private readonly InferpalConfig _live;
    private readonly Func<InferpalConfig, IInferenceProvider> _build;
    // ⚠ The inside reads its OWN copy of the settings, taken at the door: a client reads its server's address and key live,
    // so handed the live settings, a save under a turn sent the rest of that turn to the new address.
    private (IInferenceProvider Client, string Kind, InferpalConfig Copy) _inner;
    private readonly object _gate = new();

    /// <summary>Builds the inside with <paramref name="build"/>, from a copy of <paramref name="config"/> taken now.</summary>
    public SwitchingProvider(InferpalConfig config, Func<InferpalConfig, IInferenceProvider> build)
    {
        _live  = config;
        _build = build;
        var copy = CopyOf(config);
        _inner = (build(copy), InferenceProviderFactory.KindOf(config.Provider), copy);
    }

    private static InferpalConfig CopyOf(InferpalConfig config) =>
        System.Text.Json.JsonSerializer.Deserialize<InferpalConfig>(config.SnapshotNow())!;

    /// <summary>The client every call goes to now.</summary>
    public IInferenceProvider Inner { get { lock (_gate) return _inner.Client; } }

    /// <summary>The kind of server the inside was built for (<see cref="InferenceProviderFactory.KindOf"/>) — not read from
    /// the inside's type, so a test's fake has one too.</summary>
    public string Kind { get { lock (_gate) return _inner.Kind; } }

    /// <summary>
    /// Takes the live settings into the inside's copy — its server's address and key included —, and rebuilds the inside
    /// when they name another kind of server than the one it was built for; returns whether the chat's server changed
    /// (kind, address or key). Called at the turn door only (<see cref="Hardware.TurnDoor"/>): a turn keeps the server it
    /// started with.
    /// </summary>
    public bool Follow()
    {
        var live = _live.SnapshotNow();
        var kind = InferenceProviderFactory.KindOf(_live.Provider);
        lock (_gate)
        {
            var mine = _inner.Copy.SnapshotNow();
            if (System.Text.Json.Nodes.JsonNode.DeepEquals(live, mine)) return false;   // the usual case: nothing moved
            var server = RoleServers.Of(_live, ServerRole.Chat) != RoleServers.Of(_inner.Copy, ServerRole.Chat);
            if (kind == _inner.Kind)
            {
                _inner.Copy.ApplyChangesFrom(_live, mine);
                return server;
            }
            var copy = CopyOf(_live);
            _inner = (_build(copy), kind, copy);
            return true;
        }
    }

    public ProviderCapabilities Capabilities => Inner.Capabilities;
    public string ServerAddress => Inner.ServerAddress;
    public bool SharesGpuWithChat => Inner.SharesGpuWithChat;
    public bool FimFailing => Inner.FimFailing;
    public bool TakeFimPaused() => Inner.TakeFimPaused();
    public string? ConnectionRefusal => Inner.ConnectionRefusal;
    public bool IsEmbeddingCircuitOpen => Inner.IsEmbeddingCircuitOpen;

    public Task<ChatTurnResult> SendChatAsync(string model, List<ChatMessageDto> messages, IToolRegistry tools,
        Action<string>? onToken, CancellationToken ct, TaskComplexity complexity = TaskComplexity.Normal,
        string? toolChoice = null, Action<string>? onThinking = null) =>
        Inner.SendChatAsync(model, messages, tools, onToken, ct, complexity, toolChoice, onThinking);

    public Task<ChatTurnResult> SendChatAsync(string model, List<ChatMessageDto> messages, IToolRegistry tools,
        Action<string>? onToken, ReasoningEffort effort, CancellationToken ct, TaskComplexity complexity = TaskComplexity.Normal,
        string? toolChoice = null, Action<string>? onThinking = null, Action<ModelWait?>? onWaiting = null) =>
        Inner.SendChatAsync(model, messages, tools, onToken, effort, ct, complexity, toolChoice, onThinking, onWaiting);

    public Task<AgentResult> RunAgentAsync(string model, List<ChatMessageDto> history, IToolRegistry tools, Action<string> onStep,
        Action<string>? onToken, CancellationToken ct, TaskComplexity complexity = TaskComplexity.Normal,
        Action<ToolExecution>? onToolExecuted = null, Action<string>? onThinking = null,
        ReasoningEffort effort = ReasoningEffort.Server, RunSteering? steering = null, Action<ModelWait?>? onWaiting = null) =>
        Inner.RunAgentAsync(model, history, tools, onStep, onToken, ct, complexity, onToolExecuted, onThinking, effort: effort,
                            steering: steering, onWaiting: onWaiting);

    public Task<float[]?> GetEmbeddingAsync(string text, string model, CancellationToken ct) => Inner.GetEmbeddingAsync(text, model, ct);
    public Task<bool> CheckConnectionAsync(string url, CancellationToken ct) => Inner.CheckConnectionAsync(url, ct);
    public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct, string? url = null) => Inner.ListModelsAsync(ct, url);
    public Task<IReadOnlyList<InstalledModelInfo>> ListInstalledModelsAsync(CancellationToken ct, string? url = null) =>
        Inner.ListInstalledModelsAsync(ct, url);
    public Task<ModelsByKind> ListModelsByKindAsync(CancellationToken ct, string? url = null) => Inner.ListModelsByKindAsync(ct, url);

    public Task StreamFimAsync(string prefix, string suffix, int maxTokens, double temperature, Action<string> onToken,
        CancellationToken ct, string? model = null) =>
        Inner.StreamFimAsync(prefix, suffix, maxTokens, temperature, onToken, ct, model);

    public bool InlineCompletionRefused(string model) => Inner.InlineCompletionRefused(model);
    public void ResetCircuit() => Inner.ResetCircuit();
    public Task<IReadOnlyList<RunningModelInfo>> GetRunningModelsAsync(CancellationToken ct) => Inner.GetRunningModelsAsync(ct);
    public Task<IReadOnlyList<RunningModelInfo>?> ReadRunningModelsAsync(CancellationToken ct) => Inner.ReadRunningModelsAsync(ct);
    public Task UnloadModelAsync(string model, CancellationToken ct) => Inner.UnloadModelAsync(model, ct);
    public Task<ModelArchInfo?> ShowModelAsync(string model, CancellationToken ct) => Inner.ShowModelAsync(model, ct);
    public Task<bool> PullModelAsync(string model, Action<string> onStatus, CancellationToken ct) => Inner.PullModelAsync(model, onStatus, ct);
    public Task<bool> DeleteModelAsync(string model, CancellationToken ct) => Inner.DeleteModelAsync(model, ct);
    public Task<int?> GetLoadedContextWindowAsync(string model, CancellationToken ct) => Inner.GetLoadedContextWindowAsync(model, ct);
}
