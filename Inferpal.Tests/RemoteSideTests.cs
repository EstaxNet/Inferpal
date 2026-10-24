using System.IO;
using Inferpal.Localization;
using Inferpal.Services.Hardware;
using Inferpal.Services.Inference;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// VS Code connected to WSL, an SSH server or a container runs Inferpal there, where <c>localhost</c> is that machine: a
/// local address that does not answer is said for what it may be — a stopped server, or one on the computer the person
/// sits at — with the gesture that works from that side, never "start it" alone.
/// </summary>
public sealed class RemoteSideTests
{
    [Theory]
    [InlineData(null, "None")]
    [InlineData("", "None")]
    [InlineData("wsl", "Wsl")]
    [InlineData("ssh-remote", "Ssh")]
    [InlineData("tunnel", "Machine")]
    [InlineData("codespaces", "Machine")]
    [InlineData("dev-container", "Container")]
    [InlineData("attached-container", "Container")]
    [InlineData("a-remote-vscode-adds-later", "Machine")]
    public void TheRemoteName_IsAKindOfOtherSide(string? remoteName, string kind)
    {
        Assert.Equal(kind, RemoteSide.Of(remoteName).ToString());
    }

    [Theory]
    [InlineData("http://localhost:11434", true)]
    [InlineData("http://LOCALHOST:1234/v1", true)]
    [InlineData("http://127.0.0.2:11434", true)]
    [InlineData("http://[::1]:11434", true)]
    [InlineData("http://192.168.1.2:11434", false)]
    [InlineData("https://llama.home.example", false)]
    [InlineData("not a url", false)]
    public void ALoopbackAddress_IsReadByTheOneLoopbackReader(string url, bool loopback)
    {
        Assert.Equal(loopback, HardwareProbe.IsLoopback(url));
        Assert.Equal(loopback, RemoteSide.Unreachable("wsl", url, "Ollama") is not null);
    }

    [Fact]
    public void FromWsl_AStoppedServer_AndMirroredNetworking_AreBothSaid()
    {
        var text = RemoteSide.Unreachable("wsl", "http://localhost:11434", "Ollama");

        Assert.Equal(Strings.RemoteLocalhostWsl("http://localhost:11434", "Ollama"), text);
        Assert.Contains("networkingMode=mirrored", text, StringComparison.Ordinal);
    }

