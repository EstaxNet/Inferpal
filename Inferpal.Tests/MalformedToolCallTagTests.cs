using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Inferpal.Models;
using Inferpal.Services.Agent;
using Inferpal.Services.Execution;
using Inferpal.Services.Inference;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A <c>&lt;tool_call&gt;</c> block whose JSON does not parse is a call the model meant to make, not its answer.
/// </summary>
/// <remarks>
/// ⚠ Rejected whole, the block became the final answer: the run ended on raw JSON, nothing executed, no notice — and
/// the model never learned its call was malformed. The three bodies below are the battery's, verbatim (Gemma 4 under
/// <c>PromptedTools</c>, which writes its calls as text): two stop one closing bracket short, one carries a stray
/// <c>]</c>. Unfinished — every string closed, brackets left open at the end — is completed, nothing guessed; broken
/// elsewhere with its name readable, it is an unreadable call, refused by the funnel with the cause.
/// </remarks>
public class MalformedToolCallTagTests
{
    // gemma-4-12b-it-qat, agent-bugfix, host 1.6.26: the root object is never closed.
    private const string Gemma12bApplyEdits = """"
        <tool_call>
        {"name": "apply_edits", "arguments": {"edits": [{"path": "/root/battery/runs/gemma-4-12b-it-qat-agent-bugfix/ws/src/Shop/Pricing.cs", "old_content": "        return price - price * percent / 10m;\n    }", "new_content": "        return price - (price * percent / 100m);\n    }"}]}
        </tool_call>
        """";

    // gemma-4-12b-it-qat, agent-bugfix, host 1.6.24: same shape, another tool.
    private const string Gemma12bApplyDiff = """"
        <tool_call>
        {"name": "apply_diff", "arguments": {"path": "/root/battery/runs/gemma-4-12b-it-qat-agent-bugfix/ws/src/Shop/Pricing.cs", "old_content": "        return price - price * percent / 10m;\n    }", "new_content": "        return price - (price * percent / 100m);\n    }"}
        </tool_call>
        """";

    // gemma-4-26b-a4b-it-qat, py-bugfix, host 1.6.24: a stray "]" before the last brace — not an unfinished structure.
    private const string Gemma26bStrayBracket = """"
        <tool_call>
        {"name": "apply_edits", "arguments": {"edits": [{"new_content": "def apply_discount(price: float, percent: int) -> float:\n    \"\"\"Applies a percentage discount to a price.\"\"\n    if percent < 0 or percent > 100:\n        raise ValueError(\"percent must be between 0 and 100\")\n    return price - (price * percent / 100)", "old_content": "def apply_discount(price: float, percent: int) -> float:\n    \"\"\"Applies a percentage discount to a price.\"\"\n    if percent < 0 or percent > 100:\n        raise ValueError(\"percent must be between 0 and 100\")\n    return price - (price * percent / 10)", "path": "shop/pricing.py"}], "occurrence": "all"}]}
        </tool_call>
        """";

    [Fact]
    public void AnUnfinishedCall_IsTheCallTheModelWrote()
    {
        var (calls, cleaned) = InlineToolCallParser.TryParse(Gemma12bApplyEdits);

        var call = Assert.Single(Assert.IsAssignableFrom<List<ToolCallDto>>(calls));
        Assert.Equal("apply_edits", call.Function.Name);
        Assert.Null(call.Function.UnparsedArguments);
        var edit = call.Function.Arguments.GetProperty("edits")[0];
        Assert.Equal("        return price - price * percent / 10m;\n    }", edit.GetProperty("old_content").GetString());
        Assert.Equal("        return price - (price * percent / 100m);\n    }", edit.GetProperty("new_content").GetString());
        Assert.Equal(string.Empty, cleaned);   // never shown to the user as the answer
    }

    [Fact]
    public void AnUnfinishedCall_OfAnotherTool_IsReadToo()
    {
        var (calls, _) = InlineToolCallParser.TryParse(Gemma12bApplyDiff);

        var call = Assert.Single(Assert.IsAssignableFrom<List<ToolCallDto>>(calls));
        Assert.Equal("apply_diff", call.Function.Name);
        Assert.Equal("        return price - (price * percent / 100m);\n    }",
                     call.Function.Arguments.GetProperty("new_content").GetString());
    }

