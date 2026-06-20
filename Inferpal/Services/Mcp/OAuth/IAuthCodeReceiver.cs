namespace Inferpal.Services.Mcp.OAuth;

/// <summary>
/// Drives the user-facing leg of the authorization-code flow: it owns the loopback redirect URI,
/// opens the authorization URL in the browser, and waits for the redirect carrying the code/state.
/// Abstracted so <see cref="McpOAuthFlow"/> can be tested without a real browser or listener.
/// </summary>
internal interface IAuthCodeReceiver
{
    /// <summary>The loopback redirect URI the authorization server will redirect back to
    /// (e.g. <c>http://127.0.0.1:51000/callback</c>). Sent as <c>redirect_uri</c> and registered via DCR.</summary>
    string RedirectUri { get; }

    /// <summary>Opens <paramref name="authorizationUrl"/> (browser) and awaits the redirect callback,
    /// returning the <c>code</c> and <c>state</c> query parameters. Throws on error/timeout/cancellation.</summary>
    Task<(string Code, string State)> GetAuthorizationCodeAsync(string authorizationUrl, CancellationToken ct);
}
