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

    /// <summary>The server name this client is bound to (for tool namespacing and diagnostics).</summary>
    public abstract string ServerName { get; }

    /// <summary>Pages of a tools/list followed before the listing stops and says so.</summary>
    private const int MaxToolPages = 50;

    /// <summary>Transport-specific request/response exchange: the params go as given, <c>_meta</c> included.</summary>
    private protected abstract Task<JsonElement> SendRequestAsync(
        string method, JsonNode @params, CancellationToken ct);

    /// <summary>Why the last handshake or tool listing failed.</summary>
    public string? LastError { get; private protected set; }

    /// <summary>The era this connection speaks, decided by the first exchange (see <see cref="McpModern"/>).</summary>
    private protected McpEra Era { get; set; } = McpEra.Legacy;

    /// <summary>The server announced it says when its tools change; in the modern era it says so only on an open
    /// <c>subscriptions/listen</c>.</summary>
    private protected bool ServerToolsListChanged { get; set; }

    /// <summary>How many times a call that answers <c>input_required</c> with state alone is retried.</summary>
    private const int MaxRoundTrips = 3;

    /// <summary>A request in the era this connection speaks: in the modern one, every request carries its <c>_meta</c>.</summary>
    private protected Task<JsonElement> RequestAsync(string method, JsonObject @params, CancellationToken ct) =>
        SendRequestAsync(method, Era == McpEra.Modern ? McpModern.WithMeta(@params) : @params, ct);

    /// <summary>The listed tools the transport can offer (all of them, unless a transport rule leaves one out).</summary>
    private protected virtual IReadOnlyList<McpToolInfo> Admit(IReadOnlyList<McpToolInfo> tools) => tools;

    /// <summary>
    /// A result that is not a final one is not read as one: <c>tools/list</c> may only complete, and a
    /// <c>resultType</c> this client does not know is invalid by the specification's own rule.
    /// </summary>
    private static void RequireComplete(JsonElement result, string method)
    {
        var type = McpJsonRpc.ResultTypeOf(result);
        if (type != McpJsonRpc.CompleteResult)
            throw new InvalidOperationException($"{method} answered a result of type '{type}', which it may not");
    }

    /// <summary>Lists the tools the server advertises; <c>null</c> when the listing failed, with the reason in
    /// <see cref="LastError"/>.</summary>
    /// <remarks>
    /// Never an exception: an MCP server that is down must cost the user a missing tool, not a broken turn.
    /// But never an empty list either: read as one, a slow reply to a list-changed notice removed every tool
    /// of the server, and a server that did not answer showed as connected with nothing to offer.
    /// ⚠ The listing is PAGINATED by the protocol: a server may answer one page and a <c>nextCursor</c>. Reading
    /// the first page only, every tool after it is missing without a word — and the model reads a missing tool as
    /// one the server does not have. A cursor that repeats, or pages beyond the bound, stop the listing with what it
    /// has, and say so.
    /// </remarks>
    public async Task<IReadOnlyList<McpToolInfo>?> ListToolsAsync(CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(HandshakeTimeout);
            var tools = new List<McpToolInfo>();
            string? cursor = null;
            for (var page = 1; ; page++)
            {
                var @params = new JsonObject();
                if (cursor is not null) @params["cursor"] = cursor;
                var result = await RequestAsync("tools/list", @params, cts.Token).ConfigureAwait(false);
                RequireComplete(result, "tools/list");
                tools.AddRange(McpJsonRpc.ParseTools(result));

                var next = McpJsonRpc.NextCursor(result);
                if (next is null) return Admit(tools);
                if (next == cursor || page >= MaxToolPages)
                {
                    Diagnostics.Record("Mcp",
                        $"'{ServerName}' tools/list: stopped after {page} page(s) ({tools.Count} tools) — " +
                        (next == cursor ? "the server repeated its cursor" : $"more than {MaxToolPages} pages") +
                        "; any tool listed after that is not offered.");
                    return Admit(tools);
                }
                cursor = next;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            LastError = $"tools/list got no answer within {HandshakeTimeout.TotalSeconds:0} s";
            return null;
        }
        catch (Exception ex)
        {
            LastError = $"tools/list failed: {ex.Message}";
            return null;
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
        try
        {
            for (var trip = 0; ; trip++)
            {
                var result = await RequestAsync("tools/call", callParams, cts.Token).ConfigureAwait(false);
                var type = McpJsonRpc.ResultTypeOf(result);
                if (type == McpJsonRpc.CompleteResult) return McpJsonRpc.ExtractCallResult(result, toolName);
                if (type != McpJsonRpc.InputRequiredResult)
                    return $"MCP tool '{toolName}' answered a result of type '{type}', which Inferpal does not know; the call did not complete.";

                // ⚠ input_required is not a result: read as one, the model would conclude the tool answered nothing.
                // Inferpal declares no elicitation, sampling or roots, so a request for one cannot be answered — named.
                if (McpJsonRpc.InputRequestMethods(result) is { Count: > 0 } asks)
                    return $"MCP tool '{toolName}' asked for input Inferpal does not provide ({string.Join(", ", asks)}); the call did not complete.";
                if (McpJsonRpc.RequestState(result) is not { } state)
                    return $"MCP tool '{toolName}' answered input_required without saying what it needs; the call did not complete.";
                if (trip + 1 >= MaxRoundTrips)
                    return $"MCP tool '{toolName}' asked for another round trip {MaxRoundTrips} times in a row; the call was stopped there and did not complete.";
                // State alone: the retry echoes it verbatim, as a new request.
                callParams["requestState"] = state;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The call's own budget, not the user's Stop: an error the model reads. As an
            // OperationCanceledException it stopped the whole agent run like a Stop.
            throw new TimeoutException($"MCP tool '{toolName}' did not answer within {CallTimeout.TotalSeconds:0} s.");
        }
    }
}
