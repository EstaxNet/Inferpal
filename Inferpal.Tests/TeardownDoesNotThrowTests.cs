using System.IO;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A teardown that throws turns a <b>passing</b> test into a failing one.
/// </summary>
/// <remarks>
/// <para>
/// Found by the macOS CI leg, the first time it decided anything again:
/// <c>OAuthRedirectTests.ARedirectedDiscovery_IsNotFollowed</c> red with
/// <c>HttpListenerException: Address already in use</c> — thrown from <c>Dispose()</c>, after the
/// body had passed. On the managed <c>HttpListener</c> (every non-Windows leg) <c>Dispose</c>
/// re-resolves the endpoint and raises when the port has been taken since.
/// </para>
/// <para>
/// ⚠ <b>The fix was already half-written, one line above the defect</b>: <c>Stop()</c> was wrapped
/// in <c>try/catch</c> and <c>Dispose()</c> — the next statement — was not. The rule was known and
/// applied to the call that had been seen throwing, not to the class.
/// </para>
/// <para>
/// ⚠ <b>And the cost is larger than one red test.</b> That leg alone sees a path guard comparing
/// case-sensitively on a folding volume, and an ancestor symlink. <b>A gating leg that reddens for
/// something that is not the product is how a gating leg gets turned off again</b> — which is
/// exactly what had happened to it before.
/// </para>
/// </remarks>
public class TeardownDoesNotThrowTests
{
    /// <summary>
    /// Four test files share this server; one throwing teardown reddens all of them. Disposing
    /// twice is the cheap stand-in for "the cleanup survives a state it did not expect".
    /// </summary>
    [Fact]
    public void TheLoopbackServer_SurvivesBeingDisposedTwice()
    {
        var server = new LoopbackHttpServer(_ => "hello");

        // WITNESS: it really started — a server that never bound would pass the dispose below
        // without measuring anything.
        Assert.StartsWith("http://127.0.0.1:", server.BaseUrl, StringComparison.Ordinal);

        server.Dispose();
        server.Dispose();   // the assertion IS that this returns
    }

    /// <summary>
    /// Not executable from the suite — xUnit owns that <c>Dispose</c>, and reproducing "the port
    /// was taken in between" is the race the previous round refused to chase. A source assertion
    /// with its witness, like <c>InProcContractTests</c>.
    /// </summary>
    [Fact]
    public void TheOAuthListenerTeardown_GuardsEveryCall_NotOnlyTheOneSeenThrowing()
    {
        var path = Path.Combine(RepoRoot(), "Inferpal.Tests", "OAuthRedirectTests.cs");
        var code = ConventionCoverageTests.CodeOnly(path);   // comments stripped: the remark quotes the defect

        var open  = code.IndexOf("public void Dispose()", StringComparison.Ordinal);
        Assert.True(open > 0, "OAuthRedirectTests no longer has a Dispose: this rule reads nothing.");
        // ⚠ Brace MATCHING, not the first closing brace: the first `}` after the method's `{` is
        // the one of `catch { }`, and the body would stop before the very statement this rule is
        // about — a scan that measures its own extractor.
        var start = code.IndexOf('{', open);
        var depth = 0;
        var close = start;
        while (close < code.Length)
        {
            if (code[close] == '{') depth++;
            else if (code[close] == '}' && --depth == 0) break;
            close++;
        }
        var body = code[(start + 1)..close];

        // WITNESS: the body really holds the two calls the rule is about.
        Assert.Contains("_listener.Stop()", body, StringComparison.Ordinal);
        Assert.Contains("Dispose()", body[(body.IndexOf("_listener.Stop()", StringComparison.Ordinal))..],
                        StringComparison.Ordinal);

        // And neither is left bare. A statement outside a try is one that can fail the class.
        foreach (var line in body.Split('\n'))
        {
            var t = line.Trim();
            if (t.Length == 0) continue;
            Assert.StartsWith("try", t, StringComparison.Ordinal);
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
