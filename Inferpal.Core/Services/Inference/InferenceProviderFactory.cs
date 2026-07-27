using Inferpal.Config;

namespace Inferpal.Services.Inference;

/// <summary>
/// Resolves the active <see cref="IInferenceProvider"/> from <c>config.Provider</c>. Called once at
/// startup by the DI container (and directly by <see cref="GhostText.GhostTextController"/>, which
/// builds its own client). Switching provider takes effect on the next VS reload.
/// </summary>
internal static class InferenceProviderFactory
{
    /// <summary>Identifier persisted in <see cref="InferpalConfig.Provider"/>.</summary>
    public const string Ollama          = "ollama";
    /// <summary>Identifier persisted in <see cref="InferpalConfig.Provider"/>.</summary>
    public const string LmStudio        = "lmstudio";
    /// <summary>Identifier persisted in <see cref="InferpalConfig.Provider"/>.</summary>
    public const string OpenAiCompatible = "openai-compatible";

    public static IInferenceProvider Create(InferpalConfig config) =>
        (config.Provider?.Trim().ToLowerInvariant()) switch
        {
            LmStudio         => new LmStudioClient(config),
            OpenAiCompatible => new OpenAiCompatibleClient(config),
            _                => new OllamaClient(config),
        };

    /// <summary>
    /// The capabilities a given provider <paramref name="code"/> advertises, without instantiating a
    /// client. Lets the settings UI gate options to the <em>currently selected</em> provider in the
    /// dropdown (which may differ from the active singleton until the next reload), so it never
    /// surfaces an option that backend can't honour.
    /// </summary>
    public static ProviderCapabilities CapabilitiesFor(string? code) =>
        (code?.Trim().ToLowerInvariant()) switch
        {
            LmStudio         => ProviderCapabilities.LmStudio,
            OpenAiCompatible => ProviderCapabilities.OpenAiCompatible,
            _                => ProviderCapabilities.Ollama,
        };
}
