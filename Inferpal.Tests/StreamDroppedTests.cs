using System.Net;
using System.Net.Sockets;
using System.Text;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services.Inference;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A stream that breaks after the server answered — the server crashed, ran out of memory, was restarted mid-answer —
/// was reported as "Cannot reach the backend at … Check the URL in Settings": the server had been reached and had
/// streamed, so the one remedy named was the one thing that was fine.
/// </summary>
public class StreamDroppedTests
{
    /// <summary>
    /// A server that answers 200, announces a body longer than it sends, writes <paramref name="firstChunk"/> and
    /// closes the socket — the reader sees the stream end prematurely, as when the process behind it dies.
    /// </summary>
    private sealed class DroppingServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        public string BaseUrl => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

        public DroppingServer(string firstChunk)
        {
            _listener.Start();
            _ = Task.Run(async () =>
            {
                // Every connection is served: a client may probe with GETs (the loaded window) before the chat POST.
                while (true)
                {
                    TcpClient client;
                    try { client = await _listener.AcceptTcpClientAsync(); }
                    catch { return; }                                            // stopped
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            using (client)
                            {
                                var stream = client.GetStream();
                                var buffer = new byte[65536];
                                var read   = await stream.ReadAsync(buffer);
                                if (!Encoding.ASCII.GetString(buffer, 0, read).StartsWith("POST", StringComparison.Ordinal))
                                {
                                    await stream.WriteAsync(Encoding.ASCII.GetBytes(
                                        "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
                                    return;
                                }
                                var body = Encoding.UTF8.GetBytes(firstChunk);
                                var head = Encoding.ASCII.GetBytes(
                                    "HTTP/1.1 200 OK\r\nContent-Type: application/x-ndjson\r\n"
                                    + $"Content-Length: {body.Length + 10_000}\r\nConnection: close\r\n\r\n");
                                await stream.WriteAsync(head);
                                await stream.WriteAsync(body);
                                await stream.FlushAsync();
                                client.Client.Close();                         // the crash: no more bytes, ever
                            }
                        }
                        catch { }
                    });
                }
            });
        }

        public void Dispose() { try { _listener.Stop(); } catch { } }
    }

    [Fact]
    public async Task Ollama_AStreamThatBreaksMidAnswer_IsNotCalledUnreachable()
    {
        var line = System.Text.Json.JsonSerializer.Serialize(
            new { message = new { role = "assistant", content = "The answer begins" }, done = false }) + "\n";
        using var server = new DroppingServer(line);
        var client = new OllamaClient(new InferpalConfig { Provider = "ollama", BaseUrl = server.BaseUrl });

        var ex = await Assert.ThrowsAsync<AgentHttpException>(() => client.SendChatAsync(
            "m", [new ChatMessageDto("user", "hi")], EmptyToolRegistry.Instance, onToken: null, CancellationToken.None));

        Assert.StartsWith(Strings.MsgStreamDropped(server.BaseUrl), ex.Message);
        Assert.DoesNotContain(Strings.MsgUnreachable(server.BaseUrl), ex.Message);
    }

    [Fact]
    public async Task OpenAiCompatible_AStreamThatBreaksMidAnswer_IsNotCalledUnreachable()
    {
        var chunk = "data: " + System.Text.Json.JsonSerializer.Serialize(
            new { choices = new[] { new { index = 0, delta = new { content = "The answer begins" } } } }) + "\n\n";
        using var server = new DroppingServer(chunk);
        var client = new OpenAiCompatibleClient(new InferpalConfig { Provider = "openai-compatible", BaseUrl = server.BaseUrl });

        var ex = await Assert.ThrowsAsync<AgentHttpException>(() => client.SendChatAsync(
            "m", [new ChatMessageDto("user", "hi")], EmptyToolRegistry.Instance, onToken: null, CancellationToken.None));

        Assert.StartsWith(Strings.MsgStreamDropped(server.BaseUrl + "/v1"), ex.Message);   // the client's base carries /v1
    }

    [Fact]
    public async Task AServerThatNeverAnswers_IsStillUnreachable()
    {
        // Reference arm: nothing listening — the send fails before any answer, and "cannot reach" is the truth.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
        listener.Stop();
        var client = new OllamaClient(new InferpalConfig { Provider = "ollama", BaseUrl = url });

        var ex = await Assert.ThrowsAsync<AgentHttpException>(() => client.SendChatAsync(
            "m", [new ChatMessageDto("user", "hi")], EmptyToolRegistry.Instance, onToken: null, CancellationToken.None));

        Assert.StartsWith(Strings.MsgUnreachable(url), ex.Message);
    }
}
