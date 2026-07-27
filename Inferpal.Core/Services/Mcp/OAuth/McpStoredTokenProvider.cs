namespace Inferpal.Services.Mcp.OAuth;

/// <summary>
/// Supplies bearer tokens for one MCP server from the encrypted <see cref="McpTokenStore"/>, refreshing
/// proactively via <see cref="McpOAuthFlow.RefreshAsync"/> when the stored access token is missing or
/// near expiry. Never launches the interactive browser flow — returns null when re-authorization is
/// needed, leaving the explicit "Authorize" action to the settings UI.
/// </summary>
internal sealed class McpStoredTokenProvider(string serverName, McpTokenStore store, McpOAuthFlow refreshFlow)
    : IMcpTokenProvider
{
    private static readonly TimeSpan ExpirySkew = TimeSpan.FromSeconds(60);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<string?> GetAccessTokenAsync(CancellationToken ct)
    {
        var state = store.Get(serverName);
        if (state is null) return null;
        if (state.HasUsableAccessToken(ExpirySkew)) return state.AccessToken;
        if (string.IsNullOrEmpty(state.RefreshToken)) return null;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-read under the gate: a concurrent caller may have refreshed already.
            state = store.Get(serverName);
            if (state is null) return null;
            if (state.HasUsableAccessToken(ExpirySkew)) return state.AccessToken;
            if (string.IsNullOrEmpty(state.RefreshToken)) return null;

            var refreshed = await refreshFlow.RefreshAsync(state, ct).ConfigureAwait(false);
            if (refreshed is null) return null;   // refresh rejected ⇒ user must re-authorize

            store.Save(serverName, refreshed);
            return refreshed.AccessToken;
        }
        finally
        {
            _gate.Release();
        }
    }
}
