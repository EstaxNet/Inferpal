using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Inferpal.Config;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>requestHeaders</c>: the headers a model server behind a reverse proxy asks for reach it from every backend, and a
/// redirect never carries them to another host (<see cref="ServerHeaders"/>).
/// </summary>
public class ServerHeadersTests
{
    private const string Models = """{"models":[{"name":"m:1"}]}""";
    private const string Headers = "Authorization: Basic dXNlcjpwYXNz\nX-Proxy-Token: t0k3n";

    /// <summary>A raw loopback server: it answers each request from its path and headers, and keeps the headers.</summary>
    private sealed class Server : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        public readonly ConcurrentQueue<Dictionary<string, string>> Seen = new();
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public Server(Func<string, Dictionary<string, string>, (int Status, string? Location, string Body)> answer)
        {
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    TcpClient client;
                    try { client = await _listener.AcceptTcpClientAsync(_cts.Token); } catch { return; }
                    _ = Task.Run(async () =>
                    {
                        using (client)
                        {
                            var stream = client.GetStream();
                            var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                            var first = (await reader.ReadLineAsync())?.Split(' ');
                            var path = first is [_, var p, ..] ? p : "";
                            // The method and the body ride in the same map, under keys no header can have.
                            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [" method"] = first?[0] ?? "" };
                            string? line;
                            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
                                if (line.IndexOf(':') is var i and > 0) headers[line[..i].Trim()] = line[(i + 1)..].Trim();
                            if (headers.TryGetValue("Content-Length", out var cl) && int.Parse(cl) is var n and > 0)
                            {
                                var buffer = new char[n];
                                var read = 0;
                                while (read < n && await reader.ReadAsync(buffer, read, n - read) is var r and > 0) read += r;
                                headers[" body"] = new string(buffer, 0, read);
                            }
                            Seen.Enqueue(headers);
                            var (status, location, body) = answer(path, headers);
                            var bytes = Encoding.UTF8.GetBytes(body);
                            var head = $"HTTP/1.1 {status} X\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\n"
                                     + (location is null ? "" : $"Location: {location}\r\n") + "Connection: close\r\n\r\n";
                            await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
                            await stream.WriteAsync(bytes);
                        }
                    });
                }
            });
        }

        public void Dispose() { try { _cts.Cancel(); _listener.Stop(); } catch { } }
    }

    private static bool Proved(Dictionary<string, string> h) =>
        h.TryGetValue("Authorization", out var a) && a == "Basic dXNlcjpwYXNz" && h.TryGetValue("X-Proxy-Token", out var t) && t == "t0k3n";

    private static bool Token(Dictionary<string, string> h) => h.TryGetValue("X-Proxy-Token", out var t) && t == "t0k3n";

    private static async Task<IReadOnlyList<string>> ListAsync(int port, string headers)
    {
        var url = $"http://127.0.0.1:{port}";
        try
        {
            return await new OllamaClient(new InferpalConfig { Provider = "ollama", BaseUrl = url, RequestHeaders = headers })
                .ListModelsAsync(CancellationToken.None);
        }
        finally { ServerHeaders.Note(url, null); }   // a port the system gives again later must not inherit them
    }

    [Fact]
    public async Task AnOllamaBehindAProxy_IsAnsweredWithTheHeaders_AndRefusedWithout()
    {
        using var proxy = new Server((_, h) => Proved(h) ? (200, null, Models) : (401, null, "{}"));

        Assert.Equal(["m:1"], await ListAsync(proxy.Port, Headers));
        Assert.Empty(await ListAsync(proxy.Port, ""));
        Assert.Equal(2, proxy.Seen.Count);   // witness: both requests reached the proxy
    }

    [Fact]
    public async Task TheTestButtonsProbe_CarriesTheHeadersNoted_ForThatServer()
    {
        using var proxy = new Server((path, h) => path == "/api/tags" && Proved(h) ? (200, null, Models) : (401, null, "{}"));
        var url = $"http://127.0.0.1:{proxy.Port}";

        ServerHeaders.Note(url, Headers);
        string? provider;
        try { (provider, _) = await ProviderProbe.DetectWithRefusalAsync(url, null, CancellationToken.None); }
        finally { ServerHeaders.Note(url, null); }

        Assert.Equal(InferenceProviderFactory.Ollama, provider);
    }

    [Fact]
    public async Task ARedirectToAnotherHost_NeverReceivesTheHeaders()
    {
        using var elsewhere = new Server((_, _) => (200, null, Models));
        // "localhost" is another host than 127.0.0.1, though it is this machine: the host is what is judged.
        using var proxy = new Server((_, _) => (302, $"http://localhost:{elsewhere.Port}/api/tags", "{}"));

        var models = await ListAsync(proxy.Port, Headers);

        Assert.Empty(models);
        Assert.Single(proxy.Seen);
        Assert.Empty(elsewhere.Seen);   // the redirect is the answer; nothing went to the other host
    }

    [Fact]
    public async Task ARedirectOnTheSameServer_IsFollowed_WithTheHeaders()
    {
        // A relative Location, under another path of the same origin. X-Proxy-Token alone proves the follow is ours:
        // the handler's own redirect would have dropped Authorization but kept it.
        using var proxy = new Server((path, h) => path == "/api/tags" ? (308, "/v2/api/tags", "{}")
                                                : Token(h) ? (200, null, Models) : (401, null, "{}"));

        Assert.Equal(["m:1"], await ListAsync(proxy.Port, Headers));
        Assert.Equal(2, proxy.Seen.Count);
        Assert.All(proxy.Seen, h => Assert.True(Proved(h)));
    }

    [Fact]
    public async Task ARedirectToAnotherPortOfTheSameHost_IsNotFollowed()
    {
        using var other = new Server((_, _) => (200, null, Models));
        using var proxy = new Server((_, _) => (307, $"http://127.0.0.1:{other.Port}/api/tags", "{}"));

        Assert.Empty(await ListAsync(proxy.Port, Headers));
        Assert.Empty(other.Seen);
    }

    [Fact]
    public async Task A307_ResendsThePostAndItsBody_A303_TurnsItIntoAGet()
    {
        using var proxy = new Server((path, _) => path switch
        {
            "/keep" => (307, "/kept", "{}"),
            "/see"  => (303, "/seen", "{}"),
            _       => (200, null, "{}"),
        });
        var url = $"http://127.0.0.1:{proxy.Port}";
        ServerHeaders.Note(url, Headers);
        try
        {
            using var http = new HttpClient(ConnectDeadline.Create(ConnectDeadline.Timeout));
            (await http.PostAsync(url + "/keep", new StringContent("""{"a":1}"""))).Dispose();
            (await http.PostAsync(url + "/see", new StringContent("""{"b":2}"""))).Dispose();
        }
        finally { ServerHeaders.Note(url, null); }

        var seen = proxy.Seen.ToList();
        Assert.Equal(4, seen.Count);
        Assert.Equal(("POST", """{"a":1}"""), (seen[1][" method"], seen[1].GetValueOrDefault(" body")));
        Assert.Equal("GET", seen[3][" method"]);
        Assert.False(seen[3].ContainsKey(" body"));
        Assert.All(seen, h => Assert.True(Proved(h)));
    }

    [Theory]
    [InlineData("http://gw/a", "https://gw/b", true)]        // Caddy's upgrade
    [InlineData("http://gw:8080/a", "http://gw:8080/b", true)]
    [InlineData("https://gw/a", "http://gw/b", false)]       // back to clear text
    [InlineData("http://gw/a", "https://gw:8443/b", false)]  // another service
    [InlineData("http://gw/a", "http://gw2/b", false)]
    public void TheSameServer_IsItsOrigin_OrItsHttpsUpgrade(string from, string to, bool same) =>
        Assert.Equal(same, ServerHeaders.Handler.SameServer(new Uri(from), new Uri(to)));

    [Fact]
    public void MovingTheChatToAnotherServer_DropsTheHeaders_UnlessTheSameGestureWroteThem()
    {
        var before = new InferpalConfig { BaseUrl = "https://gw.example", RequestHeaders = Headers };

        var moved = new InferpalConfig { BaseUrl = "http://other:11434", RequestHeaders = Headers };
        Assert.True(ServerChange.Follow(before, moved));
        Assert.Equal("", moved.RequestHeaders);

        var rewritten = new InferpalConfig { BaseUrl = "http://other:11434", RequestHeaders = "X-Proxy-Token: new" };
        ServerChange.Follow(before, rewritten);
        Assert.Equal("X-Proxy-Token: new", rewritten.RequestHeaders);
    }

    [Fact]
    public async Task WithoutHeaders_ARedirectToAnotherHost_IsStillFollowed()
    {
        using var elsewhere = new Server((_, _) => (200, null, Models));
        using var proxy = new Server((_, _) => (302, $"http://localhost:{elsewhere.Port}/api/tags", "{}"));

        Assert.Equal(["m:1"], await ListAsync(proxy.Port, ""));
        Assert.Single(elsewhere.Seen);
    }

    [Fact]
    public void AMalformedLine_IsDropped_TheOthersKept()
    {
        var headers = ServerHeaders.Parse("# a comment\nX-Good: 1\nNoColon\nBad Name: 2\n: 3\nX-Empty:\nContent-Type: text/plain\n\n  Authorization :  Basic x  ");

        Assert.Equal(["X-Good", "Authorization"], headers.Select(h => h.Key));
        Assert.Equal(["1", "Basic x"], headers.Select(h => h.Value));
    }
}
