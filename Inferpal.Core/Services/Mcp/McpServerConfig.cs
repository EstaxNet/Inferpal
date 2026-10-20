using System.Text.Json;

namespace Inferpal.Services.Mcp;

/// <summary>Optional OAuth hints for an HTTP MCP server: a pre-registered <c>client_id</c>/<c>secret</c>
/// (for authorization servers without dynamic registration) and requested scopes. Absent ⇒ dynamic
/// client registration is attempted. The DCR-obtained client id and the tokens live in the encrypted
/// token store, not here.</summary>
internal sealed record McpOAuthConfig(
    string? ClientId = null,
    string? ClientSecret = null,
    IReadOnlyList<string>? Scopes = null);

/// <summary>An entry of the configured server list that never became a server, and why.</summary>
internal sealed record McpRejectedServer(string Name, string Reason);

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
/// <param name="DeclaresSse">The entry declares the older SSE transport (<c>"type": "sse"</c>): Streamable HTTP is tried,
/// and a failure says what it declared (<see cref="SseNote"/>).</param>
/// <param name="DisabledTools">The server's own names of the tools its entry turns off (<c>disabledTools</c>, Roo's
/// field): never offered to the model, like the ones the user turns off with <c>/tools off</c>.</param>
internal sealed record McpServerConfig(
    string Name,
    string? Command,
    IReadOnlyList<string> Args,
    IReadOnlyDictionary<string, string> Env,
    string? Url = null,
    IReadOnlyDictionary<string, string>? Headers = null,
    McpOAuthConfig? OAuth = null,
    bool Enabled = true,
    string? WorkingDirectory = null,
    bool DeclaresSse = false,
    IReadOnlyList<string>? DisabledTools = null)
{
    /// <summary>True when this entry targets a Streamable HTTP server (has a <c>url</c>) rather than stdio.</summary>
    public bool IsHttp => !string.IsNullOrWhiteSpace(Url);

    /// <summary>
    /// What a server's start failure adds when the entry declares the older SSE transport (<c>"type": "sse"</c>).
    /// </summary>
    /// <remarks>⚠ Inferpal speaks Streamable HTTP, which many of these servers also serve; when one does not, its
    /// failure alone ("405", "404") sends the user to check a URL that is right.</remarks>
    internal const string SseNote =
        " — it declares the SSE transport (\"type\": \"sse\"), which Inferpal does not speak: Streamable HTTP was tried";

    /// <summary>Why an entry declaring the WebSocket transport never becomes a server: nothing in Inferpal speaks it.</summary>
    internal const string WebSocketRefusal =
        "it declares the WebSocket transport, which Inferpal does not speak (stdio and Streamable HTTP only)";

    /// <summary>The transport an entry declares: <c>type</c> (Claude, VS Code, Continue) or <c>transport</c> (Roo).</summary>
    private static string? DeclaredTransport(JsonElement def)
    {
        foreach (var key in new[] { "type", "transport" })
            if (def.TryGetProperty(key, out var t) && t.ValueKind == JsonValueKind.String) return t.GetString();
        return null;
    }

    /// <summary>Whether a declared transport is WebSocket (<c>websocket</c>, <c>ws</c>), which nothing here speaks.</summary>
    internal static bool IsWebSocket(string? transport) =>
        string.Equals(transport, "websocket", StringComparison.OrdinalIgnoreCase)
        || string.Equals(transport, "ws", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Continue's fields for a remote server, as the headers they are: <c>requestOptions.headers</c>, and <c>apiKey</c>
    /// sent as <c>Authorization: Bearer</c> — on a copy of <paramref name="definition"/>, a header already written winning.
    /// </summary>
    /// <remarks>⚠ One reader for the repository's servers and for <c>/import</c>: read as they are written, a server
    /// that needs its key connected without it — "401", for a server the user configured right.</remarks>
    internal static System.Text.Json.Nodes.JsonObject LiftContinueFields(System.Text.Json.Nodes.JsonObject definition)
    {
        var lifted = (System.Text.Json.Nodes.JsonObject)definition.DeepClone();
        var options = lifted["requestOptions"] as System.Text.Json.Nodes.JsonObject;
        var fromOptions = options?["headers"] as System.Text.Json.Nodes.JsonObject;
        var apiKey = lifted["apiKey"] is System.Text.Json.Nodes.JsonValue k && k.TryGetValue<string>(out var key) ? key : null;
        if (fromOptions is null && apiKey is null) return lifted;

        var headers = lifted["headers"] as System.Text.Json.Nodes.JsonObject ?? new System.Text.Json.Nodes.JsonObject();
        foreach (var (name, value) in fromOptions?.ToList() ?? [])
            if (!headers.ContainsKey(name)) headers[name] = value?.DeepClone();
        if (apiKey is not null && lifted["url"] is not null && !headers.ContainsKey("Authorization"))
            headers["Authorization"] = "Bearer " + apiKey;
        lifted["headers"] = headers;
        options?.Remove("headers");
        if (options is { Count: 0 }) lifted.Remove("requestOptions");
        lifted.Remove("apiKey");
        return lifted;
    }

    /// <summary>
    /// Parses the raw config JSON into a list of server definitions.
    /// Never throws: malformed entries are skipped, returning whatever parsed cleanly.
    /// </summary>
    public static IReadOnlyList<McpServerConfig> Parse(string? json) => Parse(json, out _);

    /// <summary>The name a rejection carries when it is the WHOLE list that could not be read, not one entry.</summary>
    internal const string WholeList = "mcpServers";

    /// <summary>
    /// A server rebuilt from the fields the settings editor shows. What the editor does not show — the
    /// OAuth block, the working directory, the declared SSE transport, the tools the entry turns off — is carried over
    /// from <paramref name="source"/>.
    /// </summary>
    public static McpServerConfig FromEditor(
        McpServerConfig? source, string name, string? command, IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string> env, string? url, IReadOnlyDictionary<string, string>? headers,
        bool enabled) =>
        string.IsNullOrWhiteSpace(url)
            ? new(name, command, args, env, Enabled: enabled, WorkingDirectory: source?.WorkingDirectory,
                  DisabledTools: source?.DisabledTools)
            : new(name, null, [], new Dictionary<string, string>(), Url: url, Headers: headers,
                  OAuth: source?.OAuth, Enabled: enabled, DeclaresSse: source?.DeclaresSse == true,
                  DisabledTools: source?.DisabledTools);

    /// <summary>
    /// <see cref="Parse(string?)"/> for the settings editor, which rewrites the whole list from what it
    /// read. <paramref name="rebuildable"/> is <c>true</c> only when every entry became a server: otherwise
    /// that rewrite would drop what could not be read.
    /// </summary>
    public static IReadOnlyList<McpServerConfig> ParseForEditor(string? json, out bool rebuildable)
    {
        var servers = Parse(json, out var rejected);
        rebuildable = rejected.Count == 0;
        return servers;
    }

    /// <summary>
    /// Same, returning the <b>rejected</b> entries and why.
    /// </summary>
    /// <remarks>
    /// They surfaced nowhere. The "failed" entries of <c>Statuses()</c> only cover servers that
    /// TRIED to start: an entry rejected here never was a server, so it disappeared from the screen
    /// as well as from the diagnostics. Yet it is the most likely error - a misspelt key - and the
    /// most opaque, since the server "does not exist".
    /// </remarks>
    public static IReadOnlyList<McpServerConfig> Parse(
        string? json, out IReadOnlyList<McpRejectedServer> rejected)
    {
        var problems = new List<McpRejectedServer>();
        rejected = problems;
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

            // ⚠ Not an object (an array, a string): no server can be read from it, and that is said like invalid JSON —
            // returned empty in silence, every server vanished, and the list read as one with no server at all.
            if (root.ValueKind != JsonValueKind.Object)
            {
                Reject(problems, string.Empty, $"the server list is a JSON {root.ValueKind}, not an object of servers");
                return [];
            }

            foreach (var entry in root.EnumerateObject())
            {
                var def = entry.Value;
                if (def.ValueKind != JsonValueKind.Object)
                {
                    Reject(problems, entry.Name, $"its entry is a {def.ValueKind}, not an object");
                    continue;
                }

                var command = def.TryGetProperty("command", out var c) && c.ValueKind == JsonValueKind.String
                    ? c.GetString()
                    : null;
                var url = def.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String
                    ? u.GetString()
                    : null;

                // An entry must declare a transport: either a stdio command or an HTTP url.
                // The most likely of the three: a misspelt key ("cmd" for "command"). The server
                // then appeared NOWHERE - not even among the failures /mcp lists, which only cover
                // the ones that tried to start.
                if (string.IsNullOrWhiteSpace(command) && string.IsNullOrWhiteSpace(url))
                {
                    Reject(problems, entry.Name, $"it declares neither a \"command\" (stdio) nor a \"url\" (HTTP)");
                    continue;
                }

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

                // ⚠ The folder a stdio server starts in: unread, `"cwd": "~/tools/mcp"` started it in the editor host's
                // own folder — "can't open file server.py" — and the Visual Studio list, which rewrites the map from what
                // it read, dropped the key at the first edit.
                var cwd = def.TryGetProperty("cwd", out var w) && w.ValueKind == JsonValueKind.String
                          && !string.IsNullOrWhiteSpace(w.GetString()) ? w.GetString()!.Trim() : null;

                // Claude's and VS Code's "type", Roo's "transport": the transport the server says it speaks.
                var declared = DeclaredTransport(def);
                if (IsWebSocket(declared))
                {
                    // Tried as Streamable HTTP, a ws:// address fails on its scheme — a fault that names the URL, not the cause.
                    Reject(problems, entry.Name, WebSocketRefusal);
                    continue;
                }
                var sse = string.Equals(declared, "sse", StringComparison.OrdinalIgnoreCase);

                // Roo's tools turned off for this server, by the server's own names.
                var off = new List<string>();
                if (def.TryGetProperty("disabledTools", out var dt) && dt.ValueKind == JsonValueKind.Array)
                    foreach (var item in dt.EnumerateArray())
                        if (item.ValueKind == JsonValueKind.String && item.GetString()!.Trim() is { Length: > 0 } t && !off.Contains(t))
                            off.Add(t);

                servers.Add(new McpServerConfig(
                    entry.Name.Trim(), command, args, env,
                    Url: url, Headers: headers.Count > 0 ? headers : null, OAuth: oauth, Enabled: enabled,
                    WorkingDirectory: cwd, DeclaresSse: sse, DisabledTools: off.Count > 0 ? off : null));
            }
        }
        catch (JsonException ex)
        {
            // Malformed JSON -> treat as "no servers configured" rather than crashing startup.
            // The fallback is right; staying silent was not. One comma too many and EVERY MCP
            // server disappears - the model loses all their tools at once, and nothing says so.
            Reject(problems, string.Empty, $"the server list is not valid JSON, so no server is configured ({ex.Message})");
            return servers;
        }

        return servers;
    }

    /// <summary>Records a rejected entry once, for both readers: <c>/diagnostics</c> and the status
    /// list the two front-ends already render.</summary>
    private static void Reject(List<McpRejectedServer> problems, string name, string reason)
    {
        // A PAIR, not "name: reason" inside one string: the reason for invalid JSON carries the
        // exception message, which itself contains colons (LineNumber, BytePositionInLine). Joining
        // then splitting again would have produced an absurd server name on screen.
        problems.Add(new McpRejectedServer(string.IsNullOrEmpty(name) ? WholeList : name, reason));
        Diagnostics.Record("Mcp", string.IsNullOrEmpty(name)
            ? $"Server list ignored: {reason}."
            : $"Server '{name}' ignored: {reason}.");
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
    /// HTTP entries write <c>url</c>/<c>headers</c>; stdio entries write <c>command</c>/<c>args</c>/<c>env</c>/<c>cwd</c>.
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
                if (s.DeclaresSse) def["type"] = "sse";   // kept, or a list rewritten from records forgets it
                if (s.Headers is { Count: > 0 }) def["headers"] = s.Headers;
                if (SerializeOAuth(s.OAuth) is { } oauth) def["oauth"] = oauth;
            }
            else
            {
                def["command"] = s.Command ?? string.Empty;
                if (s.Args.Count > 0) def["args"] = s.Args;
                if (s.Env.Count > 0)  def["env"]  = s.Env;
                if (!string.IsNullOrWhiteSpace(s.WorkingDirectory)) def["cwd"] = s.WorkingDirectory;
            }
            if (!s.Enabled) def["disabled"] = true;
            if (s.DisabledTools is { Count: > 0 } off) def["disabledTools"] = off;

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
