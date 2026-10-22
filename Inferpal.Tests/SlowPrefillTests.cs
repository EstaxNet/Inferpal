using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services;
using Inferpal.Services.Agent;
using Inferpal.Services.Execution;
using Inferpal.Services.Inference;
using Inferpal.Services.Persistence;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The classes whose fake servers keep a request waiting for seconds. ⚠ Every chat request holds the process's GPU lease
/// (<c>GpuScheduler.AcquireChatLease</c>) for as long as it waits, and every indexing pass and background task waits for
/// that lease to be released: run beside them, these classes hold it for two minutes in all, and those tests run out of
/// their 30 s. Run alone, nothing waits on them — and the waiting line's delay can be shortened for the time they run.
/// </summary>
public static class SlowServerCollection
{
    public const string Name = "slow-server-serial";
}

[CollectionDefinition(SlowServerCollection.Name, DisableParallelization = true)]
public class SlowServerCollectionDefinition { }

/// <summary>
/// A slow server reading a long prompt sends nothing until its first token — Ollama not even its headers, LM Studio and
/// vLLM their headers and then silence. The wait for that first token is twice the measured cold read, never less than
/// the configured one; the wait is said while it lasts; a wait that runs out says the server was still reading, and the
/// next request waits longer; Stop is immediate. The decisions are tested on their own, the servers by an arm that waits
/// longer than the configured floor and goes through.
/// </summary>
public sealed class PrefillDecisionTests
{
    private static PrefillRates Store(string diagnosticName = "PrefillRatesTest") =>
        new(new AppDataJsonFile<Dictionary<string, PrefillRates.Entry>>("prefill-rates.json", diagnosticName)
        {
            PathOverride = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"prefill-{Guid.NewGuid():N}.json"),
        });

    [Fact]
    public void TheFirstTokenWait_IsTwiceTheMeasuredColdRead_AndNeverLessThanTheConfiguredOne()
    {
        var floor = TimeSpan.FromSeconds(300);

        Assert.Equal(floor, PrefillDeadline.For(floor, null));                              // nothing measured
        Assert.Equal(floor, PrefillDeadline.For(floor, TimeSpan.FromSeconds(100)));         // reads fast: the floor
        Assert.Equal(TimeSpan.FromSeconds(480), PrefillDeadline.For(floor, TimeSpan.FromSeconds(240)));
        // A configured wait under the connection deadline still outlasts it.
        Assert.Equal(PrefillDeadline.AfterConnect, PrefillDeadline.For(TimeSpan.FromSeconds(10), null));
    }

    /// <summary>
    /// ⚠ A quick small answer says nothing of a long cold read: the bound a long prompt taught stays (a long run timed out,
    /// a quick question, the run again — cleared, the run would time out again at the configured wait). Reference arm: a
    /// read long enough to say a speed replaces the bound.
    /// </summary>
    [Fact]
    public void AQuickSmallAnswer_LeavesTheBoundALongPromptTaught_AndALongReadReplacesIt()
    {
        var store = Store();
        Assert.True(store.RecordOverrun("http://h:1", "m", estimated: 20_000, TimeSpan.FromSeconds(120)));

        store.Record("http://h:1", "m", estimated: 500, tokens: 500, TimeSpan.FromSeconds(1));
        Assert.Equal(120, store.Estimate("http://h:1", "m", 20_000)!.Value.TotalSeconds, precision: 3);

        store.Record("http://h:1", "m", estimated: 20_000, tokens: 20_000, TimeSpan.FromSeconds(30));
        Assert.Equal(30, store.Estimate("http://h:1", "m", 20_000)!.Value.TotalSeconds, precision: 3);
    }

    /// <summary>A store read on every request says ONCE that its file cannot be read.</summary>
    [Fact]
    public void AnUnreadableStore_IsSaidOnce_NotOncePerRequest()
    {
        var name = "PrefillOnce" + Guid.NewGuid().ToString("N")[..8];
        var path = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"prefill-{Guid.NewGuid():N}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ not json");
        var store = new PrefillRates(new AppDataJsonFile<Dictionary<string, PrefillRates.Entry>>("prefill-rates.json", name)
        {
            PathOverride = path, ReportUnreadableOnce = true,
        });

        for (var i = 0; i < 5; i++) store.Estimate("http://h:1", "m", 10_000);

        Assert.Single(Diagnostics.Snapshot(), e => e.Context == name + ".Load");
    }

    [Fact]
    public void AShortRead_SaysNothingOfASpeed()
    {
        var store = Store();

        store.Record("http://s", "m", estimated: 1_000, tokens: 1_500, TimeSpan.FromSeconds(30));   // too few tokens
        store.Record("http://s", "m", estimated: 9_000, tokens: 9_000, TimeSpan.FromSeconds(2));    // too short a read

        Assert.Null(store.Estimate("http://s", "m", 10_000));
    }

    [Fact]
    public void AWarmRead_CannotShortenTheEstimate()
    {
        var store = Store();
        store.Record("http://s", "m", estimated: 10_000, tokens: 10_000, TimeSpan.FromSeconds(60));   // cold: 6 ms a token
        var cold = store.Estimate("http://s", "m", 10_000)!.Value;

        // The same size, the server's cache serving most of it — LM Studio does not say so.
        store.Record("http://s", "m", estimated: 11_000, tokens: 11_000, TimeSpan.FromSeconds(6));

        Assert.True(store.Estimate("http://s", "m", 10_000)!.Value >= cold, "a warm read made the cold estimate shorter");
    }

    [Fact]
    public void ThreeSizes_FollowAReadingThatSlowsWithDepth()
    {
        var store = Store();
        // 1 ms, then 1.5 ms, then 2 ms a token: the deeper, the slower.
        store.Record("http://s", "m", 4_000, 4_000, TimeSpan.FromSeconds(4 + 1));
        store.Record("http://s", "m", 12_000, 12_000, TimeSpan.FromSeconds(18 + 1));
        store.Record("http://s", "m", 24_000, 24_000, TimeSpan.FromSeconds(48 + 1));

        var at16 = store.Estimate("http://s", "m", 16_000)!.Value.TotalSeconds;

        Assert.InRange(at16, 26, 32);   // between the linear 21 s of the first speed and the 32 s of the last
    }

    [Fact]
    public void AnExpiry_IsAMeasure_TheNextRequestWaitsLonger_UntilARead_GoesThrough()
    {
        var store = Store();

        store.RecordOverrun("http://s", "m", estimated: 20_000, TimeSpan.FromSeconds(120));
        var after = store.Estimate("http://s", "m", 20_000)!.Value;
        store.Record("http://s", "m", estimated: 20_000, tokens: 20_000, TimeSpan.FromSeconds(150));
        var measured = store.Estimate("http://s", "m", 20_000)!.Value;

        Assert.True(after >= TimeSpan.FromSeconds(120), "an expiry taught nothing: the next request waits as little");
        Assert.True(PrefillDeadline.For(TimeSpan.FromSeconds(120), after) >= TimeSpan.FromSeconds(240));
        Assert.Equal(150, measured.TotalSeconds, 1.0);   // what went through replaces "slower than"
    }

    [Fact]
    public void OneServer_WhicheverOfItsAddressesTheCallerHolds()
    {
        var store = Store();
        store.Record("http://host:1234/v1", "m", 10_000, 10_000, TimeSpan.FromSeconds(50));

        Assert.NotNull(store.Estimate("http://host:1234", "m", 10_000));
        Assert.Null(store.Estimate("http://host:1234", "other", 10_000));
    }

    [Fact]
    public void AnUnreadableFile_IsNothingMeasured_AndIsReplaced()
    {
        var store = Store();
        Directory.CreateDirectory(Path.GetDirectoryName(store.File.Path)!);
        File.WriteAllText(store.File.Path, "{ not json");

        Assert.Null(store.Estimate("http://s", "m", 10_000));
        store.Record("http://s", "m", 10_000, 10_000, TimeSpan.FromSeconds(40));

        Assert.NotNull(store.Estimate("http://s", "m", 10_000));
    }

    [Fact]
    public void AnExpiry_OfASmallRequest_TeachesNothing()
    {
        var store = Store();

        // 1 500 tokens unanswered after 120 s: a queue or a model loading, not a reading speed.
        Assert.False(store.RecordOverrun("http://s", "m", estimated: 1_500, TimeSpan.FromSeconds(120)));

        Assert.Null(store.Estimate("http://s", "m", 30_000));
    }

    [Fact]
    public void AnExpiry_IsNeverStretchedToALargerRequest()
    {
        var store = Store();

        Assert.True(store.RecordOverrun("http://s", "m", estimated: 5_000, TimeSpan.FromSeconds(120)));

        Assert.Equal(120, store.Estimate("http://s", "m", 30_000)!.Value.TotalSeconds, 1.0);   // not 720 s
        Assert.Equal(60, store.Estimate("http://s", "m", 2_500)!.Value.TotalSeconds, 1.0);     // a smaller one, less
    }

    [Fact]
    public void TheWait_StopsGrowing_AfterTwoExpiriesInARow_UntilARead_GoesThrough()
    {
        var store = Store();

        Assert.True(store.RecordOverrun("http://s", "m", 20_000, TimeSpan.FromSeconds(300)));
        Assert.True(store.RecordOverrun("http://s", "m", 20_000, TimeSpan.FromSeconds(600)));
        // A server that accepts and never answers: without the stop, 1 200, 2 400, 4 800 s…, restarts included.
        Assert.False(store.RecordOverrun("http://s", "m", 20_000, TimeSpan.FromSeconds(1_200)));
        Assert.Equal(600, store.Estimate("http://s", "m", 20_000)!.Value.TotalSeconds, 1.0);

        store.Record("http://s", "m", 20_000, 20_000, TimeSpan.FromSeconds(500));
        Assert.True(store.RecordOverrun("http://s", "m", 20_000, TimeSpan.FromSeconds(1_000)));   // it answered: it may grow
    }

    [Fact]
    public void ABound_IsNeverSaidAsAMeasuredRead()
    {
        var store = Store();
        store.Record("http://s", "m", 10_000, 10_000, TimeSpan.FromSeconds(20));
        Assert.Equal(20, store.Describe("http://s", "m", 10_000).Measured!.Value.TotalSeconds, 1.0);   // reference arm

        store.RecordOverrun("http://s", "m", 10_000, TimeSpan.FromSeconds(120));
        var reading = store.Describe("http://s", "m", 10_000);

        Assert.Equal(120, reading.ColdRead!.Value.TotalSeconds, 1.0);
        Assert.Null(reading.Measured);   // "more than 120 s" is never said "up to about 20 s"
    }

    [Fact]
    public void AFileHeldByTheOtherEditor_IsReadAgain_NotRewrittenWithoutEveryOtherModel()
    {
        if (!OperatingSystem.IsWindows()) return;   // FileShare.None holds a reader out on Windows only
        var name  = $"PrefillRatesHeld{Guid.NewGuid():N}";
        var store = Store(name);
        store.Record("http://s", "kept", 10_000, 10_000, TimeSpan.FromSeconds(40));
        var start = DateTime.Now;
        var held  = new FileStream(store.File.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var recording = Task.Run(() => store.Record("http://s", "new", 10_000, 10_000, TimeSpan.FromSeconds(40)));
        // Released once the read has been refused, so the test leaves no window to the scheduler.
        var refused = System.Diagnostics.Stopwatch.StartNew();
        while (!Diagnostics.Snapshot().Any(e => e.Timestamp >= start && e.Context.StartsWith(name, StringComparison.Ordinal))
               && refused.Elapsed < TimeSpan.FromSeconds(10))
            Thread.Yield();
        held.Dispose();
        recording.Wait(TimeSpan.FromSeconds(30));

        Assert.NotNull(store.Estimate("http://s", "new", 10_000));    // witness: the second read went through
        Assert.NotNull(store.Estimate("http://s", "kept", 10_000));
    }

    [Fact]
    public void TheSummaryFuse_AllowsTheMeasuredReadTwiceOver_LikeTheClient()
    {
        var request = HistoryCompaction.BuildSummarizeRequest(
            [new ChatMessageDto("user", new string('x', 4_000))], int.MaxValue);
        var read    = TimeSpan.FromSeconds(200);

        var fuse = HistoryCompaction.SummaryTimeout(45, request, read);

        Assert.Equal(45 + 400, fuse.TotalSeconds, 1.0);
        Assert.True(fuse > PrefillDeadline.For(TimeSpan.FromSeconds(120), read),
            "the fuse would fire while the client still waits for a read it measured: its message names the writing time");
    }

    [Fact]
    public void TheSummaryFuse_IsNeverShorterThanASlowReadersTime()
    {
        var request    = HistoryCompaction.BuildSummarizeRequest(
            [new ChatMessageDto("user", new string('x', 40_000))], int.MaxValue);
        var slowReader = AgentOrchestrator.EstimateTokens(request.Messages) / HistoryCompaction.FuseReadTokensPerSecond;

        // A fit made of warm reads says one second.
        var fuse = HistoryCompaction.SummaryTimeout(45, request, TimeSpan.FromSeconds(1));

        Assert.Equal(45 + slowReader, fuse.TotalSeconds, 1.0);
    }

    [Fact]
    public async Task ALineSaid_IsGivenBack_WhenTheRequestEndsWithoutAnAnswer()
    {
        var said = new List<string?>();
        var wait = new PrefillWait(s => { lock (said) said.Add(s); }, 5_000, null, firstSaid: TimeSpan.FromMilliseconds(20));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (Count() == 0 && clock.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(10);

        wait.Dispose();   // a retry that starts its own line, a failure, a Stop

        lock (said)
        {
            Assert.NotNull(said[0]);   // witness: the line was said
            Assert.Null(said[^1]);
        }
        int Count() { lock (said) return said.Count; }
    }

    [Fact]
    public void ALineNeverSaid_GivesNothingBack()
    {
        var said = new List<string?>();

        using (new PrefillWait(s => said.Add(s), 5_000, null)) { }

        Assert.Empty(said);
    }
}

/// <summary>A server that keeps its answer for a while: its headers first and then silence (LM Studio, vLLM), or nothing at
/// all until its first token (Ollama). Each request gets its parts, each sent after its own silence.</summary>
internal sealed class SlowReadServer : IDisposable
{
    /// <summary>What is sent after <paramref name="Hold"/>; <see cref="Timeout.InfiniteTimeSpan"/> never sends it.</summary>
    internal sealed record Part(TimeSpan Hold, string Text);

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    // ⚠ The hold waits on this token: an infinite Task.Delay without one is held by no timer, so nothing keeps the waiting
    // connection alive — a collection finalizes its socket and the client reads a connection closed mid-answer.
    private readonly CancellationTokenSource _stop = new();
    private int _requests;
    public string BaseUrl => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

    /// <summary>One reply for every request: <paramref name="body"/> after <paramref name="hold"/>.</summary>
    public SlowReadServer(bool headersFirst, TimeSpan hold, string contentType, string body)
        : this(headersFirst, contentType, _ => [new Part(hold, body)]) { }

    /// <param name="headersFirst">Headers at once, then the parts (LM Studio, vLLM); otherwise with the first part.</param>
    /// <param name="contentType">The body's type.</param>
    /// <param name="reply">The parts of the reply to the request of this index (0 first).</param>
    public SlowReadServer(bool headersFirst, string contentType, Func<int, IReadOnlyList<Part>> reply)
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
                            // The rest of a long request is drained meanwhile: a socket closed on unread bytes is reset,
                            // and the client reads a connection closed mid-answer.
                            _ = Task.Run(async () =>
                            {
                                try { while (await stream.ReadAsync(new byte[65536]) > 0) { } }
                                catch { /* closed with the reply */ }
                            });
                            var parts   = reply(Interlocked.Increment(ref _requests) - 1);
                            var headers = Encoding.ASCII.GetBytes(
                                $"HTTP/1.1 200 OK\r\nContent-Type: {contentType}\r\nConnection: close\r\n\r\n");
                            if (headersFirst) { await stream.WriteAsync(headers); await stream.FlushAsync(); }
                            for (var i = 0; i < parts.Count; i++)
                            {
                                await Task.Delay(parts[i].Hold, _stop.Token);
                                if (i == 0 && !headersFirst) await stream.WriteAsync(headers);
                                await stream.WriteAsync(Encoding.UTF8.GetBytes(parts[i].Text));
                                await stream.FlushAsync();
                            }
                        }
                    }
                    catch { /* the client went away: nothing to answer */ }
                });
            }
        });
    }

    public void Dispose()
    {
        try { _stop.Cancel(); _listener.Stop(); } catch { /* best-effort teardown */ }
    }
}

