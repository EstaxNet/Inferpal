using Inferpal.Services.Agent;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Gemma 4 under <c>PromptedTools</c> writes the JSON call the system prompt asks for inside its OWN tags:
/// <c>&lt;|tool_call&gt;call:{"name":…,"arguments":{…}}&lt;tool_call|&gt;</c>.
/// </summary>
/// <remarks>
/// ⚠ Neither reader took it: Gemma's needs a name after <c>call:</c>, the JSON one needs <c>&lt;tool_call&gt;</c>. The raw
/// call became the answer and nothing ran. The three texts below are answers Gemma 4 12B gave on the bench, verbatim.
/// </remarks>
public class GemmaJsonBodyCallTests
{
    private static readonly string[] Offered = ["list_files", "search_in_files", "read_file"];

    [Theory]
    [InlineData("<|tool_call>call:{\"name\":\"list_files\",\"arguments\":{\"path\":\"/root/battery/runs/gemma-4-12b-it-qat-chat-edit/ws\"}}<tool_call|>",
                "list_files", "path", "/root/battery/runs/gemma-4-12b-it-qat-chat-edit/ws")]
    [InlineData("<|tool_call>call:{\"name\": \"search_in_files\", \"arguments\": {\"path\": \"/root/battery/runs/gemma-4-12b-it-qat-chat-question/ws/\", \"pattern\": \"ApplyDiscount\"}}<tool_call|>",
                "search_in_files", "pattern", "ApplyDiscount")]
    [InlineData("<|tool_call>call:{\"name\":\"search_in_files\",\"arguments\":{\"path\":\"/root/battery/runs/gemma-4-12b-it-qat-chat-question/ws\",\"pattern\":\"ApplyDiscount\",\"file_pattern\":\"*.cs\"}}<tool_call|>",
                "search_in_files", "file_pattern", "*.cs")]
    public void TheAnswersGemmaGave_AreTheCallsItMeant(string answer, string tool, string key, string value)
    {
        var (calls, cleaned) = InlineToolCallParser.FromContent(answer, Offered);

        var call = Assert.Single(calls!);
        Assert.Equal(tool, call.Function.Name);
        Assert.Null(call.Function.UnparsedArguments);
        Assert.Equal(value, call.Function.Arguments.GetProperty(key).GetString());
        Assert.Equal(string.Empty, cleaned);   // nothing of the call is left to show as the answer
    }

    /// <summary>One closing bracket short, it is completed — as the same body is between <c>&lt;tool_call&gt;</c> tags.</summary>
    [Fact]
    public void AnUnfinishedBody_IsCompleted()
    {
        var (calls, _) = InlineToolCallParser.TryParse("<|tool_call>call:{\"name\":\"read_file\",\"arguments\":{\"path\":\"a.cs\"}<tool_call|>");

        var call = Assert.Single(calls!);
        Assert.Equal("a.cs", call.Function.Arguments.GetProperty("path").GetString());
    }

    /// <summary>⚠ Reference arms: a body that names no call stays text, and Gemma's named shape is read as before.</summary>
    [Fact]
    public void ABodyWithNoName_StaysText_AndTheNamedShapeIsUnchanged()
    {
        const string noName = "<|tool_call>call:{\"path\":\"a.cs\"}<tool_call|>";
        var (none, kept) = InlineToolCallParser.TryParse(noName);
        Assert.Null(none);
        Assert.Equal(noName, kept);

        var (named, _) = InlineToolCallParser.TryParse("<|tool_call>call:read_file{path:<|\"|>a.cs<|\"|>}<tool_call|>");
        var call = Assert.Single(named!);
        Assert.Equal("read_file", call.Function.Name);
        Assert.Equal("a.cs", call.Function.Arguments.GetProperty("path").GetString());
    }
}
