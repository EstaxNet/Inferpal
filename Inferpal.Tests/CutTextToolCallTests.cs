using System.Text.Json;
using Inferpal.Config;
using Inferpal.Models;
using Inferpal.Services.Agent;
using Inferpal.Services.Inference;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A model that writes its tool calls as TEXT (Gemma under PromptedTools, Devstral's [TOOL_CALLS], Qwen's
/// &lt;tool_call&gt;) and is cut at the length limit before the closing marker: no reader found the call, the raw call
/// stayed text, and the run ended on it as its answer — the model never told to split the work. It is now read as an
/// unreadable call, which the funnel refuses with the remedy that works on a cut reply.
/// </summary>
public class CutTextToolCallTests
{
    private static readonly string[] Offered = ["write_file", "read_file"];

    [Theory]
    [InlineData("I will write it.\n<tool_call>\n{\"name\": \"write_file\", \"arguments\": {\"path\": \"Big.cs\", \"content\": \"public class Big")]
    [InlineData("I will write it.\n<tool_call><function=write_file><parameter=path>Big.cs</parameter><parameter=content>public class Big")]
    [InlineData("I will write it.\n<|tool_call>call:write_file{path:<|\"|>Big.cs<|\"|>,content:<|\"|>public class Big")]
    [InlineData("I will write it.\n[TOOL_CALLS]write_file[ARGS]{\"path\": \"Big.cs\", \"content\": \"public class Big")]
    public void ACallCutBeforeItsClosingMarker_IsReadAsAnUnreadableCall(string content)
    {
        var cut = InlineToolCallParser.CutCall(content, Offered);

        Assert.NotNull(cut);
        Assert.Equal("write_file", cut!.Value.Call.Function.Name);
        Assert.NotNull(cut.Value.Call.Function.UnparsedArguments);   // never run with default arguments
        Assert.Equal("I will write it.", cut.Value.Before);
    }

    [Fact]
    public void AToolThatWasNotOffered_IsNotACall()
    {
        // Reference arm: an answer that shows a call format, for a tool nobody offered, stays text.
        Assert.Null(InlineToolCallParser.CutCall("<tool_call>\n{\"name\": \"delete_everything\", \"arguments\": {", Offered));
        Assert.Null(InlineToolCallParser.CutCall("A plain answer, cut in the middle of a sente", Offered));
    }

    private sealed class WriteTool : IToolRegistry
    {
        public IReadOnlyList<ToolDefinition> Definitions { get; } = [new("function", new ToolFunction("write_file", "write", new { }))];
        public DiffInfo? ConsumeDiff() => null;
        public Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct) => Task.FromResult("written");
    }

    private static string Stream(string content, string finishReason) =>
        "data: " + JsonSerializer.Serialize(new { choices = new[] { new { index = 0, delta = new { content } } } }) + "\n\n"
        + "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"" + finishReason + "\"}]}\n\n"
        + "data: [DONE]\n\n";

    [Fact]
    public async Task TheBasicLoop_RefusesTheCutCall_InsteadOfEndingOnIt()
    {
        var requests = 0;
        using var server = new LoopbackHttpServer(path =>
            !path.StartsWith("/v1/chat/completions", StringComparison.Ordinal) ? null
            : Interlocked.Increment(ref requests) == 1
                ? Stream("<tool_call>\n{\"name\": \"write_file\", \"arguments\": {\"path\": \"Big.cs\", \"content\": \"public class Big", "length")
                : Stream("I will write it in two parts.", "stop"));
        var client = new OpenAiCompatibleClient(new InferpalConfig { Provider = "openai-compatible", BaseUrl = server.BaseUrl });

        var result = await client.RunAgentAsync(
            "m", [new ChatMessageDto("user", "write Big.cs")], new WriteTool(), onStep: _ => { }, onToken: null,
            CancellationToken.None);

        Assert.True(requests >= 2, $"{requests} request(s): the run ended on the cut call");   // witness
        var refused = Assert.Single(result.Executions).Output;
        Assert.Contains("NOT executed", refused);
        Assert.Contains("length limit", refused);
        Assert.DoesNotContain("<tool_call>", result.FinalResponse);
    }
}
