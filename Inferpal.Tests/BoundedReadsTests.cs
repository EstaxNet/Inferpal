using System.IO;
using System.Net.Http;
using System.Text;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// What a remote server sends is read under a ceiling: a body asked with <c>ResponseHeadersRead</c> escapes
/// <c>HttpClient.MaxResponseContentBufferSize</c>, and a line that never ends escapes <c>ReadLineAsync</c>'s caller.
/// </summary>
public class BoundedReadsTests
{
    /// <summary>A body with no declared length: the ceiling is met while reading.</summary>
    private sealed class UnsizedContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            stream.WriteAsync(bytes).AsTask();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    [Fact]
    public async Task ABodyPastTheCeiling_IsRefused_DeclaredOrNot()
    {
        await Assert.ThrowsAsync<HttpRequestException>(() => BoundedReads.BytesAsync(new ByteArrayContent(new byte[2048]), 1024, default));
        await Assert.ThrowsAsync<HttpRequestException>(() => BoundedReads.BytesAsync(new UnsizedContent(new byte[2048]), 1024, default));
        // Reference arm: a body under it is read whole.
        Assert.Equal(512, (await BoundedReads.BytesAsync(new UnsizedContent(new byte[512]), 1024, default)).Length);
    }

    [Fact]
    public async Task TheBodyIsDecodedInItsCharset()
    {
        var content = new StringContent("café", Encoding.UTF8);
        Assert.Equal("café", await BoundedReads.StringAsync(content, 1024, default));
    }

    [Fact]
    public async Task ALineThatNeverEnds_IsSkippedWithoutBeingKept_AndTheNextOneIsRead()
    {
        var lines = new BoundedLineReader(new StringReader(new string('x', 20000) + "\nnext\r\nlast"), 100);

        Assert.Equal((string.Empty, true), await lines.ReadLineAsync(default));
        Assert.Equal(("next", false), await lines.ReadLineAsync(default));
        Assert.Equal(("last", false), await lines.ReadLineAsync(default));
        Assert.Equal(((string?)null, false), await lines.ReadLineAsync(default));
    }

    /// <summary>An event stream may end its lines with \n, \r\n or a lone \r: read as one line, a CR-only stream never
    /// yields its events, and its call waits out its budget.</summary>
    [Fact]
    public async Task EveryLineEndingAnEventStreamMayUse_EndsALine_AndAnEmptyLineIsKept()
    {
        var lines = new BoundedLineReader(new StringReader("a\rb\r\nc\n\r\rd"), 100);

        var read = new List<string?>();
        while ((await lines.ReadLineAsync(default)).Line is { } line) read.Add(line);

        Assert.Equal(["a", "b", "c", "", "", "d"], read);
    }

    /// <summary>A byte-order mark is the encoding's, never text: kept, it is a U+FEFF the JSON parser refuses.</summary>
    [Fact]
    public async Task AByteOrderMark_IsNotReadAsText()
    {
        var content = new ByteArrayContent([0xEF, 0xBB, 0xBF, (byte)'{', (byte)'}']);
        Assert.Equal("{}", await BoundedReads.StringAsync(content, 1024, default));
    }
}
