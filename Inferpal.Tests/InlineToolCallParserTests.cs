using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Inferpal.Models;
using Inferpal.Services;
using Inferpal.Services.Agent;
using Inferpal.Services.Execution;
using Inferpal.Services.Tools;
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
    public void BareJson_WithAnUnknownName_IsAnAnswer_NotAToolCall()
    {
        // Ask the model for "a JSON of a person" and it answers {"name":"Alice","age":30}:
        // promoted to a call of the tool "Alice", the legitimate answer was DESTROYED
        // (pre-1.6.0 architecture review, §3.2). With the registry gate, only real tool names promote.
        var content = """{"name":"Alice","age":30}""";
        var isKnown = (string n) => n is "read_file" or "write_file";

        var (calls, cleaned) = InlineToolCallParser.TryParse(content, isKnown);

        Assert.Null(calls);
        Assert.Equal(content, cleaned);   // the answer survives untouched
    }

    [Fact]
    public void BareJson_WithARealToolName_StillPromotes_UnderTheGate()
    {
        var content = """{"name":"read_file","arguments":{"path":"a.cs"}}""";
        var isKnown = (string n) => n is "read_file" or "write_file";

        var (calls, cleaned) = InlineToolCallParser.TryParse(content, isKnown);

        var call = Assert.Single(calls!);
        Assert.Equal("read_file", call.Function.Name);
        Assert.Equal(string.Empty, cleaned);
    }

    [Fact]
    public void ExplicitToolCallTag_StaysUngated_SoATypoStillReachesTheRegistryFeedback()
    {
        // An unknown name inside an explicit <tool_call> tag is an unambiguous call attempt:
        // it must flow to the registry's "Unknown tool" feedback so the model can correct it.
        var content = "<tool_call>{\"name\":\"reed_file\",\"arguments\":{}}</tool_call>";
        var isKnown = (string n) => n == "read_file";

        var (calls, _) = InlineToolCallParser.TryParse(content, isKnown);

        var call = Assert.Single(calls!);
        Assert.Equal("reed_file", call.Function.Name);
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

    // ── UNREADABLE arguments are not ABSENT arguments ─────────────────────────

    /// <summary>
    /// ⚠ The repository already writes the rule, in <c>OpenAiCompatibleClient.ParseArguments</c>: "anything
    /// else — typically a turn cut off mid-call — is kept in UnparsedArguments and must not run:
    /// defaulting it to {} turned <c>run_tests {"filter":"Foo…</c> into the whole test suite ». Le
    /// structured path applies it; the INLINE path fell back to <c>{}</c> — and a call with no
    /// arguments is not the same call.
    /// </summary>
    [Theory]
    // The model writes the filter as raw text rather than a JSON object — the commonest shape from
    // small models, and the dearest: `run_tests` with no filter is the WHOLE suite.
    [InlineData("""{"name":"run_tests","arguments":"MyFilter"}""", "MyFilter")]
    // A turn cut in the middle of its double-encoded arguments.
    [InlineData("""{"name":"run_tests","arguments":"{\"filter\":\"Foo"}""", "filter")]
    // An array where the schema wants an object.
    [InlineData("""{"name":"list_files","arguments":["src"]}""", "src")]
    public void ArgumentsThatDoNotParse_AreKeptRaw_NotSilentlyEmptied(string content, string expectedInRaw)
    {
        var (calls, _) = InlineToolCallParser.TryParse(content);

        var call = Assert.Single(Assert.IsAssignableFrom<System.Collections.Generic.List<ToolCallDto>>(calls));
        Assert.NotNull(call.Function.UnparsedArguments);
        Assert.Contains(expectedInRaw, call.Function.UnparsedArguments!);
    }

    /// <summary>
    /// The other half, and the one that protects the user: the guard that REFUSES to execute already
    /// existed — it was only waiting for this path to mark its calls.
    /// </summary>
    [Fact]
    public async Task AnInlineCallWithUnreadableArguments_IsRefusedInsteadOfRunWithNoArguments()
    {
        var (calls, _) = InlineToolCallParser.TryParse("""{"name":"run_tests","arguments":"MyFilter"}""");
        var call = Assert.Single(Assert.IsAssignableFrom<System.Collections.Generic.List<ToolCallDto>>(calls));

        var result = await AgentOrchestrator.ExecuteToolSafeAsync(
            EmptyToolRegistry.Instance, call.Function, CancellationToken.None);

        Assert.Contains("not a valid JSON object", result);
        Assert.Contains("MyFilter", result);     // the cause is returned to the model, which can resend
    }

    /// <summary>
    /// ⚠ Reference arm, without which the fix would break the NORMAL case: a call that legitimately
    /// has no arguments (<c>get_git_status</c>, <c>get_solution_info</c>) keeps its empty object and
    /// executes. "Absent" and "unreadable" are two states, not one.
    /// </summary>
    [Fact]
    public async Task ACallWithNoArgumentsAtAll_StillRuns()
    {
        var (calls, _) = InlineToolCallParser.TryParse("""{"name":"get_git_status"}""");
        var call = Assert.Single(Assert.IsAssignableFrom<System.Collections.Generic.List<ToolCallDto>>(calls));

        Assert.Null(call.Function.UnparsedArguments);

        var result = await AgentOrchestrator.ExecuteToolSafeAsync(
            EmptyToolRegistry.Instance, call.Function, CancellationToken.None);

        Assert.DoesNotContain("not a valid JSON object", result);
    }

    // ── The same question, but on what comes off the WIRE ─────────────────────

    /// <summary>
    /// ⚠ The funnel, not the path: <c>arguments</c> is deserialized as it comes into a
    /// <see cref="JsonElement"/>, so a payload where it is not an object travels through the whole
    /// product with nobody judging it. And <c>ToolArgs</c> — by contract, so that it never throws on
    /// what the model writes — then returns the default value of EVERY argument: <c>run_tests</c>'s
    /// filter disappears, and the whole suite goes.
    /// </summary>
    [Theory]
    [InlineData(""""{"done":true,"message":{"role":"assistant","tool_calls":[{"function":{"name":"run_tests","arguments":"MyFilter"}}]}}"""")]
    [InlineData(""""{"done":true,"message":{"role":"assistant","tool_calls":[{"function":{"name":"run_tests","arguments":["MyFilter"]}}]}}"""")]
    public async Task WireArgumentsThatAreNotAnObject_AreRefused_NotRunWithDefaults(string wire)
    {
        var call = JsonSerializer.Deserialize<ChatResponse>(wire)!.Message!.ToolCalls![0].Function;

        // The witness of what it costs: read by a tool, that argument does not exist.
        Assert.Null(call.Arguments.Str("filter"));

        var result = await AgentOrchestrator.ExecuteToolSafeAsync(
            EmptyToolRegistry.Instance, call, CancellationToken.None);

        Assert.Contains("not a valid JSON object", result);
        Assert.Contains("MyFilter", result);
    }

    /// <summary>
    /// ⚠ Reference arm: the three shapes that mean "no arguments" stay executable — the missing
    /// property (<see cref="JsonValueKind.Undefined"/>), <c>null</c>, and the empty object. That is
    /// <c>ParseArguments</c>'s contract, and its readers must not
    /// diverger.
    /// </summary>
    [Theory]
    [InlineData(""""{"done":true,"message":{"role":"assistant","tool_calls":[{"function":{"name":"get_git_status"}}]}}"""")]
    [InlineData(""""{"done":true,"message":{"role":"assistant","tool_calls":[{"function":{"name":"get_git_status","arguments":null}}]}}"""")]
    [InlineData(""""{"done":true,"message":{"role":"assistant","tool_calls":[{"function":{"name":"get_git_status","arguments":{}}}]}}"""")]
    public async Task WireCallsWithoutArguments_StillRun(string wire)
    {
        var call = JsonSerializer.Deserialize<ChatResponse>(wire)!.Message!.ToolCalls![0].Function;

        var result = await AgentOrchestrator.ExecuteToolSafeAsync(
            EmptyToolRegistry.Instance, call, CancellationToken.None);

        Assert.DoesNotContain("not a valid JSON object", result);
    }
}
