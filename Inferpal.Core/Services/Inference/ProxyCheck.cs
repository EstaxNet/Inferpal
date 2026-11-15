using System.Net;
using System.Net.Http;
using System.Security.Authentication;
using Inferpal.Localization;

namespace Inferpal.Services.Inference;

/// <summary>Where one hop of the Test stands.</summary>
internal enum HopState { Passed, Failed, NotReached }

/// <summary>One hop of the Test: what happened there, and what to check when it failed.</summary>
internal sealed record ProxyHop(HopState State, string Title, string? Detail = null, IReadOnlyList<string>? Checks = null);

/// <summary>The Test, hop by hop: this machine to the address, the proxy, the model server.</summary>
/// <param name="Provider">The backend the model server signs as, when it was reached.</param>
internal sealed record ProxyReport(IReadOnlyList<ProxyHop> Hops, string? Provider)
{
    /// <summary>The model server answered.</summary>
    public bool Passed => Provider is not null;
}

/// <summary>
/// The Test of a server behind a reverse proxy, said hop by hop: whether the address was reached (and its certificate
/// trusted), whether the proxy let the request through with what it was given, whether a model server answered behind
/// it — and, at the hop that refused, what to check there for the method in use.
/// </summary>
/// <remarks>
/// ⚠ The Test notes the FORM's headers for the address while it runs (<see cref="ServerHeaders.Note"/>, process-wide):
/// a request of the chat to that origin meanwhile carries them too; the caller puts the saved ones back after.
/// ⚠ The first request goes to the address's root through the backend pipeline (<see cref="ServerHeaders"/>), so a
/// redirect to another host is SEEN here instead of followed. Cloudflare Access answers missing or wrong credentials by a
/// redirect to its sign-in page (<c>*.cloudflareaccess.com</c>): named as that, not as a redirect.
/// </remarks>
internal static class ProxyCheck
{
    private static readonly HttpClient Http = new(ConnectDeadline.Create(TimeSpan.FromSeconds(8)))
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    /// <summary>Tests <paramref name="baseUrl"/> with the headers of <paramref name="headersText"/>, as the form holds them.</summary>
    internal static async Task<ProxyReport> RunAsync(string baseUrl, string? apiKey, string? headersText, CancellationToken ct)
    {
        var root = ProviderProbe.RootOf(baseUrl);
        if (!Uri.TryCreate(root + "/", UriKind.Absolute, out var uri))
            return new([new(HopState.Failed, Strings.ProxyHopUnreachable(root), Strings.ProxyHopNotAnAddress)], null);
        var host = uri.Host;
        var form = ProxyAccess.Read(headersText);
        var sent = ServerHeaders.Parse(headersText).Count;
        ServerHeaders.Note(root, headersText);

        HttpResponseMessage response;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            response = await Http.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (Find<AuthenticationException>(ex) is not null)
        {
            return Stopped(new(HopState.Failed, Strings.ProxyHopCertificate(host), Diagnostics.RootMessage(ex),
                               [Strings.ProxyCheckTrustAuthority]));
        }
        catch (Exception ex)
        {
            return Stopped(new(HopState.Failed, Strings.ProxyHopUnreachable(host), Diagnostics.RootMessage(ex),
                               [Strings.ProxyCheckAddress]));
        }

        var reached = new ProxyHop(HopState.Passed, Strings.ProxyHopReached(host),
                                   uri.Scheme == Uri.UriSchemeHttps ? Strings.ProxyHopHttps : Strings.ProxyHopHttp);
        using (response)
        {
            var status = (int)response.StatusCode;
            var landed = response.RequestMessage?.RequestUri;
            var location = response.Headers.Location is { } loc ? (loc.IsAbsoluteUri ? loc : new Uri(uri, loc)) : null;
            var signIn = (location ?? landed)?.Host.EndsWith("cloudflareaccess.com", StringComparison.OrdinalIgnoreCase) == true;

            // No headers: the handler followed redirects itself. Landed on another host (a forward-auth sign-in page):
            // that is the answer, never "let through".
            if (!signIn && landed is not null && !string.Equals(landed.Host, uri.Host, StringComparison.OrdinalIgnoreCase))
                return Refused(reached, new(HopState.Failed, Strings.ProxyHopRedirected(landed.GetLeftPart(UriPartial.Authority)),
                                            Strings.ProxyHopRedirectedDetail, [Strings.ProxyCheckRedirect]));
            if (signIn)
                return Refused(reached, new(HopState.Failed, Strings.ProxyHopSignInPage,
                                            sent > 0 ? Strings.ProxyHopSignInSent : Strings.ProxyHopSignInNone,
                                            ChecksFor(ProxyMethod.Cloudflare)));
            if (status is 401 or 403 or 407)
            {
                var code = $"{status} {response.ReasonPhrase}".Trim();
                if (sent > 0)
                    return Refused(reached, new(HopState.Failed, Strings.ProxyHopRefused(code), Sent(form), ChecksFor(form.Method)));
                // Nothing sent, and the root refused. A proxy asking for a password says so (WWW-Authenticate: Basic); a
                // model server that wants its key on every path does not: asked again WITH the key, one that answers,
                // or refuses a key that was given, is the cause — not a proxy.
                var basicAsked = response.Headers.WwwAuthenticate.Any(h => h.Scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase));
                if (!basicAsked)
                {
                    var keyed = await ProviderProbe.ExamineAsync(baseUrl, apiKey, ct).ConfigureAwait(false);
                    if (keyed.Provider is not null || (keyed.NeedsKey && !string.IsNullOrWhiteSpace(apiKey)))
                        return Finish(reached, new(HopState.Passed, Strings.ProxyHopNoCredentials), keyed);
                }
                return Refused(reached, new(HopState.Failed, Strings.ProxyHopAsks(code), Strings.ProxyHopAsksDetail,
                                            [Strings.ProxyCheckPickMethod]));
            }
            if (location is not null && status is >= 300 and < 400)
                return Refused(reached, new(HopState.Failed, Strings.ProxyHopRedirected(location.GetLeftPart(UriPartial.Authority)),
                                            Strings.ProxyHopRedirectedDetail, [Strings.ProxyCheckRedirect]));
        }

