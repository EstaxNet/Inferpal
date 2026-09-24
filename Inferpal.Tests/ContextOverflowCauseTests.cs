using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services.Inference;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ A request too large for the model's context window was always blamed on "agent mode — the tool
/// definitions alone take several thousand tokens", with "reduce the number of enabled agent tools" as the
/// remedy: on a code action that sends no tool at all, on a question whose attached file is the whole
/// problem. Measured on the remote LM Studio (a model loaded with 8,192 tokens, the default window): a
/// 64 KB attachment and no tool is refused with "n_keep: 21816 >= n_ctx: 8192". The message now says what
/// the request is made of, largest part first, each with the gesture that shrinks it.
/// </summary>
[Collection(CultureSerialCollection.Name)]   // compares localized sentences
public class ContextOverflowCauseTests
{
    private static List<ChatMessageDto> Conversation(int earlierChars, int lastChars) =>
    [
        new("system", new string('s', 4_000)),                           // ~1,000 tokens
        new("user", new string('e', earlierChars)),
        new("assistant", "ok"),
        new("user", "Review this.\n```\n" + new string('c', lastChars) + "\n```"),
    ];

    private static List<ToolDefinition> Tools(int count) =>
        Enumerable.Range(0, count)
                  .Select(i => new ToolDefinition("function", new ToolFunction($"tool_{i}", new string('d', 1_600), new { })))
                  .ToList();

    [Fact]
    public void ARequestWithNoTool_IsNotBlamedOnTheTools_ItsLargestPartIsNamedFirst()
    {
        // The /review of a long file, or a large @file: no tool at all, one oversized message.
        var size = RequestSize.Of(Conversation(earlierChars: 400, lastChars: 60_000), defs: null);
        var msg  = OpenAiCompatibleClient.CheckContextFit(size, loadedContext: 8_192);

        Assert.Equal(0, size.Tools);                                                   // witness: no tool was sent
        Assert.NotNull(msg);
        Assert.Contains("\n- " + Strings.ContextPartLast(size.Last), msg);             // the part that overflows
        Assert.True(msg!.IndexOf(Strings.ContextPartLast(size.Last), StringComparison.Ordinal)
                  < msg.IndexOf(Strings.ContextPartSystem(size.SystemPrompt), StringComparison.Ordinal),
                    "the largest part must come first");
        Assert.DoesNotContain(Strings.ContextPartTools(size.Tools), msg);             // an empty part is not listed
    }

    [Fact]
    public void AnAgentRequestWhoseToolsDominate_StillNamesTheTools_First()
    {
        // Reference arm: the case the old message was written for keeps its cause.
        var size = RequestSize.Of(Conversation(earlierChars: 400, lastChars: 400), Tools(24));
        var msg  = OpenAiCompatibleClient.CheckContextFit(size, loadedContext: 8_192);

        Assert.True(size.Tools > size.SystemPrompt + size.Earlier + size.Last);       // witness: tools dominate
        Assert.NotNull(msg);
        var tools = msg!.IndexOf(Strings.ContextPartTools(size.Tools), StringComparison.Ordinal);
        Assert.True(tools >= 0, "the tools are not named");
        Assert.True(tools < msg.IndexOf(Strings.ContextPartLast(size.Last), StringComparison.Ordinal));
    }

    [Fact]
    public void ALongConversation_IsNamedAsSuch()
    {
        var size = RequestSize.Of(Conversation(earlierChars: 60_000, lastChars: 400), defs: null);
        var msg  = OpenAiCompatibleClient.CheckContextFit(size, loadedContext: 8_192);

        Assert.NotNull(msg);
        Assert.Contains("\n- " + Strings.ContextPartEarlier(size.Earlier), msg);
    }

    [Fact]
    public void ARequestThatFits_SaysNothing()
        => Assert.Null(OpenAiCompatibleClient.CheckContextFit(
               RequestSize.Of(Conversation(earlierChars: 400, lastChars: 400), Tools(2)), loadedContext: 8_192));

    /// <summary>
    /// ⚠ A refusal that comes back as an HTTP error status went straight to the generic server-error
    /// message, never through the overflow check — and that is the form LM Studio actually uses
    /// (measured: HTTP 400 with the body below). A generic OpenAI-compatible server exposes no loaded
    /// window, so the proactive guard never runs there: every overflow takes this path.
    /// </summary>
    [Fact]
    public async Task AnOverflowRefusedWithAnHttpStatus_GetsTheSameBreakdown()
    {
        const string refusal = """{"error":"The number of tokens to keep from the initial prompt is greater than the context length (n_keep: 21816>= n_ctx: 8192). Try to load the model with a larger context length, or provide a shorter input."}""";
        using var server = new LoopbackHttpServer(
            path => path.StartsWith("/v1/chat/completions", StringComparison.Ordinal) ? refusal : null, status: _ => 400);
        var client   = new OpenAiCompatibleClient(new InferpalConfig { Provider = "openai-compatible", BaseUrl = server.BaseUrl });
        var messages = Conversation(earlierChars: 400, lastChars: 60_000);

        var ex = await Assert.ThrowsAsync<AgentHttpException>(() => client.SendChatAsync(
            "m", messages, EmptyToolRegistry.Instance, onToken: null, CancellationToken.None));

        Assert.Contains("/v1/chat/completions", string.Join(" ", server.Paths));      // witness: the server answered
        Assert.Contains("n_keep", ex.Message);                                         // the server's words are kept
        Assert.Contains("\n- " + Strings.ContextPartLast(RequestSize.Of(messages, null).Last), ex.Message);
    }

    [Fact]
    public async Task AnyOtherRefusal_StaysTheServersOwnError()
    {
        // Reference arm: a 400 that is not about the window must not grow a breakdown.
        using var server = new LoopbackHttpServer(
            path => path.StartsWith("/v1/chat/completions", StringComparison.Ordinal) ? """{"error":"model 'm' not found"}""" : null,
            status: _ => 400);
        var client = new OpenAiCompatibleClient(new InferpalConfig { Provider = "openai-compatible", BaseUrl = server.BaseUrl });

        var ex = await Assert.ThrowsAsync<AgentHttpException>(() => client.SendChatAsync(
            "m", Conversation(400, 400), EmptyToolRegistry.Instance, onToken: null, CancellationToken.None));

        Assert.Contains("not found", ex.Message);
        Assert.DoesNotContain("\n- ", ex.Message);
    }

    [Fact]
    public void AServerSideOverflow_NamesTheParts_AndKeepsTheServersWords()
    {
        // The same fact reported by the server after the send (no loaded window known beforehand).
        const string server = "request (21816 tokens) exceeds the available context size (8192 tokens)";
        var size = RequestSize.Of(Conversation(earlierChars: 400, lastChars: 60_000), defs: null);
        var msg  = InferenceProviderBase.MapServerError(server, "http://localhost:1234/v1", () => size);

        Assert.Contains(server, msg);
        Assert.Contains("\n- " + Strings.ContextPartLast(size.Last), msg);
    }
}
