using System.IO;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Services.Inference;
using Inferpal.Services.Lsp;
using Inferpal.Services.Rag;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ An embedding model is trained with a prompt format of its own. The code index embedded every model with bare
/// text, and an empty field meant one fixed model name, copied eight times across three projects. No embedding model
/// is required: an empty field takes the best one installed, and none installed is a keyword-only search, by choice.
/// </summary>
public sealed class EmbeddingModelsTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "inferpal-tests", $"embfmt-{Guid.NewGuid():N}")).FullName;
    private readonly List<ProjectIndexService> _services = [];

    public EmbeddingModelsTests() => TestRagStore.Redirect();

    public void Dispose()
    {
        foreach (var s in _services) { try { s.Dispose(); } catch { } }
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // ── The reader ──────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("nomic-embed-text", "nomic-embed-text")]
    [InlineData(" qwen3-embedding:0.6b ", "qwen3-embedding:0.6b")]
    public void TheConfiguredModel_OrNothing(string field, string? expected) =>
        Assert.Equal(expected, EmbeddingModels.Configured(new InferpalConfig { RagEmbeddingModel = field }));

    private static Task<string?> Choose(string field, string? previous, params string[] installed) =>
        EmbeddingModels.ChooseAsync(new InferpalConfig { RagEmbeddingModel = field },
                                    new FakeInferenceProvider { ModelNames = [.. installed] }, previous, CancellationToken.None);

    [Fact]
    public async Task AFieldThatIsSet_Wins_WhateverIsInstalled() =>
        Assert.Equal("nomic-embed-text", await Choose("nomic-embed-text", null, "qwen3.8", "embeddinggemma:300m"));

    [Fact]
    public async Task AnEmptyField_TakesTheBestEmbeddingModelInstalled()
    {
        Assert.Equal("embeddinggemma:300m", await Choose("", null, "qwen3.8", "nomic-embed-text", "embeddinggemma:300m"));
        Assert.Equal("nomic-embed-text", await Choose("", null, "qwen3.8", "nomic-embed-text"));
    }

    [Fact]
    public async Task NoEmbeddingModelInstalled_IsNoModel_NotTheChatModel() =>
        Assert.Null(await Choose("", "nomic-embed-text", "qwen3.8", "devstral-small-2"));

    /// <summary>
    /// ⚠ An empty list is a backend that did not answer, not one without an embedding model: the index keeps the
    /// model it had, or an outage at start-up would discard every stored vector.
    /// </summary>
    [Fact]
    public async Task ABackendThatDidNotAnswer_KeepsThePreviousModel()
    {
        Assert.Equal("embeddinggemma:300m", await Choose("", "embeddinggemma:300m"));
        Assert.Null(await Choose("", null));
    }

    [Theory]
    [InlineData("embeddinggemma", "gemma-code")]
    [InlineData("embeddinggemma:300m", "gemma-code")]
    [InlineData("text-embedding-embeddinggemma-300m", "gemma-code")]
    [InlineData("EmbeddingGemma-300M", "gemma-code")]
    [InlineData("qwen3-embedding:0.6b", "qwen3-code")]
    [InlineData("text-embedding-qwen3-embedding-0.6b", "qwen3-code")]
    [InlineData("Qwen3_Embedding-0.6B", "qwen3-code")]
    public void EverySpellingABackendUses_LandsOnItsFamily(string model, string tag) =>
        Assert.Equal(tag, EmbeddingModels.FormatOf(model)?.Tag);

    [Theory]
    [InlineData("nomic-embed-text")]
    [InlineData("text-embedding-nomic-embed-text-v1.5")]
    [InlineData("mxbai-embed-large")]
    [InlineData("qwen3:8b")]      // a chat model of the same family name is not the embedding model
    [InlineData("qwen3.8")]
    public void AModelWithoutADocumentedFormat_IsSentTheBareText(string model)
    {
        Assert.Null(EmbeddingModels.FormatOf(model));
        Assert.Equal("retry delay", EmbeddingModels.CodeQueryText(model, "retry delay"));
        Assert.Equal("int One() => 1;", EmbeddingModels.CodeDocumentText(model, "int One() => 1;"));
        Assert.Equal(model, EmbeddingModels.CodeIndexIdentity(model));
    }

    [Fact]
    public void TheFormats_AreTheCardsOnes_ToTheCharacter()
    {
        Assert.Equal("task: code retrieval | query: retry delay", EmbeddingModels.CodeQueryText("embeddinggemma", "retry delay"));
        Assert.Equal("title: none | text: int One() => 1;", EmbeddingModels.CodeDocumentText("embeddinggemma", "int One() => 1;"));
        Assert.Equal("Instruct: Given a question about a codebase, retrieve the code that answers it\nQuery:retry delay",
                     EmbeddingModels.CodeQueryText("qwen3-embedding:0.6b", "retry delay"));
        Assert.Equal("int One() => 1;", EmbeddingModels.CodeDocumentText("qwen3-embedding:0.6b", "int One() => 1;"));
    }

    [Fact]
    public void OnlyADocumentFormat_ChangesWhatAnIndexRecords()
    {
        Assert.Equal("embeddinggemma#gemma-code", EmbeddingModels.CodeIndexIdentity("embeddinggemma"));
        // A query-only format changes nothing stored: the vectors already on disk stay valid.
        Assert.Equal("qwen3-embedding:0.6b", EmbeddingModels.CodeIndexIdentity("qwen3-embedding:0.6b"));
    }

    [Fact]
    public void ThePreSelectedModel_IsTheDefaultsFamilyWhenInstalled_ElseTheFirstListed()
    {
        Assert.Equal("embeddinggemma:300m", EmbeddingModels.PreferredOf(["nomic-embed-text", "embeddinggemma:300m"]));
        Assert.Equal("text-embedding-embeddinggemma-300m",
                     EmbeddingModels.PreferredOf(["text-embedding-nomic-embed-text-v1.5", "text-embedding-embeddinggemma-300m"]));
        Assert.Equal("nomic-embed-text", EmbeddingModels.PreferredOf(["nomic-embed-text", "mxbai-embed-large"]));
        Assert.Null(EmbeddingModels.PreferredOf([]));
    }

    // ── What the product actually sends ─────────────────────────────────────────────────────

    private static string Class(string name) =>
        $"public class {name}\n{{\n    public int One() => 1;\n    public int Two() => 2;\n}}\n";

    private async Task<ProjectIndexService> IndexedAsync(FakeInferenceProvider provider, InferpalConfig config)
    {
        var index = new ProjectIndexService(provider, config, new LspSemanticProvider());
        _services.Add(index);
        index.StartIndexing(_root);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!index.Status.Contains('✅') && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.Contains("✅", index.Status);                      // witness: the pass ran to its end
        return index;
    }

    private static Task<string> Search(ProjectIndexService index, FakeInferenceProvider provider, InferpalConfig config,
                                       string query) =>
        new SemanticSearchTool(index, provider, config)
            .ExecuteAsync(JsonSerializer.SerializeToElement(new { query }), CancellationToken.None);

    [Fact]
    public async Task WithTheFieldEmpty_EveryChunkAndEveryQuery_GoToTheInstalledEmbeddingGemma_InItsFormat()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "Alpha.cs"), Class("Alpha"));
        var provider = new FakeInferenceProvider { Embedding = [0.1f, 0.2f], ModelNames = ["qwen3.8", "embeddinggemma:300m"] };
        var config   = new InferpalConfig { RagEnabled = true, RagEmbeddingModel = "" };
        var index    = await IndexedAsync(provider, config);

        var indexing = provider.EmbeddingRequests.ToList();
        Assert.NotEmpty(indexing);                                 // witness: the pass embedded something
        Assert.All(indexing, r =>
        {
            Assert.Equal("embeddinggemma:300m", r.Model);
            Assert.StartsWith("title: none | text: ", r.Text);
        });

        provider.EmbeddingRequests.Clear();
        await Search(index, provider, config, "which class returns two");
        Assert.Contains(provider.EmbeddingRequests, r =>
            r.Model == "embeddinggemma:300m" && r.Text == "task: code retrieval | query: which class returns two");
    }

    [Fact]
    public async Task WithNoEmbeddingModelInstalled_NothingIsEmbedded_AndTheSearchSaysKeywordsOnly_NotAFailure()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "Alpha.cs"), Class("Alpha"));
        var provider = new FakeInferenceProvider { Embedding = [0.1f, 0.2f], ModelNames = ["qwen3.8"] };
        var config   = new InferpalConfig { RagEnabled = true, RagEmbeddingModel = "" };
        var index    = await IndexedAsync(provider, config);

        Assert.Null(index.QueryEmbeddingModel);
        Assert.Contains("keyword search only", index.Status);
        Assert.DoesNotContain("/index rebuild", index.Status);     // no remedy for what is a choice
        Assert.Contains("Alpha", await Search(index, provider, config, "Alpha"));   // the keyword half answers

        var none = await Search(index, provider, config, "zzqq nothing like this anywhere");
        Assert.Contains(Localization.Strings.SearchKeywordOnlyNoEmbeddingModel, none);
        Assert.Empty(provider.EmbeddingRequests);                  // never the chat model, never anything
    }

    [Fact]
    public async Task ABackendSilentAtStartUp_KeepsTheStoredVectors_AndTheirModel()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "Alpha.cs"), Class("Alpha"));
        var config = new InferpalConfig { RagEnabled = true, RagEmbeddingModel = "" };
        var first  = new FakeInferenceProvider { Embedding = [0.1f, 0.2f], ModelNames = ["qwen3.8", "embeddinggemma:300m"] };
        (await IndexedAsync(first, config)).Dispose();
        Assert.NotEmpty(first.EmbeddingRequests);                  // witness: the vectors exist

        var silent = new FakeInferenceProvider { Embedding = [0.1f, 0.2f], ModelNames = [] };
        var index  = await IndexedAsync(silent, config);
        Assert.Empty(silent.EmbeddingRequests);                    // nothing discarded, nothing recomputed
        Assert.Equal("embeddinggemma:300m", index.QueryEmbeddingModel);
    }

    [Theory]
    [InlineData("embeddinggemma", true)]         // vectors stored without its document format: not reusable
    [InlineData("qwen3-embedding:0.6b", false)]  // query-only format: the stored vectors are still the right ones
    public async Task AnIndexWrittenBeforeTheFormats_IsReEmbeddedOnlyWhenTheDocumentTextChanged(string model, bool reEmbeds)
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "Alpha.cs"), Class("Alpha"));
        var config = new InferpalConfig { RagEnabled = true, RagEmbeddingModel = model };
        var first  = new FakeInferenceProvider { Embedding = [0.1f, 0.2f] };
        (await IndexedAsync(first, config)).Dispose();
        Assert.NotEmpty(first.EmbeddingRequests);                  // witness: the first pass embedded

        // What the previous version recorded next to its vectors: the bare model name.
        await new RagDatabase(_root).SetMetaAsync("embedding_model", model, CancellationToken.None);

        var second = new FakeInferenceProvider { Embedding = [0.1f, 0.2f] };
        await IndexedAsync(second, config);
        Assert.Equal(reEmbeds, !second.EmbeddingRequests.IsEmpty);
    }

    [Fact]
    public async Task AnIndexWrittenWithTheFormat_IsReused_OnTheNextStart()
    {
        // Reference arm of the test above: without it, "re-embeds" would also pass on an index that re-embeds always.
        await File.WriteAllTextAsync(Path.Combine(_root, "Alpha.cs"), Class("Alpha"));
        var config = new InferpalConfig { RagEnabled = true, RagEmbeddingModel = "embeddinggemma" };
        (await IndexedAsync(new FakeInferenceProvider { Embedding = [0.1f, 0.2f] }, config)).Dispose();

        var second = new FakeInferenceProvider { Embedding = [0.1f, 0.2f] };
        await IndexedAsync(second, config);
        Assert.Empty(second.EmbeddingRequests);
    }
}
