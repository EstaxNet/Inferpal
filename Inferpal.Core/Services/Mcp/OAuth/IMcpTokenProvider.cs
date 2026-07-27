namespace Inferpal.Services.Mcp.OAuth;

/// <summary>
/// Supplies the current OAuth bearer token for an MCP server to <see cref="McpHttpClient"/>. The
/// implementation is responsible for proactively refreshing an expired token (using a stored refresh
/// token) but **never** launches the interactive browser flow — that is triggered explicitly from the
/// settings UI. Returns null when no usable token exists, i.e. the user must authorize first.
/// </summary>
internal interface IMcpTokenProvider
{
    Task<string?> GetAccessTokenAsync(CancellationToken ct);
}
