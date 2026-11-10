using Inferpal.Config;
using Inferpal.Services.Agent;
using Inferpal.Services.Inference;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The window the server says it loaded is the one a conversation is measured against when the setting is automatic —
/// larger as well as smaller. A window the person set stays a ceiling; a server that says nothing (Ollama) leaves the
/// setting, which is its num_ctx.
/// </summary>
/// <remarks>
/// LM Studio can load a model at 32 664 under the factory setting (8 192, never sized off Ollama): measured against the
/// setting, Inferpal compacts and cuts its prompt budgets as if it had 8 K. And the other way: asked before its first
/// request, LM Studio has not loaded the model yet; kept 30 s, that "unknown" leaves the run against a 32 768 setting
/// while the server has just loaded 8 192.
/// </remarks>
public sealed class LoadedWindowOverAutomaticTests
{
    private static Task<int> WindowAsync(InferpalConfig config, int? loaded) =>
        ContextManager.EffectiveWindowAsync(config, new FakeInferenceProvider { LoadedContextWindow = loaded }, "m",
                                            CancellationToken.None);

    [Fact]
    public async Task TheFactoryWindow_GivesWayToALargerLoadedOne()
    {
        Assert.Equal(32_664, await WindowAsync(new InferpalConfig(), loaded: 32_664));
    }

    [Fact]
    public async Task AWindowTheProductSized_GivesWayToALargerLoadedOne()
    {
        var config = new InferpalConfig { ContextWindowSize = 16_384, ContextWindowSizeAuto = 16_384 };

        Assert.Equal(32_664, await WindowAsync(config, loaded: 32_664));
    }

    [Theory]
    [InlineData(12_000, 0)]           // typed by the person, never sized by the product
    [InlineData(8_192, 16_384)]       // set back by hand under the product's mark
    public async Task AWindowThePersonSet_StaysTheCeiling(int window, int written)
    {
        var config = new InferpalConfig { ContextWindowSize = window, ContextWindowSizeAuto = written };

        Assert.Equal(window, await WindowAsync(config, loaded: 32_664));
        Assert.Equal(4_096, await WindowAsync(config, loaded: 4_096));   // a smaller loaded one still counts
    }

    [Fact]
    public async Task AnAutomaticWindow_RisesNoHigherThanTheProductSizesOne()
    {
        // vLLM's max_model_len, llama-server's -c 0: 128 K loaded would grow every budget the window scales.
        Assert.Equal(ContextWindowChoice.MeasuredCeiling, await WindowAsync(new InferpalConfig(), loaded: 131_072));
    }

    [Fact]
    public async Task NoWindowConfigured_TakesTheLoadedOne_Whole()
    {
        // Reference arm for the ceiling: 0 is "the model's own default", and the server just said what it is.
        Assert.Equal(131_072, await WindowAsync(new InferpalConfig { ContextWindowSize = 0 }, loaded: 131_072));
    }

    [Fact]
    public async Task AServerThatSaysNothing_LeavesTheSetting()
    {
        // Ollama: no loaded window reported; the setting is what it loads the model with.
        Assert.Equal(new InferpalConfig().ContextWindowSize, await WindowAsync(new InferpalConfig(), loaded: null));
    }

    private const string NotLoaded = """
        {"models":[{"key":"granite-4.2-8b","type":"llm","max_context_length":131072,"loaded_instances":[]}]}
        """;

    private const string Loaded = """
        {"models":[{"key":"granite-4.2-8b","type":"llm","max_context_length":131072,
          "loaded_instances":[{"id":"granite-4.2-8b","config":{"context_length":8192}}]}]}
        """;

    [Fact]
    public async Task AModelNotLoadedYet_IsAskedAgain_OnceTheFirstRequestLoadedIt()
    {
        var loaded = 0;
        var asked  = 0;
        using var server = new LoopbackHttpServer(path =>
        {
            if (!path.StartsWith("/api/v1/models", StringComparison.Ordinal)) return null;
            Interlocked.Increment(ref asked);
            return Volatile.Read(ref loaded) == 1 ? Loaded : NotLoaded;
        });
        // "Not loaded yet" kept no time at all here: what is measured is that it is not kept for the loaded window's 30 s.
        var client = new LmStudioClient(new InferpalConfig { Provider = "lmstudio", BaseUrl = server.BaseUrl })
            { NotLoadedLife = TimeSpan.Zero };

        Assert.Null(await client.GetLoadedContextWindowAsync("granite-4.2-8b", CancellationToken.None));
        Volatile.Write(ref loaded, 1);   // the first request loads it, at the server's own window

        Assert.Equal(8192, await client.GetLoadedContextWindowAsync("granite-4.2-8b", CancellationToken.None));
        Assert.Equal(2, asked);          // witness: read again, not served from a kept "unknown"
    }

