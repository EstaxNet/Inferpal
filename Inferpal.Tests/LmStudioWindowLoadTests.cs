using System.Text.Json;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services.Agent;
using Inferpal.Services.Hardware;
using Inferpal.Services.Inference;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Pari 127, lever 1: a model LM Studio loaded below the window Inferpal needs is reloaded at it — LM Studio loads many at
/// 8 192, where Inferpal's tools leave no room for the question. A model LM Studio loads larger is left as it is (it stays
/// one LM Studio evicts for another); the model Inferpal loaded is unloaded before another is prepared (LM Studio never
/// evicts a model loaded through its API); a refused load is said once, never tried at every question.
/// </summary>
/// <remarks>Serial: a reload holds the chat lease, and another turn answering stops it — a lease another test holds in
/// parallel would decide in its place.</remarks>
[Collection(SignalCollection.Name)]
public sealed class LmStudioWindowLoadTests : IDisposable
{
    /// <summary>A stand-in LM Studio: one model loaded at a time, at the window a request (<c>jitWindow</c>) or a load gives.</summary>
    private sealed class FakeLmStudio : IDisposable
    {
        private readonly object _gate = new();
        public string? LoadedModel;
        public int LoadedWindow;
        public readonly List<string> Calls = [];
        public Func<string, int> JitWindow = _ => 8192;
        public string? RefuseLoad;
        public bool ListUnreadable;
        public bool WindowUnpublished;
        public bool NoLoadEndpoint;
        public int Max = 131072;
        public string[] Keys = ["small/model", "big/model"];
        public readonly LoopbackHttpServer Server;

        public FakeLmStudio()
        {
            Server = new LoopbackHttpServer(Answer, (path, _) =>
                path.StartsWith("/api/v1/models/load", StringComparison.Ordinal) && RefuseLoad is not null ? 500
                : path.StartsWith("/api/v1/models", StringComparison.Ordinal) && ListUnreadable ? 503 : 200);
        }

        private string? Answer(string path, string body)
        {
            lock (_gate)
            {
                if (path.StartsWith("/api/v1/models/unload", StringComparison.Ordinal))
                {
                    Calls.Add("unload " + JsonDocument.Parse(body).RootElement.GetProperty("instance_id").GetString());
                    LoadedModel = null;
                    return "{}";
                }
                if (path.StartsWith("/api/v1/models/load", StringComparison.Ordinal))
                {
                    if (NoLoadEndpoint) return null;
                    var r = JsonDocument.Parse(body).RootElement;
                    Calls.Add($"load {r.GetProperty("model").GetString()} {r.GetProperty("context_length").GetInt32()}");
                    if (RefuseLoad is not null) return JsonSerializer.Serialize(new { error = new { message = RefuseLoad } });
                    (LoadedModel, LoadedWindow) = (r.GetProperty("model").GetString(), r.GetProperty("context_length").GetInt32());
                    return """{"status":"loaded"}""";
                }
                if (path.StartsWith("/api/v1/models", StringComparison.Ordinal))
                {
                    if (ListUnreadable) return "{}";
                    var models = Keys.Select(k => new
                    {
                        key = k, type = "llm", max_context_length = Max,
                        loaded_instances = LoadedModel != k ? []
                            : WindowUnpublished ? new object[] { new { id = k } }
                            : new object[] { new { id = k, config = new { context_length = LoadedWindow } } },
                    });
                    return JsonSerializer.Serialize(new { models });
                }
                if (path.StartsWith("/v1/chat/completions", StringComparison.Ordinal))
                {
                    var model = JsonDocument.Parse(body).RootElement.GetProperty("model").GetString()!;
                    Calls.Add("request " + model);
                    // A request loads the model LM Studio's way, evicting the one it loaded the same way.
                    (LoadedModel, LoadedWindow) = (model, JitWindow(model));
                    return """{"choices":[{"index":0,"message":{"role":"assistant","content":"."},"finish_reason":"length"}]}""";
                }
                return null;
            }
        }

        public LmStudioClient Client() => new(new InferpalConfig { Provider = "lmstudio", BaseUrl = Server.BaseUrl });
        public void Dispose() => Server.Dispose();
    }

    private readonly Func<bool> _answering = LmStudioClient.AnsweringAnywhere;

    public LmStudioWindowLoadTests() => LmStudioClient.AnsweringAnywhere = () => false;

    public void Dispose() => LmStudioClient.AnsweringAnywhere = _answering;

