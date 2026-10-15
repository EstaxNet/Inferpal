using Inferpal.Config;
using Inferpal.Services.Inference;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A Stop pressed while LM Studio is asked for the window it loaded the model with is a cancellation — never the
/// answer "unknown", kept for the questions that follow.
/// </summary>
/// <remarks>
/// The probe caught everything: the caller's cancellation came back as "the server did not say", and that answer was
/// cached for 30 seconds. The next question then took the configured window for the loaded one — a model loaded smaller
/// than the setting had its requests refused by LM Studio instead of compacted.
/// </remarks>
public sealed class LmStudioWindowProbeCancelTests
{
    private const string Loaded = """
        {"models":[{"key":"qwen/qwen3-27b","type":"llm","max_context_length":131072,
          "loaded_instances":[{"id":"qwen/qwen3-27b","config":{"context_length":8192}}]}]}
        """;

    [Fact]
    public async Task AStopDuringTheProbe_IsACancellation_AndTheNextQuestionReadsTheWindow()
    {
        var calls = 0;
        using var server = new LoopbackHttpServer(path =>
        {
            if (!path.StartsWith("/api/v1/models", StringComparison.Ordinal)) return null;
            if (Interlocked.Increment(ref calls) == 1) Thread.Sleep(2_000);   // the first probe is slow: Stop comes first
            return Loaded;
        });
        var client = new LmStudioClient(new InferpalConfig { Provider = "lmstudio", BaseUrl = server.BaseUrl });

        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetLoadedContextWindowAsync("qwen/qwen3-27b", stop.Token));

        Assert.Equal(8192, await client.GetLoadedContextWindowAsync("qwen/qwen3-27b", CancellationToken.None));
        Assert.True(calls >= 2, "witness: the second question probed again");
    }

    [Fact]
    public async Task AProbeThatAnswers_GivesTheLoadedWindow()
    {
        // Reference arm.
        using var server = new LoopbackHttpServer(path => path.StartsWith("/api/v1/models", StringComparison.Ordinal) ? Loaded : null);
        var client = new LmStudioClient(new InferpalConfig { Provider = "lmstudio", BaseUrl = server.BaseUrl });

        Assert.Equal(8192, await client.GetLoadedContextWindowAsync("qwen/qwen3-27b", CancellationToken.None));
    }
}