[Collection(SlowServerCollection.Name)]
public sealed class SlowPrefillOpenAiTests : IDisposable
{
    private readonly TimeSpan _firstSaid = PrefillWait.FirstSaid;

    // Nothing else runs beside this collection: the line is said after 1 s instead of 8, so a test sees it sooner.
    public SlowPrefillOpenAiTests() => PrefillWait.FirstSaid = TimeSpan.FromSeconds(1);

    public void Dispose() => PrefillWait.FirstSaid = _firstSaid;

    private const string FirstChunk = "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"ok\"}}]}\n\n";

    private const string Sse = FirstChunk
        + "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}],"
        + "\"usage\":{\"prompt_tokens\":30000,\"completion_tokens\":1,\"total_tokens\":30001,"
        + "\"prompt_tokens_details\":{\"cached_tokens\":10000}}}\n\ndata: [DONE]\n\n";

    private const string Nothing =
        "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";

    // The shared store is the process's: a model of its own per test, so no test reads another's speed.
    private readonly string _model = $"m-{Guid.NewGuid():N}";

    private static InferpalConfig Config(string url) =>
        new() { Provider = "openai", BaseUrl = url, QuickTimeoutSeconds = 10, NormalTimeoutSeconds = 10 };

    private static readonly List<ChatMessageDto> Question = [new("user", "Summarize the repository.")];

