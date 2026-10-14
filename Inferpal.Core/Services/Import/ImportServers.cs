using System.Text.Json.Nodes;
using Inferpal.Services.Inference;

namespace Inferpal.Services.Import;

/// <summary>A model server as Inferpal reaches it: one of its providers and the address.</summary>
internal sealed record ImportServer(string Provider, string BaseUrl)
{
    /// <summary>The same server, whatever the trailing <c>/</c> or <c>/v1</c> and the case of the host.</summary>
    public bool SameAs(string provider, string baseUrl) =>
        InferenceProviderFactory.Canonical(provider) == Provider && ImportServers.Comparable(baseUrl) == ImportServers.Comparable(BaseUrl);
}

/// <summary>
/// Which servers an imported model can be reached on: the ones the user runs (Ollama, LM Studio, a server speaking the
/// OpenAI API) — never a hosted provider, an anti-goal of the product.
/// </summary>
/// <remarks>
/// ⚠ The server is judged, never the model's name: <c>anthropic-claude-v4-1-opus</c> behind a company's own gateway is
/// that gateway's model. A base URL on a hosted provider's own host is hosted, whatever the provider field says.
/// </remarks>
internal static class ImportServers
{
    private static readonly string[] HostedHosts =
    [
        "api.openai.com", "api.anthropic.com", "openrouter.ai", "generativelanguage.googleapis.com", "api.deepseek.com",
        "api.mistral.ai", "codestral.mistral.ai", "api.groq.com", "api.together.xyz", "api.fireworks.ai", "api.x.ai",
        "api.perplexity.ai", "api.cohere.com", "api.cohere.ai", "api.cerebras.ai", "api.sambanova.ai", "api.moonshot.ai",
        "api.moonshot.cn", "dashscope.aliyuncs.com", "api.novita.ai", "api.deepinfra.com", "router.huggingface.co",
        "api-inference.huggingface.co", "integrate.api.nvidia.com", "api.studio.nebius.ai", "api.siliconflow.cn",
        "agentrouter.org",
    ];

    private static readonly string[] HostedHostEndings =
        [".openai.azure.com", ".cognitiveservices.azure.com", ".services.ai.azure.com", ".amazonaws.com"];

    /// <summary>The server a Continue model is served by; <c>null</c> when its provider is a hosted one.</summary>
    /// <param name="apiBase">The model's <c>apiBase</c>, absent when the provider's default applies.</param>
    public static ImportServer? FromContinue(string provider, string? apiBase)
    {
        var url = string.IsNullOrWhiteSpace(apiBase) ? null : apiBase.Trim().TrimEnd('/');
        return provider.Trim().ToLowerInvariant() switch
        {
            "ollama"                        => new(InferenceProviderFactory.Ollama, url ?? "http://localhost:11434"),
            "lmstudio"                      => new(InferenceProviderFactory.LmStudio, url ?? "http://localhost:1234"),
            "llama.cpp" or "llamacpp"
                or "llamafile"              => OpenAiCompatible(url ?? "http://localhost:8080"),
            "vllm"                          => OpenAiCompatible(url ?? "http://localhost:8000"),
            "msty"                          => OpenAiCompatible(url ?? "http://localhost:10000"),
            "text-gen-webui"                => OpenAiCompatible(url ?? "http://localhost:5000"),
            "openai" or "openai-compatible" => url is null ? null : OpenAiCompatible(url),
            _                               => null,
        };
    }

    /// <summary>
    /// The server of a Roo profile, with its model and key; <c>null</c> when the profile's provider is a hosted one (or
    /// VS Code's language models, which Inferpal cannot reach).
    /// </summary>
    public static ImportServer? FromRoo(JsonObject profile, out string? model, out string? apiKey)
    {
        static string? S(JsonObject o, string key) =>
            o[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

        apiKey = null;
        switch (S(profile, "apiProvider")?.ToLowerInvariant())
        {
            case "ollama":
                model = S(profile, "ollamaModelId");
                return new(InferenceProviderFactory.Ollama, (S(profile, "ollamaBaseUrl") ?? "http://localhost:11434").TrimEnd('/'));
            case "lmstudio":
                model = S(profile, "lmStudioModelId");
                return new(InferenceProviderFactory.LmStudio, (S(profile, "lmStudioBaseUrl") ?? "http://localhost:1234").TrimEnd('/'));
            case "openai":
                model  = S(profile, "openAiModelId");
                apiKey = S(profile, "openAiApiKey");
                return S(profile, "openAiBaseUrl") is { } url ? OpenAiCompatible(url.TrimEnd('/')) : null;
            default:
                model = null;
                return null;
        }
    }

    private static ImportServer? OpenAiCompatible(string url) =>
        IsHostedHost(url) ? null : new ImportServer(InferenceProviderFactory.OpenAiCompatible, url);

    /// <summary>Whether <paramref name="url"/> is a hosted provider's own address.</summary>
    internal static bool IsHostedHost(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        var host = uri.Host.ToLowerInvariant();
        return HostedHosts.Contains(host) || HostedHostEndings.Any(e => host.EndsWith(e, StringComparison.Ordinal));
    }

    /// <summary>The host and port a key is sent to (<c>gateway.corp:443</c>), or <c>null</c> for what is not a URL.</summary>
    internal static string? Authority(string? url) =>
        Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) ? uri.Authority.ToLowerInvariant() : null;

    /// <summary>An address as two spellings of one server compare: no trailing <c>/</c> or <c>/v1</c>, host lower-cased.</summary>
    internal static string Comparable(string url)
    {
        var u = url.Trim().TrimEnd('/');
        if (u.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) u = u[..^3];
        return Uri.TryCreate(u, UriKind.Absolute, out var uri)
            ? uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant() + uri.AbsolutePath.TrimEnd('/')
            : u.ToLowerInvariant();
    }
}
