using System.IO;
using System.Text;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Models;
using Inferpal.Services.Agent;
using Inferpal.Services.Execution;
using Inferpal.Services.Inference;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  A structured tool call whose arguments keep repeating their own JSON is stopped, and refused with that cause.
//
//  Captured on LM Studio with Devstral Small 2 (Fixtures/devstral-looping-arguments.json, verbatim): an apply_edits
//  broke its JSON at a C# interpolated string ($"…"), then either repeated the same edit object or opened new calls
//  inside its arguments — 150 seconds of "Thinking…" each time, the bound far away.
// ──────────────────────────────────────────────────────────────────────────────────────────────
public class ArgumentsLoopTests
{
    private sealed record Loop(string Shape, string Head, string Block);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static Loop Captured(int index)
    {
        var loops = JsonSerializer.Deserialize<Loop[]>(
            File.ReadAllText(Path.Combine(RepoRoot(), "Inferpal.Tests", "Fixtures", "devstral-looping-arguments.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal(2, loops.Length);   // witness: both captured shapes are read
        return loops[index];
    }

    private static string Text(Loop loop, int repeats) => loop.Head + string.Concat(Enumerable.Repeat(loop.Block, repeats));

    /// <summary>A structured call streamed the way LM Studio streams one: the name first, then the arguments in pieces.</summary>
    private static string CallStream(string name, string arguments, int piece = 5)
    {
        static string Chunk(object toolCall) =>
            "data: " + JsonSerializer.Serialize(new { choices = new[] { new { index = 0, delta = new { tool_calls = new[] { toolCall } } } } }) + "\n\n";

        var sb = new StringBuilder(Chunk(new { index = 0, id = "1", type = "function", function = new { name, arguments = "" } }));
        for (var i = 0; i < arguments.Length; i += piece)
            sb.Append(Chunk(new { index = 0, function = new { arguments = arguments.Substring(i, Math.Min(piece, arguments.Length - i)) } }));
        return sb.Append("data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n\ndata: [DONE]\n\n").ToString();
    }

    private static async Task<ChatTurnResult> SendAsync(string stream)
    {
        using var server = new LoopbackHttpServer(path => path.StartsWith("/v1/chat/completions", StringComparison.Ordinal) ? stream : null);
        // A window far larger than the stream: what stops the reading is the detector, never the bound.
        var client = new OpenAiCompatibleClient(new InferpalConfig
            { Provider = "openai-compatible", BaseUrl = server.BaseUrl, ContextWindowSize = 262_144 });
        return await client.SendChatAsync("m", [new ChatMessageDto("user", "rename it")], EmptyToolRegistry.Instance,
                                          onToken: null, CancellationToken.None);
    }

    private static bool Feed(Func<string, bool> repeats, string text, int piece = 5)
    {
        for (var i = 0; i < text.Length; i += piece)
            if (repeats(text.Substring(i, Math.Min(piece, text.Length - i)))) return true;
        return false;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task ACallThatKeepsRepeatingItsArguments_IsStopped_AndRefusedWithItsCause(int shape)
    {
        var loop = Captured(shape);
        var text = Text(loop, 100);
        var turn = await SendAsync(CallStream("apply_edits", text));

        var call = Assert.Single(Assert.IsAssignableFrom<List<ToolCallDto>>(turn.ToolCalls)).Function;
        Assert.True(call.StoppedRepeating, loop.Shape);
        Assert.True(turn.StoppedRepeating);
        Assert.True(turn.CutAtLimit);   // incomplete — every reader that refuses a cut reply refuses this one
        Assert.True((call.UnparsedArguments ?? call.Arguments.GetRawText()).Length < loop.Head.Length + 20 * loop.Block.Length,
                    "the client stopped reading within a few repeats");

        var refusal = await AgentOrchestrator.ExecuteToolSafeAsync(EmptyToolRegistry.Instance, call, turn.CutAtLimit,
                                                                   CancellationToken.None);
        Assert.Contains("NOT executed", refusal);
        Assert.Contains("kept repeating the same text", refusal);
        Assert.DoesNotContain("length limit", refusal);   // "split the work" is not the remedy for a loop
    }

    /// <summary>
    /// ⚠ Reference arm: a FILE repeats itself — separator lines, escaped as ─ the way System.Text.Json writes them,
    /// identical test bodies — but all of it inside one string value, where every quote is escaped. The text loop test
    /// trips on it (the witness); the call must go through untouched.
    /// </summary>
    [Fact]
    public async Task AFileThatRepeatsItself_IsWrittenUntouched()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < 12; i++)
        {
            sb.Append("// ").Append(new string('─', 100)).Append('\n');
            sb.Append("    [Fact] public void Case()\n    {\n        var cart = new Cart();\n        cart.Add(1.5m);\n")
              .Append("        Assert.Equal(\"4\", cart.ComputeTotal().ToString());\n    }\n");
        }
        var content   = sb.ToString();
        var arguments = JsonSerializer.Serialize(new { path = "tests/CartTests.cs", content });
        Assert.True(Feed(new TextLoopDetector().Repeats, arguments), "witness: the text loop test trips on these arguments");

        var turn = await SendAsync(CallStream("write_file", arguments));

        var call = Assert.Single(Assert.IsAssignableFrom<List<ToolCallDto>>(turn.ToolCalls)).Function;
        Assert.False(call.StoppedRepeating);
        Assert.False(turn.StoppedRepeating);
        Assert.Equal(content, call.Arguments.GetProperty("content").GetString());
    }

    /// <summary>
    /// ⚠ Reference arm: twelve near-identical edits on one long path — JSON structure that repeats, with raw quotes, but
    /// each edit differs by a number. The text loop test trips on it (the witness); the call must go through untouched.
    /// </summary>
    [Fact]
    public async Task SimilarEditsOnOneFile_AreAppliedUntouched()
    {
        const string path = "/root/battery/runs/mistralai-devstral-small-2-2512-agent-rename/ws/src/Shop/Pricing/Very/Long/Folder/Cart.cs";
        // Spaced as a model writes JSON (the captured Devstral calls are), not compact as System.Text.Json writes it.
        var edits = Enumerable.Range(0, 12).Select(k =>
        {
            var line = $"        // TODO: ComputeTotal{k} is kept for compatibility with the old receipt format";
            return $"{{\"path\": \"{path}\", \"old_content\": \"{line}\", \"new_content\": \"{line.Replace("ComputeTotal", "CalculateTotal")}\"}}";
        });
        var arguments = "{\"edits\": [" + string.Join(", ", edits) + "]}";
        Assert.True(Feed(new TextLoopDetector().Repeats, arguments), "witness: the text loop test trips on these arguments");

        var turn = await SendAsync(CallStream("apply_edits", arguments));

        var call = Assert.Single(Assert.IsAssignableFrom<List<ToolCallDto>>(turn.ToolCalls)).Function;
        Assert.False(call.StoppedRepeating);
        Assert.Equal(12, call.Arguments.GetProperty("edits").GetArrayLength());
    }
}
