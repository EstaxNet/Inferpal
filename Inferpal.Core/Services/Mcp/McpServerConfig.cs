using System.Text.Json;

namespace Inferpal.Services.Mcp;

/// <summary>
/// One MCP server entry parsed from <see cref="Config.InferpalConfig.McpServersJson"/>.
/// </summary>
/// <remarks>
/// The JSON format mirrors the Claude Desktop / Continue convention — a top-level map
/// keyed by server name. A <b>stdio</b> server has a <c>command</c> (+ optional <c>args</c>/<c>env</c>);
/// a <b>Streamable HTTP</b> server has a <c>url</c> (+ optional <c>headers</c>):
/// <code>
/// {
///   "filesystem": {
///     "command": "npx",
///     "args": ["-y", "@modelcontextprotocol/server-filesystem", "C:\\dev"],
///     "env": { "FOO": "bar" }
///   },
///   "remote": {
///     "url": "https://mcp.example.com/mcp",
///     "headers": { "Authorization": "Bearer ${MY_TOKEN}" }
///   }
/// }
/// </code>
/// Header values support <c>${ENV_VAR}</c> expansion at connection time so secrets stay out of the
/// stored config (see <see cref="McpHttpClient"/>).
/// </remarks>
/// <summary>Optional OAuth hints for an HTTP MCP server: a pre-registered <c>client_id</c>/<c>secret</c>
/// (for authorization servers without dynamic registration) and requested scopes. Absent ⇒ dynamic
/// client registration is attempted. The DCR-obtained client id and the tokens live in the encrypted
/// token store, not here.</summary>
internal sealed record McpOAuthConfig(
    string? ClientId = null,
    string? ClientSecret = null,
    IReadOnlyList<string>? Scopes = null);

internal sealed record McpServerConfig(
    string Name,
    string? Command,
    IReadOnlyList<string> Args,
    IReadOnlyDictionary<string, string> Env,
    string? Url = null,
    IReadOnlyDictionary<string, string>? Headers = null,
    McpOAuthConfig? OAuth = null,
    bool Enabled = true)
{
    /// <summary>True when this entry targets a Streamable HTTP server (has a <c>url</c>) rather than stdio.</summary>
    public bool IsHttp => !string.IsNullOrWhiteSpace(Url);

    /// <summary>
    /// Parses the raw config JSON into a list of server definitions.
    /// Never throws: malformed entries are skipped, returning whatever parsed cleanly.
    /// </summary>
    public static IReadOnlyList<McpServerConfig> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        var servers = new List<McpServerConfig>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Accept either { "name": {...} } directly or { "mcpServers": { "name": {...} } }.
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("mcpServers", out var wrapped)
                && wrapped.ValueKind == JsonValueKind.Object)
                root = wrapped;

            if (root.ValueKind != JsonValueKind.Object)
                return [];

            foreach (var entry in root.EnumerateObject())
            {
                var def = entry.Value;
                if (def.ValueKind != JsonValueKind.Object) continue;

                var command = def.TryGetProperty("command", out var c) && c.ValueKind == JsonValueKind.String
                    ? c.GetString()
                    : null;
                var url = def.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String
                    ? u.GetString()
                    : null;

                // An entry must declare a transport: either a stdio command or an HTTP url.
                if (string.IsNullOrWhiteSpace(command) && string.IsNullOrWhiteSpace(url)) continue;

                var args = new List<string>();
                if (def.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Array)
                    foreach (var item in a.EnumerateArray())
                        if (item.ValueKind == JsonValueKind.String)
                            args.Add(item.GetString()!);

                var env = ReadStringMap(def, "env");
                var headers = ReadStringMap(def, "headers");
                var oauth = ReadOAuth(def);

                // Optional per-server disable flag (absent ⇒ enabled). Keeps the format
                // backward-compatible with Claude Desktop / Continue configs.
                var enabled = !(def.TryGetProperty("disabled", out var d)
                                && d.ValueKind == JsonValueKind.True);

                servers.Add(new McpServerConfig(
                    entry.Name.Trim(), command, args, env,
                    Url: url, Headers: headers.Count > 0 ? headers : null, OAuth: oauth, Enabled: enabled));
            }
        }
        catch (JsonException)
        {
            // Malformed JSON → treat as "no servers configured" rather than crashing startup.
            return servers;
        }

        return servers;
    }

    private static Dictionary<string, string> ReadStringMap(JsonElement def, string property)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (def.TryGetProperty(property, out var e) && e.ValueKind == JsonValueKind.Object)
            foreach (var kv in e.EnumerateObject())
                if (kv.Value.ValueKind == JsonValueKind.String)
                    map[kv.Name] = kv.Value.GetString()!;
        return map;
    }

    private static McpOAuthConfig? ReadOAuth(JsonElement def)
    {
        if (!def.TryGetProperty("oauth", out var o) || o.ValueKind != JsonValueKind.Object) return null;

        var clientId = o.TryGetProperty("client_id", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        var secret   = o.TryGetProperty("client_secret", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;

        var scopes = new List<string>();
        if (o.TryGetProperty("scopes", out var sc) && sc.ValueKind == JsonValueKind.Array)
            foreach (var item in sc.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } v)
                    scopes.Add(v);

        if (clientId is null && secret is null && scopes.Count == 0) return null;
        return new McpOAuthConfig(clientId, secret, scopes.Count > 0 ? scopes : null);
    }

    /// <summary>
    /// Serialises a list of server definitions back to the Claude Desktop / Continue map format.
    /// HTTP entries write <c>url</c>/<c>headers</c>; stdio entries write <c>command</c>/<c>args</c>/<c>env</c>.
    /// Empty optional members are omitted; <c>disabled</c> is written only when the server is disabled.
    /// Names are de-duplicated (last write wins) since the format is a map.
    /// </summary>
    public static string Serialize(IReadOnlyList<McpServerConfig> servers)
    {
        var map = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var s in servers)
        {
            var name = s.Name.Trim();
            if (string.IsNullOrEmpty(name)) continue;

            var def = new Dictionary<string, object>();
            if (s.IsHttp)
            {
                def["url"] = s.Url!;
                if (s.Headers is { Count: > 0 }) def["headers"] = s.Headers;
                if (SerializeOAuth(s.OAuth) is { } oauth) def["oauth"] = oauth;
            }
            else
            {
                def["command"] = s.Command ?? string.Empty;
                if (s.Args.Count > 0) def["args"] = s.Args;
                if (s.Env.Count > 0)  def["env"]  = s.Env;
            }
            if (!s.Enabled) def["disabled"] = true;

            map[name] = def;
        }

        return JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true });
    }

    private static Dictionary<string, object>? SerializeOAuth(McpOAuthConfig? oauth)
    {
        if (oauth is null) return null;
        var od = new Dictionary<string, object>();
        if (!string.IsNullOrEmpty(oauth.ClientId))     od["client_id"]     = oauth.ClientId!;
        if (!string.IsNullOrEmpty(oauth.ClientSecret)) od["client_secret"] = oauth.ClientSecret!;
        if (oauth.Scopes is { Count: > 0 } sc)         od["scopes"]        = sc;
        return od.Count > 0 ? od : null;
    }
}

/// <summary>One tool advertised by an MCP server via <c>tools/list</c>.</summary>
/// <param name="Name">The tool name as exposed by the server (server-local, not namespaced).</param>
/// <param name="Description">Human/model-readable description.</param>
/// <param name="InputSchema">JSON Schema object for the tool arguments (passed straight to Ollama).</param>
internal sealed record McpToolInfo(string Name, string Description, JsonElement InputSchema);
