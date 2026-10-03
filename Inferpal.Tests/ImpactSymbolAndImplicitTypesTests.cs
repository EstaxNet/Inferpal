using System.IO;
using System.Text.Json;
using Inferpal.Localization;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Two answers of <c>analyze_code</c> in <c>impact</c> mode that left the model nowhere to go. A type declared without an
/// access modifier — <c>class Program</c>, <c>static class Extensions</c>, the second file of a <c>partial class</c>,
/// internal by default — was not seen, and the whole report was "no public API". And a <c>symbol</c> the file does not
/// declare was refused without naming what it does declare, while the name a model reaches for first is a METHOD's,
/// which <c>callgraph</c> takes.
/// </summary>
[Collection(CultureSerialCollection.Name)]   // switches the interface language
public sealed class ImpactSymbolAndImplicitTypesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"impactsym-{Guid.NewGuid():N}");

    public ImpactSymbolAndImplicitTypesTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private async Task<string> ImpactAsync(string file, string? symbol = null)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(
            symbol is null ? (object)new { mode = "impact", path = file } : new { mode = "impact", path = file, symbol }));
        return await new AnalyzeCodeTool(() => _root).ExecuteAsync(doc.RootElement, CancellationToken.None);
    }

    [Fact]
    public async Task ATypeWithoutAnAccessModifier_HasItsImpactReported()
    {
        var target = Path.Combine(_root, "Extensions.cs");
        File.WriteAllText(target, "namespace App;\nstatic class StringExtensions\n{\n    public static string Shout(this string s) => s.ToUpper();\n}\n");
        File.WriteAllText(Path.Combine(_root, "Caller.cs"),
            "namespace App;\nclass Caller { string Run() => StringExtensions.Shout(\"hi\"); }\n");

        var report = await ImpactAsync(target);

        Assert.DoesNotContain(Strings.ImpactNoPublicApi("Extensions.cs"), report);
        Assert.Contains("StringExtensions", report);
        Assert.Contains("Caller.cs", report);
    }

    [Fact]
    public async Task AMethodNamedAsTheSymbol_IsRefusedNamingTheTypes_AndTheModeThatTakesIt_InEnglish()
    {
        var target = Path.Combine(_root, "Calculator.cs");
        File.WriteAllText(target, "namespace App;\npublic class Calculator\n{\n    public int Compute(int x) => x * 2;\n}\n");

        var previous = Strings.OverrideCulture?.Name;
        string refusal;
        try
        {
            Strings.ApplyLanguage("fr");                          // the interface is French; the model's correction is not
            refusal = await ImpactAsync(target, "Compute");
        }
        finally { Strings.ApplyLanguage(previous); }

        Assert.Contains("'Compute' is not a type declared in Calculator.cs — it declares: Calculator", refusal);
        Assert.Contains("use mode='callgraph' with symbol='Compute'", refusal);
    }

    [Fact]
    public async Task ATypeNamedAsTheSymbol_IsStillAnalysed()
    {
        var target = Path.Combine(_root, "Calculator.cs");
        File.WriteAllText(target, "namespace App;\npublic class Calculator\n{\n    public int Compute(int x) => x * 2;\n}\n");

        var report = await ImpactAsync(target, "Calculator");

        Assert.Contains("## Layer 1", report);
        Assert.DoesNotContain("is not a type declared", report);
    }
}
