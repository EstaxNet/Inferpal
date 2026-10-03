using System.IO;
using Inferpal.Config;
using Inferpal.Services;
using Inferpal.Services.Agent;
using Inferpal.Services.Lsp;
using Inferpal.Services.Rag;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The project map reaches the model as ONE tool result. On this repository it was 50 253 characters — 186 namespaces
/// and 131 dependency lines — and the loop keeps 8 000, from its two ends: the model read the first namespaces and the
/// hotspots, and took the map for the project. The two long sections keep their largest namespaces within a budget
/// and say what they left out.
/// </summary>
[Collection(SignalCollection.Name)]
public sealed class ProjectMapBudgetTests : IDisposable
{
    private readonly SignalScratchDir _scratch = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"inferpal-mapbudget-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        _scratch.Dispose();
    }

    /// <summary><paramref name="count"/> namespaces, each importing the six before it.</summary>
    private async Task<string> MapOfAsync(int count)
    {
        for (var i = 0; i < count; i++)
        {
            var usings = string.Concat(Enumerable.Range(Math.Max(0, i - 6), Math.Min(i, 6))
                                                 .Select(j => $"using Contoso.Enterprise.Module{j:D3}.Services;\n"));
            var dir = Directory.CreateDirectory(Path.Combine(_root, $"Module{i:D3}")).FullName;
            for (var f = 0; f <= i % 3; f++)
                File.WriteAllText(Path.Combine(dir, $"Service{f}.cs"),
                    $"{usings}namespace Contoso.Enterprise.Module{i:D3}.Services;\npublic class Service{i}x{f} {{ }}\n");
        }
        var index = new ProjectIndexService(new FakeInferenceProvider(), new InferpalConfig(), new LspSemanticProvider());
        index.SetRoot(_root);
        return await new ProjectMapService(new NullEditorSurface(), index).GenerateMapAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AMapOfManyNamespaces_ReachesTheModelWhole_AndSaysWhatItLeftOut()
    {
        var map = await MapOfAsync(150);

        Assert.Contains("📦 NAMESPACES  (150 total)", map);                                              // witness
        Assert.Contains("🎯 HOTSPOTS", map);
        Assert.Matches(@"… \+\d+ smaller namespace\(s\) not listed \(\d+ file\(s\)\)", map);
        Assert.Equal(map, AgentOrchestrator.CapForContext(map));
    }

    [Fact]
    public async Task ASmallMap_ListsEveryNamespace_WithoutANote()
    {
        var map = await MapOfAsync(4);

        for (var i = 0; i < 4; i++)
            Assert.Contains($"Contoso.Enterprise.Module{i:D3}.Services", map);
        Assert.DoesNotContain("not listed", map);
    }
}
