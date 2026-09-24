using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Inferpal.Tests;

/// <summary>
/// Stand-in HTTP server on the loopback interface: returns a body per path, 404 when the function
/// returns <c>null</c>. Used by the inference client tests, which talk to a real socket rather than
/// to a fake <c>HttpMessageHandler</c> (the client shares a static <c>HttpClient</c>).
/// </summary>
/// <remarks>
/// ⚠ A <c>TcpListener</c>, not an <c>HttpListener</c>: on Windows an HttpListener prefix needs a URL
/// reservation, so a test passing here could refuse to start on a runner. A raw socket needs nothing.
/// The body function is called while the request is in flight, which lets a test observe the
/// client's state at that moment.
/// </remarks>
internal sealed class LoopbackHttpServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<string> _paths = [];

    /// <summary>Paths actually probed — the witness that the stand-in was really called.</summary>
    public IReadOnlyList<string> Paths { get { lock (_paths) return _paths.ToList(); } }

    public string BaseUrl => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

    /// <param name="body">The body answered for a path; <c>null</c> answers 404.</param>
    /// <param name="status">The status of a non-null body (default 200): how a test stands in for a server that refuses.</param>
    public LoopbackHttpServer(Func<string, string?> body, Func<string, int>? status = null)
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
                catch { return; }
                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        var stream = client.GetStream();
                        var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                        var line = await reader.ReadLineAsync();
                        var path = line?.Split(' ') is [_, var p, ..] ? p : string.Empty;
                        lock (_paths) _paths.Add(path);
                        var contentLength = 0;
                        var chunked       = false;
                        while (true)
                        {
                            var header = await reader.ReadLineAsync();
                            if (header is null || header.Length == 0) break;
                            if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                                int.TryParse(header["Content-Length:".Length..].Trim(), out contentLength);
                            if (header.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase)
                                && header.Contains("chunked", StringComparison.OrdinalIgnoreCase))
                                chunked = true;
                        }
                        // ⚠ Consume the request body before answering: closing a socket with unread
                        // data sends a reset, and the client reads "connection closed by the remote
                        // host" instead of the response. JsonContent streams its body CHUNKED (no
                        // Content-Length), so both framings are read. ASCII decoding: one char per byte.
                        if (chunked)
                        {
                            while (await reader.ReadLineAsync() is { } sizeLine
                                   && int.TryParse(sizeLine.Split(';')[0].Trim(),
                                                   System.Globalization.NumberStyles.HexNumber, null, out var size)
                                   && size > 0)
                            {
                                await reader.ReadBlockAsync(new char[size], 0, size);
                                await reader.ReadLineAsync(); // CRLF closing the chunk
                            }
                            await reader.ReadLineAsync();     // CRLF closing the (empty) trailer
                        }
                        else if (contentLength > 0)
                            await reader.ReadBlockAsync(new char[contentLength], 0, contentLength);
                        var payload = body(path);
                        var bytes = Encoding.UTF8.GetBytes(payload ?? "{}");
                        var code   = payload is null ? 404 : status?.Invoke(path) ?? 200;
                        var statusLine = code switch { 200 => "200 OK", 404 => "404 Not Found", 400 => "400 Bad Request", _ => $"{code} Status" };
                        // Explicit CRLF: the HTTP grammar requires it, Environment.NewLine is not CRLF
                        // everywhere, and this test also runs on a Linux runner.
                        const string crlf = "\r\n";
                        var head = Encoding.ASCII.GetBytes(
                            $"HTTP/1.1 {statusLine}{crlf}"
                            + $"Content-Type: application/json{crlf}"
                            + $"Content-Length: {bytes.Length}{crlf}"
                            + $"Connection: close{crlf}{crlf}");
                        await stream.WriteAsync(head);
                        await stream.WriteAsync(bytes);
                        await stream.FlushAsync();
                    }
                });
            }
        });
    }

    /// <summary>
    /// Teardown of a loopback listener: every call guarded, and disposing twice is a no-op.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>A teardown that throws turns a PASSING test into a failing one, and the failure names
    /// the cleanup instead of the subject.</b> `Cancel()` runs continuations, `Stop()` can raise a
    /// socket error, and a second `Dispose()` on a cancelled source throws on its own — none of
    /// which says anything about what the test measured. Four test files share this server, so one
    /// throwing teardown reddens all of them.
    /// ⚠ And the cost is larger than a red test: <b>a gating CI leg that reddens for something
    /// that is not the product is how a gating leg gets turned off again.</b> `catch {}` is the
    /// form this repository allows for pure cleanup, and this is exactly that.
    /// </remarks>
    public void Dispose()
    {
        try { _cts.Cancel(); }    catch { }
        try { _listener.Stop(); } catch { }
        try { _cts.Dispose(); }   catch { }
    }
}
