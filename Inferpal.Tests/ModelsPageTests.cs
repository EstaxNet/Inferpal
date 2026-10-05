using System.IO;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services.Inference;
using Inferpal.Services.Presentation;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The models page: "Suggest the best models" picks among the INSTALLED models from what Inferpal measured, fills the
/// form and saves nothing; "Loaded now" says what the server holds in memory and what each model is used for; the
/// unload buttons say what the server really did.
/// </summary>
[Collection(CultureSerialCollection.Name)]
public class ModelAdvisorTests
{
    private const long Gb = 1L << 30;
    private static InstalledModelInfo M(string name, double gb = 0) => new(name, (long)(gb * Gb));
    private static readonly Dictionary<string, string> Empty = [];

    private static ModelSuggestion Suggest(IReadOnlyList<InstalledModelInfo> installed, double budget = 0,
                                           Dictionary<string, string>? current = null) =>
        ModelAdvisor.Suggest(installed, budget, current ?? Empty, "nothing listed");

    private static ModelSuggestionField Field(ModelSuggestion s, string key) => s.Fields.Single(f => f.Key == key);

    [Fact]
    public void TheChatModel_IsTheMeasuredAgent_NotTheFirstListed()
    {
        var s = Suggest([M("llama3.2:3b", 2), M("gemma4:12b", 7), M("qwen3.8:27b", 17)]);

        var chat = Field(s, "defaultModel");
        Assert.Equal("qwen3.8:27b", chat.Value);
        Assert.Equal(Strings.SuggestChatRecommended("qwen3.8:27b"), chat.Reason);
        Assert.True(chat.Changed);
        Assert.DoesNotContain(s.Notes, n => n.Contains("Gemma 4 12B", StringComparison.Ordinal));   // nothing to install
    }

    [Fact]
    public void WithoutARecommendedAgent_TheModelsMeasuredForTheCardAreNamed()
    {
        var s = Suggest([M("llama3.1:8b", 5)], budget: 16);

        Assert.Equal(Strings.SuggestChatUnmeasured("llama3.1:8b"), Field(s, "defaultModel").Reason);
        // A 16 GB card: the 12 GB setups, never the 24 GB one.
        var note = Assert.Single(s.Notes, n => n.Contains("Gemma 4 12B", StringComparison.Ordinal));
        Assert.DoesNotContain("Qwen3.8", note, StringComparison.Ordinal);
    }

