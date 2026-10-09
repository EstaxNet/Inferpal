namespace Inferpal.Services.Mcp.OAuth;

/// <summary>
/// What the authorization redirect carried, read as it came: nothing in it is acted on before the flow has checked
/// <see cref="State"/> and <see cref="Issuer"/>.
/// </summary>
/// <param name="Issuer">The <c>iss</c> parameter (RFC 9207): which authorization server issued this response.</param>
/// <param name="Error">The <c>error</c> parameter of a refused authorization, with its description.</param>
internal sealed record AuthorizationResponse(string? Code, string State, string? Issuer = null,
                                             string? Error = null, string? ErrorDescription = null);

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

    /// <summary>Opens <paramref name="authorizationUrl"/> (browser) and awaits the redirect callback, returning its
    /// parameters as they came — an error response included. Throws on timeout or cancellation.</summary>
    Task<AuthorizationResponse> GetAuthorizationCodeAsync(string authorizationUrl, CancellationToken ct);
}
