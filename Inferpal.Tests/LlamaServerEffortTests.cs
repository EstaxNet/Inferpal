using System.Text.Json;
using Inferpal.Config;
using Inferpal.Models;
using Inferpal.Services.Inference;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// llama-server says which <c>reasoning_effort</c> values a model's template renders (<c>/props</c>): those go, spelled as
/// listed. A value the template does not know is not a lower effort, so what is not published is not sent.
/// </summary>
public class LlamaServerEffortTests
{
    private const string Model = "qwen3.8-27b";
    private const string Asked = "/props?model=qwen3.8-27b&autoload=false";

    private static string Props(params string[] levels) =>
        "{\"default_generation_settings\":{\"n_ctx\":8192},\"chat_template_caps\":{\"supports_reasoning_effort\":true,"
        + "\"reasoning_effort_levels\":" + JsonSerializer.Serialize(levels) + "}}";

    private const string NoLevels = "{\"default_generation_settings\":{\"n_ctx\":8192},\"chat_template_caps\":{}}";

    /// <summary>The bare <c>/props</c> says it is llama-server and lists nothing: only the read that names the model gets the levels.</summary>
    private static LoopbackHttpServer Server(Func<string?> modelProps) => new(
        (path, _) => path == Asked ? modelProps()
                   : path.StartsWith("/props", StringComparison.Ordinal) ? NoLevels
                   : path.StartsWith("/v1/chat/completions", StringComparison.Ordinal) ? UtilityCallsCarryNoReasoningTests.Sse
                   : null);

    private static OpenAiCompatibleClient Client(LoopbackHttpServer server) =>
        new(new InferpalConfig { Provider = "openai-compatible", BaseUrl = server.BaseUrl }) { LoadedWindowProbeBudget = TimeSpan.FromSeconds(30) };

    private static string? Effort(string body) =>
        JsonDocument.Parse(body).RootElement.TryGetProperty("reasoning_effort", out var value) ? value.GetString() : null;

    private static async Task<string?> SentAsync(string? props, ReasoningEffort effort)
    {
        using var server = Server(() => props);
        await UtilityCallsCarryNoReasoningTests.AskAsync(Client(server), Model, effort);

        Assert.Contains(Asked, server.Paths);   // WITNESS: "nothing sent" below means the levels were read
        return Effort(Assert.Single(UtilityCallsCarryNoReasoningTests.ChatBodies(server)));
    }

    [Theory]
    [InlineData("Low", "low")]
    [InlineData("Medium", "medium")]
    [InlineData("High", "high")]
    public async Task ALevelThePropsList_GoesAsItIs(string effort, string sent) =>
        Assert.Equal(sent, await SentAsync(Props("low", "medium", "high"), Enum.Parse<ReasoningEffort>(effort)));

    [Fact]
    public async Task ALevelGoes_SpelledAsTheServerListsIt() =>
        Assert.Equal("LOW", await SentAsync(Props("LOW"), ReasoningEffort.Low));

    [Fact]
    public async Task ALevelThePropsDoNotList_IsNotSent() =>
        Assert.Null(await SentAsync(Props("low", "high"), ReasoningEffort.Medium));

    /// <summary>As on LM Studio: a model that does not publish it may not turn its reasoning off, and sent it may reason longer.</summary>
    [Fact]
    public async Task NoReasoning_GoesOnlyIfListed()
    {
        Assert.Null(await SentAsync(Props("low", "high"), ReasoningEffort.Off));
        Assert.Equal("none", await SentAsync(Props("none", "low"), ReasoningEffort.Off));
    }

    /// <summary>An older llama-server publishes no levels at all: it gets what it always got.</summary>
    [Fact]
    public async Task AnOlderServer_IsSentNothing() =>
        Assert.Null(await SentAsync(NoLevels, ReasoningEffort.Low));

    /// <summary>Another OpenAI-compatible server has no <c>/props</c>: nothing sent, the question still goes, and it is not asked again.</summary>
    [Fact]
    public async Task AnotherServer_IsSentNothing_AndNotAskedAgain()
    {
        using var server = new LoopbackHttpServer((path, _) =>
            path.StartsWith("/v1/chat/completions", StringComparison.Ordinal) ? UtilityCallsCarryNoReasoningTests.Sse : null);
        var client = Client(server);

        await UtilityCallsCarryNoReasoningTests.AskAsync(client, Model, ReasoningEffort.Low);
        await UtilityCallsCarryNoReasoningTests.AskAsync(client, Model, ReasoningEffort.Low);

        Assert.All(UtilityCallsCarryNoReasoningTests.ChatBodies(server), b => Assert.Null(Effort(b)));
        Assert.DoesNotContain(server.Paths, p => p.Contains("model=", StringComparison.Ordinal));
    }

    /// <summary>A read that fails sends nothing this time and is not kept: the default low would otherwise be lost for every
    /// question of the next half-minute, and the model would reason at its own level.</summary>
    [Fact]
    public async Task AReadThatFailed_IsNotKept_AndTheQuestionStillGoes()
    {
        var answers = new Queue<string?>(["not json", Props("low")]);
        using var server = Server(() => answers.Dequeue());
        var client = Client(server);

        await UtilityCallsCarryNoReasoningTests.AskAsync(client, Model, ReasoningEffort.Low);
        await UtilityCallsCarryNoReasoningTests.AskAsync(client, Model, ReasoningEffort.Low);

        var bodies = UtilityCallsCarryNoReasoningTests.ChatBodies(server).ToList();
        Assert.Equal(2, bodies.Count);
        Assert.Null(Effort(bodies[0]));
        Assert.Equal("low", Effort(bodies[1]));
    }

    /// <summary>A body cut in flight is neither an HTTP nor a JSON failure: the read is optional, the question is not.</summary>
    [Fact]
    public async Task AReadCutInFlight_DoesNotFailTheQuestion()
    {
        using var server = Server(() => Props("low"));
        server.CutsBody = path => path == Asked;

        await UtilityCallsCarryNoReasoningTests.AskAsync(Client(server), Model, ReasoningEffort.Low);

        Assert.Contains(Asked, server.Paths);
        Assert.Null(Effort(Assert.Single(UtilityCallsCarryNoReasoningTests.ChatBodies(server))));
    }

    /// <summary>A question and its title ask in turn: one read serves both.</summary>
    [Fact]
    public async Task AnAnswerIsKept_ForTheNextQuestion()
    {
        using var server = Server(() => Props("low"));
        var client = Client(server);

        await UtilityCallsCarryNoReasoningTests.AskAsync(client, Model, ReasoningEffort.Low);
        await UtilityCallsCarryNoReasoningTests.AskAsync(client, Model, ReasoningEffort.Low);

        Assert.Single(server.Paths, p => p == Asked);
    }
}
