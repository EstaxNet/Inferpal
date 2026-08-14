using System.Text.Json;
using System.Text.Json.Nodes;

namespace Inferpal.Services.Mcp;

/// <summary>
/// The half of an MCP client that does not depend on how the bytes travel: listing tools, calling
/// one, and the budgets both operations run under.
/// </summary>
/// <remarks>
/// <para>
/// <c>tools/list</c> and <c>tools/call</c> are protocol, not transport — the request objects, the
/// timeouts and the response parsing are identical whether the server is spoken to over stdio or
/// Streamable HTTP. Both clients carried byte-identical copies of them, along with their own
/// <c>HandshakeTimeout</c> and <c>CallTimeout</c> constants, so the two transports could quietly
/// end up enforcing different budgets for the same call.
/// </para>
/// <para>
/// What stays per transport is exactly what differs: the handshake and
/// <see cref="SendRequestAsync"/>. HTTP re-initialises on a dropped session, stdio owns a child
/// process and its pipes — neither belongs here.
/// </para>
/// </remarks>
/// <remarks>
/// Deliberately not <c>: IMcpClient</c> — the rest of that contract (handshake, teardown, the
/// connection events) is transport all the way down. This base only owns what is genuinely shared.
/// </remarks>
internal abstract class McpClientBase
{
    /// <summary>Budget for the cheap, always-fast calls: handshake and tool listing.</summary>
    private protected static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Budget for a tool invocation, which may legitimately do real work.</summary>
    private protected static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(120);

    /// <summary>Transport-specific request/response exchange.</summary>
    private protected abstract Task<JsonElement> SendRequestAsync(
        string method, JsonNode @params, CancellationToken ct);

    /// <summary>Lists the tools the server advertises. Returns an empty list on failure.</summary>
    /// <remarks>
    /// Failure is empty, never an exception: an MCP server that is down must cost the user a
    /// missing tool, not a broken turn.
    /// </remarks>
    public async Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(HandshakeTimeout);
            var result = await SendRequestAsync("tools/list", new JsonObject(), cts.Token).ConfigureAwait(false);
            return McpJsonRpc.ParseTools(result);
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Calls a tool by its server-local name and returns the concatenated text content.
    /// </summary>
    public async Task<string> CallToolAsync(string toolName, JsonElement arguments, CancellationToken ct)
    {
        var callParams = new JsonObject { ["name"] = toolName };
        // Anything that is not an object or an array is not arguments; sending it through would
        // make the server reject a call the model could have made correctly.
        callParams["arguments"] = arguments.ValueKind is JsonValueKind.Object or JsonValueKind.Array
            ? JsonNode.Parse(arguments.GetRawText())
            : new JsonObject();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(CallTimeout);
        var result = await SendRequestAsync("tools/call", callParams, cts.Token).ConfigureAwait(false);
        return McpJsonRpc.ExtractCallResult(result, toolName);
    }
}