    // Long enough to say a speed (PrefillRates.MinTokens).
    private static readonly List<ChatMessageDto> LongQuestion =
        [new("user", "Summarize this log:\n" + string.Concat(Enumerable.Range(0, 400).Select(i => $"[{i:000}] batch {i * 7} done in {i % 13} ms\n")))];

    [Fact]
    public async Task AServerReadingLongerThanTheConfiguredWait_IsWaitedFor_AndTheWaitIsSaid()
    {
        using var server = new SlowReadServer(headersFirst: true, TimeSpan.FromSeconds(12), "text/event-stream", Sse);
        // Measured before: this server reads slowly, far slower than the 10 s the settings allow.
        PrefillRates.Shared.Record(server.BaseUrl, _model, estimated: 10, tokens: 3_000, TimeSpan.FromSeconds(300));
        var client = new OpenAiCompatibleClient(Config(server.BaseUrl));
        var steps  = new List<string>();

        var run = await client.RunAgentAsync(_model, new List<ChatMessageDto>(Question), EmptyToolRegistry.Instance,
                                             step => { lock (steps) steps.Add(step); }, null, CancellationToken.None);

        Assert.False(run.Failed, run.FinalResponse);
        Assert.Equal("ok", run.FinalResponse);
        lock (steps)
        {
            var reading = steps.FindIndex(s => s != Strings.StatusThinking);
            Assert.True(reading >= 0, "the wait was never said: " + string.Join(" | ", steps));
            Assert.Equal(Strings.StatusThinking, steps[^1]);   // the line gave its place back to the step
        }
    }