    [Fact]
    public async Task AModelLoadedTooSmall_IsReloadedAtTheWindowNeeded_AndTheStatusLineSaysIt()
    {
        using var lms = new FakeLmStudio { LoadedModel = "small/model", LoadedWindow = 8192 };
        var steps = new List<string>();

        var load = await lms.Client().EnsureWindowAsync("small/model", 16384, steps.Add, CancellationToken.None);

        Assert.Equal(new WindowLoad(16384, null), load);
        Assert.Equal(["unload small/model", "load small/model 16384"], lms.Calls);
        Assert.Equal([Strings.StatusLoadingModelWindow("small/model", 16384)], steps);
        Assert.Equal(16384, lms.LoadedWindow);
    }

    [Fact]
    public async Task AModelLmStudioLoadsLarger_IsLeftAsItIs()
    {
        // Not loaded yet: loaded as any request loads it, then measured — LM Studio's own window is larger than the target.
        using var lms = new FakeLmStudio { JitWindow = _ => 55296 };

        Assert.Null(await lms.Client().EnsureWindowAsync("big/model", 16384, null, CancellationToken.None));
        Assert.Equal(["request big/model"], lms.Calls);
        Assert.Equal(55296, lms.LoadedWindow);
    }

    [Fact]
    public async Task AModelNotLoaded_ThatLmStudioLoadsTooSmall_IsReloaded()
    {
        using var lms = new FakeLmStudio();

        Assert.Equal(new WindowLoad(16384, null), await lms.Client().EnsureWindowAsync("small/model", 16384, null, CancellationToken.None));
        Assert.Equal(["request small/model", "unload small/model", "load small/model 16384"], lms.Calls);
    }

    [Fact]
    public async Task TheModelInferpalLoaded_IsUnloadedBeforeAnotherIsPrepared()
    {
        // LM Studio never evicts a model loaded through its API: left, the next model would load beside it.
        using var lms = new FakeLmStudio { LoadedModel = "small/model", LoadedWindow = 8192, JitWindow = m => m == "big/model" ? 55296 : 8192 };
        var client = lms.Client();
        await client.EnsureWindowAsync("small/model", 16384, null, CancellationToken.None);
        lms.Calls.Clear();

        Assert.Null(await client.EnsureWindowAsync("big/model", 16384, null, CancellationToken.None));
        Assert.Equal(["unload small/model", "request big/model"], lms.Calls);
    }