    [Fact]
    public void TheMeasuredAutocompleteModel_IsProposed_WhenItFitsBesideTheOthers()
    {
        var installed = new[] { M("gemma4:12b", 7), M("mellum2:12b", 7.5), M("embeddinggemma:300m", 0.6) };

        Assert.Equal("mellum2:12b", Field(Suggest(installed, budget: 24), "inlineCompletionModel").Value);

        // A 12 GB card: the two cannot stay loaded together — the chat model completes, and the sentence says why.
        var small = Field(Suggest(installed, budget: 12), "inlineCompletionModel");
        Assert.Equal("", small.Value);
        Assert.StartsWith(Strings.SuggestFimDoesNotFit("mellum2:12b", "x", "y")[..20], small.Reason, StringComparison.Ordinal);
        Assert.Contains("12", small.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AChatModelThatAutocompletesWell_KeepsTheJob_OnAnUnknownCard()
    {
        // Unknown card: no second model beside Qwen3.8, which completes 27 in 36 itself.
        var s = Suggest([M("qwen3.8:27b", 17), M("mellum2:12b", 7.5)]);
        Assert.Equal(("", Strings.SuggestFimChatCompletes),
                     (Field(s, "inlineCompletionModel").Value, Field(s, "inlineCompletionModel").Reason));

        // REFERENCE ARM: a chat model that does not complete well gets the completion model, card unknown or not.
        Assert.Equal("mellum2:12b", Field(Suggest([M("gemma4:12b", 7), M("mellum2:12b", 7.5)]), "inlineCompletionModel").Value);
    }

    [Fact]
    public void CodeSearch_StaysAutomatic_AndAChoiceThatIsAlreadyTheBestIsNotRewritten()
    {
        var installed = new[] { M("gemma4:12b", 7), M("nomic-embed-text", 0.3), M("embeddinggemma:300m", 0.6) };

        var automatic = Field(Suggest(installed), "ragEmbeddingModel");
        Assert.Equal(("", false), (automatic.Value, automatic.Changed));
        Assert.Equal(Strings.SuggestEmbeddingAuto("embeddinggemma:300m"), automatic.Reason);

        var best = Field(Suggest(installed, current: new() { ["ragEmbeddingModel"] = "embeddinggemma:300m" }), "ragEmbeddingModel");
        Assert.Equal(("embeddinggemma:300m", false), (best.Value, best.Changed));

        // Another model set by hand: back to automatic, and the rebuild it costs is said.
        var other = Suggest(installed, current: new() { ["ragEmbeddingModel"] = "nomic-embed-text" });
        Assert.Equal(("", true), (Field(other, "ragEmbeddingModel").Value, Field(other, "ragEmbeddingModel").Changed));
        Assert.Contains(Strings.SuggestEmbeddingReindex, other.Notes);
    }

    [Fact]
    public void TheAdvancedSettings_AreNamed_NeverWritten()
    {
        var s = Suggest([M("qwen3.8:27b", 17), M("llama3.2:3b", 2)],
                        current: new() { ["agentModel"] = "llama3.2:3b", ["utilityModel"] = "llama3.2:3b" });

        Assert.Equal(ModelAdvisor.Keys, s.Fields.Select(f => f.Key));   // the three visible fields, nothing else
        var note = Assert.Single(s.Notes, n => n.Contains("llama3.2:3b", StringComparison.Ordinal));
        Assert.Contains(Strings.ModelRoleAgent, note, StringComparison.Ordinal);
        Assert.Contains(Strings.ModelRoleUtility, note, StringComparison.Ordinal);
    }

    [Fact]
    public void WhatTheFormAlreadyHolds_IsNoChange()
    {
        var installed = new[] { M("qwen3.8:27b", 17), M("embeddinggemma:300m", 0.6) };
        var current = new Dictionary<string, string>
        {
            ["defaultModel"] = "qwen3.8:27b", ["inlineCompletionModel"] = "", ["ragEmbeddingModel"] = "",
        };

        var s = Suggest(installed, current: current);
        Assert.DoesNotContain(s.Fields, f => f.Changed);
        Assert.Equal(Strings.SuggestNoChange, s.Summary);

        // REFERENCE ARM: another chat model in the form is a change, and the summary asks for a review.
        current["defaultModel"] = "llama3.2:3b";
        Assert.Equal(Strings.SuggestReview, Suggest(installed, current: current).Summary);
    }

    [Fact]
    public void NothingToSuggestFrom_IsSaid()
    {
        Assert.Equal("nothing listed", Suggest([]).Summary);
        Assert.Equal(Strings.SuggestOnlyEmbedding, Suggest([M("embeddinggemma:300m")]).Summary);
    }

    [Fact]
    public void TheRecommendedAgents_AreTheOnesTheCardTableOfTheDocsNames()
    {
        // The note quotes docs/models.md: a name it gives that the page no longer recommends would send the user to
        // download the wrong model.
        var docs  = File.ReadAllText(Path.Combine(ConversationPersistenceSilenceTests.RepoRoot(), "docs", "models.md"));
        var table = docs[docs.IndexOf("## By graphics card", StringComparison.Ordinal)..];
        table = table[..table.IndexOf("\n## ", 5, StringComparison.Ordinal)];
        foreach (var (name, card) in ModelAdvisor.RecommendedAgents)
        {
            var row = table.Split('\n').Single(l => l.StartsWith($"| **{card} GB**", StringComparison.Ordinal));
            Assert.True(row.Contains(name, StringComparison.Ordinal), $"{name} is not recommended for {card} GB by the docs.");
        }
    }
}

[Collection(CultureSerialCollection.Name)]
public class LoadedModelsCardTests
{
    private const long Gb = 1L << 30;
    private static readonly DateTime Now = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);

    private static LoadedModelsModel Build(IReadOnlyList<RunningModelInfo> running, InferpalConfig? config = null,
                                           string? embedding = null, Dictionary<string, long>? disk = null,
                                           Dictionary<string, int?>? ctx = null) =>
        LoadedModelsCard.Build(running, canUnload: true, config ?? new InferpalConfig { DefaultModel = "qwen3.8:27b" },
                               embedding, disk ?? [], ctx ?? [], Now);

    [Fact]
    public void EachModel_SaysWhatInferpalUsesItFor()
    {
        var config = new InferpalConfig
        {
            DefaultModel = "qwen3.8:27b", InlineCompletionModel = "mellum2:12b", InlineCompletionEnabled = true,
        };
        var card = Build([new("qwen3.8:27b", 17 * Gb, ""), new("mellum2:12b", 7 * Gb, ""),
                          new("embeddinggemma:300m", Gb / 2, ""), new("llama3.2:3b", 2 * Gb, "")],
                         config, embedding: "embeddinggemma:300m");

        Assert.Contains(Strings.ModelRoleChat, card.Rows[0].Uses, StringComparison.Ordinal);
        Assert.Contains(Strings.ModelRoleAgent, card.Rows[0].Uses, StringComparison.Ordinal);
        Assert.DoesNotContain(Strings.ModelRoleAutocomplete, card.Rows[0].Uses, StringComparison.Ordinal);
        Assert.Equal(Strings.LoadedModelUses(Strings.ModelRoleAutocomplete), card.Rows[1].Uses);
        Assert.Equal(Strings.LoadedModelUses(Strings.ModelRoleCodeSearch), card.Rows[2].Uses);
        Assert.Equal(Strings.LoadedModelUnused, card.Rows[3].Uses);
    }

    [Fact]
    public void Memory_IsTheServersFigure_AndAnUnreportedOneIsNeverZero()
    {
        var card = Build([new("a", 3 * Gb, ""), new("b", 0, ""), new("c", RunningModelInfo.VramNotReported, ""),
                          new("d", RunningModelInfo.VramNotReported, "")],
                         disk: new() { ["c"] = 5 * Gb });

        Assert.Contains(Strings.LoadedModelVram((3.0).ToString("0.0")), card.Rows[0].Details, StringComparison.Ordinal);
        Assert.Contains(Strings.LoadedModelCpu, card.Rows[1].Details, StringComparison.Ordinal);
        Assert.Contains(Strings.LoadedModelDiskSize((5.0).ToString("0.0")), card.Rows[2].Details, StringComparison.Ordinal);
        Assert.Equal("", card.Rows[3].Details);
        // The total is given only when every model reports its own: a partial sum would read as the whole.
        Assert.Equal(Strings.LoadedModelsCount(4), card.Summary);
    }

    [Fact]
    public void TheTotal_IsGiven_WhenEveryModelReportsIt()
    {
        var card = Build([new("a", 3 * Gb, ""), new("b", Gb, "")]);
        Assert.Equal(Strings.LoadedModelsCount(2) + " · " + Strings.LoadedModelVram((4.0).ToString("0.0")), card.Summary);
    }

    [Fact]
    public void TheContextAndTheUnloadTime_AreShownWhenTheServerSaysThem()
    {
        var card = Build([new("a", Gb, Now.AddMinutes(4.2).ToString("O"))], ctx: new() { ["a"] = 32_768 });

        Assert.Contains(Strings.LoadedModelContext(32_768.ToString("N0")), card.Rows[0].Details, StringComparison.Ordinal);
        Assert.Contains(Strings.LoadedModelUnloadsIn(5), card.Rows[0].Details, StringComparison.Ordinal);
        Assert.Equal(Strings.LoadedModelStays, LoadedModelsCard.Expiry("2318-01-01T00:00:00Z", Now));
        Assert.Equal("", LoadedModelsCard.Expiry("", Now));
    }

    [Fact]
    public async Task UnknownIsNotEmpty()
    {
        var generic = new FakeInferenceProvider { Capabilities = ProviderCapabilities.OpenAiCompatible };
        Assert.Equal(Strings.LoadedModelsUnknown,
                     (await LoadedModelsCard.ReadAsync(generic, new InferpalConfig(), null, CancellationToken.None)).Summary);

        var down = new FakeInferenceProvider { ConnectionOk = false };
        Assert.Equal(Strings.LoadedModelsUnreachable,
                     (await LoadedModelsCard.ReadAsync(down, new InferpalConfig(), null, CancellationToken.None)).Summary);

        // REFERENCE ARM: a server that answers with nothing loaded says just that.
        var idle = new FakeInferenceProvider();
        Assert.Equal(Strings.LoadedModelsNone,
                     (await LoadedModelsCard.ReadAsync(idle, new InferpalConfig(), null, CancellationToken.None)).Summary);
    }
}

[Collection(CultureSerialCollection.Name)]
public class ModelUnloaderTests
{
    private static FakeInferenceProvider Server(params string[] loaded) => new()
    {
        Running = [.. loaded.Select(n => new RunningModelInfo(n, 1L << 30, ""))],
        OnUnload = _ => true,
    };