    /// <summary>
    /// ⚠ An OpenAI-compatible server that loads a model on demand (LM Studio, llama-swap) counts the load in its first
    /// read, and says nothing of it: the first read of a model in the process is no sample — the second is.
    /// </summary>
    [Fact]
    public async Task ARead_ThatWentThrough_IsASample_OfTheTokensReadAnew_ButNotTheFirstOfTheModel()
    {
        using var server = new SlowReadServer(headersFirst: true, TimeSpan.FromSeconds(6), "text/event-stream", Sse);
        var client = new OpenAiCompatibleClient(Config(server.BaseUrl));

        await client.SendChatAsync(_model, new List<ChatMessageDto>(Question), EmptyToolRegistry.Instance, null,
                                   CancellationToken.None);
        Assert.Null(PrefillRates.Shared.Estimate(server.BaseUrl, _model, RequestSize.Of(Question, null).Total));
        await client.SendChatAsync(_model, new List<ChatMessageDto>(Question), EmptyToolRegistry.Instance, null,
                                   CancellationToken.None);

        // 30 000 counted, 10 000 of them from the server's cache: 20 000 read anew in ~6 s, so the whole request, cold,
        // takes ~9 s — not the 6 s a read that counted the cache as reading would say.
        var whole = PrefillRates.Shared.Estimate(server.BaseUrl, _model, RequestSize.Of(Question, null).Total);
        Assert.NotNull(whole);
        Assert.InRange(whole!.Value.TotalSeconds, 8.5, 12);
    }

