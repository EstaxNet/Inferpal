namespace Inferpal.Services.Mcp.OAuth;

/// <summary>
/// Supplies bearer tokens for one MCP server from the encrypted <see cref="McpTokenStore"/>, refreshing
/// proactively via <see cref="McpOAuthFlow.RefreshAsync"/> when the stored access token is missing or
/// near expiry. Never launches the interactive browser flow — returns null when re-authorization is
/// needed, leaving the explicit "Authorize" action to the settings UI.
/// </summary>
/// <param name="resource">The server's canonical URL: a stored state obtained for another one is never used.</param>
/// <param name="rotationGrace">How long a refused renewal waits for the other editor's renewal to reach the store
/// (see <see cref="GetAccessTokenAsync"/>); one second by default.</param>
internal sealed class McpStoredTokenProvider(string serverName, string resource, McpTokenStore store, McpOAuthFlow refreshFlow,
                                             TimeSpan? rotationGrace = null)
    : IMcpTokenProvider
{
    private static readonly TimeSpan ExpirySkew = TimeSpan.FromSeconds(60);
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <exception cref="McpTokenRenewalException">The renewal did not happen (the authorization server down, busy or
    /// unreachable): the sign-in still stands, and the call fails with that cause instead of asking for a sign-in.</exception>
    public async Task<string?> GetAccessTokenAsync(CancellationToken ct)
    {
        var state = store.Get(serverName);
        if (state is null || !state.BelongsTo(resource)) return null;
        if (state.HasUsableAccessToken(ExpirySkew)) return state.AccessToken;
        if (string.IsNullOrEmpty(state.RefreshToken)) return null;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-read under the gate: a concurrent caller may have refreshed already.
            state = store.Get(serverName);
            if (state is null || !state.BelongsTo(resource)) return null;
            if (state.HasUsableAccessToken(ExpirySkew)) return state.AccessToken;
            if (string.IsNullOrEmpty(state.RefreshToken)) return null;

            var refreshed = await refreshFlow.RefreshAsync(state, ct).ConfigureAwait(false);
            // Refused: renewed by the other editor meanwhile, or the user must sign in again (null).
            if (refreshed is null) return await RenewedElsewhereAsync(state, ct).ConfigureAwait(false);

            store.Save(serverName, refreshed);
            return refreshed.AccessToken;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// After a refused renewal: the access token of the state the other editor renewed meanwhile — renewed once more when
    /// it is no longer usable — or <c>null</c> when nobody did and the user must sign in again.
    /// </summary>
    /// <remarks>
    /// ⚠ The store is shared by Visual Studio and VS Code, and both see the access token expire at the same moment. Both
    /// renew with the same refresh token; an authorization server that rotates refresh tokens grants the first and
    /// refuses the second — by design, the token it carried is spent. Read as "sign in again", the editor that lost the
    /// race withdrew the server's tools while the store already held a fresh sign-in.
    /// </remarks>
    private async Task<string?> RenewedElsewhereAsync(McpOAuthState tried, CancellationToken ct)
    {
        var now = store.Get(serverName);
        if (Unchanged(now, tried))
        {
            // The winner's answer may still be on its way to the store: one short wait, on a path that is rare.
            await Task.Delay(rotationGrace ?? TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            now = store.Get(serverName);
        }
        if (now is null || !now.BelongsTo(resource) || Unchanged(now, tried)) return null;
        if (now.HasUsableAccessToken(ExpirySkew)) return now.AccessToken;
        if (string.IsNullOrEmpty(now.RefreshToken)) return null;

        var renewed = await refreshFlow.RefreshAsync(now, ct).ConfigureAwait(false);
        if (renewed is null) return null;
        store.Save(serverName, renewed);
        return renewed.AccessToken;
    }

    private static bool Unchanged(McpOAuthState? now, McpOAuthState tried) =>
        now is not null && string.Equals(now.RefreshToken, tried.RefreshToken, StringComparison.Ordinal);
}
