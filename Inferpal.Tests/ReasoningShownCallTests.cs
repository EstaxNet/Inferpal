using System.IO;
using System.Text;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Models;
using Inferpal.Services.Inference;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A reply written entirely in the reasoning channel is read as its content would be: with no tool offered, a call it
/// SHOWS is part of the answer, never a call.
/// </summary>
/// <remarks>
/// The content path already held that rule (<c>InlineToolCallParser.FromContent</c>); the reasoning path parsed every
/// call it found, whatever the request offered. A summary or an inline edit — requests that offer no tool — whose model
/// reasoned "I could call &lt;tool_call&gt;…" came back as a tool call that its caller could not run: no answer at all.
/// </remarks>
public sealed class ReasoningShownCallTests
{
    private const string ShownCall =
        "The user wants a summary. I could call <tool_call>{\"name\": \"read_file\", \"arguments\": {\"path\": \"a.cs\"}}</tool_call> "
        + "but no tool is offered here, so: the conversation set up the build and fixed one test.";

    private sealed class Offering(params string[] names) : IToolRegistry
    {
        public IReadOnlyList<ToolDefinition> Definitions { get; } =
            [.. names.Select(n => new ToolDefinition("function", new ToolFunction(n, n, new { type = "object" })))];
        public DiffInfo? ConsumeDiff() => null;
        public Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct) => Task.FromResult("");
    }

    private static async Task<ChatTurnResult> SendAsync(string reasoning, IToolRegistry tools)
    {
        var body = new StringBuilder("data: ")
            .Append(JsonSerializer.Serialize(new { choices = new[] { new { delta = new { reasoning_content = reasoning } } } }))
            .Append("\n\ndata: [DONE]\n\n").ToString();
        using var server = new LoopbackHttpServer(path => path.StartsWith("/v1/chat/completions", StringComparison.Ordinal) ? body : null);
        var client = new OpenAiCompatibleClient(new InferpalConfig
            { Provider = "openai-compatible", BaseUrl = server.BaseUrl, ContextWindowSize = 8192 });
        return await client.SendChatAsync("m", [new ChatMessageDto("user", "summarize")], tools, onToken: null, CancellationToken.None);
    }

    [Fact]
    public async Task WithNoToolOffered_ACallTheReasoningShows_IsPartOfTheAnswer()
    {
        var turn = await SendAsync(ShownCall, new Offering());

        Assert.Null(turn.ToolCalls);
        Assert.True(turn.AnswerIsReasoning);
        Assert.Contains("fixed one test", turn.TextContent);
    }

    [Fact]
    public async Task WithTheToolOffered_TheCallInTheReasoning_IsRecovered()
    {
        // Reference arm: a reasoning model that routes its call into the reasoning channel still has it run.
        var turn = await SendAsync("<tool_call>{\"name\": \"read_file\", \"arguments\": {\"path\": \"a.cs\"}}</tool_call>",
                                   new Offering("read_file"));

        var call = Assert.Single(turn.ToolCalls!);
        Assert.Equal("read_file", call.Function.Name);
    }

    [Fact]
    public void BothClients_PassTheOfferedNames()
    {
        var root = ConversationPersistenceSilenceTests.RepoRoot();
        foreach (var file in new[] { "OllamaClient.cs", "OpenAiCompatibleClient.cs" })
        {
            var code = ConventionCoverageTests.CodeOnly(Path.Combine(root, "Inferpal.Core", "Services", "Inference", file));
            var at = code.IndexOf("ReasoningOnlyTurn.Recover(", StringComparison.Ordinal);
            Assert.True(at > 0, $"{file}: witness — the recovery is called");
            Assert.Contains("OfferedNames(tools)", code[at..code.IndexOf(';', at)]);
        }
    }
}
