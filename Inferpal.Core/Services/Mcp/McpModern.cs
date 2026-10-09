using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Inferpal.Services.Mcp;

/// <summary>The two eras of MCP a server can speak.</summary>
internal enum McpEra
{
    /// <summary>Revisions up to 2025-11-25: an <c>initialize</c> handshake opens a session.</summary>
    Legacy,

    /// <summary>Revision 2026-07-28 and later: no handshake, every request carries its version and capabilities.</summary>
    Modern,
}

/// <summary>What the first exchange decided: the era to speak, or why this server cannot be spoken to at all.</summary>
/// <param name="ToolsListChanged">The server announced <c>tools.listChanged</c> — in the modern era it says so only
/// on an open <c>subscriptions/listen</c>.</param>
internal readonly record struct McpDiscovery(McpEra Era, string? Failure, bool ToolsListChanged)
{
    public static McpDiscovery Legacy => new(McpEra.Legacy, null, false);
}

/// <summary>
/// The wire facts of the 2026-07-28 revision both transports share: the per-request <c>_meta</c>, the error codes the
/// specification reserves, and the era decision a client reads off its first exchange (<c>server/discover</c>).
/// </summary>
/// <remarks>
/// ⚠ A 2026-07-28 server has no <c>initialize</c>: a client that opens with the handshake is refused outright, on both
/// transports, and the specification gives that client no way forward. The other way round, a server of the earlier
/// revisions answers a modern request with an implementation-defined error, or not at all. So the client probes
/// first and falls back — the decision rule is <see cref="Classify(JsonElement)"/> and <see cref="FromError"/>.
/// </remarks>
internal static partial class McpModern
{
    /// <summary>The revision this client speaks when the server does.</summary>
    public const string Version = "2026-07-28";

    /// <summary>What the <c>initialize</c> handshake announces. Unchanged on purpose: a server that works with it today
    /// must still work.</summary>
    public const string LegacyVersion = "2024-11-05";

    public const string VersionKey      = "io.modelcontextprotocol/protocolVersion";
    public const string ClientInfoKey   = "io.modelcontextprotocol/clientInfo";
    public const string CapabilitiesKey = "io.modelcontextprotocol/clientCapabilities";

    /// <summary>The codes the specification reserves for itself (<c>-32020</c> to <c>-32099</c>) and defines.</summary>
    public const long HeaderMismatch                  = -32020;
    public const long MissingRequiredClientCapability = -32021;
    public const long UnsupportedProtocolVersion      = -32022;

    /// <summary>How the client names itself, on every request of the modern era and in the legacy handshake.</summary>
    public static JsonObject ClientInfo() => new() { ["name"] = "Inferpal", ["version"] = "1.0" };

    /// <summary>
    /// A copy of <paramref name="params"/> carrying the per-request metadata the modern era requires.
    /// </summary>
    /// <remarks>
    /// The capabilities are EMPTY: Inferpal answers no elicitation, sampling or roots request, and a server must not
    /// ask a client for what it did not declare — so declaring nothing is what keeps such requests from arriving.
    /// </remarks>
    public static JsonObject WithMeta(JsonNode? @params)
    {
        var copy = @params?.DeepClone() as JsonObject ?? new JsonObject();
        var meta = copy["_meta"] as JsonObject ?? new JsonObject();
        meta[VersionKey]      = Version;
        meta[ClientInfoKey]   = ClientInfo();
        meta[CapabilitiesKey] = new JsonObject();
        copy["_meta"] = meta;
        return copy;
    }

    /// <summary>True when the JSON-RPC message was written for the modern era: its <c>_meta</c> names this version.</summary>
    public static bool IsModern(JsonNode? message) =>
        message is JsonObject m && m["params"] is JsonObject p && p["_meta"] is JsonObject meta
        && meta[VersionKey] is JsonValue v && v.TryGetValue<string>(out var version) && version == Version;

    /// <summary>
    /// The era a server speaks, read off its answer to <c>server/discover</c>.
    /// </summary>
    /// <remarks>
    /// Only a <c>DiscoverResult</c> — an object with <c>supportedVersions</c> — is an answer of the modern era; any other
    /// result is a server that does not know the method. A modern server that does not speak THIS revision but lists an
    /// earlier one is spoken to with the handshake of the earlier ones.
    /// </remarks>
    public static McpDiscovery Classify(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("supportedVersions", out var listed) || listed.ValueKind != JsonValueKind.Array)
            return McpDiscovery.Legacy;

