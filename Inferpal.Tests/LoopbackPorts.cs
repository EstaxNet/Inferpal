using System.Net;
using System.Net.Sockets;

namespace Inferpal.Tests;

/// <summary>
/// Loopback ports for tests that run beside others: a port seen free is another test's a moment later, so a listener bound
/// after the port was found can fail to start, and a request to a "free" port can reach someone else's server.
/// </summary>
internal static class LoopbackPorts
{
    /// <summary>An <see cref="HttpListener"/> started on a free loopback port; a start that fails (the port taken in between)
    /// tries another one.</summary>
    internal static (HttpListener Server, int Port) StartHttpListener()
    {
        for (var attempt = 1; ; attempt++)
        {
            var port = Free();
            var server = new HttpListener();
            server.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                server.Start();
                return (server, port);
            }
            catch (HttpListenerException) when (attempt < 5)
            {
                try { server.Close(); } catch { /* teardown never throws (TeardownDoesNotThrowTests) */ }
            }
        }
    }

    /// <summary>A loopback port nothing listens on, a connection to it refused. Held bound (never listening) until the socket
    /// is disposed, so no other test can take it meanwhile — except on macOS, whose stack (BSD's) drops a connection to a
    /// bound socket that does not listen instead of refusing it: the request would wait out its deadline. There the port is
    /// released at once, free at the time of asking.</summary>
    internal static Socket Refusing(out int port)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        port = ((IPEndPoint)socket.LocalEndPoint!).Port;
        // shortcut: on macOS another test may take the port before the request; a held port that refuses needs a listener
        // that resets, if this ever turns red there.
        if (OperatingSystem.IsMacOS()) socket.Dispose();
        return socket;
    }

    /// <summary>A loopback port free at the time of asking.</summary>
    internal static int Free()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
