using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

// Covers the fetch_url SSRF guard. Redirects are followed manually and every hop is
// re-validated with IsPrivateOrLoopback, so this predicate is the whole defence.
public class FetchUrlToolTests
{
    [Theory]
    // Loopback / well-known internal names
    [InlineData("http://localhost/admin")]
    [InlineData("http://127.0.0.1:11434/api/tags")]
    [InlineData("http://127.8.4.2/")]
    [InlineData("http://[::1]/")]
    [InlineData("http://metadata.google.internal/computeMetadata/v1/")]
    // RFC-1918 private ranges
    [InlineData("http://10.0.0.5/")]
    [InlineData("http://172.16.0.1/")]
    [InlineData("http://172.31.255.255/")]
    [InlineData("http://192.168.1.2:11434/")]
    // Link-local (incl. cloud metadata 169.254.169.254) and CGNAT
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://100.64.0.1/")]
    [InlineData("http://[fe80::1]/")]
    // IPv6 unique-local fc00::/7 (private LANs) and the unspecified address
    [InlineData("http://[fc00::1]/")]
    [InlineData("http://[fd12:3456:789a::1]/")]
    [InlineData("http://[::]/")]
    // 0.0.0.0/8 "this host" and IPv4-mapped IPv6 smuggling a private v4 address
    [InlineData("http://0.0.0.0/")]
    [InlineData("http://[::ffff:127.0.0.1]/")]
    [InlineData("http://[::ffff:169.254.169.254]/")]
    // Non-HTTP schemes
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("ftp://example.com/file")]
    // Unparseable
    [InlineData("not a url")]
    public void PrivateOrInvalidUrls_AreBlocked(string url) =>
        Assert.True(FetchUrlTool.IsPrivateOrLoopback(url));

    [Theory]
    [InlineData("https://example.com/page")]
    [InlineData("http://93.184.216.34/")]          // public IPv4 literal
    [InlineData("https://learn.microsoft.com/dotnet")]
    [InlineData("http://172.15.0.1/")]              // just below the RFC-1918 172.16/12 range
    [InlineData("http://172.32.0.1/")]              // just above it
    public void PublicUrls_AreAllowed(string url) =>
        Assert.False(FetchUrlTool.IsPrivateOrLoopback(url));
}
