using System.IO;
using System.Linq;
using Inferpal.Services.Lsp;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// What the C# chunker leaves OUT of the index — code that <c>search_codebase</c> then can never
/// find, with nothing saying so.
/// </summary>
public class RoslynChunkerCoverageTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "ws");

    /// <summary>
    /// As soon as a type existed in the file, top-level statements were ignored: a minimal-API
    /// <c>Program.cs</c> with a model at the bottom indexed only the model — not a single endpoint.
    /// </summary>
    [Fact]
    public void TopLevelStatements_NextToATypeDeclaration_AreIndexed()
    {
        var src = string.Join('\n',
            "var app = WebApplication.Create();",
            "app.MapGet(\"/orders\", () => Orders.All());",
            "app.MapPost(\"/orders\", (Order o) => Orders.Add(o));",
            "app.Run();",
            "",
            "public class Order",
            "{",
            "    public int Id { get; set; }",
            "    public string Name { get; set; } = \"\";",
            "}");

        var chunks = RoslynChunker.Chunk(Path.Combine(Root, "Program.cs"), src, Root);

        Assert.Contains(chunks, c => c.Content.Contains("MapGet", StringComparison.Ordinal));
    }

    /// <summary>
    /// A member past a chunk's budget was SHORTENED until it fit: its tail was indexed nowhere.
    /// </summary>
    [Fact]
    public void AMemberLongerThanTheChunkBudget_HasItsTailIndexedToo()
    {
        var body = Enumerable.Range(0, 400)
            .Select(i => $"        var value{i} = Compute({i}); // filler line number {i}");
        var src = string.Join('\n',
            new[] { "public class Big", "{", "    public void Run()", "    {" }
                .Concat(body)
                .Concat(new[] { "        FinalMarkerCall();", "    }", "}" }));

        var chunks = RoslynChunker.Chunk(Path.Combine(Root, "Big.cs"), src, Root);

        Assert.Contains(chunks, c => c.Content.Contains("FinalMarkerCall", StringComparison.Ordinal));
    }

    /// <summary>Witness: an ordinary file still gets one chunk per member, with its type.</summary>
    [Fact]
    public void AnOrdinaryType_StillGetsOneChunkPerMember()
    {
        var src = string.Join('\n',
            "public class Calc",
            "{",
            "    public int Add(int a, int b)",
            "    {",
            "        return a + b;",
            "    }",
            "}");

        var chunks = RoslynChunker.Chunk(Path.Combine(Root, "Calc.cs"), src, Root);

        Assert.Contains(chunks, c => c.TypeName == "Calc.Add");
    }
}
