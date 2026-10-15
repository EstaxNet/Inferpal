using System.Text.Json;
using Inferpal.Models;
using Inferpal.Services.Agent;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A call written in the Qwen XML or the ATEM shape whose parameters cannot be read is refused unexecuted — never run
/// with no arguments.
/// </summary>
/// <remarks>
/// The parameter pattern read the elements it could, and nothing said what it could not: a <c>&lt;parameter=filter&gt;</c>
/// left unclosed made <c>run_tests {}</c> — the whole suite in place of the one test asked for — and a <c>write_file</c>
/// lost its content the same way.
/// </remarks>
public sealed class UnclosedTextParameterTests
{
    private sealed class Recording : IToolRegistry
    {
        public List<(string Name, string Args)> Calls { get; } = [];
        public IReadOnlyList<ToolDefinition> Definitions { get; } =
            [new("function", new ToolFunction("run_tests", "runs the tests", new { }))];
        public DiffInfo? ConsumeDiff() => null;
        public Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct)
        {
            Calls.Add((name, args.GetRawText()));
            return Task.FromResult("Passed: 1");
        }
    }

    private static ToolCallFunction Parsed(string content)
    {
        var (calls, _) = InlineToolCallParser.TryParse(content, n => n == "run_tests");
        Assert.NotNull(calls);
        return Assert.Single(calls!).Function;
    }

    [Theory]
    [InlineData("<tool_call>\n<function=run_tests>\n<parameter=filter>\nCartTests\n</function>\n</tool_call>")]
    [InlineData("<atem:function_calls><atem:invoke name=\"run_tests\"><atem:parameter name=\"filter\">CartTests</atem:invoke></atem:function_calls>")]
    public async Task AnUnclosedParameter_IsRefused_NotRunWithoutArguments(string content)
    {
        var call = Parsed(content);
        Assert.Equal("run_tests", call.Name);
        Assert.NotNull(call.UnparsedArguments);

        var tools  = new Recording();
        var answer = await AgentOrchestrator.ExecuteToolSafeAsync(tools, call, replyCut: false, CancellationToken.None);

        Assert.Empty(tools.Calls);
        Assert.Contains("NOT executed", answer);
        Assert.Contains("CartTests", answer);              // the model is shown what it wrote
    }

    [Theory]
    [InlineData("<tool_call>\n<function=run_tests>\n<parameter=filter>\nCartTests\n</parameter>\n</function>\n</tool_call>")]
    [InlineData("<atem:function_calls><atem:invoke name=\"run_tests\"><atem:parameter name=\"filter\">CartTests</atem:parameter></atem:invoke></atem:function_calls>")]
    [InlineData("<tool_call>\n<function=run_tests>\n</function>\n</tool_call>")]
    public async Task AWellWrittenCall_RunsWithItsArguments(string content)
    {
        // Reference arm: closed parameters, and a call with none, run.
        var call = Parsed(content);
        Assert.Null(call.UnparsedArguments);

        var tools = new Recording();
        await AgentOrchestrator.ExecuteToolSafeAsync(tools, call, replyCut: false, CancellationToken.None);

        var (name, args) = Assert.Single(tools.Calls);
        Assert.Equal("run_tests", name);
        Assert.Equal(content.Contains("CartTests") ? """{"filter":"CartTests"}""" : "{}", args);
    }
}
