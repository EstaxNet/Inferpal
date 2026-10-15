using System.Diagnostics;
using System.IO;
using System.Net.Http;
using Inferpal.Services.Mcp.OAuth;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// An MCP sign-in whose page could not be opened says so at once — never a five-minute wait for a redirect that cannot
/// come — and in VS Code the page opens through the editor, on the machine the person sits at.
/// </summary>
/// <remarks>
/// The receiver started the browser itself and swallowed any failure: on a host running remotely (VS Code Remote: SSH,
/// WSL, a container) or a Linux without <c>xdg-open</c>, no page opened, and the sign-in waited out its five minutes
/// before blaming the person for not completing it.
/// </remarks>
public sealed class SignInPageNotOpenedTests
{
    [Fact]
    public async Task APageThatCannotOpen_FailsAtOnce_NamingWhy()
    {
        // A wait of a minute, for the failure must not come from it: a receiver that waited would end on a timeout.
        var receiver = new LoopbackAuthCodeReceiver(TimeSpan.FromMinutes(1),
                                                    (_, _, _) => Task.FromResult<string?>("no browser on this machine"));

        var watch = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            receiver.GetAuthorizationCodeAsync("https://auth.example/authorize?x=1", CancellationToken.None));

        Assert.Contains("could not be opened: no browser on this machine", ex.Message);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"the failure took {watch.Elapsed}");
    }

    [Fact]
    public async Task AnOpenedPage_IsWaitedFor_AndItsRedirectReturnsTheCode()
    {
        // Reference arm: the opener gets the page AND the redirect address, and the redirect it sends back completes.
        string? openedPage = null, redirectGiven = null;
        var receiver = new LoopbackAuthCodeReceiver(TimeSpan.FromSeconds(30), (url, redirect, ct) =>
        {
            openedPage = url;
            redirectGiven = redirect;
            _ = Task.Run(async () =>
            {
                using var http = new HttpClient();
                try { await http.GetAsync(redirect + "?code=granted&state=s1"); } catch { /* the receiver decides */ }
            });
            return Task.FromResult<string?>(null);
        });

        var response = await receiver.GetAuthorizationCodeAsync("https://auth.example/authorize?x=1", CancellationToken.None);

        Assert.Equal("https://auth.example/authorize?x=1", openedPage);
        Assert.Equal(receiver.RedirectUri, redirectGiven);
        Assert.Equal("granted", response.Code);
        Assert.Equal("s1", response.State);
    }

    [Fact]
    public void ThisMachinesOpener_RefusesAnAddressThatIsNotAWebPage_AndSaysSo()
    {
        Assert.Equal("its address is not a web address", LoopbackAuthCodeReceiver.OpenBrowser("file:///C:/Windows/notepad.exe"));
    }

    [Fact]
    public void VsCode_OpensThePage_ThroughTheEditor_WithTheRedirectForwarded()
    {
        var root   = ConversationPersistenceSilenceTests.RepoRoot();
        var bridge = SettingsSchemaDriftTests.NeutralizeTypeScriptComments(
            File.ReadAllText(Path.Combine(root, "vscode", "src", "editorBridge.ts")));
        var client = SettingsSchemaDriftTests.NeutralizeTypeScriptComments(
            File.ReadAllText(Path.Combine(root, "vscode", "src", "hostClient.ts")));

        Assert.Contains("conn.onRequest('env/openExternal'", client);
        Assert.Contains("openExternal: true", client);
        // The redirect is forwarded BEFORE the page opens, and a forward to another port is said, not waited for.
        var forward = bridge.IndexOf("vscode.env.asExternalUri(", StringComparison.Ordinal);
        var open    = bridge.IndexOf("vscode.env.openExternal(vscode.Uri.parse(url", StringComparison.Ordinal);
        Assert.True(forward > 0 && open > forward, "the redirect is forwarded, then the page opened");
        Assert.Contains("portOf(external) !== portOf(local)", bridge);
    }
}
