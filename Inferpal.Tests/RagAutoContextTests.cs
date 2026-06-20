using Inferpal.Services.Rag;
using Xunit;

namespace Inferpal.Tests;

// Formatting of the per-turn auto-injected RAG context block: dedup vs attachments, budget,
// chunk cap, and the empty cases.
public class RagAutoContextTests
{
    private static (RagChunk, float) Chunk(string file, string rel, string content, int start = 1, int end = 5) =>
        (new RagChunk { FilePath = file, RelPath = rel, Content = content, StartLine = start, EndLine = end, ContentHash = "h" }, 0.5f);

    [Fact]
    public void Empty_WhenNoResults()
    {
        Assert.Equal("", RagAutoContext.Build([], new HashSet<string>()));
    }

    [Fact]
    public void FormatsHeaderAndChunk()
    {
        var block = RagAutoContext.Build(
            [Chunk(@"C:\p\Auth.cs", "Auth.cs", "public void Login() {}", 10, 12)],
            new HashSet<string>());

        Assert.Contains("Relevant code", block);
        Assert.Contains("Auth.cs:10-12", block);
        Assert.Contains("public void Login()", block);
    }

    [Fact]
    public void SkipsChunksFromAlreadyAttachedFiles()
    {
        var attached = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\p\Auth.cs" };
        var block = RagAutoContext.Build(
            [
                Chunk(@"C:\p\Auth.cs",  "Auth.cs",  "attached file content"),
                Chunk(@"C:\p\Other.cs", "Other.cs", "other file content"),
            ],
            attached);

        Assert.DoesNotContain("attached file content", block);
        Assert.Contains("other file content", block);
    }

    [Fact]
    public void AllChunksAttached_ReturnsEmpty()
    {
        var attached = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\p\A.cs" };
        var block = RagAutoContext.Build([Chunk(@"C:\p\A.cs", "A.cs", "x")], attached);
        Assert.Equal("", block);
    }

    [Fact]
    public void CapsToMaxChunks()
    {
        var results = Enumerable.Range(0, 10)
            .Select(i => Chunk($@"C:\p\F{i}.cs", $"F{i}.cs", $"content {i}"))
            .ToList();

        var block = RagAutoContext.Build(results, new HashSet<string>(), maxChunks: 2);

        Assert.Contains("F0.cs", block);
        Assert.Contains("F1.cs", block);
        Assert.DoesNotContain("F2.cs", block);
    }

    [Fact]
    public void BudgetStopsAfterFirstChunk_ButAlwaysIncludesOne()
    {
        var big = new string('x', 5000);
        var results = new[]
        {
            Chunk(@"C:\p\Big1.cs", "Big1.cs", big),
            Chunk(@"C:\p\Big2.cs", "Big2.cs", big),
        };

        var block = RagAutoContext.Build(results, new HashSet<string>(), budget: 100);

        Assert.Contains("Big1.cs", block);       // at least one chunk always included
        Assert.DoesNotContain("Big2.cs", block);  // budget exceeded → stop
    }
}
