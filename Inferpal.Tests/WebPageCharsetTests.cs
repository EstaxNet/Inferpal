using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// fetch_url and @Docs read a page with <c>ReadAsStringAsync</c>, which knows only the charset of the HTTP header. A
/// static page served as plain <c>text/html</c> — nginx's and Apache's default — declares its encoding in a
/// <c>&lt;meta&gt;</c> the reader never looks at: a GBK, Shift_JIS or ISO-8859-2 page reaches the model as mojibake.
/// </summary>
public class WebPageCharsetTests
{
    static WebPageCharsetTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private static byte[] Page(string html, string encoding) => Encoding.GetEncoding(encoding).GetBytes(html);

    private static HttpContent Served(byte[] body, string? charset)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("text/html") { CharSet = charset };
        return content;
    }

    private const string Chinese = "<html><head><meta charset=\"gbk\"><title>文档</title></head><body>中文说明</body></html>";
    private const string Polish =
        "<html><head><meta http-equiv=\"Content-Type\" content=\"text/html; charset=iso-8859-2\"></head><body>Zażółć gęślą jaźń</body></html>";

    [Fact]
    public async Task TheFrameworkReader_IgnoresTheMetaCharset()
    {
        // The measurement this class exists for: the reader both sites used.
        var text = await Served(Page(Chinese, "gbk"), charset: null).ReadAsStringAsync();

        Assert.DoesNotContain("中文说明", text);
    }

    [Theory]
    [InlineData(Chinese, "gbk", "中文说明")]
    [InlineData(Polish, "iso-8859-2", "Zażółć gęślą jaźń")]
    public async Task AMetaCharset_IsRead_WhenTheHeaderHasNone(string html, string encoding, string expected)
    {
        var text = await WebPage.ReadTextAsync(Served(Page(html, encoding), charset: null), CancellationToken.None);

        Assert.Contains(expected, text);
    }

    [Fact]
    public async Task TheHeaderCharset_WinsOverAContradictoryMeta()
    {
        // The HTTP header is the transport's word: a meta left over from an old template does not override it.
        var html = "<html><head><meta charset=\"iso-8859-1\"></head><body>中文说明</body></html>";

        var text = await WebPage.ReadTextAsync(Served(Encoding.UTF8.GetBytes(html), charset: "utf-8"), CancellationToken.None);

        Assert.Contains("中文说明", text);
    }

    [Fact]
    public async Task ALegacyHeaderCharset_IsDecoded()
    {
        var text = await WebPage.ReadTextAsync(Served(Page(Polish, "iso-8859-2"), charset: "ISO-8859-2"), CancellationToken.None);

        Assert.Contains("Zażółć gęślą jaźń", text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("no-such-charset")]
    public async Task AnUndeclaredOrUnknownCharset_FallsBackToUtf8(string? charset)
    {
        // Reference arm: the ordinary modern page, UTF-8 with no declaration at all — and a header naming nothing
        // .NET knows, which ReadAsStringAsync answers with an exception.
        var html = "<html><body>Zażółć — 中文</body></html>";

        var text = await WebPage.ReadTextAsync(Served(Encoding.UTF8.GetBytes(html), charset), CancellationToken.None);

        Assert.Contains("Zażółć — 中文", text);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Theory]
    [InlineData("Inferpal.Core", "Services", "Tools", "FetchUrlTool.cs")]
    [InlineData("Inferpal.Core", "Services", "Tools", "WebSearchTool.cs")]
    [InlineData("Inferpal.Core", "Services", "Docs", "DocCrawler.cs")]
    public void EveryPageReader_GoesThroughTheSharedDecoder(params string[] parts)
    {
        // The decoder is pure and tested above; this keeps the three sites that read a web page ON it — none of them
        // is reachable from the suite (fetch_url and @Docs refuse a loopback address by design, web_search only talks
        // to its search engine).
        var code = ConventionCoverageTests.CodeOnly(Path.Combine(RepoRoot(), Path.Combine(parts)));

        Assert.Contains("WebPage.ReadTextAsync(", code);
        Assert.DoesNotContain("ReadAsStringAsync(", code);
    }
}