    private static Task<string> Unload(FakeInferenceProvider s, IReadOnlyList<string>? only = null, bool answering = false) =>
        ModelUnloader.UnloadAsync(s, new InferpalConfig(), only, CancellationToken.None, () => answering);

    // ⚠ An EMPTY list is also what a server that did not answer gives back: read as "nothing loaded" before the
    // unload, and as "unloaded" after it, about a model the card had just shown and that may still be in memory.
    [Fact]
    public async Task AListingThatFailsBeforeTheUnload_SaysTheServerIsUnreachable_NotThatNothingWasLoaded()
    {
        var s = Server();
        s.ConnectionOk = false;

        Assert.Equal(Strings.MsgUnreachable(new InferpalConfig().BaseUrl), await Unload(s));
    }

    [Fact]
    public async Task AListingThatFailsAfterTheUnload_SaysTheResultIsUnknown_NotThatItWasUnloaded()
    {
        var s = Server("a");
        s.OnUnload = _ => { s.Running.Clear(); s.ConnectionOk = false; return false; };   // the server went silent

        Assert.Equal(Strings.UnloadUnverified("a"), await Unload(s));
    }

    [Fact]
    public async Task AnEmptyListFromAServerThatAnswers_IsStillNothingLoaded()   // reference arm
    {
        Assert.Equal(Strings.UnloadNothing, await Unload(Server()));
    }

