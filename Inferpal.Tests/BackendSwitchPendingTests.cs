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

    [Theory]
    [InlineData("InferpalToolWindowData.Connection.cs")]   // the heartbeat
    [InlineData("InferpalToolWindowData.ChatTurn.cs")]     // the send pre-flight
    [InlineData("InferpalToolWindowData.Rag.cs")]          // /setup and the first run
    public void EveryVisualStudioConnectionMessage_ChecksForAPendingSwitch(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md"))) dir = dir.Parent;
        var path = Path.Combine(dir!.FullName, "Inferpal", "ToolWindow", file);
        Assert.True(File.Exists(path), $"{file} is gone — this guard checks nothing any more.");

        Assert.Contains("InferenceProviderFactory.PendingSwitch(_client, _config.Provider)",
                        ConventionCoverageTests.CodeOnly(path));
    }
}
