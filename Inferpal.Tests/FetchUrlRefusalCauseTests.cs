using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Inferpal.Services;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  fetch_url names the cause of a refusal — and refuses before asking, when it already knows.
//
//  One sentence answered three different things: "Access denied: fetching private/loopback
//  addresses is not allowed". The guard treats an address it cannot parse, a scheme it does not
//  fetch and a host name that does not resolve as private — failing closed, rightly — and then said
//  so. A model that wrote docs.python.org/3/library without https:// was told the site is private;
//  on a machine with no network every fetch was "private". And the two causes readable from the
//  text alone came AFTER the approval prompt: the user was asked to allow a fetch that was then
//  refused anyway.
//
//  ⚠ The DNS check stays after the prompt: resolving a name the model chose sends that name to a
//  DNS server, which is exactly the exfiltration channel the prompt exists to gate.
// ──────────────────────────────────────────────────────────────────────────────────────────────
public class FetchUrlRefusalCauseTests
{
    private sealed class CountingApproval(bool approve) : IApprovalService
    {
        public int Calls { get; private set; }
        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct, string? subject = null, DiffInfo? diff = null, bool forcePrompt = false)
        {
            Calls++;
            return Task.FromResult(approve);
        }
    }

    private static JsonElement Url(string url) =>
        JsonDocument.Parse(JsonSerializer.Serialize(new { url })).RootElement;

    [Theory]
    [InlineData("docs.python.org/3/library/asyncio.html", "https://docs.python.org/3/library/asyncio.html")]
    [InlineData("example.com", "https://example.com")]
    public async Task AnAddressWithoutItsScheme_IsNamedAsSuch_WithTheFix_AndNeverAsked(string url, string suggestion)
    {
        var approval = new CountingApproval(approve: true);
        var result   = await new FetchUrlTool(approval).ExecuteAsync(Url(url), CancellationToken.None);

        Assert.Contains("http:// or https://", result);
        Assert.Contains(suggestion, result);
        Assert.DoesNotContain("private", result, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, approval.Calls);
    }

    [Fact]
    public async Task SomethingThatIsNoAddress_IsNamedAsSuch_WithoutAGuessedFix()
    {
        var approval = new CountingApproval(approve: true);
        var result   = await new FetchUrlTool(approval).ExecuteAsync(Url("not a url"), CancellationToken.None);

        Assert.Contains("http:// or https://", result);
        Assert.DoesNotContain("https://not", result);
        Assert.DoesNotContain("private", result, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, approval.Calls);
    }

    [Theory]
    [InlineData("ftp://example.com/file", "ftp")]
    [InlineData("file:///etc/passwd", "file")]
    public async Task AnotherScheme_IsNamedAsSuch_AndNeverAsked(string url, string scheme)
    {
        var approval = new CountingApproval(approve: true);
        var result   = await new FetchUrlTool(approval).ExecuteAsync(Url(url), CancellationToken.None);

        Assert.Contains($"{scheme}://", result);
        Assert.Contains("http:// and https://", result);
        Assert.DoesNotContain("private", result, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, approval.Calls);
    }

    [Theory]
    [InlineData("http://127.0.0.1:9/")]
    [InlineData("http://localhost:5000/api")]
    [InlineData("http://192.168.1.2/")]
    public async Task APrivateAddressReadableFromTheText_IsRefusedBeforeTheQuestion(string url)
    {
        var approval = new CountingApproval(approve: true);
        var result   = await new FetchUrlTool(approval).ExecuteAsync(Url(url), CancellationToken.None);

        Assert.Contains("private/loopback", result);
        Assert.Equal(0, approval.Calls);
    }

    [Fact]
    public async Task AHostThatDoesNotResolve_IsNamedAsSuch_NotAsPrivate()
    {
        // .invalid is reserved never to resolve (RFC 6761). Approved: the refusal comes from the
        // DNS check, after the prompt.
        var approval = new CountingApproval(approve: true);
        var ex = await Record.ExceptionAsync(() => new FetchUrlTool(approval)
            .ExecuteAsync(Url("https://no-such-host.invalid/page"), CancellationToken.None));

        Assert.NotNull(ex);
        Assert.Contains("no-such-host.invalid", ex!.Message);
        Assert.Contains("could not be resolved", ex.Message);
        Assert.DoesNotContain("private", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.IsNotType<ArgumentException>(ex);   // not "check the arguments": the name may be right
        Assert.Equal(1, approval.Calls);
    }

    [Fact]
    public async Task APublicAddress_StillGoesThroughTheQuestion()
    {
        // Reference arm: denied, so nothing leaves the machine.
        var approval = new CountingApproval(approve: false);
        var result   = await new FetchUrlTool(approval).ExecuteAsync(Url("https://example.com/page"), CancellationToken.None);

        Assert.Equal("Cancelled by user.", result);
        Assert.Equal(1, approval.Calls);
    }
}
