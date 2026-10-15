using System.IO;
using System.Text.Json;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The exact references of an <c>impact</c> report are those of the symbol declared in the file analysed — not of the
/// same name declared in another file that shares its file name.
/// </summary>
/// <remarks>
/// The compiler-resolved section was narrowed to the declaring file by its NAME alone (<c>Cart.cs</c>): with a
/// <c>Cart.cs</c> in two folders, the first in path order answered — "declared at" the other file, and its uses listed
/// as the uses of the type asked about, under "resolved by the C# compiler"; and the first one was called ambiguous
/// because of the second. The narrowing now takes the full path.
/// </remarks>
public sealed class ImpactHomonymousFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"impact-homonym-{Guid.NewGuid():N}");

    public ImpactHomonymousFileTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Alpha"));
        Directory.CreateDirectory(Path.Combine(_root, "Beta"));
        File.WriteAllText(Path.Combine(_root, "Alpha", "Cart.cs"),
            "namespace Alpha;\npublic class Cart\n{\n    public int Total() => 1;\n}\n");
        File.WriteAllText(Path.Combine(_root, "Alpha", "UseAlpha.cs"),
            "namespace Alpha;\npublic class UseAlpha\n{\n    public int Run() => new Cart().Total();\n}\n");
        File.WriteAllText(Path.Combine(_root, "Beta", "Cart.cs"),
            "namespace Beta;\npublic class Cart\n{\n    public int Total() => 2;\n}\n");
        File.WriteAllText(Path.Combine(_root, "Beta", "UseBeta.cs"),
            "namespace Beta;\npublic class UseBeta\n{\n    public int Run() => new Cart().Total();\n}\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private async Task<string> ImpactAsync(string file, string symbol)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { mode = "impact", path = file, symbol }));
        return (await new AnalyzeCodeTool(() => _root).ExecuteAsync(doc.RootElement, CancellationToken.None)).Replace('\\', '/');
    }

    private static string Exact(string report)
    {
        var start = report.IndexOf("## Exact references", StringComparison.Ordinal);
        Assert.True(start >= 0, "witness: the report has its compiler-resolved section\n" + report);
        return report[start..];
    }

    [Fact]
    public async Task TheSecondOfTwoHomonymousFiles_IsAnsweredAboutItself()
    {
        var exact = Exact(await ImpactAsync(Path.Combine(_root, "Beta", "Cart.cs"), "Cart"));

        Assert.Contains("declared at Beta/Cart.cs", exact);
        Assert.Contains("Beta/UseBeta.cs", exact);
        Assert.DoesNotContain("Alpha/", exact);
    }

    [Fact]
    public async Task TheFirstOfTwoHomonymousFiles_IsNotCalledAmbiguous()
    {
        // The file first in path order had the right declaration, under a false warning: the other file's type, in
        // another namespace, was listed as a declaration "sharing this name".
        var exact = Exact(await ImpactAsync(Path.Combine(_root, "Alpha", "Cart.cs"), "Cart"));

        Assert.Contains("declared at Alpha/Cart.cs", exact);
        Assert.Contains("Alpha/UseAlpha.cs", exact);
        Assert.DoesNotContain("Beta/", exact);
    }

    [Fact]
    public async Task TwoDeclarationsInTheFileAnalysed_AreStillCalledAmbiguous()
    {
        // Reference arm: the warning stays where it is true — the name declared twice in the file asked about.
        var twice = Path.Combine(_root, "Twice.cs");
        File.WriteAllText(twice,
            "namespace One\n{\n    public class Basket\n    {\n        public int A() => 1;\n    }\n}\n"
          + "namespace Two\n{\n    public class Basket\n    {\n        public int B() => 2;\n    }\n}\n");

        var exact = Exact(await ImpactAsync(twice, "Basket"));

        Assert.Contains("declared at Twice.cs", exact);
        Assert.Contains("other declaration(s) share this name", exact);
    }
}
