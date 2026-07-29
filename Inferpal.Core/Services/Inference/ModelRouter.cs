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

    private static string FirstNonEmpty(params string?[] candidates)
    {
        foreach (var c in candidates)
            if (!string.IsNullOrWhiteSpace(c))
                return c.Trim();
        return string.Empty;
    }
}
