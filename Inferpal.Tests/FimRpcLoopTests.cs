using System.IO;
using System.Text;
using System.Text.Json;
using Inferpal.Fim;
using Nerdbank.Streams;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The wire between ghost text (net472, inside devenv) and the inference sidecar (net8).
/// </summary>
/// <remarks>
/// Both ends of the framing are hand-written, in two assemblies nothing compiles together: exactly
/// the situation where a divergence produces no error at all, just silent ghost text. These tests
/// exercise the protocol on the server side; the client is held to the same grammar
/// (<c>Content-Length</c>, UTF-8 body, camelCase JSON-RPC 2.0).
///
/// Cancellation here is not an exceptional case but the NORMAL one: every keystroke makes the
/// in-flight completion stale.
/// </remarks>
public class FimRpcLoopTests
{
    [Fact]
    public async Task Complete_AnswersTheFramedRequestWithTheCompletion()
    {
        var (client, server) = FullDuplexStream.CreatePair();
        var loop = new FimRpcLoop(server, server, (request, _) =>
            Task.FromResult($"<<{request.Prefix}|{request.Suffix}|{request.MaxTokens}|{request.Model}>>"));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var running = loop.RunAsync(cts.Token);

        await SendAsync(client, """
            {"jsonrpc":"2.0","id":7,"method":"fim/complete","params":
             {"prefix":"var x = ","suffix":";","maxTokens":42,"temperature":0.3,"model":"qwen"}}
            """);

        var (id, result) = await ReadResultAsync(client);
        Assert.Equal(7, id);
        Assert.Equal("<<var x = |;|42|qwen>>", result);

        client.Dispose();
        await running;
    }

    [Fact]
    public async Task Cancel_CancelsTheInflightCompletionAndStillAnswers()
    {
        var (client, server) = FullDuplexStream.CreatePair();
        var started  = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var loop = new FimRpcLoop(server, server, async (_, ct) =>
        {
            started.TrySetResult(true);
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { observed.TrySetResult(true); throw; }
            return "jamais";
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var running = loop.RunAsync(cts.Token);

        await SendAsync(client, """{"jsonrpc":"2.0","id":3,"method":"fim/complete","params":{"prefix":"a","suffix":"b"}}""");
        await started.Task;

        await SendAsync(client, """{"jsonrpc":"2.0","method":"fim/cancel","params":{"id":3}}""");
        Assert.True(await observed.Task);

        // A cancellation is still an answer: the client awaits a result per id, and leaving it
        // unanswered would hang its completion until the pipe dies.
        var (id, result) = await ReadResultAsync(client);
        Assert.Equal(3, id);
        Assert.Equal(string.Empty, result);

        client.Dispose();
        await running;
    }

    [Fact]
    public async Task UnknownMethod_IsIgnoredAndTheLoopKeepsServing()
    {
        var (client, server) = FullDuplexStream.CreatePair();
        var loop = new FimRpcLoop(server, server, (_, _) => Task.FromResult("ok"));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var running = loop.RunAsync(cts.Token);

        await SendAsync(client, """{"jsonrpc":"2.0","id":1,"method":"fim/inconnu","params":{}}""");
        await SendAsync(client, """{"jsonrpc":"2.0","id":2,"method":"fim/complete","params":{"prefix":"","suffix":""}}""");

        // The only answer that arrives is the one for the known request: an unknown message must
        // neither answer nor kill the loop.
        var (id, result) = await ReadResultAsync(client);
        Assert.Equal(2, id);
        Assert.Equal("ok", result);

        client.Dispose();
        await running;
    }

    [Fact]
    public async Task ClosedInput_EndsTheLoop()
    {
        // The sidecar never outlives the editor that started it: devenv dies, stdin closes.
        var (client, server) = FullDuplexStream.CreatePair();
        var loop = new FimRpcLoop(server, server, (_, _) => Task.FromResult(string.Empty));

        var running = loop.RunAsync(CancellationToken.None);
        client.Dispose();

        await running.WaitAsync(TimeSpan.FromSeconds(10));
    }

    // ── The same framing as the in-process client ─────────────────────────────

    private static async Task SendAsync(Stream stream, string json)
    {
        var body   = Encoding.UTF8.GetBytes(json);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        await stream.WriteAsync(header);
        await stream.WriteAsync(body);
        await stream.FlushAsync();
    }

    private static async Task<(int Id, string? Result)> ReadResultAsync(Stream stream)
    {
        var length = await ReadHeadersAsync(stream);
        Assert.True(length > 0, "no body announced by the headers");

        var body = new byte[length];
        var read = 0;
        while (read < length)
        {
            var n = await stream.ReadAsync(body.AsMemory(read, length - read));
            Assert.True(n > 0, "stream closed before the announced body ended");
            read += n;
        }

        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(body));
        return (doc.RootElement.GetProperty("id").GetInt32(),
                doc.RootElement.GetProperty("result").GetString());
    }

    private static async Task<int> ReadHeadersAsync(Stream stream)
    {
        var line   = new StringBuilder();
        var length = -1;
        var one    = new byte[1];

        while (true)
        {
            if (await stream.ReadAsync(one.AsMemory(0, 1)) <= 0) return -1;
            if (one[0] != (byte)'\n') { if (one[0] != (byte)'\r') line.Append((char)one[0]); continue; }

            var text = line.ToString();
            line.Clear();
            if (text.Length == 0) return length;

            const string marker = "Content-Length:";
            if (text.StartsWith(marker, StringComparison.OrdinalIgnoreCase))
                length = int.Parse(text[marker.Length..].Trim());
        }
    }
}