    [Fact]
    public async Task ARefusedLoad_IsSaidWithItsCause_AndNotTriedAgain()
    {
        using var lms = new FakeLmStudio { LoadedModel = "small/model", LoadedWindow = 8192, RefuseLoad = "insufficient system resources" };
        var client = lms.Client();

        var load = await client.EnsureWindowAsync("small/model", 16384, null, CancellationToken.None);
        Assert.Equal(0, load!.Window);
        Assert.Contains("insufficient system resources", load.Refusal);
        // Loaded again LM Studio's way: the turn is measured against the window the model has.
        Assert.Equal("request small/model", lms.Calls[^1]);
        Assert.Equal("small/model", lms.LoadedModel);

        lms.Calls.Clear();
        Assert.Null(await client.EnsureWindowAsync("small/model", 16384, null, CancellationToken.None));
        Assert.DoesNotContain(lms.Calls, c => c.StartsWith("load", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AListThatCannotBeRead_LoadsNothingOnAGuess()
    {
        using var lms = new FakeLmStudio { ListUnreadable = true };

        Assert.Null(await lms.Client().EnsureWindowAsync("small/model", 16384, null, CancellationToken.None));
        Assert.DoesNotContain(lms.Calls, c => c.StartsWith("load", StringComparison.Ordinal) || c.StartsWith("unload", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ALoadedModelThatDoesNotSayItsWindow_IsNotJudged()
    {
        // Read as 0, it would be reloaded before every question.
        using var lms = new FakeLmStudio { LoadedModel = "small/model", LoadedWindow = 8192, WindowUnpublished = true };

        Assert.Null(await lms.Client().EnsureWindowAsync("small/model", 16384, null, CancellationToken.None));
        Assert.Empty(lms.Calls);
    }

    [Fact]
    public async Task WhileAQuestionIsAnsweredAnywhere_NothingIsUnloadedOrLoaded()
    {
        using var lms = new FakeLmStudio { LoadedModel = "small/model", LoadedWindow = 8192 };
        LmStudioClient.AnsweringAnywhere = () => true;
        Assert.Null(await lms.Client().EnsureWindowAsync("small/model", 16384, null, CancellationToken.None));
        Assert.Empty(lms.Calls);
    }

    [Fact]
    public void ByDefault_TheGuardReadsTheAccount_ThisProcessIncluded()
    {
        using (GpuScheduler.AcquireChatLease())
            Assert.True(_answering());
    }

    [Fact]
    public async Task AModelTheServerDoesNotList_IsNotJudged()
    {
        using var lms = new FakeLmStudio();

        Assert.Null(await lms.Client().EnsureWindowAsync("other/model", 16384, null, CancellationToken.None));
        Assert.Empty(lms.Calls);
    }

    [Fact]
    public async Task TheWindowAsked_IsNeverAboveTheModelsMaximum()
    {
        using var lms = new FakeLmStudio { LoadedModel = "small/model", LoadedWindow = 8192, Max = 12288 };

        Assert.Equal(new WindowLoad(12288, null), await lms.Client().EnsureWindowAsync("small/model", 16384, null, CancellationToken.None));
        Assert.Equal("load small/model 12288", lms.Calls[^1]);
    }

    [Fact]
    public async Task AServerWithoutTheNativeLoad_IsLeftAsItIs_AndNotAskedAgain()
    {
        using var lms = new FakeLmStudio { LoadedModel = "small/model", LoadedWindow = 8192, NoLoadEndpoint = true };
        var client = lms.Client();

        Assert.Null(await client.EnsureWindowAsync("small/model", 16384, null, CancellationToken.None));
        Assert.Equal("small/model", lms.LoadedModel);   // loaded again LM Studio's way
        lms.Calls.Clear();
        Assert.Null(await client.EnsureWindowAsync("small/model", 16384, null, CancellationToken.None));
        Assert.Empty(lms.Calls);
    }

    [Fact]
    public async Task TheInstanceUnloaded_IsTheOneTheServerLists_NeverTheNameAsked()
    {
        // Asked without its variant, listed with it: unloaded by the name asked, the load would add a second copy.
        using var lms = new FakeLmStudio { Keys = ["small/model@q4"], LoadedModel = "small/model@q4", LoadedWindow = 8192 };

        await lms.Client().EnsureWindowAsync("small/model", 16384, null, CancellationToken.None);
        Assert.Equal("unload small/model@q4", lms.Calls[0]);
    }

    [Fact]
    public async Task ARefusedLoad_IsTriedAgainOnceItsTimeHasPassed()
    {
        var life = LmStudioClient.RefusalLife;
        LmStudioClient.RefusalLife = TimeSpan.Zero;
        try
        {
            using var lms = new FakeLmStudio { LoadedModel = "small/model", LoadedWindow = 8192, RefuseLoad = "insufficient system resources" };
            var client = lms.Client();
            await client.EnsureWindowAsync("small/model", 16384, null, CancellationToken.None);
            lms.RefuseLoad = null;
            lms.LoadedModel = "small/model";   // loaded again at LM Studio's own window by a request

            Assert.Equal(new WindowLoad(16384, null), await client.EnsureWindowAsync("small/model", 16384, null, CancellationToken.None));
        }
        finally { LmStudioClient.RefusalLife = life; }
    }

    [Fact]
    public async Task EachTurn_AsksForTheWindowNeeded_AndARefusalIsSaidToBothFrontEnds()
    {
        // The automatic window asks at least the smallest where the tools and a real question fit; a window the person set
        // is asked as it is.
        Assert.Equal(16384, ContextManager.TargetWindow(8192, automatic: true));
        Assert.Equal(32768, ContextManager.TargetWindow(32768, automatic: true));
        Assert.Equal(8192, ContextManager.TargetWindow(8192, automatic: false));
        var config = new InferpalConfig();
        var client = new FakeInferenceProvider { WindowLoadResult = new WindowLoad(0, "HTTP 500: insufficient system resources") };

        var decision = await ContextManager.PrepareAsync([new ChatMessageDto("user", "hi")], config, client, 0, null,
                                                         CancellationToken.None, model: "small/model", toolTokens: 7900);

        // The target is read from the suite's shared config file, which other tests write: judged on what was asked.
        var (asked, target) = Assert.Single(client.EnsuredWindows);
        Assert.Equal("small/model", asked);
        Assert.Equal(Strings.MsgModelWindowNotLoaded("small/model", target, "HTTP 500: insufficient system resources"),
                     decision.WindowNotice);

        // A turn without tools (a code action): nothing to make room for.
        await ContextManager.PrepareAsync([new ChatMessageDto("user", "hi")], config, client, 0, null, CancellationToken.None, model: "small/model");
        Assert.Single(client.EnsuredWindows);
    }
}
