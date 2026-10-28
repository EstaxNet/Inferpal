using System.Net;
using Inferpal.Localization;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Behind a corporate proxy set by HTTP_PROXY, .NET sends a request to localhost to the proxy too, which cannot reach
/// the person's machine: without the product's proxy, the local model server that runs is said unreachable, and a
/// proxy's 502 or 403 reads as the server's own silence or refusal ("start Ollama", "check the API key"). The product's
/// proxy goes direct to the loopback, keeps the system settings' proxy untouched, and the texts name the proxy a
/// request goes through.
/// </summary>
// Switches Strings.OverrideCulture (process-wide): serialized collection.
[Collection(CultureSerialCollection.Name)]
public class NetworkProxyTests
{
    private static readonly Uri Corporate = new("http://proxy.corp.example:8080/");

    /// <summary>A proxy that takes every request, loopback included — what .NET does with HTTP_PROXY.</summary>
    private sealed class TakesEverything(params string[] bypassed) : IWebProxy
    {
        public ICredentials? Credentials { get; set; }
        public Uri? GetProxy(Uri destination) => IsBypassed(destination) ? null : Corporate;
        public bool IsBypassed(Uri host) => bypassed.Contains(host.Host);
    }

    [Theory]
    [InlineData("http://localhost:11434")]
    [InlineData("http://127.0.0.1:1234")]
    [InlineData("http://[::1]:11434")]
    public void FromTheVariables_ALoopbackServer_IsReachedDirect_WhereTheVariablesProxyWouldTakeIt(string url)
    {
        var variables = new TakesEverything();
        Assert.Equal(Corporate, variables.GetProxy(new Uri(url)));   // reference arm: the bare proxy takes it

        var proxy = NetworkProxy.Of(variables, fromVariables: true);
        Assert.True(proxy.IsBypassed(new Uri(url)));
        Assert.Null(NetworkProxy.Through(proxy, url));
    }

    [Fact]
    public void FromTheVariables_AServerOnTheNetwork_KeepsTheProxy_AndNoProxy()
    {
        var proxy = NetworkProxy.Of(new TakesEverything("192.168.1.2"), fromVariables: true);
        Assert.Equal(Corporate, NetworkProxy.Through(proxy, "http://192.168.1.20:11434"));
        Assert.Null(NetworkProxy.Through(proxy, "http://192.168.1.2:11434"));   // NO_PROXY honored
        // No http(s) scheme: no request of the product goes there, no proxy is named for it.
        Assert.Null(NetworkProxy.Through(proxy, "localhost:11434"));
        Assert.Null(NetworkProxy.Through(proxy, "not a url"));
    }

    [Fact]
    public void TheSystemSettingsProxy_IsKeptAsItIs_WithItsOwnFailover()
    {
        // .NET reads a PAC list's failover (next proxy, then direct) off the system proxy's own type: a wrapper hides it.
        var settings = new TakesEverything();
        Assert.Same(settings, NetworkProxy.Of(settings, fromVariables: false));
    }

    [Fact]
    public void TheConnectionTexts_NameTheProxyAndNoProxy_InEveryLanguage()
    {
        var previous = Strings.OverrideCulture?.Name;
        try
        {
            foreach (var culture in new[] { "en", "fr", "de", "es", "it", "ja", "ko", "pl", "ru", "zh-CN" })
            {
                Strings.ApplyLanguage(culture);
                var lost = Strings.WithProxyNote(Strings.MsgConnectionGuardFailed("http://192.168.1.2:11434", "Ollama"),
                                                 "Ollama", Corporate);
                Assert.Contains(Corporate.ToString(), lost);
                Assert.Contains("NO_PROXY", lost);
                Assert.Contains("Ollama", lost);

                var status = Strings.StatusNotReached(Strings.StatusUnreachable, Corporate);
                Assert.StartsWith(Strings.StatusUnreachable, status);
                Assert.Contains(Corporate.ToString(), status);
            }
        }
        finally { Strings.ApplyLanguage(previous); }
    }

    [Fact]
    public void WithoutAProxy_TheTextsAreTheOnesTheyWere()
    {
        var text = Strings.MsgConnectionGuardFailed("http://192.168.1.2:11434", "Ollama");
        Assert.Equal(text, Strings.WithProxyNote(text, "Ollama", proxy: null));
        Assert.Equal(Strings.StatusUnreachable, Strings.StatusNotReached(Strings.StatusUnreachable, proxy: null));
    }
}
