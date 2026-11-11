using System.Net.Sockets;
using System.Text;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The stand-in server the suite pairs paths and bodies on (<c>Paths.Zip(Bodies)</c>): requests that arrive together — a
/// client that asks the server's loaded models while it sends a chat — finish reading in another order than they started.
/// </summary>
public class LoopbackHttpServerTests
{
    /// <summary>A POST whose body comes late, a GET answered meanwhile: each body stays paired with its own path.</summary>
    [Fact]
    public async Task ABodyStaysWithItsPath_WhenAnotherRequestEndsFirst()
    {
        using var server = new LoopbackHttpServer(_ => "{}");
        var port = new Uri(server.BaseUrl).Port;

        using var post = new TcpClient();
        await post.ConnectAsync("127.0.0.1", port);
        var stream = post.GetStream();
        const string json = "{\"chat\":true}";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            $"POST /v1/chat/completions HTTP/1.1\r\nHost: x\r\nContent-Length: {json.Length}\r\n\r\n"));
        await stream.FlushAsync();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (server.Paths.Count == 0 && DateTime.UtcNow < deadline) await Task.Delay(10);   // the POST's path is in

        using (var http = new System.Net.Http.HttpClient())
            await http.GetStringAsync(server.BaseUrl + "/api/v0/models");                     // ends before the POST's body
        await stream.WriteAsync(Encoding.ASCII.GetBytes(json));
        await stream.FlushAsync();
        var buffer = new byte[256];
        _ = await stream.ReadAsync(buffer);                                                   // answered: its body was read

        var pairs = server.Paths.Zip(server.Bodies).ToList();
        Assert.Contains(("/v1/chat/completions", json), pairs);
        Assert.Contains(("/api/v0/models", string.Empty), pairs);
    }
}
