using System.Text.Json;

namespace Inferpal.Services.Mcp;

/// <summary>
/// A connection to a single MCP server: handshake, tool discovery, and tool invocation.
/// Abstracts <see cref="McpStdioClient"/> so <see cref="McpToolService"/> can be driven by a
/// fake transport in tests (the production factory always yields a stdio client).
/// </summary>
internal interface IMcpClient : IAsyncDisposable
{
    /// <summary>The server name this client is bound to (for tool namespacing and diagnostics).</summary>
    string ServerName { get; }

    /// <summary>Last connection error, if <see cref="StartAsync"/> returned <c>false</c>.</summary>
    string? LastError { get; }

    /// <summary>True when the server requires OAuth authorization the user hasn't completed yet
    /// (HTTP only; always false for stdio).</summary>
    bool NeedsAuthorization { get; }

    /// <summary>Raised when the server signals its advertised tool set changed (live re-discovery).</summary>
    event Action? ToolsChanged;

    /// <summary>Raised once when the connection drops unexpectedly (not via <see cref="IAsyncDisposable.DisposeAsync"/>).</summary>
    event Action? Closed;

    /// <summary>Connects and performs the handshake. Returns <c>false</c> (never throws) on failure.</summary>
    Task<bool> StartAsync(CancellationToken ct);

    /// <summary>Lists the tools the server advertises. Returns an empty list on failure.</summary>
    Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken ct);

    /// <summary>Calls a tool by its server-local name and returns the concatenated text content.</summary>
    Task<string> CallToolAsync(string toolName, JsonElement arguments, CancellationToken ct);
}
