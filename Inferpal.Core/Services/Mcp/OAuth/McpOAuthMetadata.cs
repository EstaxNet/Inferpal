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

        return new AuthServerMetadata(
            ReadString(root, "issuer"),
            authorize!,
            token!,
            ReadString(root, "registration_endpoint"),
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
