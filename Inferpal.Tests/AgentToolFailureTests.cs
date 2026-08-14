using System.IO;
using System.Text.Json;
using Inferpal.Models;
using Inferpal.Services.Agent;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

// A malformed tool call must not kill the turn. Tools validate their arguments by THROWING
// (PathSanitizer raises on a missing path), and a small local model omitting a required argument
// is routine — before this guard it escaped the agent loop and took the whole run with it.
public class AgentToolFailureTests
{
    private sealed class ThrowingRegistry(Exception toThrow) : IToolRegistry
    {
        public IReadOnlyList<ToolDefinition> Definitions => [];
        public DiffInfo? ConsumeDiff() => null;
        public Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct) =>
            throw toThrow;
    }

    private static JsonElement NoArgs => JsonDocument.Parse("{}").RootElement;

    [Fact]
    public async Task ThrowingTool_BecomesAnErrorTheModelCanRead()
    {
        var registry = new ThrowingRegistry(new ArgumentException("path manquant"));

        var result = await AgentOrchestrator.ExecuteToolSafeAsync(
            registry, "search_in_files", NoArgs, CancellationToken.None);

        Assert.Contains("search_in_files", result);
        Assert.Contains("path manquant", result);
        // It must read as an instruction to retry, not as a crash dump.
        Assert.Contains("try again", result, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(NullReferenceException))]
    [InlineData(typeof(IOException))]
    public async Task AnyToolFailure_IsContained(Type exceptionType)
    {
        var registry = new ThrowingRegistry((Exception)Activator.CreateInstance(exceptionType)!);

        var result = await AgentOrchestrator.ExecuteToolSafeAsync(
            registry, "read_file", NoArgs, CancellationToken.None);

        Assert.StartsWith("Error:", result);
    }

    [Fact]
    public async Task Cancellation_StillPropagates()
    {
        // The contract is "never throws EXCEPT on cancellation" — swallowing it here would leave
        // the loop running after the user pressed stop.
        var registry = new ThrowingRegistry(new OperationCanceledException());

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            AgentOrchestrator.ExecuteToolSafeAsync(registry, "read_file", NoArgs, CancellationToken.None));
    }
}
