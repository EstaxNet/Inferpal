using System.Net.Http;

namespace Inferpal.Services.Inference;

/// <summary>
/// The pipeline of the backend client: a connection that cannot be established within
/// <see cref="Timeout"/> fails as "unreachable" instead of waiting for the operating system to give up.
/// </summary>
/// <remarks>
/// <para>Without a connect timeout, a backend that never answers the TCP handshake — a powered-off machine behind a
/// router, a firewall that drops, a stopped Ollama under WSL's mirrored networking, where a closed loopback port
/// drops instead of refusing — holds every question for as long as the OS retries: about 21 s on Windows, 75 s on
/// macOS, 127 s on Linux. The answer then says "cannot reach", which was known two minutes earlier.</para>
/// <para>The connect timeout only covers establishing the connection (TCP, TLS). A generation that runs for minutes,
/// a model that loads cold, a server that accepts and stays silent: none of them is affected — those are bounded by
/// each call's own deadline.</para>
/// <para><see cref="SocketsHttpHandler"/> reports an expired connect timeout as a cancellation whose token is nobody's
/// (<c>TaskCanceledException</c> over a <c>TimeoutException</c>). Every caller reads a cancellation it did not ask
/// for as its own deadline — "the backend stopped responding (inactivity timeout)" — or as the user's cancel. It is
/// judged here on the TOKEN, never on the exception's shape: when the request's token is not cancelled, nobody
/// cancelled, so the connection could not be made.</para>
/// </remarks>
internal sealed class ConnectDeadline : DelegatingHandler
{
    /// <summary>How long establishing a connection to the backend may take.</summary>
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly TimeSpan _timeout;

    private ConnectDeadline(HttpMessageHandler inner, TimeSpan timeout) : base(inner) => _timeout = timeout;

    /// <summary>The backend pipeline: a <see cref="SocketsHttpHandler"/> bounded by <paramref name="timeout"/>, its
    /// expiry reported as a failed connection. <paramref name="connect"/> replaces the socket connection (tests).</summary>
    internal static HttpMessageHandler Create(
        TimeSpan timeout,
        Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>>? connect = null)
    {
        var sockets = new SocketsHttpHandler { ConnectTimeout = timeout };
        if (connect is not null) sockets.ConnectCallback = connect;
        return new ConnectDeadline(sockets, timeout);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            return await base.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new HttpRequestException(
                $"Nothing answered at {request.RequestUri?.Authority} within {_timeout.TotalSeconds:0} s "
                + "(no connection could be established).", ex);
        }
    }
}