    [Fact]
    public async Task AWaitThatRunsOut_BeforeTheFirstToken_SaysTheServerWasStillReading_AndTeachesTheNextRequest()
    {
        using var server = new SlowReadServer(headersFirst: true, Timeout.InfiniteTimeSpan, "text/event-stream", Sse);
        var client = new OpenAiCompatibleClient(Config(server.BaseUrl));
        var size   = RequestSize.Of(LongQuestion, null).Total;

        var ex = await Assert.ThrowsAsync<AgentHttpException>(() => client.SendChatAsync(
            _model, new List<ChatMessageDto>(LongQuestion), EmptyToolRegistry.Instance, null, CancellationToken.None));

        Assert.True(ex.PrefillOverran, ex.Message);
        Assert.EndsWith(Tail(Strings.MsgPrefillOverran(server.BaseUrl + "/v1", TimeSpan.Zero, size, quick: false)), ex.Message);
        Assert.Equal(0, client.ConsecutiveFailures);   // a read that runs out is not a server that cannot be reached
        Assert.True(PrefillRates.Shared.Estimate(server.BaseUrl, _model, size) >= TimeSpan.FromSeconds(9),
                    "the expiry taught nothing: the next request would run out the same way");
    }

    [Fact]
    public async Task AWaitThatNoLongerGrows_SaysTheServerMayNotAnswer()
    {
        using var server = new SlowReadServer(headersFirst: true, Timeout.InfiniteTimeSpan, "text/event-stream", Sse);
        var size = RequestSize.Of(LongQuestion, null).Total;
        PrefillRates.Shared.RecordOverrun(server.BaseUrl, _model, size, TimeSpan.FromSeconds(1));
        PrefillRates.Shared.RecordOverrun(server.BaseUrl, _model, size, TimeSpan.FromSeconds(2));
        var client = new OpenAiCompatibleClient(Config(server.BaseUrl));

        var ex = await Assert.ThrowsAsync<AgentHttpException>(() => client.SendChatAsync(
            _model, new List<ChatMessageDto>(LongQuestion), EmptyToolRegistry.Instance, null, CancellationToken.None));

        Assert.True(ex.PrefillOverran, ex.Message);
        Assert.EndsWith(Tail(Strings.MsgPrefillOverran(server.BaseUrl + "/v1", TimeSpan.Zero, size, false, waitsLonger: false)),
                        ex.Message);
    }

    [Fact]
    public async Task AForcedCallRetried_GivesItsLineBack_AndTheRetrySaysItsOwn()
    {
        // The first request reads 2.5 s and returns nothing (a model that cannot write a forced call), the retry 2.5 s too:
        // each says its line after 1 s.
        using var server = new SlowReadServer(headersFirst: true, "text/event-stream",
            i => [new(TimeSpan.FromSeconds(2.5), i == 0 ? Nothing : Sse)]);
        var client = new OpenAiCompatibleClient(new InferpalConfig
        {
            Provider = "openai", BaseUrl = server.BaseUrl, QuickTimeoutSeconds = 30, NormalTimeoutSeconds = 30,
        });
        var said   = new List<string?>();

        var turn = await client.SendChatAsync(_model, new List<ChatMessageDto>(Question), new PrefillWaitingLineTests.OneTool(),
                                              null, ReasoningEffort.Server, CancellationToken.None, toolChoice: "required",
                                              onWaiting: s => { lock (said) said.Add(s); });

        Assert.Equal("ok", turn.TextContent);   // witness: the retry answered
        lock (said)
        {
            var givenBack = said.IndexOf(null);
            Assert.True(givenBack > 0 && givenBack < said.Count - 1 && said[givenBack + 1] is not null,
                        "no line given back before the retry's own: " + string.Join(" | ", said.Select(s => s ?? "(null)")));
            Assert.Null(said[^1]);
        }
    }

    /// <summary>A wait configured under the connection deadline (10 s) still runs past it: the server was reached, the
    /// expiry is a read, and it is learned.</summary>
    [Fact]
    public async Task AWaitConfiguredUnderTheConnectDeadline_StillRunsPastIt_AndIsLearned()
    {
        using var server = new SlowReadServer(headersFirst: false, Timeout.InfiniteTimeSpan, "text/event-stream", Sse);
        var client = new OpenAiCompatibleClient(Config(server.BaseUrl));
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<AgentHttpException>(() => client.SendChatAsync(
            _model, new List<ChatMessageDto>(LongQuestion), EmptyToolRegistry.Instance, null, CancellationToken.None));

        Assert.True(ex.PrefillOverran, ex.Message);
        Assert.True(clock.Elapsed >= PrefillDeadline.AfterConnect - TimeSpan.FromSeconds(1), $"{clock.Elapsed}");
        Assert.NotNull(PrefillRates.Shared.Estimate(server.BaseUrl, _model, 30_000));
        Assert.Equal(0, client.ConsecutiveFailures);   // the wait still grows: not a server that cannot be reached
    }

