using System.IO;
using Inferpal.Services.Rag;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A file of one to three lines is the whole file, not a fragment of one: it becomes one chunk. Dropped as "too short",
/// an <c>__init__.py</c>, a barrel <c>index.ts</c> or a one-line minified script was never indexed, and nothing said so.
/// </summary>
public class TinyFileChunkTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "inferpal-tiny-root");

    [Theory]
    [InlineData("pkg/__init__.py", "from .core import run\n")]
    [InlineData("src/index.ts", "export * from './a';\nexport * from './b';")]
    [InlineData("dist/app.min.js", "function a(){return 1}")]
    public void ATinyFile_IsOneChunk(string relative, string content)
    {
        var chunk = Assert.Single(CodeChunker.Chunk(Path.Combine(Root, relative), content, Root));
        Assert.Contains(content.Split('\n')[0], chunk.Content);
    }
}
