using Inferpal.Config;

namespace Inferpal.Services.Inference;

/// <summary>
/// The task a model is being resolved for. Each role maps to a per-feature override in
/// <see cref="InferpalConfig"/>, with <see cref="InferpalConfig.DefaultModel"/> as the final
/// fallback everywhere.
/// </summary>
internal enum ModelRole
{
    /// <summary>Plain chat — always <see cref="InferpalConfig.DefaultModel"/>.</summary>
    Chat,
    /// <summary>Autonomous agent loop (<see cref="InferpalConfig.AgentModel"/>).</summary>
    Agent,
    /// <summary>Explain / Fix / Refactor code actions (<see cref="InferpalConfig.CodeActionsModel"/>).</summary>
    CodeActions,
    /// <summary>Inline Edit — falls back to <see cref="ModelRole.CodeActions"/> first.</summary>
    InlineEdit,
    /// <summary>Fill-in-the-Middle ghost text (<see cref="InferpalConfig.InlineCompletionModel"/>).</summary>
    Fim,
    /// <summary>
    /// Auxiliary background tasks — session titles, commit messages, compaction summaries
    /// (<see cref="InferpalConfig.UtilityModel"/>).
    /// </summary>
    Utility,
}

/// <summary>
/// Central task→model resolution ("Model Router" V1). Every feature that needs a model name asks
/// this class instead of hand-rolling its own fallback chain — the chains used to be duplicated
/// across the VS commands and the chat VM, and drifting copies is exactly the bug this prevents.
/// </summary>
/// <remarks>
/// V1 is a plain lookup table (no VRAM-aware auto mode yet): an empty per-role override means
/// "use the chat model". The swap-cost economics are handled by the user's choice of a small
/// utility model plus the <c>keep_alive</c> idle-unload policy, which keeps both models warm on
/// backends that honour it.
/// </remarks>
internal static class ModelRouter
{
    /// <summary>Resolves the effective model name for <paramref name="role"/>. Never empty.</summary>
    public static string Resolve(InferpalConfig config, ModelRole role) => role switch
    {
        ModelRole.Agent       => FirstNonEmpty(config.AgentModel, config.DefaultModel),
        ModelRole.CodeActions => FirstNonEmpty(config.CodeActionsModel, config.DefaultModel),
        ModelRole.InlineEdit  => FirstNonEmpty(config.InlineEditModel, config.CodeActionsModel, config.DefaultModel),
        ModelRole.Fim         => FirstNonEmpty(config.InlineCompletionModel, config.DefaultModel),
        ModelRole.Utility     => FirstNonEmpty(config.UtilityModel, config.DefaultModel),
        _                     => config.DefaultModel,
    };

    /// <summary>
    /// V2 "auto" mode for the utility role, pure core (unit-tested directly): route to the
    /// <c>/bench</c>-recommended utility model only when it is already warm. An explicit
    /// <see cref="InferpalConfig.UtilityModel"/> always wins; a cold candidate falls back to the
    /// plain resolution — the VRAM swap a cold load triggers costs more than a title or commit
    /// message saves ("only route to the small model when the gain is net", ROADMAP §4).
    /// </summary>
    /// <param name="benchRecommended">Utility pick of the last persisted <c>/bench</c> run.</param>
    /// <param name="warmModels">Names currently loaded on the backend (tag-tolerant match).</param>
    public static string ResolveUtility(
        InferpalConfig config, string? benchRecommended, IEnumerable<string> warmModels)
    {
        var configured = Resolve(config, ModelRole.Utility);
        if (!config.ModelRouterAuto)                          return configured;
        if (!string.IsNullOrWhiteSpace(config.UtilityModel))  return configured;
        if (string.IsNullOrWhiteSpace(benchRecommended))      return configured;
        return warmModels.Any(m => SameModel(m, benchRecommended!)) ? benchRecommended! : configured;
    }

    /// <summary>
    /// Gathers the auto-mode inputs (persisted <c>/bench</c> recommendation, currently running
    /// models) and delegates to <see cref="ResolveUtility"/>. Cheap when auto mode is off or an
    /// explicit utility model is set (no I/O); best-effort otherwise — any backend hiccup degrades
    /// to the plain resolution. <c>/api/ps</c> is a plain HTTP call, not gated by the
    /// <see cref="GpuScheduler"/>, so this is safe to call while holding the chat lease.
    /// </summary>
    public static async Task<string> ResolveUtilityAsync(
        InferpalConfig config, IOllamaChatClient client, CancellationToken ct)
    {
        if (!config.ModelRouterAuto || !string.IsNullOrWhiteSpace(config.UtilityModel))
            return Resolve(config, ModelRole.Utility);

        string? recommended = null;
        if (await Bench.BenchStore.LoadAsync() is { } saved)
            recommended = Commands.BenchCommandHandler.Recommend(saved.Results).Utility;

        // The orchestrator only holds the chat-client view; without the full provider there is
        // no warm-model info and the pure core falls back to the plain resolution.
        IEnumerable<string> warm = [];
        try
        {
            if (client is IInferenceProvider provider && provider.Capabilities.VramMonitoring)
                warm = (await provider.GetRunningModelsAsync(ct)).Select(m => m.Name);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Diagnostics.Swallow("ModelRouter.ResolveUtilityAsync", ex); }

        return ResolveUtility(config, recommended, warm);
    }

    /// <summary>Tag-tolerant model name comparison (<c>llama3.1</c> vs <c>llama3.1:latest</c>),
    /// same rule as the bench runner and the arena.</summary>
    internal static bool SameModel(string x, string y) =>
        string.Equals(x, y, StringComparison.OrdinalIgnoreCase)
        || string.Equals(x.Split(':')[0], y.Split(':')[0], StringComparison.OrdinalIgnoreCase);

    private static string FirstNonEmpty(params string?[] candidates)
    {
        foreach (var c in candidates)
            if (!string.IsNullOrWhiteSpace(c))
                return c.Trim();
        return string.Empty;
    }
}