    [Fact]
    public async Task Stop_DuringTheRead_IsImmediate_AndTeachesNothing()
    {
        using var server = new SlowReadServer(headersFirst: true, Timeout.InfiniteTimeSpan, "text/event-stream", Sse);
        var client = new OpenAiCompatibleClient(Config(server.BaseUrl));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var clock = System.Diagnostics.Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SendChatAsync(
            _model, new List<ChatMessageDto>(LongQuestion), EmptyToolRegistry.Instance, null, stop.Token));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"Stop took {clock.Elapsed.TotalSeconds:0.0} s");
        Assert.Null(PrefillRates.Shared.Estimate(server.BaseUrl, _model, 1_000));
    }

    /// <summary>The message after its duration, which a loaded machine rounds either way.</summary>
    internal static string Tail(string message) => message[message.IndexOf(": ", StringComparison.Ordinal)..];
}

[Collection(SlowServerCollection.Name)]
public sealed class SlowPrefillOllamaTests
{
    private static string Done(int evaluated, long loadNanoseconds = 0, long readNanoseconds = 0, int? cached = null) =>
        "{\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"done\":false}\n"
        + "{\"message\":{\"role\":\"assistant\",\"content\":\"\"},\"done\":true,"
        + $"\"prompt_eval_count\":{evaluated},\"eval_count\":1,\"load_duration\":{loadNanoseconds}"
        + (readNanoseconds > 0 ? $",\"prompt_eval_duration\":{readNanoseconds}" : "")
        + (cached is { } c ? $",\"prompt_eval_cached_count\":{c}" : "") + "}\n";

    /// <summary>
    /// ⚠ Ollama counts in <c>prompt_eval_count</c> the tokens its cache served (<c>prompt_eval_cached_count</c>), and its
    /// duration covers only the others: the sample is the part it read anew. Learned at the whole request's size, its
    /// new part's time says a speed as many times too fast as the cache served, and the tokens to read come out short.
    /// </summary>
    [Theory]
    [InlineData(2_500)]   // mostly served by the cache
    [InlineData(8_000)]   // under half of it served: no filter on the share sees this one
    public async Task AReadOllamasCachePartlyServed_IsLearnedAtTheSizeItRead(int evaluated)
    {
        var total = RequestSize.Of(LongQuestion, null).Total;
        Assert.True(total > 10_000, $"the request is {total} tokens");   // witness
        using var server = new SlowReadServer(headersFirst: false, TimeSpan.FromSeconds(1), "application/x-ndjson",
                                              Done(total, readNanoseconds: 6_000_000_000, cached: total - evaluated));
        var client = new OllamaClient(Config(server.BaseUrl, floor: 60));

        var turn = await client.SendChatAsync(_model, new List<ChatMessageDto>(LongQuestion), EmptyToolRegistry.Instance, null,
                                              CancellationToken.None);

        // One sample: the cold read of the whole request is its new part's speed over the whole size.
        var expected = 6.0 * total / evaluated;
        Assert.InRange(PrefillRates.Shared.Estimate(server.BaseUrl, _model, total)!.Value.TotalSeconds, expected - 0.5, expected + 0.5);
        Assert.Equal(total, PrefillRates.Shared.Tokens(server.BaseUrl, _model, total));
        Assert.Equal(total + 1, turn.TokensUsed);   // the window holds the cached tokens too
    }

    /// <summary>Reference arm: the same read with nothing from the cache is learned.</summary>
    [Fact]
    public async Task AReadOllamasCacheServedNothingOf_IsLearned()
    {
        var total = RequestSize.Of(LongQuestion, null).Total;
        using var server = new SlowReadServer(headersFirst: false, TimeSpan.FromSeconds(1), "application/x-ndjson",
                                              Done(total, readNanoseconds: 6_000_000_000, cached: 0));
        var client = new OllamaClient(Config(server.BaseUrl, floor: 60));

        await client.SendChatAsync(_model, new List<ChatMessageDto>(LongQuestion), EmptyToolRegistry.Instance, null,
                                   CancellationToken.None);

        Assert.InRange(PrefillRates.Shared.Estimate(server.BaseUrl, _model, total)!.Value.TotalSeconds, 5, 8);
    }

    /// <summary>
    /// ⚠ Ollama's own measure of the read (prompt_eval_duration) is the sample: the time to the first chunk also counts
    /// what the model spent writing a tool call, which Ollama sends only once complete. Here the first chunk comes after
    /// 12 s, of which the server says 6 s were reading.
    /// </summary>
    [Fact]
    public async Task OllamasOwnReadDuration_IsTheSample_NotTheTimeToTheFirstChunk()
    {
        using var server = new SlowReadServer(headersFirst: false, TimeSpan.FromSeconds(12), "application/x-ndjson",
                                              Done(30_000, readNanoseconds: 6_000_000_000));
        var client = new OllamaClient(Config(server.BaseUrl, floor: 60));

        await client.SendChatAsync(_model, new List<ChatMessageDto>(LongQuestion), EmptyToolRegistry.Instance, null,
                                   CancellationToken.None);

        var cold = PrefillRates.Shared.Estimate(server.BaseUrl, _model, RequestSize.Of(LongQuestion, null).Total);
        Assert.NotNull(cold);
        Assert.InRange(cold!.Value.TotalSeconds, 5, 8);
    }

    private readonly string _model = $"m-{Guid.NewGuid():N}";

