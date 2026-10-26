using System.IO;
using Inferpal.Config;
using Inferpal.Services.Inference;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// "Your code stays on this machine" is said only when it is true: every role in use — the chat, completions when on,
/// code search when the index is — runs on this machine. A server elsewhere is named instead: said whatever the
/// address, the line would promise what a team server, or one role on another machine, breaks.
/// </summary>
public sealed class PrivacyPromiseTests
{
    private static InferpalConfig Config(string chat, bool completions = false, bool index = false) => new()
    {
        BaseUrl = chat,
        InlineCompletionEnabled = completions,
        RagEnabled = index,
    };

    [Theory]
    [InlineData("http://localhost:11434")]
    [InlineData("http://127.0.0.1:1234")]
    [InlineData("http://localhost:8080/v1")]
    public void EverythingHere_PromisesThisMachine(string chat) =>
        Assert.Empty(RoleServers.OffMachineHosts(Config(chat, completions: true, index: true)));

    [Fact]
    public void AChatOnAnotherMachine_IsNamed() =>
        Assert.Equal(["203.0.113.5"], RoleServers.OffMachineHosts(Config("http://203.0.113.5:11434")));

    [Fact]
    public void ARoleThatIsOff_SendsNothing()
    {
        var c = Config("http://localhost:11434");
        c.InlineCompletionRunsOn  = RoleServers.OnOther;
        c.InlineCompletionBaseUrl = "http://203.0.113.7:8080";
        Assert.Empty(RoleServers.OffMachineHosts(c));        // completions off
        c.InlineCompletionEnabled = true;
        Assert.Equal(["203.0.113.7"], RoleServers.OffMachineHosts(c));
    }

    [Fact]
    public void SearchOnTheAutocompleteServer_IsNamedOnce()
    {
        var c = Config("http://localhost:11434", completions: true, index: true);
        c.InlineCompletionRunsOn  = RoleServers.OnOther;
        c.InlineCompletionBaseUrl = "http://203.0.113.7:8080";
        c.RagEmbeddingRunsOn      = RoleServers.OnAutocomplete;
        Assert.Equal(["203.0.113.7"], RoleServers.OffMachineHosts(c));
    }

    /// <summary>@Docs embeds its queries on the search server whatever the index setting: with sources, that server counts.</summary>
    [Fact]
    public void DocsSources_SendToTheSearchServer_EvenWithTheIndexOff()
    {
        var c = Config("http://localhost:11434");
        c.RagEmbeddingRunsOn  = RoleServers.OnOther;
        c.RagEmbeddingBaseUrl = "http://203.0.113.9:11434";
        Assert.Empty(RoleServers.OffMachineHosts(c));   // reference arm: no index, no sources
        c.DocSitesJson = """[ { "id": "react", "title": "React", "startUrl": "https://react.dev/learn" } ]""";
        Assert.Equal(["203.0.113.9"], RoleServers.OffMachineHosts(c));
    }

    [Theory]
    [InlineData("not an address")]
    [InlineData("gpu-box:11434")]   // no scheme: read as the scheme "gpu-box", with no host
    public void AnAddressThatCannotBeRead_IsNeverTakenForThisMachine_AndIsNamedAsWritten(string chat) =>
        Assert.Equal([chat], RoleServers.OffMachineHosts(Config(chat)));

    [Fact]
    public void VisualStudio_SaysWhereTheCodeGoes()
    {
        var chrome = ConventionCoverageTests.CodeOnly(Path.Combine(ConventionCoverageTests.RepoRoot(), "Inferpal", "ToolWindow",
                                                                   "InferpalToolWindowData.Chrome.cs"));
        var at = chrome.IndexOf("private void RefreshWelcomeLine()", StringComparison.Ordinal);
        Assert.True(at >= 0, "RefreshWelcomeLine not found: this rule would measure nothing");   // WITNESS
        var line = chrome[at..chrome.IndexOf("\n    }", at, StringComparison.Ordinal)];
        // Judged on the settings file: the other editor can move a role to another machine.
        Assert.Contains("InferpalConfig.LoadOwnCopyToFollow() ?? _config", line, StringComparison.Ordinal);
        Assert.Contains("RoleServers.OffMachineHosts(servers)", line, StringComparison.Ordinal);
        Assert.Contains("Strings.WelcomeLineRemote(", line, StringComparison.Ordinal);
        Assert.Contains("Strings.WelcomeRemote(", line, StringComparison.Ordinal);
    }

    [Fact]
    public void VsCode_IsToldWhereTheCodeGoes_AndSaysIt()
    {
        var server = ConventionCoverageTests.CodeOnly(Path.Combine(ConventionCoverageTests.RepoRoot(), "Inferpal.Host", "HostServer.cs"));
        var status = server[server.IndexOf("public async Task<BackendStatusResult> BackendStatusAsync", StringComparison.Ordinal)..];
        Assert.Contains("RoleServers.OffMachineHosts(", status[..status.IndexOf("\n    }", StringComparison.Ordinal)], StringComparison.Ordinal);

        var provider = WebviewRebuildTests.TsCode("chatViewProvider.ts");
        Assert.Contains("offMachine: s.offMachine", provider, StringComparison.Ordinal);

        var welcome = WebviewRebuildTests.Body(WebviewRebuildTests.TsCode("webview/main.ts"), "function renderWelcomeLine(");
        Assert.Contains("connection?.offMachine", welcome, StringComparison.Ordinal);
        // A status that does not say where the servers are promises nothing: the line is left empty.
        Assert.Matches(new System.Text.RegularExpressions.Regex(@"if \(!hosts\) \{\s*welcomeLine\.textContent = '';\s*return;"), welcome);
        Assert.Contains("t('welcomeLineRemote'", welcome, StringComparison.Ordinal);
        Assert.Contains("t('welcomeRemote'", welcome, StringComparison.Ordinal);
    }
}
