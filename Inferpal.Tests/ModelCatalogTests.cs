using System.Collections.Generic;
using System.Text.Json;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

// Covers the shared model-name classification (embedding keyword filter, previously
// duplicated between the two VMs with diverging lists) and the first-run code-first
// default-model choice.
public class ModelCatalogTests
{
    // ── IsEmbeddingModel ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("nomic-embed-text")]
    [InlineData("mxbai-embed-large:latest")]
    [InlineData("BGE-m3")]                  // case-insensitive
    [InlineData("all-minilm")]
    [InlineData("multilingual-e5-large")]
    public void IsEmbeddingModel_True_ForEmbeddingFamilies(string name) =>
        Assert.True(ModelCatalog.IsEmbeddingModel(name));

    [Theory]
    [InlineData("qwen2.5-coder:14b")]
    [InlineData("llama3.1:8b")]
    [InlineData("mistral-nemo")]
    [InlineData("devstral:24b")]
    public void IsEmbeddingModel_False_ForChatModels(string name) =>
        Assert.False(ModelCatalog.IsEmbeddingModel(name));

    // ── PickBestChatModel ──────────────────────────────────────────────────────

    [Fact]
    public void PickBestChatModel_PrefersCodeModels_OverGeneralOnes() =>
        Assert.Equal("qwen2.5-coder:14b", ModelCatalog.PickBestChatModel(
            ["llama3.1:8b", "qwen2.5-coder:14b", "mistral:7b"]));

    [Fact]
    public void PickBestChatModel_MatchesTagSuffixesCaseInsensitively() =>
        Assert.Equal("CodeLlama:13b-instruct",
            ModelCatalog.PickBestChatModel(["CodeLlama:13b-instruct"]));

    [Fact]
    public void PickBestChatModel_RespectsPriorityOrder_NotListOrder() =>
        // mistral appears first in the list but llama3.1 ranks higher in the priority table.
        Assert.Equal("llama3.1:8b", ModelCatalog.PickBestChatModel(
            ["mistral:7b", "llama3.1:8b"]));

    [Fact]
    public void PickBestChatModel_FallsBackToFirstAvailable_WhenNothingMatches() =>
        Assert.Equal("some-exotic-model", ModelCatalog.PickBestChatModel(
            ["some-exotic-model", "another-unknown"]));

    // ── /models markdown tables ────────────────────────────────────────────────

    [Fact]
    public void FormatRunningModels_TableWithVramInMb()
    {
        var table = ModelCatalog.FormatRunningModels(
            [new RunningModelInfo("qwen3:8b", 512L * 1024 * 1024, "2026-01-01T00:00:00Z")]);
        Assert.Contains(Strings.ModelsRunningHeader, table);
        Assert.Contains("| `qwen3:8b` | 512 MB |", table);
    }

    [Fact]
    public void FormatInstalledModels_MarksLoadedModels_AndShowsHints()
    {
        var table = ModelCatalog.FormatInstalledModels(
            ["qwen3:8b", "mistral:7b"],
            [new RunningModelInfo("qwen3:8b", 512L * 1024 * 1024, "2026-01-01T00:00:00Z")]);
        Assert.Contains("| `qwen3:8b` | 512 MB 🟢 |", table);
        Assert.Contains("| `mistral:7b` | — |", table);
        Assert.Contains("`/models pull <name>`", table);
    }

    // ── VRAM estimation (hardware-aware) ───────────────────────────────────────

    const long Gb = 1_073_741_824L;

    [Fact]
    public void EstimateVramBytes_AppliesOverheadFactor() =>
        // 5 GB on disk → 6 GB estimated (×1.2).
        Assert.Equal((long)(5 * Gb * 1.2), ModelCatalog.EstimateVramBytes(5 * Gb));

    [Fact]
    public void EstimateVramBytes_ZeroOrNegativeDisk_IsZero()
    {
        Assert.Equal(0, ModelCatalog.EstimateVramBytes(0));
        Assert.Equal(0, ModelCatalog.EstimateVramBytes(-1));
    }

    [Fact]
    public void TrioFitsBudget_True_WhenSumWithinBudget()
    {
        // 5 + 0.3 GB disk → ~6.4 GB estimated, fits a 24 GB budget.
        var fits = ModelCatalog.TrioFitsBudget(24, [5 * Gb, 300 * 1024 * 1024L], out var needed);
        Assert.True(fits);
        Assert.True(needed < 24);
    }

    [Fact]
    public void TrioFitsBudget_False_WhenSumExceedsBudget()
    {
        // 10 + 10 GB disk → ~24 GB estimated, over an 8 GB budget.
        var fits = ModelCatalog.TrioFitsBudget(8, [10 * Gb, 10 * Gb], out var needed);
        Assert.False(fits);
        Assert.True(needed > 8);
    }

