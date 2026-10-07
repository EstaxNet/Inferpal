using Inferpal.Services.Agent;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A call written as text with its arguments beside the name — <c>{"name":"search_in_files","pattern":…,"path":…}</c> —
/// carries those arguments.
/// </summary>
/// <remarks>
/// ⚠ Read as a call without arguments, what the model wrote was dropped: the call ran with <c>{}</c>, so
/// <c>run_tests</c> ran the WHOLE suite and <c>search_in_files</c> was refused for a pattern the model had given.
/// </remarks>
public class FlatToolCallArgumentsTests
{
    [Fact]
    public void TheAnswerGemmaGave_CarriesItsArguments()
    {
        // Gemma 4 12B on the bench, verbatim: arguments at the top level, then a stray closer.
        var (calls, cleaned) = InlineToolCallParser.FromContent(
            "I will begin by exploring the codebase to understand the `ApplyDiscount` logic and identify why the test is failing. I'll start by searching for the `ApplyDiscount` method in the `Shop` project.\n\n<tool_call>\n{\"name\": \"search_in_files\", \"pattern\": \"ApplyDiscount\", \"path\": \"/root/battery/runs/gemma-4-12b-it-qat-agent-bugfix/ws\"}\n</tool_class>\n</tool_call>",
            ["search_in_files"]);

        var call = Assert.Single(calls!);
        Assert.Equal("search_in_files", call.Function.Name);
        Assert.Equal("ApplyDiscount", call.Function.Arguments.GetProperty("pattern").GetString());
        Assert.Equal("/root/battery/runs/gemma-4-12b-it-qat-agent-bugfix/ws", call.Function.Arguments.GetProperty("path").GetString());
        Assert.DoesNotContain("tool_c", cleaned);
    }

    [Fact]
    public void AFlatFilter_NarrowsTheRun_InsteadOfRunningTheWholeSuite()
    {
        var (calls, _) = InlineToolCallParser.TryParse("<tool_call>{\"name\":\"run_tests\",\"filter\":\"Pricing\",\"timeout_seconds\":60}</tool_call>");

        var args = Assert.Single(calls!).Function.Arguments;
        Assert.Equal("Pricing", args.GetProperty("filter").GetString());
        Assert.Equal(60, args.GetProperty("timeout_seconds").GetInt32());
        Assert.False(args.TryGetProperty("name", out _));   // the call's name is not one of its arguments
    }

    [Fact]
    public void TheKeysThatIdentifyACall_AreNotArguments()
    {
        var (ids, _) = InlineToolCallParser.TryParse("<tool_call>{\"id\":\"call_1\",\"type\":\"function\",\"name\":\"list_files\",\"path\":\"src\"}</tool_call>");

        Assert.Equal(["path"], Assert.Single(ids!).Function.Arguments.EnumerateObject().Select(p => p.Name));
    }

    /// <summary>⚠ Reference arms: a call with no arguments still has none, and nested arguments are read as before.</summary>
    [Fact]
    public void NoArguments_AndNestedArguments_AreUnchanged()
    {
        var (none, _) = InlineToolCallParser.TryParse("<tool_call>{\"name\":\"get_git_status\"}</tool_call>");
        var bare = Assert.Single(none!).Function;
        Assert.Null(bare.UnparsedArguments);
        Assert.Empty(bare.Arguments.EnumerateObject());

        var (nested, _) = InlineToolCallParser.TryParse("<tool_call>{\"name\":\"list_files\",\"arguments\":{\"path\":\"src\"}}</tool_call>");
        Assert.Equal("src", Assert.Single(nested!).Function.Arguments.GetProperty("path").GetString());
    }
}
