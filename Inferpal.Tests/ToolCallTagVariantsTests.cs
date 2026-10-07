using Inferpal.Services.Agent;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A call written between tags whose spelling drifts — Gemma 4 under <c>PromptedTools</c> writes <c>call:tool_call</c>
/// after its own opener, and closes with <c>&lt;/tool__call&gt;</c>.
/// </summary>
/// <remarks>
/// ⚠ Matched strictly, each of these stayed in the content, became the answer, and the turn ended with nothing done.
/// The texts below are answers Gemma 4 12B gave on the bench, verbatim.
/// </remarks>
public class ToolCallTagVariantsTests
{
    private static readonly string[] Offered = ["list_files", "search_in_files"];

    [Theory]
    [InlineData("<|tool_call>call:tool_call>\n{\"name\": \"search_in_files\", \"arguments\": {\"path\": \"/root/battery/runs/gemma-4-12b-it-qat-chat-question/ws\", \"pattern\": \"ApplyDiscount\"}}\n</tool_call>",
                "search_in_files", "pattern", "ApplyDiscount")]
    [InlineData("<|tool_call>call:tool_call\n{\"name\":\"search_in_files\",\"arguments\":{\"path\":\"/root/battery/runs/gemma-4-12b-it-qat-agent-bugfix/ws/tests/Shop.Tests\",\"pattern\":\"ApplyDiscount_TenPercent_OnHundred_ReturnsNinety\"}}\n</tool_call>",
                "search_in_files", "pattern", "ApplyDiscount_TenPercent_OnHundred_ReturnsNinety")]
    [InlineData("<tool_call>\n{\"name\": \"list_files\", \"arguments\": {\"path\": \"/root/battery/runs/gemma-4-12b-it-qat-py-bugfix/ws\"}}\n</tool__call>",
                "list_files", "path", "/root/battery/runs/gemma-4-12b-it-qat-py-bugfix/ws")]
    public void TheAnswersGemmaGave_AreTheCallsItMeant(string answer, string tool, string key, string value)
    {
        var (calls, cleaned) = InlineToolCallParser.FromContent(answer, Offered);

        var call = Assert.Single(calls!);
        Assert.Equal(tool, call.Function.Name);
        Assert.Null(call.Function.UnparsedArguments);
        Assert.Equal(value, call.Function.Arguments.GetProperty(key).GetString());
        Assert.Equal(string.Empty, cleaned);   // nothing of the call is left to show as the answer
    }

    /// <summary>A stray closer before the real one is consumed with it: nothing of the tags is shown.</summary>
    [Fact]
    public void AStrayCloserBeforeTheRealOne_IsConsumed()
    {
        var (calls, cleaned) = InlineToolCallParser.TryParse(
            "Searching.\n<tool_call>\n{\"name\": \"list_files\", \"arguments\": {\"path\": \"src\"}}\n</tool_class>\n</tool_call>");

        Assert.Equal("list_files", Assert.Single(calls!).Function.Name);
        Assert.Equal("Searching.", cleaned);
    }

    /// <summary>A call wrapped in Gemma's own syntax after "call:tool_call" is the call it wraps.</summary>
    [Fact]
    public void AWrappedCallInGemmasSyntax_IsTheCallItWraps()
    {
        var (calls, cleaned) = InlineToolCallParser.TryParse(
            "<|tool_call>call:tool_call{name:<|\"|>search_in_files<|\"|>,arguments:{pattern:<|\"|>ApplyDiscount<|\"|>}}<tool_call|>");

        var call = Assert.Single(calls!);
        Assert.Equal("search_in_files", call.Function.Name);
        Assert.Equal("ApplyDiscount", call.Function.Arguments.GetProperty("pattern").GetString());
        Assert.Equal(string.Empty, cleaned);
    }

    /// <summary>
    /// ⚠ A "call:tool_call" whose body names no call stays the call of that name, as it always was: the registry answers
    /// "Unknown tool" with the list of tools and the model sends it again. Left as text, it became the answer and the
    /// turn ended — what Gemma 4 wrote in 21 bench runs.
    /// </summary>
    [Theory]
    [InlineData("<|tool_call>call:tool_call{foo:<|\"|>x<|\"|>}<tool_call|>", false)]
    [InlineData("<|tool_call>call:tool_call{name:\"run_command\",arguments={\"command\":\"pip install pytest\"}}<tool_call|>", true)]
    public void ABodyThatNamesNoCall_StaysTheToolCallCall(string text, bool unreadable)
    {
        var (calls, _) = InlineToolCallParser.TryParse(text);

        var call = Assert.Single(calls!);
        Assert.Equal("tool_call", call.Function.Name);
        Assert.Equal(unreadable, call.Function.UnparsedArguments is not null);
    }

    /// <summary>⚠ Reference arms: two well-formed calls are still two calls, and a tag in prose with no call in it stays
    /// text.</summary>
    [Fact]
    public void WellFormedCalls_AndProse_AreUnchanged()
    {
        var (two, _) = InlineToolCallParser.TryParse(
            "<tool_call>{\"name\":\"list_files\",\"arguments\":{\"path\":\"a\"}}</tool_call>\n<tool_call>{\"name\":\"list_files\",\"arguments\":{\"path\":\"b\"}}</tool_call>");
        Assert.Equal(2, two!.Count);

        const string prose = "Write the call as <tool_call>…</tool_toolbox> in the reply.";
        var (none, kept) = InlineToolCallParser.TryParse(prose);
        Assert.Null(none);
        Assert.Equal(prose, kept);
    }
}
