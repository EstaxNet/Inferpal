using System.Collections.Concurrent;
using System.IO;
using Inferpal.Localization;
using Inferpal.Services.Inference;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The morning's first question waits for the model to load, and the card said "Reading the prompt — longer than usual":
/// the person looked at their prompt for a load. A server that publishes its loaded models is asked when the wait is said;
/// a model not in memory is named as loading, with no measured read promised.
/// </summary>
public class ModelLoadingWaitTests
{
    private static readonly TimeSpan Soon = TimeSpan.FromMilliseconds(1);

    [Fact]
    public void ALoad_IsNamedAsSuch_WithNoReadPromised()
    {
        var loading = new ModelWait(18_400, TimeSpan.FromSeconds(50), TimeSpan.FromSeconds(12), TimeSpan.FromMinutes(5), Loading: true);

        Assert.Equal(Strings.WaitLoadingModel, loading.Title);
        Assert.False(loading.Longer);              // past the measured read, but the read has not begun
        Assert.Null(loading.Fraction);             // no bar creeping toward a bound
        Assert.Equal(RunSummary.Duration(TimeSpan.FromSeconds(50)), loading.Times);
        Assert.Equal(Strings.WaitDetailLoading(18_400, RunSummary.Duration(TimeSpan.FromMinutes(5))), loading.Detail);

        // Reference arm: the same wait, the model in memory, is a read longer than usual.
        var reading = loading with { Loading = false };
        Assert.Equal(Strings.WaitReadingLonger, reading.Title);
    }

