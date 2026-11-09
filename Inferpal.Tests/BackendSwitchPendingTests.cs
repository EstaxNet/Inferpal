using System.IO;
using Inferpal.Config;
using Inferpal.Services.Inference;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  A backend switched in Visual Studio's Settings is said to apply at restart.
//
//  Visual Studio builds its client once; Settings (and /setup) write the new backend into the live
//  configuration at once. Requests then went through the previous backend's client to the new
//  address, and every connection message blamed the new backend — "cannot reach LM Studio, start LM
//  Studio" — while it was running. Retry could not help; only a restart applies the switch.
// ──────────────────────────────────────────────────────────────────────────────────────────────
public class BackendSwitchPendingTests
{
    [Fact]
    public void AClientBuiltForOllama_WithLmStudioNowConfigured_HasASwitchPending()
    {
        var config = new InferpalConfig { Provider = InferenceProviderFactory.Ollama };
        var client = InferenceProviderFactory.Create(config);
        Assert.Equal(InferenceProviderFactory.Ollama, InferenceProviderFactory.CodeOf(client));   // WITNESS

        config.Provider = InferenceProviderFactory.LmStudio;   // what saving the Settings does to the live instance

        Assert.Equal(InferenceProviderFactory.LmStudio, InferenceProviderFactory.PendingSwitch(client, config.Provider));
    }

    [Theory]
    [InlineData("lmstudio", "lmstudio")]
    [InlineData("openai-compatible", "openai")]   // the legacy spelling names the same backend
    [InlineData("ollama", "")]                    // an empty code is what Create builds: Ollama
    [InlineData("ollama", "OLLAMA ")]
    public void TheSameBackend_HasNoSwitchPending(string built, string configured)
    {
        var client = InferenceProviderFactory.Create(new InferpalConfig { Provider = built });

        Assert.Null(InferenceProviderFactory.PendingSwitch(client, configured));
    }

    [Fact]
    public void AClientTheFactoryDidNotBuild_IsNeverSaidToBeSwitching()
    {
        Assert.Null(InferenceProviderFactory.PendingSwitch(new FakeInferenceProvider(), InferenceProviderFactory.LmStudio));
    }

    /// <summary>
    /// Visual Studio's client rebuilds itself at the turn door (<see cref="SwitchingProvider"/>): no message of its window
    /// waits for a restart any more. The heartbeat probes the CLIENT's server — under a turn, the one it started with —, and
    /// /setup sends what it adopted through the door.
    /// </summary>
    [Theory]
    [InlineData("InferpalToolWindowData.Connection.cs", "var url = _client.ServerAddress;")]   // the heartbeat
    [InlineData("InferpalToolWindowData.Rag.cs", "FollowServerForSetup();")]                   // /setup and the first run
    public void VisualStudiosConnectionMessages_SpeakOfTheClientsServer_NeverOfARestart(string file, string follows)
    {
        var path = Path.Combine(ConventionCoverageTests.RepoRoot(), "Inferpal", "ToolWindow", file);
        Assert.True(File.Exists(path), $"{file} is gone — this guard checks nothing any more.");
        var code = ConventionCoverageTests.CodeOnly(path);

        Assert.Contains(follows, code, StringComparison.Ordinal);
        Assert.DoesNotContain("PendingSwitch(", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// ⚠ The property, not a list of files: in the Visual Studio adapter "cannot reach" is written by <c>UnreachableText</c>
    /// and by the heartbeat's edge, and nowhere else; both name the client's own server, and every refusal reads
    /// <c>UnreachableText</c>.
    /// </summary>
    [Fact]
    public void TheVisualStudioUnreachableSentence_HasOneWriter_ThatNamesTheClientsServer()
    {
        var root = Path.Combine(ConventionCoverageTests.RepoRoot(), "Inferpal");
        var files = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                             .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                                      && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                             .ToList();
        Assert.True(files.Count > 40, $"only {files.Count} adapter sources read: the scan measures nothing.");   // WITNESS

        var sites = files.SelectMany(f => Enumerable.Repeat(Path.GetFileName(f),
                                          ConventionCoverageTests.CodeOnly(f).Split("Strings.MsgConnectionLost(").Length - 1))
                         .ToList();
        Assert.Equal(["InferpalToolWindowData.Connection.cs", "InferpalToolWindowData.Connection.cs"], sites);

        var connection = ConventionCoverageTests.CodeOnly(Path.Combine(root, "ToolWindow", "InferpalToolWindowData.Connection.cs"));
        var writer = connection[connection.IndexOf("private string UnreachableText()", StringComparison.Ordinal)..];
        writer = writer[..writer.IndexOf(';')];
        Assert.Contains("Strings.MsgConnectionLost(_client.ServerAddress, InferenceProviderFactory.DisplayName(ServerCode),", writer, StringComparison.Ordinal);

        // The two refusals read it: the send pre-flight and Regenerate.
        foreach (var file in new[] { "InferpalToolWindowData.ChatTurn.cs", "InferpalToolWindowData.ToolInvocation.cs" })
            Assert.Contains("ChatMessageItem.NoticeMsg(UnreachableText())",
                            ConventionCoverageTests.CodeOnly(Path.Combine(root, "ToolWindow", file)), StringComparison.Ordinal);
    }
}
