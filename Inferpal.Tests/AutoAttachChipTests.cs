using System.IO;
using Inferpal.Services.Rag;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The Visual Studio auto-attach chip (<c>🔮 File.cs</c>) carries the file's best semantic-search chunks, and reaches
/// the model as <c>[Attached: 🔮 File.cs]</c> — which reads as the file. Without a line naming the excerpts, the model
/// lists the methods it was shown as everything the file defines; a file the chunks cover entirely must stay silent.
/// </summary>
public sealed class AutoAttachChipTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "inferpal-chip-" + Guid.NewGuid().ToString("N"));

    public AutoAttachChipTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static RagChunk Chunk(int start, int end, string content) =>
        new() { FilePath = "C:\\ws\\Pricing.cs", RelPath = "Pricing.cs", StartLine = start, EndLine = end, Content = content };

    [Fact]
    public void ExcerptsOfALongerFile_SayWhatTheyAre_AndWhichLines()
    {
        var content = RagAutoContext.ChipContent("Pricing.cs",
            [Chunk(3, 7, "ApplyLoyaltyDiscount"), Chunk(58, 60, "RoundToCent")], totalLines: 80);

        Assert.StartsWith("Excerpts of Pricing.cs", content);
        Assert.Contains("lines 3-7, 58-60", content);
        Assert.Contains("not the whole file", content);
        Assert.Contains("ApplyLoyaltyDiscount", content);                                     // witness: the excerpts are there
        Assert.Contains("RoundToCent", content);
    }

    [Fact]
    public void AFileTheChunksCoverEntirely_IsShownAsItIs()
    {
        // Reference arm: a small file is one chunk, and saying "not the whole file" about it would be false.
        Assert.Equal("the whole file", RagAutoContext.ChipContent("Pricing.cs", [Chunk(1, 12, "the whole file")], totalLines: 12));
    }

    [Fact]
    public void OverlappingChunksThatCoverTheFile_AreWhole()
    {
        var content = RagAutoContext.ChipContent("Pricing.cs", [Chunk(25, 50, "B"), Chunk(1, 30, "A")], totalLines: 50);

        Assert.DoesNotContain("Excerpts of", content);
        Assert.Equal("B\n\n...\n\nA", content);                                               // relevance order kept
    }

    [Fact]
    public void AnUnknownLineCount_KeepsTheNote()
    {
        Assert.StartsWith("Excerpts of", RagAutoContext.ChipContent("Pricing.cs", [Chunk(1, 12, "x")], totalLines: null));
    }

    [Fact]
    public void TheLineCount_IsReadFromDisk()
    {
        var path = Path.Combine(_dir, "Pricing.cs");
        File.WriteAllText(path, string.Join("\n", Enumerable.Range(1, 10).Select(i => $"line {i}")) + "\n");

        Assert.Equal("all", RagAutoContext.ChipContent(path, [Chunk(1, 10, "all")]));
        Assert.StartsWith("Excerpts of Pricing.cs", RagAutoContext.ChipContent(path, [Chunk(1, 5, "half")]));
    }

    [Fact]
    public void TheViewModelsChip_IsBuiltByTheSharedFunction()
    {
        // Not executable from this suite (Remote UI): a source scan, with its witness.
        var code = ConventionCoverageTests.CodeOnly(ConventionCoverageTests.ProjectSources("Inferpal")
            .Single(f => Path.GetFileName(f) == "InferpalToolWindowData.Mentions.cs"));

        Assert.Contains("isAutoAttach: true", code);                                          // WITNESS: the chip is here
        Assert.Contains("RagAutoContext.ChipContent(", code);
        Assert.DoesNotContain("string.Join(\"\\n\\n...\\n\\n\"", code);
    }
}
