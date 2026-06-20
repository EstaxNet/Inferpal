using System.Text.Json;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

public class InlineToolCallParserTests
{
    [Fact]
    public void FlatObjectWithIdAndArgumentsObject_IsRecovered()
    {
        // The exact shape observed from qwen3.6 in agent mode.
        var content = """{"id":"1","name":"fetch_url","arguments":{"url":"https://github.com/continuedev/continue"}}""";

        var (calls, cleaned) = InlineToolCallParser.TryParse(content);

        Assert.NotNull(calls);
        var call = Assert.Single(calls!);
        Assert.Equal("fetch_url", call.Function.Name);
        Assert.Equal("https://github.com/continuedev/continue",
                     call.Function.Arguments.GetProperty("url").GetString());
        Assert.Equal(string.Empty, cleaned); // whole content consumed
    }

    [Fact]
    public void ToolCallTagBlock_IsRecovered()
    {
        var content = "<tool_call>\n{\"name\": \"read_file\", \"arguments\": {\"path\": \"a.cs\"}}\n</tool_call>";

        var (calls, _) = InlineToolCallParser.TryParse(content);

        var call = Assert.Single(calls!);
        Assert.Equal("read_file", call.Function.Name);
        Assert.Equal("a.cs", call.Function.Arguments.GetProperty("path").GetString());
    }

    [Fact]
    public void ToolCallTag_WithSurroundingProse_StripsTagFromCleaned()
    {
        var content = "Sure, let me do that.\n<tool_call>{\"name\":\"list_files\",\"arguments\":{}}</tool_call>";

        var (calls, cleaned) = InlineToolCallParser.TryParse(content);

        Assert.Single(calls!);
        Assert.DoesNotContain("tool_call", cleaned);
        Assert.Contains("Sure", cleaned);
    }

    [Fact]
    public void JsonArray_RecoversMultipleCalls()
    {
        var content = """[{"name":"read_file","arguments":{"path":"a.cs"}},{"name":"read_file","arguments":{"path":"b.cs"}}]""";

        var (calls, _) = InlineToolCallParser.TryParse(content);

        Assert.NotNull(calls);
        Assert.Equal(2, calls!.Count);
        Assert.Equal("b.cs", calls[1].Function.Arguments.GetProperty("path").GetString());
    }

    [Fact]
    public void ParametersAlias_IsAccepted()
    {
        var content = """{"name":"search_codebase","parameters":{"query":"foo"}}""";

        var (calls, _) = InlineToolCallParser.TryParse(content);

        var call = Assert.Single(calls!);
        Assert.Equal("foo", call.Function.Arguments.GetProperty("query").GetString());
    }

    [Fact]
    public void ArgumentsAsJsonString_IsParsed()
    {
        var content = """{"name":"fetch_url","arguments":"{\"url\":\"https://x.test\"}"}""";

        var (calls, _) = InlineToolCallParser.TryParse(content);

        var call = Assert.Single(calls!);
        Assert.Equal("https://x.test", call.Function.Arguments.GetProperty("url").GetString());
    }

    [Fact]
    public void NestedFunctionShape_IsRecovered()
    {
        var content = """{"function":{"name":"get_git_status","arguments":{}}}""";

        var (calls, _) = InlineToolCallParser.TryParse(content);

        var call = Assert.Single(calls!);
        Assert.Equal("get_git_status", call.Function.Name);
    }

    [Fact]
    public void FencedJson_IsRecovered()
    {
        var content = "```json\n{\"name\":\"list_files\",\"arguments\":{}}\n```";

        var (calls, _) = InlineToolCallParser.TryParse(content);

        var call = Assert.Single(calls!);
        Assert.Equal("list_files", call.Function.Name);
    }

    [Fact]
    public void MissingArguments_DefaultsToEmptyObject()
    {
        var content = """{"name":"get_solution_info"}""";

        var (calls, _) = InlineToolCallParser.TryParse(content);

        var call = Assert.Single(calls!);
        Assert.Equal(JsonValueKind.Object, call.Function.Arguments.ValueKind);
    }

    [Fact]
    public void PlainProse_ReturnsNullAndUnchangedContent()
    {
        var content = "My name is Inferpal, your developer assistant.";

        var (calls, cleaned) = InlineToolCallParser.TryParse(content);

        Assert.Null(calls);
        Assert.Equal(content, cleaned);
    }

    [Fact]
    public void JsonWithoutNameField_IsNotAToolCall()
    {
        var content = """{"goal":"something","steps":[1,2,3]}""";

        var (calls, cleaned) = InlineToolCallParser.TryParse(content);

        Assert.Null(calls);
        Assert.Equal(content, cleaned);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyOrWhitespace_ReturnsNull(string? content)
    {
        var (calls, _) = InlineToolCallParser.TryParse(content);
        Assert.Null(calls);
    }

    [Fact]
    public void QwenXmlFunctionShape_IsRecovered()
    {
        // The exact shape qwen3.6 (LM Studio) leaks into the reasoning channel under tool_choice:"required".
        var content =
            "<tool_call>\n<function=read_file>\n<parameter=path>\nrecette.txt\n</parameter>\n</function>\n</tool_call>";

        var (calls, cleaned) = InlineToolCallParser.TryParse(content);

        var call = Assert.Single(calls!);
        Assert.Equal("read_file", call.Function.Name);
        Assert.Equal("recette.txt", call.Function.Arguments.GetProperty("path").GetString());
        Assert.Equal(string.Empty, cleaned);
    }

    [Fact]
    public void QwenXmlFunction_MultipleParameters_AndTypeInference()
    {
        var content =
            "<tool_call><function=search><parameter=query>foo bar</parameter><parameter=top_k>5</parameter></function></tool_call>";

        var (calls, _) = InlineToolCallParser.TryParse(content);

        var call = Assert.Single(calls!);
        Assert.Equal("search", call.Function.Name);
        Assert.Equal("foo bar", call.Function.Arguments.GetProperty("query").GetString());
        Assert.Equal(5, call.Function.Arguments.GetProperty("top_k").GetInt32()); // numeric value kept as JSON number
    }

    [Fact]
    public void QwenXmlFunction_NoParameters_DefaultsToEmptyArgs()
    {
        var content = "<tool_call><function=get_git_status></function></tool_call>";

        var (calls, _) = InlineToolCallParser.TryParse(content);

        var call = Assert.Single(calls!);
        Assert.Equal("get_git_status", call.Function.Name);
        Assert.Equal(JsonValueKind.Object, call.Function.Arguments.ValueKind);
    }
}
