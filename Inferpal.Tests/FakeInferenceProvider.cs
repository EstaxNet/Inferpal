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

    /// <summary>The server failure messages must name (see MsgEmptyResponseFrom).</summary>
    public string ServerAddress { get; set; } = "http://localhost:1234";
    /// <summary>False = a server on another machine than the chat's: background work there does not wait for a chat.</summary>
    public bool SharesGpuWithChat { get; set; } = true;
    public List<string>             ModelNames { get; set; } = [];
    public List<RunningModelInfo>   Running    { get; set; } = [];
    public List<InstalledModelInfo> Installed  { get; set; } = [];
    public ChatTurnResult           ChatResult { get; set; } = new(string.Empty, null, 0, 0);
    public float[]?                 Embedding  { get; set; }
    public bool                     ConnectionOk { get; set; } = true;
    public bool                     IsEmbeddingCircuitOpen { get; set; }

    /// <summary>Optional override of <see cref="SendChatAsync(string, List{ChatMessageDto}, IToolRegistry, Action{string}, ReasoningEffort, CancellationToken, TaskComplexity, string, Action{string}, Action{string})"/> (token callback + ct);
    /// null keeps the default constant <see cref="ChatResult"/>. Lets protocol tests
    /// stream tokens and honour cancellation.</summary>
    public Func<Action<string>?, CancellationToken, Task<ChatTurnResult>>? OnChat { get; set; }

    /// <summary>Richer override of <see cref="SendChatAsync(string, List{ChatMessageDto}, IToolRegistry, Action{string}, ReasoningEffort, CancellationToken, TaskComplexity, string, Action{string}, Action{string})"/> for tests that need the prompt and
    /// the exposed tools (e.g. the /bench runner) — checked before <see cref="OnChat"/>.</summary>
    public Func<string, List<ChatMessageDto>, IToolRegistry, Action<string>?, Task<ChatTurnResult>>? OnChatRequest { get; set; }

    /// <summary>
    /// Drives the REASONING channel: called with the turn's <c>onThinking</c>, if there is one.
    /// Without this lever, no test can see that the host relayed one notification PER DELTA, with no
    /// throttle, to a client that threw the text away.
    /// </summary>
    public Action<Action<string>>? DriveThinking { get; set; }

    /// <summary>Drives the waiting line a turn was given (what a client says while the server reads the prompt), so a
    /// test sees where the caller shows it, and what it shows once the first token came (<c>null</c>).</summary>
    public Action<Action<string?>>? DriveWaiting { get; set; }

    /// <summary>Scripted FIM completion (prefix, suffix → streamed text); null = no-op like a
    /// backend without FIM output.</summary>
    public Func<string, string, string>? OnFim { get; set; }

    /// <summary>Optional override of <see cref="GetEmbeddingAsync"/> (chunk text → vector);
    /// null keeps the constant <see cref="Embedding"/>. Lets the RAG lifecycle tests count
    /// embedding calls and act mid-pass (e.g. rewrite a file while it is being indexed).</summary>
    public Func<string, float[]?>? OnEmbedding { get; set; }

    /// <summary>Asynchronous override of <see cref="GetEmbeddingAsync"/>, given the request's token: lets a test hold an
    /// embedding until the turn is stopped. Wins over <see cref="OnEmbedding"/> when set.</summary>
    public Func<string, CancellationToken, Task<float[]?>>? OnEmbeddingAsync { get; set; }

    /// <summary>Result of <see cref="DeleteModelAsync"/> (keyed by model name).</summary>
    public Func<string, bool>          OnDelete { get; set; } = _ => true;
    /// <summary>Result of <see cref="PullModelAsync"/> (keyed by model name).</summary>
    public Func<string, bool>          OnPull   { get; set; } = _ => true;
    /// <summary>Architecture returned by <see cref="ShowModelAsync"/> (keyed by model name).</summary>
    public Func<string, ModelArchInfo?> OnShow  { get; set; } = _ => null;

    // ── Call recording ──────────────────────────────────────────────────────────
    public List<string> Deleted    { get; } = [];
    public List<string> Pulled     { get; } = [];
    public List<string> Unloaded   { get; } = [];
    /// <summary>Model name of every <see cref="SendChatAsync(string, List{ChatMessageDto}, IToolRegistry, Action{string}, ReasoningEffort, CancellationToken, TaskComplexity, string, Action{string}, Action{string})"/> call, in order.</summary>
    public List<string> ChatModels { get; } = [];
    /// <summary>Every <see cref="RunAgentAsync"/> call (model + the history it received), in order.</summary>
    public List<(string Model, List<ChatMessageDto> History)> AgentRuns { get; } = [];
    /// <summary>The reasoning effort every call asked for — chat turn and agent run alike — in order.</summary>
    public List<ReasoningEffort> Efforts { get; } = [];

    // ── IChatTurnClient ───────────────────────────────────────────────────────
    public Task<ChatTurnResult> SendChatAsync(
        string model, List<ChatMessageDto> messages, IToolRegistry tools, Action<string>? onToken,
        CancellationToken ct, TaskComplexity complexity = TaskComplexity.Normal,
        string? toolChoice = null, Action<string>? onThinking = null)
        => SendChatAsync(model, messages, tools, onToken, ReasoningEffort.Server, ct, complexity, toolChoice, onThinking);

    public Task<ChatTurnResult> SendChatAsync(
        string model, List<ChatMessageDto> messages, IToolRegistry tools, Action<string>? onToken,
        ReasoningEffort effort, CancellationToken ct, TaskComplexity complexity = TaskComplexity.Normal,
        string? toolChoice = null, Action<string>? onThinking = null, Action<string?>? onWaiting = null)
    {
        ChatModels.Add(model);
        Efforts.Add(effort);
        if (onThinking is not null) DriveThinking?.Invoke(onThinking);
        if (onWaiting is not null) DriveWaiting?.Invoke(onWaiting);
        return OnChatRequest?.Invoke(model, messages, tools, onToken)
            ?? OnChat?.Invoke(onToken, ct)
            ?? Task.FromResult(ChatResult);
    }

    // ── IInferenceProvider ──────────────────────────────────────────────────────
    /// <summary>
    /// When true, <see cref="RunAgentAsync"/> answers through <see cref="SendChatAsync(string, List{ChatMessageDto}, IToolRegistry, Action{string}, ReasoningEffort, CancellationToken, TaskComplexity, string, Action{string}, Action{string})"/> — one model turn, like the
    /// real basic loop, so <see cref="OnChat"/>/<see cref="OnChatRequest"/> script it (streaming, cancellation,
    /// failures, the model name). Off, it returns the constant <see cref="ChatResult"/>.
    /// </summary>
    public bool RunAgentThroughChat { get; set; }

    /// <summary>When set, <see cref="RunAgentAsync"/> returns it as is — a run with tool executions and how it ended
    /// (a loop stopped after work was done), which one scripted chat turn cannot produce.</summary>
    public AgentResult? AgentRunResult { get; set; }

    /// <summary>When set, <see cref="RunAgentAsync"/> is this run: the tools and the loop's report of each call, for a
    /// test that plays a batch of calls the way the loop does (the real funnel, the real registry).</summary>
    public Func<List<ChatMessageDto>, IToolRegistry, Action<ToolExecution>?, CancellationToken, Task<AgentResult>>? OnAgentRun { get; set; }

    /// <summary>The steering each <see cref="RunAgentAsync"/> was given, in order (<c>null</c>: a run nobody steers).</summary>
    public List<RunSteering?> Steerings { get; } = new();

    public Task<AgentResult> RunAgentAsync(
        string model, List<ChatMessageDto> history, IToolRegistry tools, Action<string> onStep,
        Action<string>? onToken, CancellationToken ct, TaskComplexity complexity = TaskComplexity.Normal,
        Action<ToolExecution>? onToolExecuted = null, Action<string>? onThinking = null,
        ReasoningEffort effort = ReasoningEffort.Server, RunSteering? steering = null)
    {
        AgentRuns.Add((model, history));
        Steerings.Add(steering);
        // A run answered through SendChatAsync records its effort there.
        if (!RunAgentThroughChat) Efforts.Add(effort);
        if (OnAgentRun is { } run) return run(history, tools, onToolExecuted, ct);
        if (AgentRunResult is not null)
            return Task.FromResult(AgentRunResult with { UpdatedHistory = history });
        if (RunAgentThroughChat)
            return RunOneChatTurnAsync(model, history, tools, onToken, effort, ct, complexity, onThinking);
        if (onThinking is not null) DriveThinking?.Invoke(onThinking);
        return Task.FromResult(new AgentResult(ChatResult.TextContent, [], history));
    }

    private async Task<AgentResult> RunOneChatTurnAsync(
        string model, List<ChatMessageDto> history, IToolRegistry tools, Action<string>? onToken,
        ReasoningEffort effort, CancellationToken ct, TaskComplexity complexity, Action<string>? onThinking)
    {
        // The real loop never throws for a backend failure: it returns the message as the result. Mirrored, or a test
        // of a caller would see an exception the caller never gets.
        ChatTurnResult turn;
        try { turn = await SendChatAsync(model, history, tools, onToken, effort, ct, complexity, null, onThinking); }
        catch (AgentHttpException ex) { return new AgentResult(ex.Message, [], history, Failed: true); }
        return new AgentResult(turn.TextContent, [], history, turn.TokensUsed, turn.PromptTokens,
                               AnswerCut: turn.CutAtLimit, AnswerRepeating: turn.StoppedRepeating,
                               AnswerIsReasoning: turn.AnswerIsReasoning);
    }

    /// <summary>Every embedding request, as sent: the exact text and the model it names.</summary>
    public System.Collections.Concurrent.ConcurrentQueue<(string Text, string Model)> EmbeddingRequests { get; } = new();

    public Task<float[]?> GetEmbeddingAsync(string text, string model, CancellationToken ct)
    {
        EmbeddingRequests.Enqueue((text, model));
        if (OnEmbeddingAsync is { } held) return held(text, ct);
        return Task.FromResult(OnEmbedding is not null ? OnEmbedding(text) : Embedding);
    }

    public Task<bool> CheckConnectionAsync(string url, CancellationToken ct) => Task.FromResult(ConnectionOk);
    public string? ConnectionRefusal { get; set; }

    /// <summary>The URL the last <see cref="ListModelsAsync"/> received — the settings panel
    /// must list the models of the FORM's URL, not of the saved one, and without
    /// this witness the test could not tell the difference.</summary>
    public string? LastListModelsUrl;

    /// <summary>When set, <see cref="ListModelsAsync"/> waits for it first — a model server slow to answer.</summary>
    public Func<Task>? BeforeListModels { get; set; }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct, string? url = null)
    {
        LastListModelsUrl = url;
        if (BeforeListModels is { } wait) await wait();
        return ModelNames;
    }

    public Task<IReadOnlyList<InstalledModelInfo>> ListInstalledModelsAsync(CancellationToken ct, string? url = null) =>
        Task.FromResult<IReadOnlyList<InstalledModelInfo>>(Installed);

    /// <summary>The last FIM call received (model, tokens, temperature); null until one arrives.</summary>
    public (string? Model, int MaxTokens, double Temperature)? LastFim { get; private set; }

    public Task StreamFimAsync(string prefix, string suffix, int maxTokens, double temperature,
        Action<string> onToken, CancellationToken ct, string? model = null)
    {
        LastFim = (model, maxTokens, temperature);
        if (OnFim?.Invoke(prefix, suffix) is { } completion) onToken(completion);
        return Task.CompletedTask;
    }

    /// <summary>The server refuses inline completion for every model (<see cref="IInferenceProvider.InlineCompletionRefused"/>).</summary>
    public bool FimRefused { get; set; }

    public bool InlineCompletionRefused(string model) => FimRefused;

    public void ResetCircuit() { }

    /// <summary>The VRAM-poll lever: without it no test can see what the service does with a
    /// backend that stops answering — which is precisely where it kept stale state.</summary>
    public Action? OnRunningModels { get; set; }

    public Task<IReadOnlyList<RunningModelInfo>> GetRunningModelsAsync(CancellationToken ct)
    {
        OnRunningModels?.Invoke();
        return Task.FromResult<IReadOnlyList<RunningModelInfo>>(RunningNotSaid ? [] : Running);
    }

    /// <summary>The loaded list cannot be read (null from <see cref="ReadRunningModelsAsync"/>), as on LM Studio behind a
    /// proxy that routes only <c>/v1</c>; <see cref="GetRunningModelsAsync"/> then reads empty, like the real clients.</summary>
    public bool RunningNotSaid { get; set; }

    public async Task<IReadOnlyList<RunningModelInfo>?> ReadRunningModelsAsync(CancellationToken ct) =>
        RunningNotSaid ? null : await GetRunningModelsAsync(ct);

    /// <summary>The window the "server" reports a model loaded with; null = it cannot say.</summary>
    public int? LoadedContextWindow { get; set; }

    /// <summary>Models asked about, in order — the witness that the right model was looked up.</summary>
    public List<string> LoadedContextQueries { get; } = [];

    /// <summary>Runs when the loaded window is asked for — a test holds the request there (an ACP prompt being built).</summary>
    public Func<CancellationToken, Task>? OnLoadedContextWindow { get; set; }

    public async Task<int?> GetLoadedContextWindowAsync(string model, CancellationToken ct)
    {
        LoadedContextQueries.Add(model);
        if (OnLoadedContextWindow is { } hold) await hold(ct);
        return LoadedContextWindow;
    }

    /// <summary>Whether an unload takes the model out of <see cref="Running"/>; by default the list does not move.</summary>
    public Func<string, bool> OnUnload { get; set; } = _ => false;

    public Task UnloadModelAsync(string model, CancellationToken ct)
    {
        Unloaded.Add(model);
        if (OnUnload(model)) Running.RemoveAll(m => m.Name == model);
        return Task.CompletedTask;
    }

    public Task<ModelArchInfo?> ShowModelAsync(string model, CancellationToken ct) =>
        Task.FromResult(OnShow(model));

    public Task<bool> PullModelAsync(string model, Action<string> onStatus, CancellationToken ct)
    {
        Pulled.Add(model);
        foreach (var status in PullStatuses) onStatus(status);
        return Task.FromResult(OnPull(model));
    }

    /// <summary>The progress lines a pull reports, in order, before it ends.</summary>
    public string[] PullStatuses { get; set; } = [];

    public Task<bool> DeleteModelAsync(string model, CancellationToken ct)
    {
        Deleted.Add(model);
        return Task.FromResult(OnDelete(model));
    }
}
