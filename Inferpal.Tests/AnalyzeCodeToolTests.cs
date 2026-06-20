using System.IO;
using System.Text.Json;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

// analyze_code is a thin façade that forwards args to one of three analyzers by 'mode'.
// These tests pin the routing: an unknown mode is rejected, and each valid mode reaches a
// distinct analyzer (identified by its characteristic output header).
public class AnalyzeCodeToolTests
{
    private static JsonElement Args(object o) =>
        JsonDocument.Parse(JsonSerializer.Serialize(o)).RootElement;

    private static AnalyzeCodeTool ToolFor(string root) => new(() => root);

    [Theory]
    [InlineData("frobnicate")]
    [InlineData("")]
    public async Task UnknownMode_IsRejected(string mode)
    {
        var tool   = ToolFor(Path.GetTempPath());
        var result = await tool.ExecuteAsync(Args(new { mode }), CancellationToken.None);
        Assert.Contains("Unknown mode", result);
    }

    [Fact]
    public async Task NexusMode_RoutesToCrossLanguageAnalyzer()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var tool   = ToolFor(dir);
            var result = await tool.ExecuteAsync(Args(new { mode = "nexus", root = dir }), CancellationToken.None);
            // Characteristic of the nexus analyzer's report header.
            Assert.Contains("cross-language", result, System.StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task CallgraphMode_RoutesToCallGraphAnalyzer()
    {
        var dir  = Directory.CreateTempSubdirectory().FullName;
        var file = Path.Combine(dir, "Sample.cs");
        await File.WriteAllTextAsync(file,
            "public class Sample\n{\n    public void DoWork()\n    {\n        Helper();\n    }\n\n    public void Helper()\n    {\n    }\n}\n");
        try
        {
            var tool   = ToolFor(dir);
            var result = await tool.ExecuteAsync(Args(new { mode = "callgraph", path = file }), CancellationToken.None);
            // "### Callees" is a hardcoded (culture-independent) section header unique to the
            // call-graph analyzer; the parsed method names prove the file was actually analysed.
            Assert.Contains("Callees", result);
            Assert.Contains("DoWork", result);
            Assert.Contains("Helper", result);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
