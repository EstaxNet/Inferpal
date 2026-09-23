using System.IO;
using Inferpal.Services;
using Inferpal.Services.Rag;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ The per-turn auto-context reads the same index as <c>search_codebase</c>, and a file saved a
/// moment ago is not re-indexed yet: saved, then asked about within the debounce — the most relevant
/// file there is — its chunks came back under "Relevant code" in their OLD version, and nothing said
/// so. Unlike an attached file, its current content is nowhere in the prompt.
/// </summary>
public sealed class AutoContextStaleSnippetTests
{
    private static RagHit Hit(string rel, string body) =>
        new(new RagChunk
        {
            FilePath  = @"C:\ws\" + rel,
            RelPath   = rel,
            StartLine = 1,
            EndLine   = 5,
            Content   = body,
        }, 0.8f, IsCosine: true);

    private static HashSet<string> Set(params string[] rel) =>
        rel.Select(r => @"C:\ws\" + r).ToHashSet(PathComparer.Default);

    [Fact]
    public void ASnippetOfAFileChangedSinceIndexed_IsLeftOut_AndSaid()
    {
        var block = RagAutoContext.Build(
            [Hit("Svc/Edited.cs", "int Old() => 1;"), Hit("Svc/Other.cs", "int Other() => 2;")],
            attachedPaths: new HashSet<string>(),
            notYetReindexed: Set("Svc/Edited.cs"));

        Assert.DoesNotContain("int Old()", block);          // the version from before the change
        Assert.Contains("int Other()", block);              // witness: the block was built
        Assert.Contains("changed since they were indexed", block);
    }

    [Fact]
    public void WhenEveryMatchIsStale_TheBlockStillSaysWhy()
    {
        var block = RagAutoContext.Build(
            [Hit("Svc/Edited.cs", "int Old() => 1;")],
            attachedPaths: new HashSet<string>(),
            notYetReindexed: Set("Svc/Edited.cs"));

        Assert.DoesNotContain("int Old()", block);
        Assert.Contains("changed since they were indexed", block);
    }

    [Fact]
    public void AnUpToDateIndex_SaysNothingMore()
    {
        // Reference arm.
        var block = RagAutoContext.Build([Hit("Svc/Other.cs", "int Other() => 2;")], new HashSet<string>(),
                                         notYetReindexed: new HashSet<string>());

        Assert.DoesNotContain("changed since", block);
    }

    [Theory]
    [InlineData("Inferpal", "ToolWindow", "InferpalToolWindowData.Attachments.cs")]
    [InlineData("Inferpal.Host", "HostServer.cs")]
    public void BothFrontEnds_PassTheFilesTheIndexIsBehindOn(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md"))) dir = dir.Parent;
        var code = ConventionCoverageTests.CodeOnly(Path.Combine(dir!.FullName, Path.Combine(parts)));

        var at = code.IndexOf("RagAutoContext.Build(", StringComparison.Ordinal);
        Assert.True(at >= 0, "the auto-context call is gone: this test would measure nothing");
        var call = code[at..code.IndexOf(';', at)];
        Assert.Contains("NotYetReindexed", call, StringComparison.Ordinal);
    }
}
