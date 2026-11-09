using Inferpal.Config;

namespace Inferpal.Services.Inference;

/// <summary>
/// Resolves the active <see cref="IInferenceProvider"/> from <c>config.Provider</c>. Called once at
/// startup by the DI container (and directly by the FIM sidecar <c>Inferpal.Fim</c>, which builds
/// its own client for the in-process <c>GhostTextController</c>). Visual Studio wraps it in a <see cref="SwitchingProvider"/>,
/// which builds a new one when the kind of server changes.
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

    public static IInferenceProvider Create(InferpalConfig config) => Create(config, ServerRole.Chat);

    /// <summary>A client for <paramref name="role"/>: of its server's backend, talking to its server's address with its
    /// server's key, read live from <paramref name="config"/> (<see cref="RoleServers"/>).</summary>
    public static IInferenceProvider Create(InferpalConfig config, ServerRole role)
    {
        var code = Canonical(RoleServers.Of(config, role).Provider).Trim().ToLowerInvariant();

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
            LmStudio         => new LmStudioClient(config, role),
            OpenAiCompatible => new OpenAiCompatibleClient(config, role),
            _                => new OllamaClient(config, role),
        };
    }

    /// <summary>The backend <paramref name="client"/> was built for; null for a client this factory does not build.</summary>
    public static string? CodeOf(IInferenceProvider client) => client switch
    {
        SwitchingProvider s    => s.Kind,
        LmStudioClient         => LmStudio,          // before its base class
        OpenAiCompatibleClient => OpenAiCompatible,
        OllamaClient           => Ollama,
        _                      => null,
    };

    /// <summary>
    /// The backend <paramref name="configured"/> names, when <paramref name="client"/> was built for another one;
    /// null when they agree, or when the client's backend is unknown.
    /// </summary>
    /// <remarks>⚠ The host of VS Code and ACP builds its client once. A backend chosen since lands in the live
    /// configuration at once, while requests keep going through the previous backend's client — its protocol, to the new
    /// address: every connection message would blame the new backend while it runs. Only a restart of the host applies
    /// the switch, and that is what must be said. Visual Studio's client rebuilds itself instead
    /// (<see cref="SwitchingProvider"/>, whose <see cref="CodeOf"/> is the kind it was built for).</remarks>
    public static string? PendingSwitch(IInferenceProvider client, string? configured)
    {
        if (CodeOf(client) is not { } built) return null;
        var now = KindOf(configured);
        return now == built ? null : now;
    }

    /// <summary>The backend <see cref="Create(InferpalConfig)"/> builds for <paramref name="configured"/>: an unknown code is Ollama.</summary>
    public static string KindOf(string? configured)
    {
        var now = Canonical(configured).Trim().ToLowerInvariant();
        return now is Ollama or LmStudio or OpenAiCompatible ? now : Ollama;
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
