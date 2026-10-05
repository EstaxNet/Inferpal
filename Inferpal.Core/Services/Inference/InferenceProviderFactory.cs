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

    /// <summary>
    /// The code <paramref name="code"/> names: case and spaces ignored, and <c>openai</c> — the value the published
    /// documentation long gave — read as <see cref="OpenAiCompatible"/>. An unknown code is returned as is.
    /// </summary>
    public static string Canonical(string? code)
    {
        var key = code?.Trim().ToLowerInvariant();
        return key switch
        {
            Ollama or LmStudio or OpenAiCompatible => key,
            "openai"                               => OpenAiCompatible,
            _                                      => code ?? string.Empty,
        };
    }

    public static IInferenceProvider Create(InferpalConfig config)
    {
        var code = Canonical(config.Provider).Trim().ToLowerInvariant();

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

    /// <summary>The backend <paramref name="client"/> was built for; null for a client this factory does not build.</summary>
    public static string? CodeOf(IInferenceProvider client) => client switch
    {
        LmStudioClient         => LmStudio,          // before its base class
        OpenAiCompatibleClient => OpenAiCompatible,
        OllamaClient           => Ollama,
        _                      => null,
    };

    /// <summary>
    /// The backend <paramref name="configured"/> names, when <paramref name="client"/> was built for another one;
    /// null when they agree, or when the client's backend is unknown.
    /// </summary>
    /// <remarks>⚠ Visual Studio builds its client once. A backend chosen in Settings, or detected by <c>/setup</c>,
    /// lands in the live configuration at once, while requests keep going through the previous backend's client — its
    /// protocol, to the new address. Every connection message then blamed the new backend ("cannot reach LM Studio —
    /// start LM Studio") while it was running, and Retry could not help: only a restart applies the switch, and that
    /// is what must be said.</remarks>
    public static string? PendingSwitch(IInferenceProvider client, string? configured)
    {
        if (CodeOf(client) is not { } built) return null;
        var now = Canonical(configured).Trim().ToLowerInvariant();
        if (now is not (Ollama or LmStudio or OpenAiCompatible)) now = Ollama;   // what Create would build
        return now == built ? null : now;
    }

    /// <summary>
    /// The capabilities a given provider <paramref name="code"/> advertises, without instantiating a
    /// client. Lets the settings UI gate options to the <em>currently selected</em> provider in the
    /// dropdown (which may differ from the active singleton until the next reload), so it never
    /// surfaces an option that backend can't honour.
    /// </summary>
    public static ProviderCapabilities CapabilitiesFor(string? code) =>
        Canonical(code).Trim().ToLowerInvariant() switch
        {
            LmStudio         => ProviderCapabilities.LmStudio,
            OpenAiCompatible => ProviderCapabilities.OpenAiCompatible,
            _                => ProviderCapabilities.Ollama,
        };

    /// <summary>
    /// User-facing name of the configured backend, for connection messages: telling an LM Studio
    /// user "cannot reach Ollama, run ollama serve" sent them chasing the wrong process.
    /// </summary>
    public static string DisplayName(string? code) =>
        Canonical(code).Trim().ToLowerInvariant() switch
        {
            LmStudio         => "LM Studio",
            OpenAiCompatible => "OpenAI-compatible", // same label as the settings dropdown
            _                => "Ollama",
        };
}