    private static async Task<ModelWait> FirstLineAsync(Func<CancellationToken, Task<bool?>>? isLoaded)
    {
        var said = new ConcurrentQueue<ModelWait?>();
        using (new PrefillWait(said.Enqueue, 12_000, null, TimeSpan.FromMinutes(10), firstSaid: Soon, isLoaded: isLoaded))
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (said.IsEmpty && DateTime.UtcNow < deadline) await Task.Delay(10);
        }
        Assert.True(said.TryPeek(out var first) && first is not null, "the wait was never said");
        return first!;
    }

    /// <summary>Asked only when the line was first said, a load shorter than that silence counted as reading time: the
    /// first line said "longer than usual" over a load. The server is asked with the request.</summary>
    [Fact]
    public async Task ALoadThatEndsBeforeTheFirstLine_IsNotCountedAsReading()
    {
        var answers = new ConcurrentQueue<bool?>([false]);
        var said = new ConcurrentQueue<ModelWait?>();
        using (new PrefillWait(said.Enqueue, 12_000, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(10),
                               firstSaid: TimeSpan.FromMilliseconds(1_500),
                               isLoaded: _ => Task.FromResult(answers.TryDequeue(out var a) ? a : true)))
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (said.IsEmpty && DateTime.UtcNow < deadline) await Task.Delay(10);
        }
        Assert.True(said.TryPeek(out var first) && first is not null, "the wait was never said");
        Assert.False(first!.Loading);
        Assert.True(first.ReadFrom > TimeSpan.Zero, "the load was counted as reading");
    }

    [Fact]
    public async Task TheServerIsAsked_WhenTheWaitIsSaid()
    {
        Assert.True((await FirstLineAsync(_ => Task.FromResult<bool?>(false))).Loading);
        Assert.False((await FirstLineAsync(_ => Task.FromResult<bool?>(true))).Loading);
        // A server that cannot say (null), or says nothing in time, leaves the line a read.
        Assert.False((await FirstLineAsync(_ => Task.FromResult<bool?>(null))).Loading);
        Assert.False((await FirstLineAsync(null)).Loading);
    }

    /// <summary>Counted from the request, a 20 s load made a 6 s read "longer than usual" the moment the model was in
    /// memory: the read is timed from the load's end.</summary>
    [Fact]
    public void AfterALoad_TheReadIsTimedFromTheLoadsEnd()
    {
        var now = TimeSpan.FromSeconds(20);
        var (loading, asking, readFrom) = PrefillWait.Next(true, loading: true, TimeSpan.Zero, now);
        Assert.False(loading);
        Assert.False(asking);
        Assert.Equal(now, readFrom);

        var reading = new ModelWait(8_000, TimeSpan.FromSeconds(23), TimeSpan.FromSeconds(6), TimeSpan.FromMinutes(5),
                                    loading, readFrom);
        Assert.Equal(Strings.WaitReading, reading.Title);
        Assert.Equal(0.5, reading.Fraction!.Value, 3);
        Assert.Equal(Strings.WaitTimesOfAbout(RunSummary.Duration(TimeSpan.FromSeconds(3)),
                                              RunSummary.Duration(TimeSpan.FromSeconds(6))), reading.Times);

        // Reference arm: a read slower than its record is still said so.
        Assert.Equal(Strings.WaitReadingLonger, (reading with { Elapsed = TimeSpan.FromSeconds(27) }).Title);
    }

    [Fact]
    public void AnUnknownAnswer_StopsTheAskingAtTheStart_ButNeverEndsALoad()
    {
        Assert.Equal((false, false, TimeSpan.Zero), PrefillWait.Next(null, loading: false, TimeSpan.Zero, TimeSpan.FromSeconds(8)));
        Assert.Equal((true, true, TimeSpan.Zero), PrefillWait.Next(null, loading: true, TimeSpan.Zero, TimeSpan.FromSeconds(13)));
        Assert.Equal((true, true, TimeSpan.Zero), PrefillWait.Next(false, loading: false, TimeSpan.Zero, TimeSpan.FromSeconds(8)));
    }

    private static async Task<bool> Within(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!done() && DateTime.UtcNow < deadline) await Task.Delay(10);
        return done();
    }

    /// <summary>A request with no line to say (a gesture, a compaction under a task) still learns from its read: the wait
    /// follows the load when asked to, and says it ended after the last question answered "not loaded" — the instant the
    /// question was ASKED: the server said it then or later. Timed from the answer (here 0.7 s late) or from the "loaded"
    /// one, the read would be counted short.</summary>
    [Fact]
    public async Task WithNoLineToSay_TheLoadIsFollowed_WhenAsked()
    {
        var calls = 0;
        using var wait = new PrefillWait(null, 12_000, null, TimeSpan.FromMinutes(10), followLoad: true,
            isLoaded: async _ =>
            {
                if (Interlocked.Increment(ref calls) > 1) return true;
                await Task.Delay(700);
                return false;
            });

        Assert.True(await Within(() => wait.Load.EndedAfter > TimeSpan.Zero), "the load's end was never seen");
        Assert.False(wait.Load.Loading);
        Assert.True(wait.Load.EndedAfter < TimeSpan.FromMilliseconds(350), $"timed from {wait.Load.EndedAfter}");
    }

    /// <summary>Reference arm: not asked to, a wait with no line asks the server nothing (Ollama measures its own read).
    /// The first question would be asked in the constructor.</summary>
    [Fact]
    public void WithNoLineToSay_NothingIsAsked_UnlessTheLoadIsFollowed()
    {
        var asked = 0;
        using (new PrefillWait(null, 12_000, null, TimeSpan.FromMinutes(10),
                               isLoaded: _ => { Interlocked.Increment(ref asked); return Task.FromResult<bool?>(false); }))
            Assert.Equal(0, Volatile.Read(ref asked));
    }

    /// <summary>What the wait saw is frozen at the first token: an answer that comes after it says nothing of the read.</summary>
    [Fact]
    public async Task WhatTheWaitSaw_IsFrozenAtTheFirstToken()
    {
        // Continuations inline: the late answer runs the wait's code before SetResult returns, so what is read after it
        // is what the wait made of it.
        var late = new TaskCompletionSource<bool?>();
        var answers = 0;
        using var wait = new PrefillWait(null, 12_000, null, TimeSpan.FromMinutes(10), followLoad: true,
                                         isLoaded: _ => Interlocked.Increment(ref answers) == 1 ? Task.FromResult<bool?>(false) : late.Task);

        Assert.True(await Within(() => wait.Load.Loading), "the load was never seen");
        Assert.True(await Within(() => Volatile.Read(ref answers) >= 2), "the server was not asked again");
        wait.Arrived();
        late.SetResult(true);   // the load's end, said after the first token

        Assert.True(wait.Load.Loading);
        Assert.Equal(TimeSpan.Zero, wait.Load.EndedAfter);
    }

    /// <summary>
    /// LM Studio's <c>/api/v1</c> lists an instance from the start of a JIT load (measured: 0.5 s into a 5 s load), so the
    /// load read as "in memory"; only <c>/api/v0</c>'s state says <c>loading</c>. And it names a model with its variant
    /// (<c>qwen/qwen3-27b@q4</c>): read with the shared rule, a model in memory was "loading" for the whole read.
    /// </summary>
    [Fact]
    public async Task LmStudio_AsksTheStateOfTheModel_ByItsOwnNaming()
    {
        var state = "loading";
        using var server = new LoopbackHttpServer(path =>
            path.StartsWith("/api/v1/models", StringComparison.Ordinal)
                ? """{"models":[{"key":"qwen/qwen3-27b@q4","loaded_instances":[{"id":"qwen/qwen3-27b@q4"}]}]}"""
            : path.StartsWith("/api/v0/models", StringComparison.Ordinal)
                ? $$"""{"data":[{"id":"qwen/qwen3-4b-thinking-2507","state":"loaded"},{"id":"qwen/qwen3-27b@q8","state":"not-loaded"},{"id":"qwen/qwen3-27b@q4","state":"{{state}}"}]}"""
            : null);
        var probe = new LmStudioClient(new Inferpal.Config.InferpalConfig { Provider = "lmstudio", BaseUrl = server.BaseUrl })
            .LoadedProbe("qwen/qwen3-27b")!;

        Assert.False(await probe(CancellationToken.None));
        state = "loaded";
        Assert.True(await probe(CancellationToken.None));   // the variant in memory, whichever is listed first
        // A model the server does not list: unknown, never "not loaded".
        var other = new LmStudioClient(new Inferpal.Config.InferpalConfig { Provider = "lmstudio", BaseUrl = server.BaseUrl })
            .LoadedProbe("qwen/qwen3-4b")!;
        Assert.Null(await other(CancellationToken.None));
    }

    [Fact]
    public void BothClients_AskTheirServer_OnlyOneThatPublishesItsLoadedModels()
    {
        string Code(string file) => ConventionCoverageTests.CodeOnly(Path.Combine(
            ConventionCoverageTests.RepoRoot(), "Inferpal.Core", "Services", "Inference", file));
        Assert.Contains("isLoaded: LoadedProbe(model)", Code("OllamaClient.cs"), StringComparison.Ordinal);
        Assert.Contains("isLoaded: LoadedProbe(model)", Code("OpenAiCompatibleClient.cs"), StringComparison.Ordinal);
        // A generic OpenAI-compatible server lists no loaded models: its empty list must never read as "not loaded".
        Assert.Contains("!Capabilities.VramMonitoring ? null", Code("InferenceProviderBase.cs"), StringComparison.Ordinal);
        Assert.False(ProviderCapabilities.OpenAiCompatible.VramMonitoring);
    }
}
