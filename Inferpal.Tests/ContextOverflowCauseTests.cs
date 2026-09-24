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
