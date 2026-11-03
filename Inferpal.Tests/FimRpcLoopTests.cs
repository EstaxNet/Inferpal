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

        var (id, result) = await ReadResultAsync(client).WaitAsync(TimeSpan.FromSeconds(10));
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

    /// <summary>
    /// A UTF-8 BOM at the head of the stream must not kill the session in silence.
    /// </summary>
    /// <remarks>
    /// ⚠ Any client whose stdin writer emits its preamble makes
    /// <c>EF BB BF</c> arrive before <c>Content-Length:</c> — which is what
    /// <c>Process.StandardInput</c> does under PowerShell 5.1, where merely touching it takes its
    /// <c>BaseStream</c>. Without tolerance the first line no longer starts with the marker, the
    /// length stays unknown, and the loop gives up: the process exits with code 0, empty stderr,
    /// without having answered. A healthy sidecar that looks dead — a diagnosis that did accuse the
    /// product before the measurement pointed at the client.
    /// </remarks>
    [Fact]
    public async Task LeadingBom_IsToleratedInsteadOfEndingTheSession()
    {
        var (client, server) = FullDuplexStream.CreatePair();
        var loop = new FimRpcLoop(server, server, (_, _) => Task.FromResult("ok"));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var running = loop.RunAsync(cts.Token);

        // The BOM is written BEFORE the first frame, exactly as a StreamWriter lays it down.
        await client.WriteAsync(new byte[] { 0xEF, 0xBB, 0xBF });
        await SendAsync(client, """{"jsonrpc":"2.0","id":7,"method":"fim/complete","params":{"prefix":"","suffix":""}}""");

        // ⚠ BOUND. Without BOM tolerance the loop gives up without answering, and this read then
        // waits FOREVER: the test does not go red, it HANGS — and a hanging test passes for a slow
        // test, not for a missing guard. Measured: removing the tolerance made the suite come out
        // on a 600 s timeout instead of failing.
        var (id, result) = await ReadResultAsync(client).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(7, id);
        Assert.Equal("ok", result);

        client.Dispose();
        await running;
    }

    /// <summary>
    /// Completions that just paused after repeated failures say so once (<c>"notice"</c>), on an empty answer: the ghost
    /// text says it in the status bar even when no key asked. A completion never carries it, nor consumes it.
    /// </summary>
    [Theory]
    [InlineData("", true)]
    [InlineData("x + 1", false)]
    public async Task APause_IsCarriedOnce_OnAnEmptyAnswer(string completion, bool carried)
    {
        var (client, server) = FullDuplexStream.CreatePair();
        var taken = 0;
        var loop = new FimRpcLoop(server, server, (_, _) => Task.FromResult(completion), () => true,
                                  () => Interlocked.Increment(ref taken) == 1 ? "paused" : null);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var running = loop.RunAsync(cts.Token);
        await SendAsync(client, """{"jsonrpc":"2.0","id":4,"method":"fim/complete","params":{"prefix":"a","suffix":"b"}}""");

        var length = await ReadHeadersAsync(client).WaitAsync(TimeSpan.FromSeconds(10));
        var body = new byte[length];
        for (var read = 0; read < length;) read += await client.ReadAsync(body.AsMemory(read, length - read));
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(body));

        Assert.Equal(completion, doc.RootElement.GetProperty("result").GetString());   // witness: the answer itself
        Assert.Equal(carried ? "paused" : null,
                     doc.RootElement.TryGetProperty("notice", out var n) ? n.GetString() : null);
        Assert.Equal(carried ? 1 : 0, taken);

        client.Dispose();
        await running;
    }

    /// <summary>
    /// ⚠ Visual Studio says the pause where the person looks: the in-process half says <c>"notice"</c> in the status bar
    /// as it arrives — before it looks for the request, which the next keystroke may have cancelled. Held on the source:
    /// devenv is not in the suite.
    /// </summary>
    [Fact]
    public void VisualStudio_SaysAPause_WhenNoKeyAsked()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var sidecar = ConventionCoverageTests.CodeOnly(Path.Combine(dir!.FullName, "Inferpal.InProc", "Fim", "FimSidecar.cs"));
        var ghost = ConventionCoverageTests.CodeOnly(Path.Combine(dir.FullName, "Inferpal.InProc", "GhostText", "GhostTextController.cs"));
        var program = ConventionCoverageTests.CodeOnly(Path.Combine(dir.FullName, "Inferpal.Fim", "Program.cs"));

        Assert.Contains("\"failing\"", sidecar);                                          // WITNESS
        var said = sidecar.IndexOf("GhostText.CompletionAskWatcher.Say(paused);", StringComparison.Ordinal);
        Assert.True(said > 0 && said < sidecar.IndexOf("!_pending.TryRemove(idElement.GetInt32()", StringComparison.Ordinal),
                    "the pause is said before the request is looked for");
        Assert.Contains("CompletionAskWatcher.Say(", ghost);                                        // WITNESS
        Assert.Contains("AskedCompletion.PauseNotice(client)", program);
        Assert.Contains("\"--ui-culture\"", program);                                       // Auto = Visual Studio's language
        Assert.Contains("\" --ui-culture \"", sidecar);
    }

    /// <summary>An empty answer from a server that failed says so (<c>"failing"</c>): the key of inline completion names
    /// the server, not the model. A completion, or an empty answer from a working server, carries no mark.</summary>
    [Theory]
    [InlineData("", true, true)]
    [InlineData("", false, false)]
    [InlineData("x + 1", true, false)]
    public async Task AnEmptyAnswer_SaysWhetherTheServerFailed(string completion, bool serverFailing, bool marked)
    {
        var (client, server) = FullDuplexStream.CreatePair();
        var loop = new FimRpcLoop(server, server, (_, _) => Task.FromResult(completion), () => serverFailing);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var running = loop.RunAsync(cts.Token);
        await SendAsync(client, """{"jsonrpc":"2.0","id":3,"method":"fim/complete","params":{"prefix":"a","suffix":"b"}}""");

        var length = await ReadHeadersAsync(client).WaitAsync(TimeSpan.FromSeconds(10));
        var body = new byte[length];
        for (var read = 0; read < length;) read += await client.ReadAsync(body.AsMemory(read, length - read));
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(body));

        Assert.Equal(completion, doc.RootElement.GetProperty("result").GetString());   // witness: the answer itself
        Assert.Equal(marked, doc.RootElement.TryGetProperty("failing", out var f) && f.GetBoolean());

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