    [Fact]
    public void TrioFitsBudget_AlwaysTrue_WhenBudgetUnknown() =>
        // Budget <= 0 means "unknown" — never warn without data.
        Assert.True(ModelCatalog.TrioFitsBudget(0, [100 * Gb], out _));

    [Fact]
    public void SingleModelExceeds_RespectsKnownBudgetOnly()
    {
        Assert.True(ModelCatalog.SingleModelExceeds(8, 10 * Gb));   // 12 GB est. > 8
        Assert.False(ModelCatalog.SingleModelExceeds(24, 10 * Gb)); // 12 GB est. < 24
        Assert.False(ModelCatalog.SingleModelExceeds(0, 100 * Gb)); // unknown budget
    }

    // ── num_ctx VRAM bound ─────────────────────────────────────────────────────

    static Dictionary<string, JsonElement> Info(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    // Llama-3.1-8B-style metadata.
    const string LlamaInfo = """
        {
          "general.architecture": "llama",
          "llama.block_count": 32,
          "llama.attention.head_count": 32,
          "llama.attention.head_count_kv": 8,
          "llama.embedding_length": 4096,
          "llama.context_length": 131072
        }
        """;

    [Fact]
    public void ParseArch_ReadsArchitecturePrefixedKeys()
    {
        var arch = ModelCatalog.ParseArch(Info(LlamaInfo));
        Assert.NotNull(arch);
        Assert.Equal(32, arch!.BlockCount);
        Assert.Equal(8, arch.HeadCountKv);
        Assert.Equal(4096, arch.EmbeddingLength);
        Assert.Equal(131072, arch.ContextLength);
    }

    [Fact]
    public void ParseArch_NoGqa_FallsBackHeadCountKvToHeadCount()
    {
        var arch = ModelCatalog.ParseArch(Info("""
            {"general.architecture":"x","x.block_count":24,"x.attention.head_count":16,"x.embedding_length":2048}
            """));
        Assert.Equal(16, arch!.HeadCountKv);   // missing head_count_kv → equals head_count
    }

    [Fact]
    public void ParseArch_MissingEssentialFields_ReturnsNull() =>
        Assert.Null(ModelCatalog.ParseArch(Info("""{"general.architecture":"x"}""")));

    [Fact]
    public void ParseArch_ArrayValuedCount_TakesMax()
    {
        var arch = ModelCatalog.ParseArch(Info("""
            {"general.architecture":"x","x.block_count":[8,12,10],"x.attention.head_count":16,"x.embedding_length":2048}
            """));
        Assert.Equal(12, arch!.BlockCount);
    }

    [Fact]
    public void KvCacheBytesPerToken_Llama8B_Is128KiB()
    {
        // head_dim = 4096/32 = 128 → 2 × 32 × 8 × 128 × 2 bytes = 131072.
        var arch = ModelCatalog.ParseArch(Info(LlamaInfo))!;
        Assert.Equal(131072, ModelCatalog.KvCacheBytesPerToken(arch));
    }

    [Fact]
    public void MaxSafeNumCtx_CapsAtModelContextLength_WhenVramIsAmple()
    {
        // 24 GB budget, ~5 GB weights → tons of headroom, so capped by the 131072 trained ctx.
        var arch = ModelCatalog.ParseArch(Info(LlamaInfo))!;
        Assert.Equal(131072, ModelCatalog.MaxSafeNumCtx(24, 5 * Gb, arch));
    }

    [Fact]
    public void MaxSafeNumCtx_BoundedByVram_WhenTight()
    {
        // 6 GB budget, 5 GB weights → ~1 GB for KV → ~8192 tokens (1 GB / 128 KiB ≈ 8192), 1024-floored.
        var arch  = ModelCatalog.ParseArch(Info(LlamaInfo))!;
        var maxCtx = ModelCatalog.MaxSafeNumCtx(6, 5 * Gb, arch);
        Assert.True(maxCtx is > 0 and < 131072);
        Assert.Equal(0, maxCtx % 1024);   // floored to a clean step
    }

    [Fact]
    public void MaxSafeNumCtx_WeightsExceedBudget_OrUnknown_ReturnsZero()
    {
        var arch = ModelCatalog.ParseArch(Info(LlamaInfo))!;
        Assert.Equal(0, ModelCatalog.MaxSafeNumCtx(4, 5 * Gb, arch)); // weights > budget
        Assert.Equal(0, ModelCatalog.MaxSafeNumCtx(0, 5 * Gb, arch)); // unknown budget
    }
}
