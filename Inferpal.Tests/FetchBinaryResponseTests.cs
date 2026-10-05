using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  fetch_url names a binary response instead of decoding it.
//
//  A PDF (a common link from web_search) or an image came back as UTF-8-decoded bytes — noise read as
//  content — under a footer inviting the model to read on with start_char, one download per window.
//  read_file names a binary file; the web reader did not.
// ──────────────────────────────────────────────────────────────────────────────────────────────
public class FetchBinaryResponseTests
{
    private static async Task<WebPage.WebText> Read(byte[] bytes, string mediaType)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        return await WebPage.ReadAsync(content, CancellationToken.None);
    }

    private static readonly byte[] Pdf =
        [.. Encoding.ASCII.GetBytes("%PDF-1.7\n%\u00e2\u00e3\n1 0 obj\n<< /Length 42 >>\nstream\n"), 0x78, 0x9C, 0x00, 0x01, 0x00, 0xFF];

    [Fact]
    public async Task APdf_IsNamed_NotDecoded()
    {
        var page = await Read(Pdf, "application/pdf");

        var text = FetchUrlTool.Readable(page);

        Assert.True(page.Binary);
        Assert.Contains("binary", text);
        Assert.Contains("application/pdf", text);
        Assert.Contains($"{Pdf.Length} bytes", text);
        Assert.DoesNotContain("%PDF", text);
    }

    [Fact]
    public async Task ATextResponse_IsStillText()
    {
        // Reference arm: text — including text served under a generic type — comes back as written.
        var page = await Read(Encoding.UTF8.GetBytes("public class A { }\n"), "application/octet-stream");

        Assert.False(page.Binary);
        Assert.Equal("public class A { }", FetchUrlTool.Readable(page));
    }

    [Fact]
    public async Task AUtf16Page_IsText_ItsNulsAreHalfItsCharacters()
    {
        var page = await Read([.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("hello")], "text/plain");

        Assert.False(page.Binary);
        Assert.Equal("hello", FetchUrlTool.Readable(page));
    }
}