    [Fact]
    public async Task OneRequestsTwoReads_OfAModelNotLoadedYet_CostOneReadOfTheList()
    {
        // The window is read where the turn is measured and again in front of the request: a model the list never shows
        // loaded (an instance under another id) costs one read per request, not two.
        var asked = 0;
        using var server = new LoopbackHttpServer(path =>
        {
            if (!path.StartsWith("/api/v1/models", StringComparison.Ordinal)) return null;
            Interlocked.Increment(ref asked);
            return NotLoaded;
        });
        var client = new LmStudioClient(new InferpalConfig { Provider = "lmstudio", BaseUrl = server.BaseUrl });

        Assert.Null(await client.GetLoadedContextWindowAsync("granite-4.2-8b", CancellationToken.None));
        Assert.Null(await client.GetLoadedContextWindowAsync("granite-4.2-8b", CancellationToken.None));
        Assert.Equal(1, asked);
    }

    [Fact]
    public async Task AModelTheServerUnloaded_ReadsAsUnknown_NeverAsTheWindowItWasLoadedWith()
    {
        // A reload by request takes the model's default settings, not the last load's; and a remembered window too small
        // would refuse every request before the one that loads the model is sent. Unknown never blocks a request.
        var loaded = 1;
        var asked  = 0;
        using var server = new LoopbackHttpServer(path =>
        {
            if (!path.StartsWith("/api/v1/models", StringComparison.Ordinal)) return null;
            Interlocked.Increment(ref asked);
            return Volatile.Read(ref loaded) == 1 ? Loaded : NotLoaded;
        });
        var client = new LmStudioClient(new InferpalConfig { Provider = "lmstudio", BaseUrl = server.BaseUrl });

        Assert.Equal(8192, await client.GetLoadedContextWindowAsync("granite-4.2-8b", CancellationToken.None));
        Volatile.Write(ref loaded, 0);
        // Another model asked for takes the probe's one cached answer: the next read of granite goes to the server.
        await client.GetLoadedContextWindowAsync("some-other-model", CancellationToken.None);

        Assert.Null(await client.GetLoadedContextWindowAsync("granite-4.2-8b", CancellationToken.None));
        Assert.Equal(3, asked);   // witness: the last read saw the model unloaded, not a kept answer
    }

    [Fact]
    public async Task AModelTheServerDoesNotList_IsNotAskedForAtEveryRequest()
    {
        // Only a model listed and not loaded yet is asked again: an id the list never holds (an empty list behind a proxy,
        // a name no entry matches) would otherwise cost a read of the list per request.
        var asked = 0;
        using var server = new LoopbackHttpServer(path =>
        {
            if (!path.StartsWith("/api/v1/models", StringComparison.Ordinal)) return null;
            Interlocked.Increment(ref asked);
            return NotLoaded;
        });
        var client = new LmStudioClient(new InferpalConfig { Provider = "lmstudio", BaseUrl = server.BaseUrl });

        Assert.Null(await client.GetLoadedContextWindowAsync("some-other-model", CancellationToken.None));
        Assert.Null(await client.GetLoadedContextWindowAsync("some-other-model", CancellationToken.None));
        Assert.Equal(1, asked);
    }

    [Fact]
    public async Task ALoadedWindow_IsKept_AndNotAskedForAtEveryRequest()
    {
        // Reference arm: the 30 s cache still serves a model that is loaded.
        var asked = 0;
        using var server = new LoopbackHttpServer(path =>
        {
            if (!path.StartsWith("/api/v1/models", StringComparison.Ordinal)) return null;
            Interlocked.Increment(ref asked);
            return Loaded;
        });
        var client = new LmStudioClient(new InferpalConfig { Provider = "lmstudio", BaseUrl = server.BaseUrl });

        Assert.Equal(8192, await client.GetLoadedContextWindowAsync("granite-4.2-8b", CancellationToken.None));
        Assert.Equal(8192, await client.GetLoadedContextWindowAsync("granite-4.2-8b", CancellationToken.None));
        Assert.Equal(1, asked);
    }
}
