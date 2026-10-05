using System.IO;
using System.Linq;
using Inferpal.Services.Lsp;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  The C# index keeps the members that fit on one line.
//
//  A member was indexed only if it spanned two lines: properties, fields, interface signatures, enum
//  values and expression-bodied methods without a doc comment were in no chunk, and the type's
//  header kept the fallback from running — search_codebase could never return "CustomerEmail".
// ──────────────────────────────────────────────────────────────────────────────────────────────
public class RoslynOneLineMemberTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "inferpal-oneline");
    private static List<Services.Rag.RagChunk> Chunk(string src) =>
        RoslynChunker.Chunk(Path.Combine(Root, "File.cs"), src, Root);

    private static void Indexed(string src, string text) =>
        Assert.True(Chunk(src).Any(c => c.Content.Contains(text, StringComparison.Ordinal)),
                    $"'{text}' is in no chunk.");

    [Fact]
    public void ADtosProperties_AreIndexed() =>
        Indexed("public class OrderDto\n{\n    public int Id { get; set; }\n    public string CustomerEmail { get; set; } = \"\";\n}\n",
                "CustomerEmail");

    [Fact]
    public void AnInterfacesSignatures_AreIndexed() =>
        Indexed("public interface IOrders\n{\n    Task SaveAsync(Order o);\n    Task<Order?> FindAsync(int id);\n}\n", "SaveAsync");

    [Fact]
    public void AnEnumsValues_AreIndexed() =>
        Indexed("public enum Status\n{\n    Pending,\n    Shipped,\n}\n", "Shipped");

    [Fact]
    public void APositionalRecordOnOneLine_IsIndexed_BesideAnotherType() =>
        // Alone in its file it produces no chunk and the regex fallback takes the file: the loss needs another type.
        Indexed("namespace App;\n\npublic record Point(int X, int Y);\n\npublic class Calc\n{\n    public int Add(int a, int b)\n"
              + "    {\n        return a + b;\n    }\n}\n", "record Point(int X, int Y)");

    [Fact]
    public void AMultiLineMember_KeepsItsOwnChunk()
    {
        // Reference arm: a member long enough for its own chunk keeps it, and the one-liner beside it is not merged in.
        var chunks = Chunk("public class Calc\n{\n    public int Add(int a, int b)\n    {\n        return a + b;\n    }\n"
                         + "    public int Zero => 0;\n    public int Sub(int a, int b)\n    {\n        return a - b;\n    }\n}\n");

        Assert.Contains(chunks, c => c.TypeName == "Calc.Add" && !c.Content.Contains("Zero", StringComparison.Ordinal));
        Assert.Contains(chunks, c => c.TypeName == "Calc.Sub");
        Assert.Contains(chunks, c => c.Content.Contains("Zero => 0", StringComparison.Ordinal));
    }
}
