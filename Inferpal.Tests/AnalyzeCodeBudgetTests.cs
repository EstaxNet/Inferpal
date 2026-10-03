using System.IO;
using System.Text.Json;
using Inferpal.Services.Agent;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>analyze_code</c> answers in ONE tool result, and the loop cuts a longer one in its middle. Measured on this
/// repository: <c>impact</c> on the configuration class, 17 086 characters — Layer 2 and the entry points vanished,
/// the tests were cut to their last lines; <c>callgraph</c> "both" on a view-model file, 11 819 — the end of the
/// callers and the start of the callees. Each list is budgeted, its count stays complete, and what it left is said.
/// </summary>
public sealed class AnalyzeCodeBudgetTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"analyze-{Guid.NewGuid():N}");

    public AnalyzeCodeBudgetTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private async Task<string> RunAsync(object args)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(args));
        return await new AnalyzeCodeTool(() => _root).ExecuteAsync(doc.RootElement, CancellationToken.None);
    }

    [Fact]
    public async Task TheImpactOfAWidelyUsedFile_ReachesTheModelWhole_EverySectionIncluded()
    {
        var target = Path.Combine(_root, "Core", "Settings.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, "namespace App.Core;\npublic class Settings { public int Value { get; set; } }\n");
        for (var i = 0; i < 120; i++)
        {
            var dir = Directory.CreateDirectory(Path.Combine(_root, "Features", $"FeatureNumber{i:D3}")).FullName;
            File.WriteAllText(Path.Combine(dir, $"FeatureHandler{i:D3}.cs"),
                $"using App.Core;\nnamespace App.Features;\npublic class FeatureHandler{i:D3} {{ Settings s = new(); }}\n");
        }
        var tests = Directory.CreateDirectory(Path.Combine(_root, "Tests")).FullName;
        for (var i = 0; i < 60; i++)
            File.WriteAllText(Path.Combine(tests, $"SettingsBehaviourNumber{i:D3}Tests.cs"),
                $"using App.Core;\nnamespace App.Tests;\npublic class SettingsBehaviourNumber{i:D3}Tests {{ Settings s = new(); }}\n");

        var report = await RunAsync(new { mode = "impact", path = target });

        Assert.Contains("## Layer 1 · Direct dependants  (180)", report);                               // witness
        Assert.Contains("## Layer 2", report);
        Assert.Contains("## Entry points", report);
        Assert.Contains("## Tests  (60)", report);
        Assert.Contains("**Risk:", report);
        Assert.Contains("more not listed (the count above is complete)", report);
        Assert.Equal(report, AgentOrchestrator.CapForContext(report));
    }

    [Fact]
    public async Task TheCallGraphOfALongFile_ReachesTheModelWhole_AndNamesTheMethodsItLeftOut()
    {
        var body = string.Concat(Enumerable.Range(0, 60).Select(i =>
            $"    public void OperationNumber{i:D2}() {{ " +
            string.Concat(Enumerable.Range(1, 6).Select(k => $"OperationNumber{(i + k) % 60:D2}(); ")) + "}\n"));
        var file = Path.Combine(_root, "Big.cs");
        File.WriteAllText(file, $"namespace App;\npublic class Big\n{{\n{body}}}\n");

        var report = await RunAsync(new { mode = "callgraph", path = file, direction = "both" });

        Assert.Contains("### Callers", report);                                                            // witness
        Assert.Contains("### Callees", report);
        Assert.Matches(@"\(\+\d+ method\(s\) not shown here: OperationNumber\d\d", report);
        Assert.Contains("symbol=<method>", report);
        Assert.Equal(report, AgentOrchestrator.CapForContext(report));
    }

    [Fact]
    public async Task ASmallFile_IsReportedWhole_WithoutANote()
    {
        var file = Path.Combine(_root, "Small.cs");
        File.WriteAllText(file, "namespace App;\npublic class Small\n{\n"
                              + "    public void OperationNumber00() { OperationNumber01(); }\n"
                              + "    public void OperationNumber01() { OperationNumber00(); }\n}\n");

        var report = await RunAsync(new { mode = "callgraph", path = file, direction = "both" });

        Assert.Contains("**OperationNumber01**", report);
        Assert.DoesNotContain("not shown here", report);
        Assert.DoesNotContain("more not listed", report);
    }
}