    // A window the long question fits in: these tests are about the wait, not the window (OllamaRequestFitTests).
    private static InferpalConfig Config(string url, int floor = 10) =>
        new() { Provider = "ollama", BaseUrl = url, QuickTimeoutSeconds = floor, NormalTimeoutSeconds = floor,
                ContextWindowSize = 32_768 };

    private static readonly List<ChatMessageDto> Question = [new("user", "Summarize the repository.")];

    // Long enough to say a speed, and for a read of 3 000 tokens to be less than half of it.
    private static readonly List<ChatMessageDto> LongQuestion =
        [new("user", "Summarize this log:\n" + string.Concat(Enumerable.Range(0, 1_600).Select(i => $"[{i:0000}] batch {i * 7} done in {i % 13} ms\n")))];

    [Fact]
    public async Task OllamaSendingNothingUntilItsFirstToken_IsWaitedFor_WhenItReadsSlowly()
    {
        using var server = new SlowReadServer(headersFirst: false, TimeSpan.FromSeconds(12), "application/x-ndjson", Done(30_000));
        PrefillRates.Shared.Record(server.BaseUrl, _model, estimated: 10, tokens: 3_000, TimeSpan.FromSeconds(300));
        var client = new OllamaClient(Config(server.BaseUrl));

        var turn = await client.SendChatAsync(_model, new List<ChatMessageDto>(Question), EmptyToolRegistry.Instance, null,
                                              CancellationToken.None);

        Assert.Equal("ok", turn.TextContent);
    }

    [Fact]
    public async Task OllamaThatNeverAnswers_PastTheConnectDeadline_RunsOutAsAReadNotAsADeadServer()
    {
        using var server = new SlowReadServer(headersFirst: false, Timeout.InfiniteTimeSpan, "application/x-ndjson", Done(1));
        var client = new OllamaClient(Config(server.BaseUrl, floor: 16));

        var ex = await Assert.ThrowsAsync<AgentHttpException>(() => client.SendChatAsync(
            _model, new List<ChatMessageDto>(LongQuestion), EmptyToolRegistry.Instance, null, CancellationToken.None));

        Assert.True(ex.PrefillOverran, ex.Message);
        Assert.Equal(0, client.ConsecutiveFailures);   // a read that runs out, while the wait grows, is not a dead server
    }

    /// <summary>
    /// ⚠ Once the wait no longer grows, an expiry counts against the server again: one that accepts a request and never
    /// answers must still open the breaker. Reference arm: the same expiry while the wait grows counts nothing (above).
    /// </summary>
    [Fact]
    public async Task OllamaThatNeverAnswers_OnceTheWaitNoLongerGrows_CountsAgainstTheServer()
    {
        using var server = new SlowReadServer(headersFirst: false, Timeout.InfiniteTimeSpan, "application/x-ndjson", Done(1));
        var size = RequestSize.Of(LongQuestion, null).Total;
        for (var i = 0; i < PrefillRates.MaxGrowth; i++)
            Assert.True(PrefillRates.Shared.RecordOverrun(server.BaseUrl, _model, size, TimeSpan.FromSeconds(5)));
        var client = new OllamaClient(Config(server.BaseUrl, floor: 16));

        var ex = await Assert.ThrowsAsync<AgentHttpException>(() => client.SendChatAsync(
            _model, new List<ChatMessageDto>(LongQuestion), EmptyToolRegistry.Instance, null, CancellationToken.None));

        Assert.True(ex.PrefillOverran, ex.Message);
        Assert.Equal(1, client.ConsecutiveFailures);
    }

    /// <summary>A wait configured under the connection deadline (10 s) still runs past it, so a slow read is learned —
    /// Ollama sends nothing, not even its headers, before its first token.</summary>
    [Fact]
    public async Task OllamaWithAWaitUnderTheConnectDeadline_StillLearnsASlowRead()
    {
        using var server = new SlowReadServer(headersFirst: false, Timeout.InfiniteTimeSpan, "application/x-ndjson", Done(1));
        var client = new OllamaClient(Config(server.BaseUrl, floor: 10));

        var ex = await Assert.ThrowsAsync<AgentHttpException>(() => client.SendChatAsync(
            _model, new List<ChatMessageDto>(LongQuestion), EmptyToolRegistry.Instance, null, CancellationToken.None));

        Assert.True(ex.PrefillOverran, ex.Message);
        Assert.NotNull(PrefillRates.Shared.Estimate(server.BaseUrl, _model, 30_000));
    }

    /// <summary>An Ollama that sends no cached count counts the evaluated tokens alone: a count under half the request
    /// says the cache served the rest, and the read is no cold read.</summary>
    [Fact]
    public async Task AReadMostlyServedFromTheCache_ByAnOllamaThatSendsNoCachedCount_IsNoColdRead()
    {
        var size = RequestSize.Of(LongQuestion, null).Total;
        // Ollama evaluated 3 000 tokens of a request of about 14 000: its cache served the rest.
        Assert.True(size > 6_000, $"the request is {size} tokens: 3 000 would not be less than half of it");   // witness
        using var server = new SlowReadServer(headersFirst: false, TimeSpan.FromSeconds(6), "application/x-ndjson", Done(3_000));
        var client = new OllamaClient(Config(server.BaseUrl));

        await client.SendChatAsync(_model, new List<ChatMessageDto>(LongQuestion), EmptyToolRegistry.Instance, null,
                                   CancellationToken.None);

        Assert.Null(PrefillRates.Shared.Estimate(server.BaseUrl, _model, size));
    }

