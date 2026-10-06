using System.IO;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Services;
using Inferpal.Services.Commands;
using Inferpal.Services.Inference;
using Inferpal.Services.Presentation;
using Inferpal.Services.Shell;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A command deadline the settings cannot express, a similarity no cosine reaches, a model path in the support bundle.
/// </summary>
public class SettingsBoundsAndBundleTests
{
    // ── Command deadline ─────────────────────────────────────────────────────

    /// <summary>
    /// ⚠ The VS Code panel declared no bound on the command deadline, and <c>config.json</c> is written by hand too: 0
    /// stopped every command the instant it started (reported as a time-out), a negative value made <c>CancelAfter</c>
    /// throw and the command never ran.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ACommandDeadlineOfZeroOrLess_StillRunsTheCommand(int seconds)
    {
        var session = new ShellSession(() => Path.GetTempPath(), new InferpalConfig { CommandTimeoutSeconds = seconds });

        var output = await session.RunAsync("echo deadline-witness", null, CancellationToken.None);

        Assert.Contains("deadline-witness", output, StringComparison.Ordinal);
        Assert.DoesNotContain("timed out", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheDeadlineReader_KeepsAnOrdinaryValue()
    {
        // Reference arm: an ordinary value is kept; one that cannot be a deadline reads as not set (the factory value).
        Assert.Equal(45,  ChildProcess.CommandDeadlineSeconds(new InferpalConfig { CommandTimeoutSeconds = 45 }));
        Assert.Equal(120, ChildProcess.CommandDeadlineSeconds(new InferpalConfig { CommandTimeoutSeconds = 0 }));
    }

    // ── Similarity threshold ─────────────────────────────────────────────────

    /// <summary>
    /// ⚠ A cosine similarity is at most 1: a threshold above it lets no semantic hit through, and the search falls back to
    /// keywords without a word. The VS Code panel stored any number; the Visual Studio window clamped it in silence.
    /// </summary>
    [Fact]
    public void TheSimilarityThreshold_IsBoundedForBothPanels()
    {
        var field = SettingsSchema.Field("ragSimilarityThreshold");
        Assert.True(field.Accepts(0.35));
        Assert.True(field.Accepts(0.0));
        Assert.False(field.Accepts(1.5));
        Assert.False(field.Accepts(-0.1));
        Assert.False(field.Accepts(double.NaN));

        // The VS Code panel's decimal box reads the bounds like its integer box…
        var panel = WebviewRebuildTests.TsCode("webview/settings.ts");
        var floatCase = panel[panel.IndexOf("case 'float': {", StringComparison.Ordinal)..];
        floatCase = floatCase[..floatCase.IndexOf("break;", StringComparison.Ordinal)];
        Assert.Contains("const value = ok ? parseFloat(raw) : NaN;", floatCase, StringComparison.Ordinal);   // WITNESS
        Assert.Contains("(field.min == null || value >= field.min) && (field.max == null || value <= field.max)",
                        floatCase, StringComparison.Ordinal);

        // …and the Visual Studio window names the value and keeps the saved one instead of clamping it.
        var window = ConventionCoverageTests.CodeOnly(Path.Combine(
            ConversationPersistenceSilenceTests.RepoRoot(), "Inferpal", "ToolWindow", "InferpalSettingsData.cs"));
        Assert.Contains("SettingsSchema.Field(\"ragSimilarityThreshold\").Accepts(rst)", window, StringComparison.Ordinal);
        Assert.DoesNotContain("Math.Clamp(rst", window, StringComparison.Ordinal);
    }

    // ── Support bundle ───────────────────────────────────────────────────────

    /// <summary>
    /// ⚠ llama-server names its model after the file it loaded (<c>-m</c>) unless given an alias: the Models line of the
    /// support bundle — the file pasted into a public issue — carried that path, home directory included, local or on a
    /// remote server that no local scrub knows.
    /// </summary>
    [Fact]
    public void TheSupportBundle_NamesAModelLoadedFromAFile_ByItsFileNameOnly()
    {
        var home   = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var config = new InferpalConfig
        {
            DefaultModel = Path.Combine(home, "models", "Qwen3.8-27B-Q4_K_M.gguf"),
            AgentModel   = "/home/bob/llm/devstral-small-2-Q4.gguf",
            UtilityModel = "qwen/qwen3-coder-30b",
            RagEmbeddingModel = "hf.co/nomic-ai/nomic-embed-text-v1.5-GGUF:Q8_0",
        };

        var bundle = DiagnosticsCommandHandler.Handle(["/diagnostics", "export"],
            new DiagnosticsExportContext(config, "Test front-end")).CopyToClipboard!;

        Assert.Contains("…/Qwen3.8-27B-Q4_K_M.gguf", bundle, StringComparison.Ordinal);
        Assert.Contains("…/devstral-small-2-Q4.gguf", bundle, StringComparison.Ordinal);
        Assert.DoesNotContain(home, bundle, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/home/bob", bundle, StringComparison.Ordinal);
        // Reference arm: a model name that is not a path is kept whole, slashes included.
        Assert.Contains("`qwen/qwen3-coder-30b`", bundle, StringComparison.Ordinal);
        Assert.Contains("`hf.co/nomic-ai/nomic-embed-text-v1.5-GGUF:Q8_0`", bundle, StringComparison.Ordinal);
    }
}

/// <summary>
/// A successful connection check does not end the pause of refused inline completions.
/// </summary>
/// <remarks>Ghost text yields to a chat turn marked busy in the signal folder: these tests use a folder of their own,
/// never the real one (another suite marking a turn busy would skip every completion and prove nothing).</remarks>
[Collection(SignalCollection.Name)]
public sealed class FimBreakerConnectionCheckTests : IDisposable
{
    private readonly SignalScratchDir _signals = new();

    public void Dispose() => _signals.Dispose();


    private static Task CompleteAsync(InferenceProviderBase client) =>
        client.StreamFimAsync("int Add(int a, int b) {\n    ", "\n}", 16, 0.2, _ => { }, CancellationToken.None, "m");

    /// <summary>
    /// ⚠ A successful connection check reset BOTH breakers, and VS Code checks every 30 s: a server that refuses every
    /// completion (a model without an insert slot) answers every check, so its five-minute pause lasted thirty seconds
    /// at most and the refused completions went on. A check proves the server is reachable — not that completions work.
    /// </summary>
    [Fact]
    public async Task Ollama_AConnectionCheck_DoesNotReopenRefusedCompletions()
    {
        using var server = new LoopbackHttpServer(
            path => path.StartsWith("/api/generate", StringComparison.Ordinal) ? "{\"error\":\"model runner has unexpectedly stopped\"}"
                  : path.StartsWith("/api/tags", StringComparison.Ordinal)     ? JsonSerializer.Serialize(new { models = Array.Empty<object>() })
                  : null,
            path => path.StartsWith("/api/generate", StringComparison.Ordinal) ? 500 : 200);
        var client = new OllamaClient(new InferpalConfig { Provider = "ollama", BaseUrl = server.BaseUrl });

        for (var round = 0; round < 4; round++)
        {
            for (var i = 0; i < 5; i++) await CompleteAsync(client);
            Assert.True(await client.CheckConnectionAsync(server.BaseUrl, CancellationToken.None));   // WITNESS: the check succeeds
        }

        Assert.InRange(server.Paths.Count(p => p.StartsWith("/api/generate", StringComparison.Ordinal)), 1, 5);
    }

    [Fact]
    public async Task LmStudio_AConnectionCheck_DoesNotReopenRefusedCompletions()
    {
        using var server = new LoopbackHttpServer(
            path => path.StartsWith("/v1/completions", StringComparison.Ordinal) ? "{\"error\":{\"message\":\"model does not support completions\"}}"
                  : path.StartsWith("/v1/models", StringComparison.Ordinal)      ? JsonSerializer.Serialize(new { data = Array.Empty<object>() })
                  : null,
            path => path.StartsWith("/v1/completions", StringComparison.Ordinal) ? 500 : 200);
        var client = new LmStudioClient(new InferpalConfig { Provider = "lmstudio", BaseUrl = server.BaseUrl });

        for (var round = 0; round < 4; round++)
        {
            for (var i = 0; i < 5; i++) await CompleteAsync(client);
            Assert.True(await client.CheckConnectionAsync(server.BaseUrl, CancellationToken.None));   // WITNESS
        }

        Assert.InRange(server.Paths.Count(p => p.StartsWith("/v1/completions", StringComparison.Ordinal)), 1, 5);
    }

    [Fact]
    public async Task TheManualRetry_StillReopensCompletions()
    {
        // Reference arm: the user's own Retry resets both, as before.
        using var server = new LoopbackHttpServer(
            path => path.StartsWith("/api/generate", StringComparison.Ordinal) ? "{\"error\":\"x\"}" : null, _ => 500);
        var client = new OllamaClient(new InferpalConfig { Provider = "ollama", BaseUrl = server.BaseUrl });

        for (var i = 0; i < 5; i++) await CompleteAsync(client);
        var before = server.Paths.Count;
        client.ResetCircuit();
        await CompleteAsync(client);

        Assert.Equal(before + 1, server.Paths.Count);
    }
}