    [Fact]
    public async Task ABrokenCall_WithItsNameReadable_IsRefusedWithItsCause_NotShownAsTheAnswer()
    {
        var (calls, cleaned) = InlineToolCallParser.TryParse(Gemma26bStrayBracket);

        var call = Assert.Single(Assert.IsAssignableFrom<List<ToolCallDto>>(calls));
        Assert.Equal("apply_edits", call.Function.Name);
        Assert.Contains("\"occurrence\": \"all\"}]}", call.Function.UnparsedArguments);
        Assert.DoesNotContain("tool_call", cleaned);

        var result = await AgentOrchestrator.ExecuteToolSafeAsync(
            EmptyToolRegistry.Instance, call.Function, replyCut: false, CancellationToken.None);
        Assert.Contains("not a valid JSON object, so it was NOT executed", result);
    }

    /// <summary>⚠ Reference arms: a tag the parser already read keeps its call, and a block that names no call stays
    /// text — a well-formed object without a name (an answer quoting the format) and a broken one without a name.</summary>
    [Theory]
    [InlineData("<tool_call>\n{\n\"action\": \"\"\n}\n</tool_call>")]
    [InlineData("<tool_call>{\"args\": {\"x\": 1}</tool_call>")]
    [InlineData("<tool_call>{\"command\": \"dotnet --version\"]}</tool_call>")]
    public void ABlockThatNamesNoCall_StaysText(string content)
    {
        var (calls, cleaned) = InlineToolCallParser.TryParse(content);

        Assert.Null(calls);
        Assert.Equal(content, cleaned);
    }

    [Fact]
    public void AWellFormedCall_IsUnchanged()
    {
        var content = "<tool_call>\n{\"name\": \"read_file\", \"arguments\": {\"path\": \"a.cs\"}}\n</tool_call>";

        var (calls, _) = InlineToolCallParser.TryParse(content);

        var call = Assert.Single(Assert.IsAssignableFrom<List<ToolCallDto>>(calls));
        Assert.Null(call.Function.UnparsedArguments);
        Assert.Equal("a.cs", call.Function.Arguments.GetProperty("path").GetString());
    }

    /// <summary>A string still open at the end is not an unfinished structure: completing it would be writing the
    /// model's content for it.</summary>
    [Fact]
    public void AStringLeftOpen_IsNotCompleted()
    {
        var content = "<tool_call>{\"name\": \"write_file\", \"arguments\": {\"path\": \"a.cs\", \"content\": \"class A {}</tool_call>";

        var (calls, _) = InlineToolCallParser.TryParse(content);

        var call = Assert.Single(Assert.IsAssignableFrom<List<ToolCallDto>>(calls));
        Assert.Equal("write_file", call.Function.Name);
        Assert.NotNull(call.Function.UnparsedArguments);
    }

    /// <summary>
    /// ⚠ The other reader of the same parser: every unreadable call carries the empty object as its arguments, so two
    /// DIFFERENT malformed calls of one tool compared as the same call — a "repeat" that stopped the stream.
    /// </summary>
    [Fact]
    public void TwoDifferentUnreadableCalls_AreNotARepeat()
    {
        var reasoning = new StringBuilder()
            .Append("<tool_call>{\"name\": \"apply_edits\", \"arguments\": {\"edits\": [{\"old_content\": \"a\"}], \"x\": 1}]}</tool_call>")
            .Append("<tool_call>{\"name\": \"apply_edits\", \"arguments\": {\"edits\": [{\"old_content\": \"b\"}], \"x\": 1}]}</tool_call>");

        Assert.False(new RepeatedCallDetector().Repeats(reasoning));
    }

    [Fact]
    public void TheSameUnreadableCallTwice_IsARepeat()
    {
        const string call = "<tool_call>{\"name\": \"apply_edits\", \"arguments\": {\"edits\": [{\"old_content\": \"a\"}], \"x\": 1}]}</tool_call>";

        Assert.True(new RepeatedCallDetector().Repeats(new StringBuilder(call + call)));
    }
}