    [Fact]
    public async Task AServerThatHangsAfterItsFirstToken_IsGivenTheWaitBetweenTwoChunks_NotTheReadingOne()
    {
        // NDJSON has no blank line after a chunk: nothing else re-arms the wait before the silence.
        const string first = "{\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"done\":false}\n";
        using var server = new SlowReadServer(headersFirst: false, "application/x-ndjson",
            _ => [new(TimeSpan.Zero, first), new(Timeout.InfiniteTimeSpan, Done(1))]);
        PrefillRates.Shared.Record(server.BaseUrl, _model, estimated: 10, tokens: 3_000, TimeSpan.FromSeconds(300));
        var client = new OllamaClient(Config(server.BaseUrl));
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));   // the reading wait would be 600 s

        var ex = await Assert.ThrowsAsync<AgentHttpException>(() => client.SendChatAsync(
            _model, new List<ChatMessageDto>(Question), EmptyToolRegistry.Instance, null, guard.Token));

        Assert.False(ex.PrefillOverran, ex.Message);
        Assert.Equal(Strings.MsgTimeout(server.BaseUrl), ex.Message);
    }

    [Fact]
    public async Task TheModelsLoad_IsNotReading()
    {
        var size = RequestSize.Of(LongQuestion, null).Total;
        // 11 s before the first token, 5 of them loading the model: a read of about 6 s.
        using var server = new SlowReadServer(headersFirst: false, TimeSpan.FromSeconds(11), "application/x-ndjson",
                                              Done(size, loadNanoseconds: 5_000_000_000));
        var client = new OllamaClient(Config(server.BaseUrl, floor: 30));

        await client.SendChatAsync(_model, new List<ChatMessageDto>(LongQuestion), EmptyToolRegistry.Instance, null,
                                   CancellationToken.None);

        var read = PrefillRates.Shared.Estimate(server.BaseUrl, _model, size);
        Assert.NotNull(read);   // witness: the read was kept
        Assert.InRange(read!.Value.TotalSeconds, 5, 9);
    }
}

/// <summary>Where each caller shows the line said while the server reads the prompt, and the step it gives back.</summary>
public sealed class PrefillWaitingLineTests
{
    internal sealed class OneTool : IToolRegistry
    {
        public IReadOnlyList<ToolDefinition> Definitions { get; } = [new("function", new ToolFunction("read_file", "reads", new { }))];
        public DiffInfo? ConsumeDiff() => null;
        public Task<string> ExecuteAsync(string name, System.Text.Json.JsonElement args, CancellationToken ct) => Task.FromResult("class Cart {}");
    }

    [Fact]
    public async Task EveryCallOfAnAgentRun_SaysTheWait_ThenGivesItsStepBack()
    {
        const string Reading = "reading the prompt";
        // The plan, the step that reads a file, an empty answer — and the synthesis that replaces it.
        var replies = new Queue<ChatTurnResult>(
        [
            new("""{"goal":"answer","steps":[{"id":1,"description":"Read Cart.cs","tool":"read_file"}]}""", null, 0, 0),
            new(string.Empty, [new ToolCallDto(new ToolCallFunction("read_file",
                    System.Text.Json.JsonDocument.Parse("""{"path":"Cart.cs"}""").RootElement.Clone()))], 0, 0),
            new(string.Empty, null, 0, 0),
            new("Cart holds the items.", null, 0, 0),
        ]);
        var client = new FakeInferenceProvider
        {
            OnChatRequest = (_, _, _, _) => Task.FromResult(replies.Count > 1 ? replies.Dequeue() : replies.Peek()),
            DriveWaiting  = waiting => { waiting(Reading); waiting(null); },
        };
        var steps = new List<string>();

        var result = await new AgentOrchestrator(client, new InferpalConfig { ContextWindowSize = 8192, CompactionEnabled = false })
            .RunAsync(model: "m", history: [new("system", "s"), new("user", "What does Cart do?")], tools: new OneTool(),
                      onStep: steps.Add, onToken: null, onPlanReady: null, onStepUpdate: null,
                      onToolExecuted: null, onStreamReset: null, ct: CancellationToken.None);

        Assert.Equal("Cart holds the items.", result.FinalResponse);   // witness: the synthesis ran
        var givenBack = steps.Select((s, i) => (s, i)).Where(p => p.s == Reading && p.i + 1 < steps.Count)
                             .Select(p => steps[p.i + 1]).ToHashSet();
        Assert.Contains(Strings.StatusAgentPlanning, givenBack);
        Assert.Contains(Strings.StatusThinking, givenBack);
        Assert.Contains(Strings.StatusAgentSynthesizing, givenBack);
    }

    [Fact]
    public void TheHostsQuestionWithoutTools_SaysTheWaitOnItsStatusLine()
    {
        var call = ChatTurnBudgetParityTests.ModelCalls("Inferpal.Host/HostServer.cs")
            .Single(c => ((Microsoft.CodeAnalysis.CSharp.Syntax.MemberAccessExpressionSyntax)c.Expression).Name.Identifier.Text == "SendChatAsync");

        Assert.Contains(call.ArgumentList.Arguments, a => a.NameColon?.Name.Identifier.Text == "onWaiting");
    }
}
