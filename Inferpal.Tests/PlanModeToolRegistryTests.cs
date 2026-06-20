using System.Text.Json;
using Inferpal.Models;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

public class PlanModeToolRegistryTests
{
    private sealed class FakeRegistry : IToolRegistry
    {
        public List<string> Executed { get; } = [];
        public DiffInfo? Diff { get; set; }

        public IReadOnlyList<ToolDefinition> Definitions =>
        [
            Def("read_file"), Def("write_file"), Def("run_command"), Def("search_codebase"),
            Def("apply_diff"), Def("get_git_status"), Def("my_custom_shell"), Def("run_tests"),
        ];

        public Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct)
        {
            Executed.Add(name);
            return Task.FromResult($"ran:{name}");
        }

        public DiffInfo? ConsumeDiff() => Diff;

        private static ToolDefinition Def(string name) =>
            new("function", new ToolFunction(name, "d", new { }));
    }

    private static JsonElement NoArgs() => JsonSerializer.Deserialize<JsonElement>("{}");

    [Fact]
    public void Definitions_KeepOnlyReadOnlyTools()
    {
        var plan = new PlanModeToolRegistry(new FakeRegistry());

        var names = plan.Definitions.Select(d => d.Function.Name).ToList();

        Assert.Equal(["read_file", "search_codebase", "get_git_status"], names);
    }

    [Fact]
    public void Definitions_ExcludeUnknownToolsByDefault()
    {
        // User shell tools and MCP tools have unknown side effects — whitelist only.
        var plan = new PlanModeToolRegistry(new FakeRegistry());

        Assert.DoesNotContain("my_custom_shell", plan.Definitions.Select(d => d.Function.Name));
    }

    [Fact]
    public void Definitions_ExcludeRunTests()
    {
        // run_tests is read-only for loop detection, but plan mode executes nothing.
        var plan = new PlanModeToolRegistry(new FakeRegistry());

        Assert.DoesNotContain("run_tests", plan.Definitions.Select(d => d.Function.Name));
    }

    [Fact]
    public async Task ExecuteAsync_AllowedTool_PassesThrough()
    {
        var inner = new FakeRegistry();
        var plan  = new PlanModeToolRegistry(inner);

        var result = await plan.ExecuteAsync("read_file", NoArgs(), CancellationToken.None);

        Assert.Equal("ran:read_file", result);
        Assert.Equal(["read_file"], inner.Executed);
    }

    [Fact]
    public async Task ExecuteAsync_BlockedTool_NeverReachesInnerRegistry()
    {
        // Safety net for inline-parsed tool calls that bypass the definitions list.
        var inner = new FakeRegistry();
        var plan  = new PlanModeToolRegistry(inner);

        var result = await plan.ExecuteAsync("write_file", NoArgs(), CancellationToken.None);

        Assert.Empty(inner.Executed);
        Assert.Contains("not available in plan mode", result);
        Assert.Contains("write_file", result);
    }

    [Fact]
    public async Task ExecuteAsync_IsCaseInsensitive()
    {
        var inner = new FakeRegistry();
        var plan  = new PlanModeToolRegistry(inner);

        await plan.ExecuteAsync("READ_FILE", NoArgs(), CancellationToken.None);

        Assert.Equal(["READ_FILE"], inner.Executed);
    }

    [Fact]
    public void ConsumeDiff_DelegatesToInner()
    {
        var diff  = new DiffInfo("old", "new", "f.cs");
        var inner = new FakeRegistry { Diff = diff };
        var plan  = new PlanModeToolRegistry(inner);

        Assert.Same(diff, plan.ConsumeDiff());
    }
}
