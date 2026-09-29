using System.Text;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Models;
using Inferpal.Services;
using Inferpal.Services.Agent;
using Inferpal.Services.Inference;
using Inferpal.Services.Presentation;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ Cohere's turn (North Mini Code) — reasoning up to <c>&lt;|END_THINKING|&gt;</c>, calls in
/// <c>&lt;|START_ACTION|&gt;[…]&lt;|END_ACTION|&gt;</c>, the answer in <c>&lt;|START_TEXT|&gt;…&lt;|END_TEXT|&gt;</c> — is left as text by
/// LM Studio. Unread, no call ran: the chat scenarios ended on the raw reasoning with the call written under it, and
/// the user read the markers. The texts below are North Mini Code's own, as the bench received them.
/// </summary>
public class CohereFormatTests
{
    private const string ShellTurn =
        "The user wants to know which version of the .NET SDK is installed on this machine. I need to run a command to "
        + "check this.\n\nLet me run the standard dotnet command to check the version.<|END_THINKING|><|START_ACTION|>[\n"
        + "    {\"tool_call_id\": \"0\", \"tool_name\": \"run_command\", \"parameters\": {\"command\": \"dotnet --version\"}}\n"
        + "]<|END_ACTION|>";

    private const string AnswerTurn =
        "I've already applied the changes. I should summarize what was done.<|END_THINKING|><|START_TEXT|>The Counter page "
        + "now has a Reset button that sets the count back to zero.<|END_TEXT|>";

    /// <summary>The stream as a server sends it: small chunks, markers cut in two.</summary>
    private static string ThroughTheEnvelope(string text, int chunk)
    {
        var envelope = new ChannelEnvelope();
        var answer   = new StringBuilder();
        for (var i = 0; i < text.Length; i += chunk)
            answer.Append(envelope.Push(text.Substring(i, Math.Min(chunk, text.Length - i))).Answer);
        return answer.Append(envelope.Flush().Answer).ToString();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(4096)]
    public void TheCall_IsRead_AndTheReasoningIsNotTheAnswer(int chunk)
    {
        var content = ThroughTheEnvelope(ShellTurn, chunk);

        var (calls, cleaned) = InlineToolCallParser.TryParse(content);

        var call = Assert.Single(calls!);
        Assert.Equal("run_command", call.Function.Name);
        Assert.Equal("dotnet --version", call.Function.Arguments.GetProperty("command").GetString());
        Assert.Equal(string.Empty, MarkdownParser.StripThinkTags(cleaned));   // the reasoning is not shown
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(4096)]
    public void TheAnswer_IsShownWithoutReasoningOrMarkers(int chunk) =>
        Assert.Equal("The Counter page now has a Reset button that sets the count back to zero.",
                     MarkdownParser.StripThinkTags(ThroughTheEnvelope(AnswerTurn, chunk)));

    [Fact]
    public void SeveralCallsInOneAction_AreAllRead()
    {
        var (calls, _) = InlineToolCallParser.TryParse(
            "<|START_ACTION|>[{\"tool_call_id\": \"0\", \"tool_name\": \"read_file\", \"parameters\": {\"path\": \"a.cs\"}}, "
            + "{\"tool_call_id\": \"1\", \"tool_name\": \"read_file\", \"parameters\": {\"path\": \"b.cs\"}}]<|END_ACTION|>");

        Assert.Equal(["a.cs", "b.cs"], calls!.Select(c => c.Function.Arguments.GetProperty("path").GetString()));
    }

    /// <summary>Reference arm: content that is not Cohere's passes through the envelope untouched, chunked or not.</summary>
    [Theory]
    [InlineData("if (a < b && c <| d) return;")]
    [InlineData("Use `<|START` only in docs, x < y.")]
    [InlineData("Plain answer.")]
    public void OtherContent_IsUntouched(string text)
    {
        Assert.Equal(text, ThroughTheEnvelope(text, 1));
        Assert.Equal(text, ThroughTheEnvelope(text, 4096));
    }

    // ── Through the production client ─────────────────────────────────────────

    private sealed class ShellRegistry : IToolRegistry
    {
        public IReadOnlyList<ToolDefinition> Definitions { get; } =
            [new("function", new ToolFunction("run_command", "run a command", new { }))];
        public DiffInfo? ConsumeDiff() => null;
        public Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct) => Task.FromResult("10.0.401");
    }

    /// <summary>The turn streamed in chunks of 9 characters, as SSE content deltas.</summary>
    private static string Stream(string content)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < content.Length; i += 9)
            sb.Append("data: ").Append(JsonSerializer.Serialize(new
            {
                choices = new[] { new { index = 0, delta = new { content = content.Substring(i, Math.Min(9, content.Length - i)) } } },
            })).Append("\n\n");
        return sb.Append("data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n").ToString();
    }

    [Fact]
    public async Task TheProductionClient_RunsTheCall()
    {
        using var server = new LoopbackHttpServer(
            path => path.StartsWith("/v1/chat/completions", StringComparison.Ordinal) ? Stream(ShellTurn) : null);
        var client = new OpenAiCompatibleClient(new InferpalConfig { Provider = "openai-compatible", BaseUrl = server.BaseUrl });

        var turn = await client.SendChatAsync(
            "north-mini-code-1.0", [new ChatMessageDto("user", "Which .NET SDK is installed?")], new ShellRegistry(),
            onToken: null, CancellationToken.None);

        Assert.Contains("/v1/chat/completions", string.Join(" ", server.Paths));   // witness: the model answered
        var call = Assert.Single(turn.ToolCalls!);
        Assert.Equal("run_command", call.Function.Name);
        Assert.DoesNotContain("<|", turn.TextContent);
    }
}
