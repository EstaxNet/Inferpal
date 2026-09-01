using System.Text.Json;
using System.Text.RegularExpressions;

namespace Inferpal.Services.Mcp.OAuth;

/// <summary>OAuth 2.0 Protected Resource Metadata (RFC 9728), as advertised by an MCP server.</summary>
internal sealed record ProtectedResourceMetadata(string? Resource, IReadOnlyList<string> AuthorizationServers);

/// <summary>OAuth 2.0 Authorization Server Metadata (RFC 8414) — the endpoints and capabilities an MCP
/// client needs to run the authorization-code flow.</summary>
internal sealed record AuthServerMetadata(
    string? Issuer,
    string AuthorizationEndpoint,
    string TokenEndpoint,
    string? RegistrationEndpoint,
    IReadOnlyList<string> ScopesSupported,
    IReadOnlyList<string> CodeChallengeMethodsSupported);

/// <summary>
/// Pure parsing/derivation for MCP authorization discovery: the <c>WWW-Authenticate</c> challenge, the
/// two well-known metadata documents, the well-known URLs and default endpoints, and the canonical
/// resource URI (RFC 8707). No I/O — the HTTP fetching lives in the OAuth flow.
/// </summary>
internal static partial class McpOAuthMetadata
{
    /// <summary>Extracts the <c>resource_metadata</c> URL from a <c>WWW-Authenticate</c> header
    /// (RFC 9728 §5.1), or null when the header carries no such parameter.</summary>
    public static string? ParseResourceMetadataUrl(string? wwwAuthenticate)
    {
        if (string.IsNullOrWhiteSpace(wwwAuthenticate)) return null;
        var m = ResourceMetadataParam().Match(wwwAuthenticate);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Parses an RFC 9728 Protected Resource Metadata document.</summary>
    public static ProtectedResourceMetadata ParseProtectedResourceMetadata(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new ProtectedResourceMetadata(
            ReadString(root, "resource"),
            ReadStringArray(root, "authorization_servers"));
    }

    /// <summary>Parses an RFC 8414 Authorization Server Metadata document. Throws if the mandatory
    /// <c>authorization_endpoint</c>/<c>token_endpoint</c> are missing.</summary>
    public static AuthServerMetadata ParseAuthServerMetadata(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var authorize = ReadString(root, "authorization_endpoint");
        var token     = ReadString(root, "token_endpoint");
        if (string.IsNullOrEmpty(authorize) || string.IsNullOrEmpty(token))
            throw new FormatException("Authorization Server Metadata is missing authorization_endpoint or token_endpoint.");

        // Checked here, at the parse, rather than at each use: this record is the only thing the
        // flow carries forward, so validating it once means no later reader has to remember.
        RequireWebEndpoint(authorize, "authorization_endpoint");
        RequireWebEndpoint(token,     "token_endpoint");
        var registration = ReadString(root, "registration_endpoint");
        if (!string.IsNullOrEmpty(registration)) RequireWebEndpoint(registration, "registration_endpoint");

        return new AuthServerMetadata(
            ReadString(root, "issuer"),
            authorize!,
            token!,
            registration,
            ReadStringArray(root, "scopes_supported"),
            ReadStringArray(root, "code_challenge_methods_supported"));
    }

    /// <summary>Well-known Protected Resource Metadata URL for an MCP server (used when the
    /// <c>WWW-Authenticate</c> header omits one): <c>&lt;origin&gt;/.well-known/oauth-protected-resource</c>.</summary>
    public static string DefaultProtectedResourceMetadataUrl(Uri serverUrl) =>
        $"{Origin(serverUrl)}/.well-known/oauth-protected-resource";

    /// <summary>Well-known Authorization Server Metadata URL for an issuer/base URL:
    /// <c>&lt;origin&gt;/.well-known/oauth-authorization-server</c>.</summary>
    public static string DefaultAuthServerMetadataUrl(Uri authServer) =>
        $"{Origin(authServer)}/.well-known/oauth-authorization-server";

    /// <summary>Default endpoints (RFC-free fallback) when an AS exposes no metadata document, relative
    /// to the authorization base URL: <c>/authorize</c>, <c>/token</c>, <c>/register</c>.</summary>
    public static AuthServerMetadata DefaultEndpoints(Uri authServer)
    {
        var origin = Origin(authServer);
        return new AuthServerMetadata(origin, $"{origin}/authorize", $"{origin}/token", $"{origin}/register", [], [Pkce.Method]);
    }

    /// <summary>The canonical resource identifier of an MCP server (RFC 8707 / the <c>resource</c>
    /// parameter): lowercase scheme+host, explicit non-default port, no fragment, no trailing slash.</summary>
    public static string CanonicalResource(Uri serverUrl)
    {
        var s = $"{serverUrl.Scheme.ToLowerInvariant()}://{serverUrl.Host.ToLowerInvariant()}";
        if (!serverUrl.IsDefaultPort) s += $":{serverUrl.Port}";
        var path = serverUrl.AbsolutePath.TrimEnd('/');
        return s + path;
    }

    private static string Origin(Uri uri)
    {
        var s = $"{uri.Scheme.ToLowerInvariant()}://{uri.Host.ToLowerInvariant()}";
        return uri.IsDefaultPort ? s : $"{s}:{uri.Port}";
    }

    /// <summary>
    /// Validates a URL that came out of <b>remote</b> discovery before anything is done with it, and
    /// returns it parsed. HTTPS anywhere, HTTP only on loopback; everything else is refused.
    /// </summary>
    /// <param name="url">The candidate, from a metadata document or a <c>WWW-Authenticate</c> header.</param>
    /// <param name="what">Field name, for the message the user will read.</param>
    /// <exception cref="InvalidOperationException">The URL is not a web endpoint we may use.</exception>
    /// <remarks>
    /// <para>
    /// <b>Every string here is chosen by the far end.</b> The authorization-server URL comes from the
    /// MCP server's own Protected Resource Metadata, the endpoints from a document fetched at that
    /// URL, and the resource-metadata URL itself from a <c>WWW-Authenticate</c> response header. They
    /// were used unchecked, and two of the uses are not "just" a bad request:
    /// </para>
    /// <list type="bullet">
    ///   <item><c>authorization_endpoint</c> is handed to the browser opener, which shells out with
    ///         <c>UseShellExecute = true</c> — on Windows that runs whatever the scheme is registered
    ///         to, so a <c>file:</c> or custom-scheme value turns "authorize this MCP server" into
    ///         "launch this program".</item>
    ///   <item><c>token_endpoint</c> receives the authorization code and the client secret. Over
    ///         plain <c>http</c> to an arbitrary host, that is the credential itself, in the clear,
    ///         to a party of the server's choosing.</item>
    /// </list>
    /// <para>
    /// Loopback <c>http</c> stays allowed because that is how local MCP servers are actually run, and
    /// it is the one case where plaintext costs nothing.
    /// </para>
    /// </remarks>
    public static Uri RequireWebEndpoint(string? url, string what)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new InvalidOperationException($"The authorization server's {what} is not an absolute URL: '{url}'.");

        if (uri.Scheme == Uri.UriSchemeHttps) return uri;

        if (uri.Scheme == Uri.UriSchemeHttp && IsLoopback(uri)) return uri;

        throw new InvalidOperationException(
            $"Refusing the authorization server's {what}: '{url}'. " +
            "Only https, or http on loopback, may be used — this value comes from the remote server.");
    }

    /// <summary>Loopback by literal address or by the reserved name, without a DNS lookup.</summary>
    private static bool IsLoopback(Uri uri) =>
        uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || (System.Net.IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && System.Net.IPAddress.IsLoopback(ip));

    private static string? ReadString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static IReadOnlyList<string> ReadStringArray(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) return [];
        var list = new List<string>();
        foreach (var item in arr.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } s)
                list.Add(s);
        return list;
    }

    [GeneratedRegex(@"resource_metadata\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex ResourceMetadataParam();
}
