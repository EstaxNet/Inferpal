using System.Reflection;
using Inferpal.Services.Lsp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Inside the one-file ACP executable the runtime lists no assembly on disk, so the C# analysis takes the framework from
/// the assemblies loaded in memory, by the names the executable carries. A .NET type must then bind as it does from files.
/// </summary>
public sealed class InMemoryFrameworkReferencesTests
{
    // ⚠ Uri and XDocument live in System.Private.Uri and System.Private.Xml.Linq, which no compile reference names: the
    // facades that do (System.Runtime, System.Xml.XDocument) forward to them.
    private const string Code = "using System; using System.Collections.Generic; using System.Linq; using System.Xml.Linq; "
                              + "class C { List<int> M() => new List<int> { 1 }.Where(x => x > 0).ToList(); "
                              + "string H() => new Uri(\"http://x\").Host + new XDocument().Root; }";

    private static IReadOnlyList<Diagnostic> Errors(IReadOnlyList<MetadataReference> references) =>
        [.. CSharpCompilation.Create("probe", [CSharpSyntaxTree.ParseText(Code)], references,
                                     new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)];

    [Fact]
    public void TheFrameworkReadInMemory_BindsDotNetTypes()
    {
        var references = CSharpSemanticIndex.InMemoryFrameworkReferences(
            ["System.Runtime", "System.Collections", "System.Linq", "System.Xml.XDocument"]);

        Assert.True(references.Count >= 5, $"{references.Count} reference(s)");   // the four, and the core library
        Assert.Empty(Errors(references));
    }

    [Fact]
    public void WithoutReferences_NoDotNetTypeBinds()
    {
        // Reference arm: what the analysis gets inside one file when nothing stands in for the missing files.
        Assert.Empty(CSharpSemanticIndex.InMemoryFrameworkReferences([]));
        Assert.NotEmpty(Errors([]));
    }

    [Fact]
    public void AnExecutableThatCarriesNoList_GivesNoName()
    {
        // Every build but the one-file one: the files are on disk, and no list is embedded.
        Assert.Empty(CSharpSemanticIndex.FrameworkNamesOf(typeof(InMemoryFrameworkReferencesTests).Assembly));
        Assert.Empty(CSharpSemanticIndex.FrameworkNamesOf(null));
    }
}