    [Fact]
    public async Task UnloadAll_UnloadsEveryLoadedModel_AndSaysWhich()
    {
        var s = Server("a", "b");
        Assert.Equal(Strings.UnloadDone("a, b"), await Unload(s));
        Assert.Equal(["a", "b"], s.Unloaded);
        Assert.Empty(s.Running);
    }

    [Fact]
    public async Task AModelTheServerKeeps_IsNamed_NeverCountedUnloaded()
    {
        var s = Server("a", "b");
        s.OnUnload = n => n == "a";
        Assert.Equal(Strings.UnloadDone("a") + " " + Strings.UnloadKept("b"), await Unload(s));
    }

    [Fact]
    public async Task OneModel_UnloadsThatModelOnly()
    {
        var s = Server("a", "b");
        await Unload(s, ["b"]);
        Assert.Equal(["b"], s.Unloaded);
    }

    [Fact]
    public async Task NothingIsPulledFromUnderAnAnswer()
    {
        var s = Server("a");
        Assert.Equal(Strings.UnloadWhileAnswering, await Unload(s, answering: true));
        Assert.Empty(s.Unloaded);
    }

    [Fact]
    public async Task AServerWithoutUnload_IsSaid_AndNothingLoaded_Too()
    {
        var generic = Server("a");
        generic.Capabilities = ProviderCapabilities.OpenAiCompatible;
        Assert.Equal(Strings.UnloadNotSupported, await Unload(generic));
        Assert.Equal(Strings.UnloadNothing, await Unload(Server()));
    }
}

public partial class HostServerTests
{
    [Fact]
    public async Task LoadedModels_SayWhatEachModelIsUsedFor()
    {
        using var h = CreateHarness(cfg => cfg.DefaultModel = "qwen3.8:27b");
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        h.Fake.Running = [new("qwen3.8:27b", 17L << 30, "")];

        var card = await h.Client.InvokeAsync<LoadedModelsModel>("settings/loadedModels")
            .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        var row = Assert.Single(card.Rows);
        Assert.Equal("qwen3.8:27b", row.Name);
        Assert.Contains(Strings.ModelRoleChat, row.Uses, StringComparison.Ordinal);
        Assert.True(card.CanUnload);
    }

    [Fact]
    public async Task Unload_OnAServerThatCannot_SaysSo_AndRereadsTheList()
    {
        using var h = CreateHarness();
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        h.Fake.Capabilities = ProviderCapabilities.OpenAiCompatible;

        var card = await h.Client.InvokeWithParameterObjectAsync<LoadedModelsModel>("settings/unloadModels", new { names = (string[]?)null })
            .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        Assert.Equal(Strings.UnloadNotSupported, card.Message);
        Assert.Equal(Strings.LoadedModelsUnknown, card.Summary);
        Assert.Empty(h.Fake.Unloaded);
    }

    [Fact]
    public async Task Suggest_AsksTheServerTheFormNames_AndProposesTheMeasuredModels()
    {
        using var h = CreateHarness();
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        h.Fake.Installed = [new("llama3.2:3b", 2L << 30), new("qwen3.8:27b", 17L << 30), new("embeddinggemma:300m", 1L << 29)];

        var s = await h.Client.InvokeWithParameterObjectAsync<ModelSuggestion>("settings/suggestModels", new
            {
                baseUrl = "http://typed-but-not-saved:1234", provider = "lmstudio",
                current = new Dictionary<string, string> { ["defaultModel"] = "llama3.2:3b" },
            })
            .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        var chat = s.Fields.Single(f => f.Key == "defaultModel");
        Assert.Equal(("qwen3.8:27b", true), (chat.Value, chat.Changed));
        Assert.Equal(Strings.SuggestReview, s.Summary);
        // The form's server, not the saved one: the provider was built from what the panel sent.
        Assert.Contains(h.ProviderConfigs, c => c.BaseUrl == "http://typed-but-not-saved:1234" && c.Provider == "lmstudio");
    }
}
