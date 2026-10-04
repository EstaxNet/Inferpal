using Inferpal.Config;
using Inferpal.Services.Inference;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A profile names a family, and its measured fit was measured on one release of it. On a real LM Studio server the
/// adoption of the default model took <c>qwen3.5-4b</c> — an earlier, small release of the family — over the
/// <c>qwen/qwen3.8-27b</c> the battery measured (and that was loaded), because the server listed it first; the notice
/// told the user it was "the best of the installed models". A base model (<c>qwen3.5-2b-base</c>) is in the same
/// family and could have been taken the same way.
/// </summary>
public class DefaultModelReleaseTests
{
    // The chat models of the test server, as listed: the order is the server's, not a ranking.
    private static readonly string[] ServerModels =
    [
        "qwen3.5-4b", "qwen3.5-2b", "qwen3.5-2b-base", "qwen3.5-0.8b-base", "unsloth/nvidia-nemotron-3.5-lightning-30b-a3b",
        "google/gemma-4-31b-qat", "qwen/qwen3-coder-30b", "meta/muse-glimmer", "lmstudio-community/glm-4.7-flash",
        "qwen/qwen3.8-27b", "mistralai/devstral-small-2-2512", "gemma-4-12b-it-qat", "openai/gpt-oss-20b",
        "ministral-3-8b-base-2512", "meta-llama-3.1-8b-instruct", "prism-ml/bonsai-27b", "qwen2.5-coder-7b-instruct",
        "qwen/qwen3-4b-thinking-2507", "text-embedding-qwen3-embedding-0.6b",
    ];

    [Fact]
    public void OnTheTestServer_TheFreshInstallAdoptsTheMeasuredRelease()
    {
        Assert.Equal("qwen/qwen3.8-27b", ModelCatalog.FirstModelToAdopt(new InferpalConfig(), ServerModels));
        Assert.Equal("qwen/qwen3.8-27b", ModelCatalog.FirstModelToAdopt(new InferpalConfig(), Enumerable.Reverse(ServerModels).ToArray()));
    }

    [Theory]
    [InlineData("qwen3.5-4b", "qwen/qwen3.8-27b")]
    [InlineData("qwen3.6:35b-a3b", "qwen3.8:27b")]
    public void WithinAFamily_TheReleaseItsProfileListsFirst_Wins_WhateverTheListOrder(string older, string measured)
    {
        Assert.Equal(measured, ModelCatalog.PickBestChatModel([older, measured]));
        Assert.Equal(measured, ModelCatalog.PickBestChatModel([measured, older]));
    }

    [Theory]
    [InlineData("qwen3.5-2b-base", "llama3.1:8b")]                 // the base model's family is the best measured one
    [InlineData("ministral-3-8b-base-2512", "gemma-4-12b-it-qat")]
    public void ABaseModel_ComesAfterAChatModel(string baseModel, string chat)
    {
        Assert.Equal(chat, ModelCatalog.PickBestChatModel([baseModel, chat]));
        Assert.Equal(chat, ModelCatalog.PickBestChatModel([chat, baseModel]));
    }

    [Fact]
    public void WhenOnlyBaseModelsAreInstalled_OneIsStillReturned() =>
        Assert.Equal("qwen3.5-2b-base", ModelCatalog.PickBestChatModel(["qwen3.5-2b-base"]));

    [Theory]
    [InlineData("qwen3.5-2b-base", true)]
    [InlineData("ministral-3-8b-base-2512", true)]
    [InlineData("Base_Model:latest", true)]
    [InlineData("database-coder", false)]
    [InlineData("qwen/qwen3.8-27b", false)]
    public void ABaseModel_IsRecognizedByAWordOfItsName(string name, bool isBase) =>
        Assert.Equal(isBase, ModelCatalog.IsBaseModel(name));
}
