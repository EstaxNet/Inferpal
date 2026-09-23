using System.Collections.Generic;
using System.Linq;
using Inferpal.Config;
using Inferpal.Models;
using Inferpal.Services;
using Inferpal.Services.Hardware;
using Inferpal.Services.Inference;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The VRAM badge was only ever updated <b>on success</b> — so it went on asserting that models
/// occupy the GPU long after that had stopped being true.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ <b>The XML doc promised the opposite of what the code did</b>: <i>"Empty list when Ollama is
/// unreachable or no models are loaded"</i>. In reality a failed poll left <c>_currentModels</c>
/// untouched and raised no event — the window kept its last text.
/// </para>
/// <para>
/// ⚠ <b>And the commonest path is not even a failure</b>: switching the backend to LM Studio or an
/// OpenAI-compatible server turns <c>Capabilities.VramMonitoring</c> off, the service returns
/// <b>before</b> publishing anything, and the header goes on advertising the previous Ollama
/// models. A wrong number, displayed permanently, in a product whose connection badge already names
/// the configured backend (rule 11) precisely so as not to lie about this.
/// </para>
/// <para>
/// ⚠ <b>Empty is not "nothing is loaded" — it is "I cannot say"</b>, and it is the only honest
/// badge: this repository holds that a wrong number costs more than a silence. A transient failure
/// therefore empties the badge, and the next poll fills it again.
/// </para>
/// <para>
/// ⚠ <b>And the trace went through <c>Debug.WriteLine</c></b>, which Release strips: the only
/// explanation for an empty badge did not exist in the published build, and <c>/diagnostics</c> —
/// the channel one opens for exactly this — saw nothing.
/// </para>
/// </remarks>
[Collection("Diagnostics")]   // clears and reads the static ring
public class VramBadgeStalenessTests
{
    private static RunningModelInfo Loaded(string name) =>
        new(name, SizeVram: 6_000_000_000L, ExpiresAt: "2026-09-19T00:00:00Z");

    private static (ModelLifetimeService Service, List<IReadOnlyList<RunningModelInfo>> Seen)
        Build(FakeInferenceProvider client)
    {
        var service = new ModelLifetimeService(client, new InferpalConfig());
        var seen    = new List<IReadOnlyList<RunningModelInfo>>();
        service.ModelsRefreshed += m => seen.Add(m);
        return (service, seen);
    }

    [Fact]
    public async Task AWorkingPoll_PublishesWhatIsLoaded()
    {
        // WITNESS: the nominal path works, otherwise "empty" below would prove nothing.
        var client = new FakeInferenceProvider { Running = [Loaded("qwen3:8b")] };
        var (service, seen) = Build(client);
        using (service)
        {
            await service.RefreshAsync();

            Assert.Single(service.CurrentModels);
            Assert.Equal("qwen3:8b", service.CurrentModels[0].Name);
            Assert.Single(seen);
        }
    }

    [Fact]
    public async Task ABackendThatCannotAnswer_EmptiesTheBadgeInsteadOfKeepingIt()
    {
        var client = new FakeInferenceProvider { Running = [Loaded("qwen3:8b")] };
        var (service, seen) = Build(client);
        using (service)
        {
            await service.RefreshAsync();                       // a true state first
            Assert.Single(service.CurrentModels);

            client.OnRunningModels = () => throw new System.Net.Http.HttpRequestException("down");
            await service.RefreshAsync();

            Assert.Empty(service.CurrentModels);
            // And the subscriber is told: without an event the window keeps its last text.
            Assert.Equal(2, seen.Count);
            Assert.Empty(seen[1]);
        }
    }

    [Fact]
    public async Task ABackendWithoutVramMonitoring_EmptiesTheBadgeToo()
    {
        // The commonest path: the user moves from Ollama to an OpenAI-compatible server.
        var client = new FakeInferenceProvider { Running = [Loaded("qwen3:8b")] };
        var (service, seen) = Build(client);
        using (service)
        {
            await service.RefreshAsync();
            Assert.Single(service.CurrentModels);

            client.Capabilities = ProviderCapabilities.OpenAiCompatible;
            await service.RefreshAsync();

            Assert.Empty(service.CurrentModels);
            Assert.Equal(2, seen.Count);
        }
    }

    [Fact]
    public async Task ATransientFailure_IsFollowedByARealRefresh()
    {
        // Reference arm: emptying is not giving up. The next poll fills the badge again.
        var client = new FakeInferenceProvider { Running = [Loaded("qwen3:8b")] };
        var (service, _) = Build(client);
        using (service)
        {
            client.OnRunningModels = () => throw new System.Net.Http.HttpRequestException("down");
            await service.RefreshAsync();
            Assert.Empty(service.CurrentModels);

            client.OnRunningModels = null;
            await service.RefreshAsync();

            Assert.Single(service.CurrentModels);
        }
    }

    [Fact]
    public async Task TheCauseReachesTheDiagnosticsChannel()
    {
        // ⚠ Debug.WriteLine is stripped in Release: the only explanation for the empty badge did
        // not exist in the published build. /diagnostics is the channel the user opens for it.
        Diagnostics.Clear();
        var client = new FakeInferenceProvider
        {
            Running = [Loaded("qwen3:8b")],
            OnRunningModels = () => throw new System.Net.Http.HttpRequestException("connection refused"),
        };
        var (service, _) = Build(client);
        using (service)
        {
            await service.RefreshAsync();

            Assert.Contains(Diagnostics.Snapshot(),
                e => e.Context.Contains("ModelLifetime", System.StringComparison.OrdinalIgnoreCase));
        }
    }
}