    [Fact]
    public void FromAnSshServer_TheTunnelUsesTheAddressesPort()
    {
        var text = RemoteSide.Unreachable("ssh-remote", "http://localhost:1234/v1", "LM Studio");

        Assert.Equal(Strings.RemoteLocalhostSsh("http://localhost:1234/v1", "LM Studio", 1234), text);
        Assert.Contains("ssh -R 1234:localhost:1234", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("tunnel", "http://localhost:11434")]       // no SSH access to a tunnelled machine
    [InlineData("codespaces", "http://localhost:11434")]   // a Codespace is no `ssh` target as written
    [InlineData("ssh-remote", "http://localhost/v1")]      // no port of its own: a tunnel would bind port 80 there
    public void WhereNoReverseTunnelCanBeOpened_NoneIsOffered(string remoteName, string url)
    {
        var text = RemoteSide.Unreachable(remoteName, url, "Ollama");

        Assert.Equal(Strings.RemoteLocalhostMachine(url, "Ollama"), text);
        Assert.DoesNotContain("ssh -R", text, StringComparison.Ordinal);
    }

    [Fact]
    public void FromAContainer_TheMachineThatRunsIt_WithDockerEnginesCondition()
    {
        var text = RemoteSide.Unreachable("dev-container", "http://127.0.0.1:11434", "Ollama");

        Assert.Contains("http://host.docker.internal:11434", text, StringComparison.Ordinal);
        Assert.Contains("--add-host=host.docker.internal:host-gateway", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "http://localhost:11434")]          // where the person sits: the ordinary steps
    [InlineData("wsl", "https://llama.home.example")]     // a server by its name is the same from both sides
    public void ElsewhereOrByName_NothingIsSaidAboutTheOtherSide(string? remoteName, string url)
    {
        Assert.Null(RemoteSide.Unreachable(remoteName, url, "Ollama"));
    }

    [Fact]
    public void TheConnectionMessage_FromWsl_IsTheRemoteOne()
    {
        var text = Strings.MsgConnectionLost("http://localhost:11434", "Ollama", refusal: null, remoteName: "wsl");

        Assert.Equal(Strings.RemoteLocalhostWsl("http://localhost:11434", "Ollama"), text);
        // Reference arm: where the person sits, the ordinary steps stay.
        Assert.Equal(Strings.MsgConnectionGuardFailed("http://localhost:11434", "Ollama"),
                     Strings.MsgConnectionLost("http://localhost:11434", "Ollama", refusal: null));
    }

    [Fact]
    public void ARefusal_IsStillARefusal_FromTheRemoteSide()
    {
        // A server that refused the check answered: it is reachable from there, whatever side this is.
        Assert.Equal(Strings.MsgConnectionRefused("http://localhost:11434", "Ollama", "HTTP 401"),
                     Strings.MsgConnectionLost("http://localhost:11434", "Ollama", "HTTP 401", remoteName: "wsl"));
    }

    [Fact]
    public void FromAContainer_TheFirstLaunchAlsoLooksOnTheMachineThatRunsIt()
    {
        var inContainer = ServerScan.Places(null, "dev-container").Select(p => p.Url).ToList();
        var here        = ServerScan.Places(null).Select(p => p.Url).ToList();

        Assert.Contains("http://host.docker.internal:11434", inContainer);
        Assert.Contains("http://localhost:11434", inContainer);
        Assert.DoesNotContain(here, url => url.Contains(RemoteSide.ContainerHost, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("wsl", "RemoteSetupWsl")]
    [InlineData("ssh-remote", "RemoteSetupSsh")]
    [InlineData("tunnel", "RemoteSetupMachine")]
    [InlineData("dev-container", "RemoteSetupContainer")]
    public void TheFirstLaunch_SaysWhereHereIs_FromTheRemoteSideOnly(string remoteName, string key)
    {
        Assert.Equal(typeof(Strings).GetProperty(key)!.GetValue(null), RemoteSide.SetupNote(remoteName));
        Assert.Null(RemoteSide.SetupNote(null));
    }

    [Theory]
    [InlineData("en")] [InlineData("fr")] [InlineData("de")] [InlineData("es")] [InlineData("it")]
    [InlineData("ja")] [InlineData("ko")] [InlineData("pl")] [InlineData("ru")] [InlineData("zh-CN")]
    public void InTheChat_EveryPathAndCommandIsCode_OrMarkdownEatsItsBackslash(string culture)
    {
        // The chat renders Markdown, where "\." is an escaped dot: %UserProfile%\.wslconfig shows without its backslash.
        var resx = System.Xml.Linq.XDocument.Load(Path.Combine(ConversationPersistenceSilenceTests.RepoRoot(), "Inferpal.Core",
            "Localization", culture == "en" ? "Strings.resx" : $"Strings.{culture}.resx"));
        foreach (var key in new[] { "RemoteLocalhostWsl", "RemoteLocalhostSsh", "RemoteLocalhostContainer" })
        {
            var text = resx.Descendants("data").Single(d => (string?)d.Attribute("name") == key).Element("value")!.Value;
            var outsideCode = string.Concat(text.Split('`').Where((_, i) => i % 2 == 0));
            Assert.DoesNotContain("\\", outsideCode, StringComparison.Ordinal);
            Assert.DoesNotContain("--add-host", outsideCode, StringComparison.Ordinal);
            Assert.DoesNotContain("ssh -R", outsideCode, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void VsCode_TellsTheHostWhereItRunsIt_AndTheFirstLaunchShowsIt()
    {
        string Source(string file) => SettingsSchemaDriftTests.NeutralizeTypeScriptComments(File.ReadAllText(
            Path.Combine(ConversationPersistenceSilenceTests.RepoRoot(), "vscode", "src", file)));

        Assert.Contains("remoteName: vscode.env.remoteName", Source("extension.ts"), StringComparison.Ordinal);
        Assert.Contains("remoteName: this.options.remoteName", Source("hostClient.ts"), StringComparison.Ordinal);
        Assert.Contains("if (s.remoteNote) setupEl.appendChild(setupText('p', 'setup-hint', s.remoteNote));",
                        Source(Path.Combine("webview", "main.ts")), StringComparison.Ordinal);
    }
}
