using System.IO;
using Inferpal.Config;
using Inferpal.Models;
using Inferpal.Services.Agent;
using Inferpal.Services.Hardware;
using Inferpal.Services.Inference;
using Inferpal.ToolWindow;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Visual Studio takes a new kind of model server without a restart: a save while a turn runs leaves that turn on the
/// server it started with, and the next question goes to the new one. Played through the window (<see cref="VsWindowHarness"/>):
/// the turn holds the door, the settings window's save goes through it.
/// </summary>
[Collection(SignalCollection.Name)]
public sealed class ServerSwitchWithoutRestartTests
{
    [Fact]
    public async Task ANewKindSavedDuringATurn_LeavesTheTurnOnItsServer_AndTheNextQuestionGoesToTheNewOne()
    {
        var ollama = new FakeInferenceProvider();
        var lmStudio = new FakeInferenceProvider();
        var config = new InferpalConfig { DefaultModel = "m", Provider = InferenceProviderFactory.Ollama };
        var client = new SwitchingProvider(config, c =>
            InferenceProviderFactory.KindOf(c.Provider) == InferenceProviderFactory.LmStudio ? lmStudio : ollama);
        using var vs = new VsWindowHarness(config: config, client: client);

        IInferenceProvider? insideAtTheEndOfTheTurn = null;
        ollama.OnAgentRun = async (history, _, _, _) =>
        {
            // The settings window saves LM Studio while this turn runs.
            config.Provider = InferenceProviderFactory.LmStudio;
            config.BaseUrl  = "http://localhost:1234";
            InferpalSettingsData.FollowServerAtTheDoor(client);
            await Task.Delay(200);
            insideAtTheEndOfTheTurn = client.Inner;
            return new AgentResult("from Ollama", [], history);
        };
        lmStudio.OnAgentRun = (history, _, _, _) => Task.FromResult(new AgentResult("from LM Studio", [], history));

        await vs.AskAsync("first");
        Assert.Same(ollama, insideAtTheEndOfTheTurn);   // the turn kept its server
        await VsWindowHarness.UntilAsync(() => ReferenceEquals(client.Inner, lmStudio));   // taken once it ended

        await vs.AskAsync("second");
        Assert.Single(ollama.AgentRuns);
        Assert.Single(lmStudio.AgentRuns);
        Assert.Contains(vs.Window.Messages, m => m.Role == "assistant" && m.Content.Contains("from LM Studio", StringComparison.Ordinal));
        Assert.False(TurnDoor.Busy);
    }

    /// <summary>Reference arm: a new kind saved while nothing runs is taken at once, no question needed.</summary>
    [Fact]
    public void ANewKindSavedAtRest_IsTakenAtOnce()
    {
        var ollama = new FakeInferenceProvider();
        var lmStudio = new FakeInferenceProvider();
        var config = new InferpalConfig { Provider = InferenceProviderFactory.Ollama };
        var client = new SwitchingProvider(config, c =>
            InferenceProviderFactory.KindOf(c.Provider) == InferenceProviderFactory.LmStudio ? lmStudio : ollama);

        config.Provider = InferenceProviderFactory.LmStudio;
        InferpalSettingsData.FollowServerAtTheDoor(client);

        Assert.Same(lmStudio, client.Inner);
    }
}
