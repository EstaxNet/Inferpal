using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;

namespace Inferpal.Services.Inference;

/// <summary>
/// The headers a model server behind a reverse proxy asks for (<c>requestHeaders</c>: <c>Authorization: Basic …</c>, a
/// proxy token, Cloudflare Access's pair), added to every request sent to that server's ORIGIN — one funnel in the
/// backend pipeline, never a call site each.
/// </summary>
/// <remarks>
/// <para>⚠ A header is a secret, and a redirect must not carry it elsewhere. <see cref="SocketsHttpHandler"/> drops
/// <c>Authorization</c> on a redirect but keeps every other header, to whatever origin the answer names. A request that
/// carries these headers therefore follows its redirects HERE, to the same host only (Caddy's <c>http → https</c>
/// passes), and stops on the 3xx otherwise. A request without them keeps the handler's own redirects: the model
/// server's address is the person's, and a <c>308</c> in front of it works.</para>
/// <para>The origins are learnt from the clients as they build their URLs (<see cref="Note"/>): the pipeline is shared
/// by every configuration of the process, and a configuration is not reachable from a handler.</para>
/// </remarks>
internal static class ServerHeaders
{
    private static readonly ConcurrentDictionary<string, IReadOnlyList<KeyValuePair<string, string>>> ByOrigin =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The most redirects a request carrying the headers follows.</summary>
    private const int MaxRedirects = 5;

    /// <summary>The headers of <paramref name="text"/>, one <c>Name: value</c> a line; blank lines and <c>#</c> comments
    /// are skipped, a malformed line is dropped and said once — by its name, never its value.</summary>
    internal static IReadOnlyList<KeyValuePair<string, string>> Parse(string? text)
    {
        var headers = new List<KeyValuePair<string, string>>();
        foreach (var raw in (text ?? string.Empty).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var colon = line.IndexOf(':');
            var name = colon > 0 ? line[..colon].Trim() : string.Empty;
            var value = colon > 0 ? line[(colon + 1)..].Trim() : string.Empty;
            // A Content-* header belongs to the body, never to the request: it would not be sent.
            if (name.Length == 0 || value.Length == 0 || !IsToken(name) || name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
            {
                // The value is the secret: the line is said by its name alone.
                var shown = (name.Length > 0 ? name : "(no name)") + ": …";
                Diagnostics.DroppedLineOnce("RequestHeaders", "Request header ignored (expected Name: value)", shown, shown);
                continue;
            }
            headers.Add(new(name, value));
        }
        return headers;
    }

    /// <summary>The server at <paramref name="baseUrl"/> takes the headers of <paramref name="text"/> (none: none).</summary>
    internal static void Note(string? baseUrl, string? text)
    {
        var origin = OriginOf(baseUrl);
        if (origin is null) return;
        var headers = Parse(text);
        if (headers.Count == 0) ByOrigin.TryRemove(origin, out _);
        else ByOrigin[origin] = headers;
    }

    /// <summary>Forgets every origin (tests).</summary>
    internal static void Clear() => ByOrigin.Clear();

    /// <summary>The headers a request to <paramref name="uri"/> carries; empty when its origin has none.</summary>
    internal static IReadOnlyList<KeyValuePair<string, string>> For(Uri? uri) =>
        OriginOf(uri) is { } o && ByOrigin.TryGetValue(o, out var h) ? h : [];

    private static string? OriginOf(string? url) =>
        Uri.TryCreate((url ?? string.Empty).Trim(), UriKind.Absolute, out var u) ? OriginOf(u) : null;

    private static string? OriginOf(Uri? u) =>
        u is { IsAbsoluteUri: true } && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)
            ? $"{u.Scheme}://{u.IdnHost}:{u.Port}"
            : null;

    // RFC 9110 token: what a header name may be made of.
    private static bool IsToken(string name) =>
        name.All(ch => ch < 127 && ch > 32 && "()<>@,;:\\\"/[]?={}".IndexOf(ch) < 0);

    /// <summary>The pipeline stage: adds the headers, and follows a headed request's redirects to its own host only.
    /// <paramref name="redirecting"/> follows redirects as before; <paramref name="direct"/> follows none.</summary>
    internal sealed class Handler(HttpMessageHandler redirecting, HttpMessageHandler direct) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _redirecting = new(redirecting);
        private readonly HttpMessageInvoker _direct = new(direct);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var headers = For(request.RequestUri);
            if (headers.Count == 0) return await _redirecting.SendAsync(request, ct).ConfigureAwait(false);

            for (var hop = 0; ; hop++)
            {
                foreach (var (name, value) in headers)
                {
                    request.Headers.Remove(name);
                    request.Headers.TryAddWithoutValidation(name, value);
                }
                var response = await _direct.SendAsync(request, ct).ConfigureAwait(false);
                var next = RedirectTarget(response, request.RequestUri!);
                if (next is null || hop >= MaxRedirects || !SameServer(request.RequestUri!, next))
                    return response;   // ⚠ Another server never sees the headers: the 3xx is the answer.
                response.Dispose();
                request = Follow(request, response.StatusCode, next);
                headers = For(next).Count > 0 ? For(next) : headers;
            }
        }

        /// <summary>The same origin, or the same host moving from http to https on its default port (Caddy's upgrade).
        /// Never back to http, where the secret would travel in clear, nor to another port, another service.</summary>
        internal static bool SameServer(Uri from, Uri to) =>
            OriginOf(from) == OriginOf(to)
            || (from.Scheme == Uri.UriSchemeHttp && to.Scheme == Uri.UriSchemeHttps && to.IsDefaultPort
                && string.Equals(from.IdnHost, to.IdnHost, StringComparison.OrdinalIgnoreCase));

        private static Uri? RedirectTarget(HttpResponseMessage response, Uri from) =>
            (int)response.StatusCode is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is { } loc
                ? (loc.IsAbsoluteUri ? loc : new Uri(from, loc))
                : null;

        /// <summary>The next hop, as <see cref="SocketsHttpHandler"/> builds it: 307 and 308 resend the method and the
        /// body; 303, and 301 or 302 after a POST, become a GET without one.</summary>
        private static HttpRequestMessage Follow(HttpRequestMessage from, HttpStatusCode status, Uri to)
        {
            var keep = status is HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect
                       || (status != HttpStatusCode.SeeOther && from.Method != HttpMethod.Post);
            var next = new HttpRequestMessage(keep ? from.Method : HttpMethod.Get, to)
            {
                Version = from.Version,
                Content = keep ? from.Content : null,
            };
            foreach (var h in from.Headers) next.Headers.TryAddWithoutValidation(h.Key, h.Value);
            foreach (var o in from.Options) ((IDictionary<string, object?>)next.Options)[o.Key] = o.Value;
            return next;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _redirecting.Dispose(); _direct.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
