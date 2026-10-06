using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Models;
using Inferpal.Services.Execution;
using Inferpal.Services.Inference;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  Arguments complete, then silence: the model is writing past the call, and the call is refused.
//
//  Captured on LM Studio with Devstral Small 2: the unescaped `$"` closed the whole arguments object early —
//  {"path":…,"old_content":"…($"} — the server held the call complete and streamed nothing of what the model went on
//  writing, 150 to 337 s, until the model was unloaded. Over 348 captured calls the end of the turn follows the last
//  argument fragment within 0.02 s.
// ──────────────────────────────────────────────────────────────────────────────────────────────
public sealed class ClosedThenSilentCallTests
{
    /// <summary>Streams <c>before</c>, keeps the connection silent for <c>hold</c>, then streams <c>after</c>.</summary>
    private sealed class HoldingServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        public string BaseUrl => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

        public HoldingServer(string before, TimeSpan hold, string after)
        {
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    TcpClient client;
                    try { client = await _listener.AcceptTcpClientAsync(); }
                    catch { return; }
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
                                await stream.WriteAsync(Encoding.ASCII.GetBytes(
                                    "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nConnection: close\r\n\r\n"));
                                await stream.WriteAsync(Encoding.UTF8.GetBytes(before));
                                await stream.FlushAsync();
                                await Task.Delay(hold);
                                await stream.WriteAsync(Encoding.UTF8.GetBytes(after));
                                await stream.FlushAsync();
                            }
                        }
                        catch { }
                    });
                }
            });
        }

        public void Dispose()
        {
            try { _listener.Stop(); } catch { }
        }
    }

    private static string Chunk(object toolCall) =>
        "data: " + JsonSerializer.Serialize(new { choices = new[] { new { index = 0, delta = new { tool_calls = new[] { toolCall } } } } }) + "\n\n";

    private static string Arguments(string text) =>
        Chunk(new { index = 0, id = "1", type = "function", function = new { name = "apply_diff", arguments = "" } })
        + Chunk(new { index = 0, function = new { arguments = text } });

    private const string End = "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n\ndata: [DONE]\n\n";

    // The captured shape: the object closes right after the unescaped `$"`.
    private const string ClosedEarly =
        "{\"path\": \"src/Shop/Inventory.cs\", \"old_content\": \"    public void Reserve(string sku, int quantity)\\n    {\\n"
        + "        if (Stock(sku) < quantity)\\n            throw new InvalidOperationException($\"\t}";

    private static async Task<(ChatTurnResult Turn, TimeSpan Took)> SendAsync(string before, TimeSpan hold, string after)
    {
        using var server = new HoldingServer(before, hold, after);
        var client = new OpenAiCompatibleClient(new InferpalConfig
            { Provider = "openai-compatible", BaseUrl = server.BaseUrl, ContextWindowSize = 32_768 })
            { ClosedArgumentsSilence = TimeSpan.FromSeconds(1) };
        var clock = Stopwatch.StartNew();
        var turn = await client.SendChatAsync("m", [new ChatMessageDto("user", "change Reserve")], EmptyToolRegistry.Instance,
                                              onToken: null, CancellationToken.None);
        return (turn, clock.Elapsed);
    }

    [Fact]
    public async Task ClosedArguments_ThenSilence_RefuseTheCall_AfterTheShortWait()
    {
        var (turn, took) = await SendAsync(Arguments(ClosedEarly), TimeSpan.FromSeconds(30), End);

        var call = Assert.Single(turn.ToolCalls!).Function;
        Assert.Equal(ArgumentsShapeWatcher.ClosedThenSilent(TimeSpan.FromSeconds(1)), call.BrokenShape);
        Assert.True(took < TimeSpan.FromSeconds(20), $"took {took}");   // under the hold: the short wait decided
    }

    [Fact]
    public async Task ClosedArguments_ThenTheEndOfTheTurn_IsAnOrdinaryCall()
    {
        // Reference arm: the end of the turn comes at once, as it does for every well-formed call.
        var (turn, _) = await SendAsync(Arguments("""{"path": "a.cs", "old_content": "x", "new_content": "y"}"""),
                                        TimeSpan.Zero, End);

        var call = Assert.Single(turn.ToolCalls!).Function;
        Assert.Null(call.BrokenShape);
        Assert.Equal("y", call.Arguments.GetProperty("new_content").GetString());
    }

    [Fact]
    public async Task ASilenceInsideOpenArguments_IsNotCutShort()
    {
        // Reference arm: a model that pauses in the MIDDLE of its arguments is still writing the call.
        var before = Arguments("""{"path": "a.cs", "old_content": "x", "new_content": "y""");
        var after  = Chunk(new { index = 0, function = new { arguments = "\"}" } }) + End;

        var (turn, took) = await SendAsync(before, TimeSpan.FromSeconds(3), after);

        var call = Assert.Single(turn.ToolCalls!).Function;
        Assert.Null(call.BrokenShape);
        Assert.Equal("y", call.Arguments.GetProperty("new_content").GetString());
        Assert.True(took >= TimeSpan.FromSeconds(3), $"took {took}");   // WITNESS: the pause did happen
    }
}
