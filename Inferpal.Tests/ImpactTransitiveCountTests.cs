using System.IO;
using System.Text.Json;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>analyze_code</c> impact: the Layer 2 heading counts every transitive dependant it found, not the first sixty.
/// </summary>
/// <remarks>
/// The search stopped at the number of entries the report lists, so a file with seventy transitive dependants read
/// "Layer 2 (60)" — and the tests, entry points and risk verdict built from that list were undercounted, under a
/// comment promising the headings stay complete. The cap now bounds the LIST, which says what it leaves out.
/// </remarks>
public sealed class ImpactTransitiveCountTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"impact2-{Guid.NewGuid():N}");

    public ImpactTransitiveCountTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public async Task SeventyTransitiveDependants_AreCountedSeventy()
    {
        var target = Path.Combine(_root, "Core.cs");
        File.WriteAllText(target, "namespace App;\npublic class Core\n{\n    public int Value() => 1;\n}\n");
        File.WriteAllText(Path.Combine(_root, "Service.cs"),
            "namespace App;\npublic class Service\n{\n    public int Run() => new Core().Value();\n}\n");
        for (var i = 0; i < 70; i++)
            File.WriteAllText(Path.Combine(_root, $"Feature{i:D2}.cs"),
                $"namespace App;\npublic class Feature{i:D2}\n{{\n    public int Go() => new Service().Run();\n}}\n");

        using var args = JsonDocument.Parse(JsonSerializer.Serialize(new { mode = "impact", path = target }));
        var report = await new AnalyzeCodeTool(() => _root).ExecuteAsync(args.RootElement, CancellationToken.None);

        Assert.Contains("Service.cs", report, StringComparison.Ordinal);                         // witness: layer 1 found
        Assert.Contains("## Layer 2 · Transitive dependants  (70)", report, StringComparison.Ordinal);
    }
}