        var through = new ProxyHop(HopState.Passed, sent > 0 ? Strings.ProxyHopLetThrough : Strings.ProxyHopNoCredentials);
        return Finish(reached, through, await ProviderProbe.ExamineAsync(baseUrl, apiKey, ct).ConfigureAwait(false));

        // The model server's hop: it answered, it refused the API key (a 401/403 once through the proxy is the
        // server's), or something else answers there.
        static ProxyReport Finish(ProxyHop reached, ProxyHop through, ProviderProbe.ProbeAnswer answer) => new(
        [
            reached, through,
            answer.Provider is { } provider
                ? new ProxyHop(HopState.Passed, Strings.ProxyHopServer(InferenceProviderFactory.DisplayName(provider)))
                : answer.NeedsKey
                    ? new ProxyHop(HopState.Failed, Strings.ProxyHopKeyRefused(answer.Refusal ?? string.Empty), null, [Strings.ProxyCheckApiKey])
                    : new ProxyHop(HopState.Failed, Strings.ProxyHopNotAServer, answer.Refusal, [Strings.ProxyCheckForward]),
        ], answer.Provider);

        ProxyReport Stopped(ProxyHop first) =>
            new([first, new(HopState.NotReached, Strings.ProxyHopProxyNotReached), NotReached()], null);
        ProxyReport Refused(ProxyHop first, ProxyHop proxy) => new([first, proxy, NotReached()], null);
        ProxyHop NotReached() => new(HopState.NotReached, Strings.ProxyHopServerNotReached, Strings.ProxyHopStoppedBefore);
    }

    /// <summary>What was sent, said without a secret.</summary>
    private static string Sent(ProxyAccessForm form) => form.Method switch
    {
        ProxyMethod.Basic      => Strings.ProxySentBasic(form.User),
        ProxyMethod.Bearer     => Strings.ProxySentBearer,
        ProxyMethod.Cloudflare => Strings.ProxySentCloudflare(form.ClientId),
        _                      => Strings.ProxySentCustom,
    };

    /// <summary>What to check at a proxy that refused, for the method in use.</summary>
    internal static IReadOnlyList<string> ChecksFor(ProxyMethod method) => method switch
    {
        ProxyMethod.Basic      => [Strings.ProxyCheckBasicBrowser, Strings.ProxyCheckBasicHash, Strings.ProxyCheckBasicCase],
        ProxyMethod.Bearer     => [Strings.ProxyCheckBearerWhole, Strings.ProxyCheckBearerExpired],
        ProxyMethod.Cloudflare => [Strings.ProxyCheckCfPolicy, Strings.ProxyCheckCfSecret],
        _                      => [Strings.ProxyCheckCustom],
    };

    private static T? Find<T>(Exception? ex) where T : Exception
    {
        for (; ex is not null; ex = ex.InnerException)
            if (ex is T t) return t;
        return null;
    }
}