        var versions = ReadStrings(listed);
        if (versions.Contains(Version, StringComparer.Ordinal))
            return new McpDiscovery(McpEra.Modern, null, AnnouncesToolsListChanged(result));
        return versions.Any(IsEarlierVersion) ? McpDiscovery.Legacy : new McpDiscovery(McpEra.Legacy, NoCommonVersion(versions), false);
    }

    /// <summary>
    /// The era a server speaks, read off the way it REFUSED <c>server/discover</c>.
    /// </summary>
    /// <remarks>
    /// ⚠ Only a recognised modern error identifies a modern server; anything else — <c>-32601</c>, a 2025 session error,
    /// a status without a JSON-RPC body, no answer — is a server of the earlier revisions. The fallback is never keyed to
    /// one code: those servers answer an unknown request before their handshake each in their own way.
    /// </remarks>
    public static McpDiscovery FromError(Exception error)
    {
        var (code, message, data) = error switch
        {
            McpRpcException rpc                          => (rpc.Code, rpc.ServerMessage, rpc.RpcData),
            McpHttpRefusedException { RpcCode: { } c } h => (c, h.ServerMessage, h.RpcData),
            _                                            => ((long?)null, string.Empty, (JsonElement?)null),
        };
        switch (code)
        {
            case UnsupportedProtocolVersion:
                var supported = data is { ValueKind: JsonValueKind.Object } d
                                && d.TryGetProperty("supported", out var s) && s.ValueKind == JsonValueKind.Array
                    ? ReadStrings(s)
                    : [];
                if (supported.Contains(Version, StringComparer.Ordinal))
                    return new McpDiscovery(McpEra.Legacy, $"the server refused protocol version {Version} while listing it as supported: {message}", false);
                return supported.Any(IsEarlierVersion) ? McpDiscovery.Legacy : new McpDiscovery(McpEra.Legacy, NoCommonVersion(supported), false);
            case HeaderMismatch or MissingRequiredClientCapability:
                return new McpDiscovery(McpEra.Legacy, $"the server refused the discovery request: {message}", false);
            default:
                return McpDiscovery.Legacy;
        }
    }

    /// <summary>A date-shaped version earlier than this revision: one the <c>initialize</c> handshake serves.</summary>
    private static bool IsEarlierVersion(string version) =>
        VersionShape().IsMatch(version) && string.CompareOrdinal(version, Version) < 0;

    private static string NoCommonVersion(IReadOnlyList<string> versions) =>
        versions.Count == 0
            ? $"the server names no protocol version it supports; Inferpal speaks {Version} and the initialize handshake of the earlier versions"
            : $"the server supports protocol version {string.Join(", ", versions)} only; Inferpal speaks {Version} and the initialize handshake of the earlier versions";

    private static bool AnnouncesToolsListChanged(JsonElement result) =>
        result.TryGetProperty("capabilities", out var caps) && caps.ValueKind == JsonValueKind.Object
        && caps.TryGetProperty("tools", out var tools) && tools.ValueKind == JsonValueKind.Object
        && tools.TryGetProperty("listChanged", out var changed) && changed.ValueKind == JsonValueKind.True;

    private static List<string> ReadStrings(JsonElement array) =>
        array.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}$")]
    private static partial Regex VersionShape();
}

/// <summary>
/// A JSON-RPC error the server answered, with its code and data — what the era decision and the version negotiation
/// read. The message is the one both clients have always given (<c>MCP error: …</c>).
/// </summary>
internal sealed class McpRpcException : InvalidOperationException
{
    public McpRpcException(JsonElement error) : base($"MCP error: {McpJsonRpc.ErrorMessage(error)}")
    {
        ServerMessage = McpJsonRpc.ErrorMessage(error);
        Code          = McpJsonRpc.ErrorCode(error);
        RpcData       = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("data", out var d) ? d.Clone() : null;
    }

    /// <summary>The <c>code</c> of the error; <c>null</c> when the server sent none, or not as an integer.</summary>
    public long? Code { get; }

    /// <summary>The <c>data</c> of the error, detached from the response it came in.</summary>
    public JsonElement? RpcData { get; }

    /// <summary>The server's own words, without the <c>MCP error:</c> prefix.</summary>
    public string ServerMessage { get; }
}

/// <summary>
/// An HTTP refusal (a status outside 2xx), with the reason its body gave — and, when that body is a JSON-RPC error, its
/// code and data: a modern server refuses with <c>400</c> and a recognised code where an earlier one says nothing useful.
/// </summary>
/// <remarks>Still an <see cref="HttpRequestException"/>: every caller that branches on the status keeps working.</remarks>
internal sealed class McpHttpRefusedException(string message, System.Net.HttpStatusCode status, long? rpcCode,
                                              JsonElement? rpcData, string serverMessage)
    : HttpRequestException(message, null, status)
{
    public long? RpcCode { get; } = rpcCode;
    public JsonElement? RpcData { get; } = rpcData;
    public string ServerMessage { get; } = serverMessage;
}
