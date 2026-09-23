using System.IO;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Services;
using Inferpal.Services.Docs;
using Inferpal.Services.Execution;
using Inferpal.Services.Lsp;
using Inferpal.Services.Mcp;
using Inferpal.Services.Rag;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ The project map is cached for two minutes, and only <c>refresh: true</c> dropped the cache. An
/// agent that creates a file and then looks at the map — the ordinary way to check its own work —
/// read the map from before, with nothing saying it was one: the file it had just written was not
/// in the project. <c>@tree</c> reads the same cache.
/// </summary>
[Collection(SignalCollection.Name)]
public class ProjectMapStalenessTests : IDisposable
{
    private readonly SignalScratchDir _scratch = new();
    private readonly string _root = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"inferpal-mapstale-{Guid.NewGuid():N}")).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        _scratch.Dispose();
    }

    private static JsonElement Args(object o) => JsonSerializer.SerializeToElement(o);

    private ToolRegistry Registry()
    {
        var config = new InferpalConfig();
        var client = new FakeInferenceProvider();
        var editor = new NullEditorSurface();
        var approval = new NoopApproval();
        var index  = new ProjectIndexService(client, config, new LspSemanticProvider());
        index.SetRoot(_root);
        return new ToolRegistry(editor, approval, config, index, client,
                                new ProjectMapService(editor, index), new McpToolService(config, approval),
                                new DocsIndexService(client, config), new OpenDocumentOverlay(), new NullDebugSession());
    }

    [Fact]
    public async Task AFileTheAgentWrites_IsInTheNextMap()
    {
        File.WriteAllText(Path.Combine(_root, "Probe.cs"), "namespace MapProbe;\npublic class Probe { }\n");
        var registry = Registry();

        var before = await registry.ExecuteAsync("generate_project_map", Args(new { }), CancellationToken.None);
        Assert.Contains("Scanned: 1 source files", before);   // witness: the map reads the workspace

        var wrote = await registry.ExecuteAsync("write_file",
            Args(new { path = "Second.cs", content = "namespace MapProbe;\npublic class Second { }\n" }), CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(_root, "Second.cs")), wrote);

        var after = await registry.ExecuteAsync("generate_project_map", Args(new { }), CancellationToken.None);
        Assert.Contains("Scanned: 2 source files", after);
    }

    [Fact]
    public async Task AReadOnlyCall_KeepsTheCachedMap()
    {
        // Reference arm: the cache stays a cache — reading a file changes nothing on disk.
        File.WriteAllText(Path.Combine(_root, "Probe.cs"), "namespace MapProbe;\npublic class Probe { }\n");
        var registry = Registry();

        await registry.ExecuteAsync("generate_project_map", Args(new { }), CancellationToken.None);
        File.WriteAllText(Path.Combine(_root, "Outside.cs"), "namespace MapProbe;\npublic class Outside { }\n");   // not through a tool
        await registry.ExecuteAsync("read_file", Args(new { path = "Probe.cs" }), CancellationToken.None);

        var again = await registry.ExecuteAsync("generate_project_map", Args(new { }), CancellationToken.None);
        Assert.Contains("Scanned: 1 source files", again);
    }
}
