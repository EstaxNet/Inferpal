using Inferpal.Config;

namespace Inferpal.Services.Inference;

/// <summary>What a model does, and so which server it runs on.</summary>
internal enum ServerRole
{
    /// <summary>The chat and the agent: the server of <see cref="InferpalConfig.BaseUrl"/>.</summary>
    Chat,
    /// <summary>Inline completions (ghost text).</summary>
    Completion,
    /// <summary>Code-search embeddings: the project index, <c>@Docs</c>, and the queries against them.</summary>
    Search,
}

/// <summary>
/// The server each role runs on: the chat's, unless the settings put completions or code search on another — ghost text
/// and the index on this machine while the chat runs on a team server.
/// </summary>
/// <remarks>
/// ⚠ A key belongs to its server: a role on another server is sent that server's key, never the chat's. And two servers
/// on one MACHINE share its graphics card: a role yields to the chat when its server runs on the chat's machine, not
/// only on its port — the same machine under two ports is still one GPU.
/// </remarks>
internal static class RoleServers
{
    /// <summary><c>…RunsOn</c>: the chat's server.</summary>
    public const string OnChat = "chat";
    /// <summary><c>…RunsOn</c>: the server of the role's own address.</summary>
    public const string OnOther = "other";
    /// <summary><c>ragEmbeddingRunsOn</c>: the completion's server.</summary>
    public const string OnAutocomplete = "autocomplete";

    /// <summary>The server <paramref name="role"/> runs on: its backend code, address and key.</summary>
    public static (string Provider, string BaseUrl, string ApiKey) Of(InferpalConfig c, ServerRole role) => role switch
    {
        ServerRole.Completion when Is(c.InlineCompletionRunsOn, OnOther) && !string.IsNullOrWhiteSpace(c.InlineCompletionBaseUrl)
            => (Or(c.InlineCompletionProvider, c.Provider), c.InlineCompletionBaseUrl.Trim(), c.InlineCompletionApiKey ?? ""),
        ServerRole.Search when Is(c.RagEmbeddingRunsOn, OnOther) && !string.IsNullOrWhiteSpace(c.RagEmbeddingBaseUrl)
            => (Or(c.RagEmbeddingProvider, c.Provider), c.RagEmbeddingBaseUrl.Trim(), c.RagEmbeddingApiKey ?? ""),
        ServerRole.Search when Is(c.RagEmbeddingRunsOn, OnAutocomplete)
            => Of(c, ServerRole.Completion),
        _ => (c.Provider, c.BaseUrl, c.ApiKey ?? ""),
    };

    /// <summary><paramref name="role"/> runs on the chat's server (the same address): the chat's client serves it.</summary>
    public static bool OnChatServer(InferpalConfig c, ServerRole role) =>
        role == ServerRole.Chat || ServerScan.SameAddress(Of(c, role).BaseUrl, c.BaseUrl);

    /// <summary><paramref name="role"/>'s server runs on the chat's machine, so on its graphics card: it yields to a chat
    /// answering there. Unknown hosts are taken as the same machine — yielding when not needed costs a pause, not
    /// yielding when needed steals the card from an answer.</summary>
    public static bool SharesMachineWithChat(InferpalConfig c, ServerRole role) =>
        OnChatServer(c, role) || SameMachine(Of(c, role).BaseUrl, c.BaseUrl);

    /// <summary>The model a role on another server completes with: its own, never the chat's, which that server may not
    /// have; null when none is chosen.</summary>
    public static string? CompletionModel(InferpalConfig c, string? asked) =>
        !string.IsNullOrWhiteSpace(asked) ? asked
        : !string.IsNullOrWhiteSpace(c.InlineCompletionModel) ? c.InlineCompletionModel
        : OnChatServer(c, ServerRole.Completion) ? c.DefaultModel
        : null;

    /// <summary>Says once what <paramref name="role"/>'s settings leave to a fallback: "another server" with no address
    /// runs on the chat's, and a server of its own whose kind is not set is spoken to as the chat's kind.</summary>
    public static void NoteFallbacks(InferpalConfig c, ServerRole role)
    {
        var (runsOn, url, provider, what) = role switch
        {
            ServerRole.Completion => (c.InlineCompletionRunsOn, c.InlineCompletionBaseUrl, c.InlineCompletionProvider, "Inline completions"),
            ServerRole.Search     => (c.RagEmbeddingRunsOn, c.RagEmbeddingBaseUrl, c.RagEmbeddingProvider, "Code-search embeddings"),
            _                     => (null, null, null, null),
        };
        if (what is null || !Is(runsOn, OnOther)) return;
        if (string.IsNullOrWhiteSpace(url))
            Diagnostics.RecordOnce("Settings",
                $"{what} are set to run on another server, but no address is given: they run on the chat's server.",
                "role-no-address|" + role);
        else if (string.IsNullOrWhiteSpace(provider))
            Diagnostics.RecordOnce("Settings",
                $"{what} run on {Commands.DiagnosticsCommandHandler.RedactEndpoint(url)}, whose kind of server is not set: " +
                $"it is spoken to as the chat's ({c.Provider}). The Test button beside its address sets it.",
                "role-no-kind|" + role + "|" + url);
    }

    private static bool SameMachine(string? a, string? b)
    {
        string? Host(string? url)
        {
            if (Hardware.HardwareProbe.IsLoopback(url)) return "localhost";
            return Uri.TryCreate(ProviderProbe.RootOf(url), UriKind.Absolute, out var u) ? u.Host.ToLowerInvariant() : null;
        }
        var (ha, hb) = (Host(a), Host(b));
        return ha is null || hb is null || ha == hb;
    }

    private static bool Is(string? value, string expected) => string.Equals(value?.Trim(), expected, StringComparison.OrdinalIgnoreCase);

    private static string Or(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
