using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Inferpal.Config;
using Inferpal.Services;
using Inferpal.Services.Lsp;
using Inferpal.Services.Prompting;
using Inferpal.Services.Rag;
using Inferpal.Services.Tools;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Positional records, primary constructors, record structs and attributed types are types for every reader that lists
/// them by pattern — the project map, <c>/doc</c>'s context, the impact analysis — through one reader,
/// <see cref="CSharpTypeDeclarations"/>.
/// </summary>
/// <remarks>
/// The map read 1 of the 259 records of this repository's Core: its pattern wanted a <c>{</c> right after the name, and a
/// positional record has none, nor does a class whose primary constructor comes first. The impact analysis read
/// <c>record struct Money</c> as a type named "struct", then found "struct" in every file.
/// </remarks>
[Collection(SignalCollection.Name)]
public sealed class CSharpTypeDeclarationTests : IDisposable
{
    private readonly SignalScratchDir _scratch = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"typedecl-{Guid.NewGuid():N}");

    public CSharpTypeDeclarationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
        _scratch.Dispose();
    }

    private const string Shapes =
        "namespace Shapes;\n" +
        "public record Point(int X, int Y);\n" +
        "public readonly record struct Money(decimal Amount);\n" +
        "public sealed class Renderer(ILog log, int size = (1 + 2)) : RendererBase(log), IRender\n{\n}\n" +
        "[Serializable] public class Tagged { }\n" +
        "public class Plain : IRender { }\n" +
        "public interface IRender { }\n" +
        "public abstract class RendererBase(ILog log) { }\n" +
        "public interface ILog { }\n";

    [Fact]
    public void TheReader_SeesEveryShape_WithItsBases()
    {
        var types = CSharpTypeDeclarations.Read(Shapes);

        Assert.Equal(["Point", "Money", "Renderer", "Tagged", "Plain", "IRender", "RendererBase", "ILog"],
                     types.Select(t => t.Name));
        Assert.Equal("record", types.Single(t => t.Name == "Money").Kind);
        Assert.Equal(["RendererBase", "IRender"], types.Single(t => t.Name == "Renderer").BaseTypes);
        Assert.Equal(["IRender"], types.Single(t => t.Name == "Plain").BaseTypes);
        Assert.Equal(["IDictionary", "IEquatable"],
                     CSharpTypeDeclarations.Read("class Cache<TKey, TValue> : IDictionary<TKey, TValue>, IEquatable<Cache<TKey, TValue>> where TKey : notnull\n{\n}\n")[0].BaseTypes);
    }

    [Fact]
    public async Task TheMap_ListsPositionalRecords_AndClassesWithAPrimaryConstructor()
    {
        File.WriteAllText(Path.Combine(_root, "Shapes.cs"), Shapes);
        var index = new ProjectIndexService(new FakeInferenceProvider(), new InferpalConfig(), new LspSemanticProvider());
        index.SetRoot(_root);

        var map = await new ProjectMapService(new NullEditorSurface(), index).GenerateMapAsync(CancellationToken.None);

        Assert.Contains("classes: 4  interfaces: 2  records: 2", map, StringComparison.Ordinal);
        // A class whose primary constructor comes before its base list implements its interface.
        Assert.Contains("← Renderer, Plain", map, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheImpactOfARecordStruct_IsWhereItIsUsed_NotWhereverStructIsWritten()
    {
        var target = Path.Combine(_root, "Money.cs");
        File.WriteAllText(target, "namespace App;\npublic readonly record struct Money(decimal Amount);\n");
        File.WriteAllText(Path.Combine(_root, "Till.cs"), "namespace App;\npublic class Till\n{\n    public Money Total() => new Money(1);\n}\n");
        File.WriteAllText(Path.Combine(_root, "Other.cs"), "namespace App;\npublic struct Other { }\n");

        using var args = JsonDocument.Parse(JsonSerializer.Serialize(new { mode = "impact", path = target }));
        var report = await new AnalyzeCodeTool(() => _root).ExecuteAsync(args.RootElement, CancellationToken.None);

        Assert.Contains("Till.cs", report, StringComparison.Ordinal);
        Assert.DoesNotContain("Other.cs", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DocOnAPositionalRecord_ReadsTheInterfaceItImplements()
    {
        File.WriteAllText(Path.Combine(_root, "IEntity.cs"), "namespace N;\npublic interface IEntity\n{\n    int Key { get; }\n}\n");
        var source  = Path.Combine(_root, "Order.cs");
        var content = "namespace N;\npublic sealed record Order(int Key) : IEntity;\n";

        var block = await DocContextExtractor.BuildContextBlockAsync(source, content, CancellationToken.None);

        Assert.Contains("Key", block, StringComparison.Ordinal);
        Assert.Contains("IEntity", block, StringComparison.Ordinal);
    }

    [Fact]
    public void OnThisRepository_TheReaderFindsTheTypesTheCompilerDeclares()
    {
        // The compiler's list, file by file, against the reader's: a miss is a type the map, /doc and the impact
        // analysis do not know; an extra is a name they invent.
        var missed = new List<string>();
        var invented = new List<string>();
        var declared = 0;
        foreach (var file in ConventionCoverageTests.CoreSources("").Concat(ConventionCoverageTests.CoreSources("../Inferpal.Host")))
        {
            var source = File.ReadAllText(file);
            var compiler = CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes()
                .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.BaseTypeDeclarationSyntax>()
                .Select(t => t.Identifier.ValueText).ToList();
            var read = CSharpTypeDeclarations.Read(source).Select(t => t.Name).ToList();
            declared += compiler.Count;
            foreach (var name in compiler.Except(read)) missed.Add($"{Path.GetFileName(file)}: {name}");
            foreach (var name in read.Except(compiler)) invented.Add($"{Path.GetFileName(file)}: {name}");
        }

        Assert.True(declared > 500, $"only {declared} declarations read: the scan is not reading the sources");
        Assert.True(missed.Count == 0, $"{missed.Count} of {declared} types missed:\n" + string.Join("\n", missed.Take(40)));
        Assert.True(invented.Count == 0, $"{invented.Count} names invented:\n" + string.Join("\n", invented.Take(40)));
    }

    [Fact]
    public void NoOtherProductSource_ReadsTypeDeclarationsByItsOwnPattern()
    {
        // A pattern that names two declaration keywords as alternatives is a type reader. There is one.
        var alternation = new Regex(@"\b(class|interface|record|struct|enum)\|(class|interface|record|struct|enum)\b");
        var owners = new List<string>();
        foreach (var file in ConventionCoverageTests.CoreSources(""))
        {
            var root = CSharpSyntaxTree.ParseText(File.ReadAllText(file)).GetRoot();
            if (root.DescendantTokens().Any(t => t.IsKind(SyntaxKind.StringLiteralToken) && alternation.IsMatch(t.ValueText)))
                owners.Add(Path.GetFileName(file));
        }

        // Witness: the reader itself is found — the scan reads string literals.
        Assert.Contains("CSharpTypeDeclarations.cs", owners);
        Assert.Equal(["CSharpTypeDeclarations.cs"], owners);
    }
}
