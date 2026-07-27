using System.Net.Http;
using System.Text.Json;

namespace Inferpal.Services.Inference;

/// <summary>
/// Auto-detects which inference backend is running at a base URL by probing each backend's
/// <b>signature</b> endpoint. Used by the settings "Test connection" button and by first-run
/// discovery so the user doesn't have to pick the provider manually.
/// </summary>
/// <remarks>
/// The endpoints are mutually exclusive: Ollama answers <c>/api/tags</c> (and has no <c>/api/v1</c>),
/// LM Studio answers its native <c>/api/v1/models</c> (or legacy <c>/api/v0/models</c>) while a plain
/// OpenAI-compatible server (vLLM, llama.cpp, …) only answers <c>/v1/models</c>. Probed most-specific
/// first so the generic OpenAI endpoint — which both Ollama and LM Studio also expose — is the last resort.
/// </remarks>
internal static class ProviderProbe
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };

    /// <summary>
    /// Returns the detected provider code (<see cref="InferenceProviderFactory.Ollama"/>,
    /// <see cref="InferenceProviderFactory.LmStudio"/>, or
    /// <see cref="InferenceProviderFactory.OpenAiCompatible"/>), or <c>null</c> when nothing answers.
    /// </summary>
    public static async Task<string?> DetectAsync(string? baseUrl, string? apiKey, CancellationToken ct)
    {
        var root = RootOf(baseUrl);
        if (string.IsNullOrEmpty(root)) return null;

        // Ollama answers /api/tags with {"models":[...]}; LM Studio and OpenAI-compatible servers
        // answer their /models endpoints with {"data":[...]}. We require that discriminating root
        // property so a reverse proxy that returns 200 (with an HTML index or a catch-all page) for
        // every path can't be mistaken for a backend — a bare status code is not enough.
        var ollamaOk = await ProbeAsync($"{root}/api/tags", apiKey, "models", ct).ConfigureAwait(false);
        // v1 (0.4.0+) wraps the list in "models"; the legacy v0 endpoint uses "data".
        var lmStudioOk = !ollamaOk &&
            (await ProbeAsync($"{root}/api/v1/models", apiKey, "models", ct).ConfigureAwait(false)
             || await ProbeAsync($"{root}/api/v0/models", apiKey, "data", ct).ConfigureAwait(false));
        var openAiOk = !ollamaOk && !lmStudioOk &&
            await ProbeAsync($"{root}/v1/models", apiKey, "data", ct).ConfigureAwait(false);

        return Classify(ollamaOk, lmStudioOk, openAiOk);
    }

    /// <summary>Pure decision from which signature endpoints responded (testable, no network).</summary>
    internal static string? Classify(bool ollamaTagsOk, bool lmStudioNativeOk, bool openAiV1Ok) =>
        ollamaTagsOk       ? InferenceProviderFactory.Ollama
        : lmStudioNativeOk ? InferenceProviderFactory.LmStudio
        : openAiV1Ok       ? InferenceProviderFactory.OpenAiCompatible
        : null;

    /// <summary>Host root: strips a trailing <c>/v1</c> so both Ollama and OpenAI-style URLs probe correctly.</summary>
    internal static string RootOf(string? baseUrl)
    {
        var b = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
        if (b.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            b = b[..^3].TrimEnd('/');
        return b;
    }

    private static async Task<bool> ProbeAsync(string url, string? apiKey, string requiredRootProperty, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(apiKey))
                req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
            using var resp = await _http.SendAsync(req, cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return false;
            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            return HasRootProperty(body, requiredRootProperty);
        }
        catch { return false; }
    }

    /// <summary>
    /// True when <paramref name="body"/> is a JSON object exposing <paramref name="property"/> at its
    /// root. Rejects HTML/empty/non-JSON bodies that a misconfigured reverse proxy may return with 200.
    /// </summary>
    internal static bool HasRootProperty(string? body, string property)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(property, out _);
        }
        catch { return false; }
    }
}
