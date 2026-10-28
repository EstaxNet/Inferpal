using System.Net;
using System.Net.Http;

namespace Inferpal.Services;

/// <summary>
/// The proxy every HTTP handler of the product sets: the system's (<see cref="HttpClient.DefaultProxy"/> — the proxy
/// variables, else the Windows or macOS settings), and, when it comes from the variables, never for this machine.
/// </summary>
/// <remarks>
/// ⚠ .NET sends a request to <c>localhost</c> through <c>HTTP_PROXY</c> like any other, and a corporate proxy cannot
/// reach the person's machine: the local model server that runs is said unreachable. A loopback address goes direct,
/// as in Chrome and in Ollama's own client; a local debugging proxy set by the variables does not see that traffic.
/// ⚠ The system settings' proxy is never wrapped: it carries its own failover (a PAC list "PROXY p1; PROXY p2; DIRECT"
/// tries the next entry, then direct), which .NET reads off an internal interface a wrapper would hide. The variables
/// carry no such list.
/// </remarks>
internal sealed class NetworkProxy(IWebProxy system) : IWebProxy
{
    /// <summary>The instance every handler of the product sets as its <c>Proxy</c> (ConventionCoverageTests rule 38).</summary>
    public static readonly IWebProxy Shared = Of(HttpClient.DefaultProxy, FromVariables());

    /// <summary>The system proxy, wrapped when it comes from the proxy variables.</summary>
    internal static IWebProxy Of(IWebProxy system, bool fromVariables) => fromVariables ? new NetworkProxy(system) : system;

    /// <summary>The variables .NET reads its proxy from, before the system settings.</summary>
    private static bool FromVariables() =>
        new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY" }.Any(name =>
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name.ToLowerInvariant())));

    /// <inheritdoc/>
    public ICredentials? Credentials
    {
        get => system.Credentials;
        set => system.Credentials = value;
    }

    /// <inheritdoc/>
    public Uri? GetProxy(Uri destination) => IsBypassed(destination) ? null : system.GetProxy(destination);

    /// <inheritdoc/>
    public bool IsBypassed(Uri host) => host.IsLoopback || system.IsBypassed(host);

    /// <summary>The proxy a request of the product to <paramref name="url"/> goes through, or <c>null</c> when it
    /// goes direct.</summary>
    public static Uri? Through(string? url) => Through(Shared, url);

    internal static Uri? Through(IWebProxy proxy, string? url)
    {
        // An address without its http(s) scheme ("localhost:11434") parses with "localhost" as its scheme: no request
        // of the product goes there, so no proxy is named for it.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var destination)
            || destination.Scheme != Uri.UriSchemeHttp && destination.Scheme != Uri.UriSchemeHttps)
            return null;
        if (proxy.IsBypassed(destination)) return null;
        var through = proxy.GetProxy(destination);
        // A proxy that answers the destination itself means "direct" (the .NET Framework contract).
        return through is null || through == destination ? null : through;
    }
}
