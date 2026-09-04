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

    public static IInferenceProvider Create(InferpalConfig config)
    {
        var code = config.Provider?.Trim().ToLowerInvariant();

        // ⚠ Falling back to Ollama is the right behaviour — a config with no `provider` predates
        // multi-backend support — but it was COMPLETELY silent, including for a hand-written or
        // misspelled code: the product then talked to a different backend than the one you
        // believed you had chosen, and nothing anywhere said so. It cost a false measurement to
        // the VS Code front-end review, which had written "openai" instead of
        // "openai-compatible". The fallback stays; it is traced.
        if (!string.IsNullOrEmpty(code) && code != Ollama && code != LmStudio && code != OpenAiCompatible)
            // English like every other Diagnostics context: this is text the user reads in
            // /diagnostics.
            Diagnostics.Swallow($"InferenceProviderFactory: unknown backend code \"{code}\" — falling back to Ollama",
                                new ArgumentOutOfRangeException(nameof(config.Provider), code, null));

        return code switch
        {
            LmStudio         => new LmStudioClient(config),
            OpenAiCompatible => new OpenAiCompatibleClient(config),
            _                => new OllamaClient(config),
        };
    }

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

    /// <summary>
    /// User-facing name of the configured backend, for connection messages: telling an LM Studio
    /// user "cannot reach Ollama, run ollama serve" sent them chasing the wrong process (§27.6).
    /// </summary>
    public static string DisplayName(string? code) =>
        (code?.Trim().ToLowerInvariant()) switch
        {
            LmStudio         => "LM Studio",
            OpenAiCompatible => "OpenAI-compatible", // same label as the settings dropdown
            _                => "Ollama",
        };
}
