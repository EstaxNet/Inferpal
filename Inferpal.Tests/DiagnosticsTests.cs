using System.IO;
using System.Linq;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

// Serialized: Diagnostics holds static state (ring buffer + flags).
[CollectionDefinition("Diagnostics", DisableParallelization = true)]
public class DiagnosticsCollection { }

[Collection("Diagnostics")]
public class DiagnosticsTests : IDisposable
{
    public DiagnosticsTests() => Reset();

    public void Dispose() => Reset();

    private static void Reset()
    {
        Diagnostics.Clear();
        Diagnostics.FileLoggingEnabled = false;
        Diagnostics.LogPathOverride = null;
    }

    /// <summary>
    /// An unknown backend code falls back to Ollama — but it <b>says so</b> now.
    /// </summary>
    /// <remarks>
    /// The fallback is the right behaviour (a config predating multi-backend support has no
    /// <c>provider</c>), but it was completely silent: the product then talked to a different
    /// backend than the one you believed you had chosen. It cost a false measurement to the VS
    /// Code front-end review, which had written "openai" instead of "openai-compatible".
    /// </remarks>
    [Theory]
    [InlineData("openai")]          // the exact typo that spoiled a measurement
    [InlineData("lm-studio")]       // not a code: the real one is "lmstudio", no dash
    public void UnknownProviderCode_FallsBackToOllama_AndSaysSo(string code)
    {
        var provider = Services.Inference.InferenceProviderFactory.Create(
            new Config.InferpalConfig { Provider = code });

        Assert.IsType<Services.Inference.OllamaClient>(provider);
        var entry = Assert.Single(Diagnostics.Snapshot());
        Assert.Contains(code.Trim().ToLowerInvariant(), entry.Context, StringComparison.Ordinal);
    }

    /// <summary>The three valid codes — and the absence of a code — trace nothing.</summary>
    [Theory]
    [InlineData("ollama")]
    [InlineData("lmstudio")]
    [InlineData("openai-compatible")]
    [InlineData("")]
    [InlineData(null)]
    public void KnownProviderCode_TracesNothing(string? code)
    {
        Services.Inference.InferenceProviderFactory.Create(new Config.InferpalConfig { Provider = code! });
        Assert.Empty(Diagnostics.Snapshot());
    }

    [Fact]
    public void Swallow_RecordsContextAndExceptionDetail()
    {
        Diagnostics.Swallow("MyContext", new InvalidOperationException("boom"));

        var entry = Assert.Single(Diagnostics.Snapshot());
        Assert.Equal("MyContext", entry.Context);
        Assert.Contains("InvalidOperationException", entry.Detail);
        Assert.Contains("boom", entry.Detail);
    }

    [Fact]
    public void Record_KeepsInsertionOrderOldestFirst()
    {
        Diagnostics.Record("a", "1");
        Diagnostics.Record("b", "2");

        var snap = Diagnostics.Snapshot();
        Assert.Equal(["a", "b"], snap.Select(e => e.Context));
    }

    [Fact]
    public void Ring_IsBoundedToCapacity_DroppingOldest()
    {
        for (var i = 0; i < Diagnostics.Capacity + 50; i++)
            Diagnostics.Record("ctx", i.ToString());

        var snap = Diagnostics.Snapshot();
        Assert.Equal(Diagnostics.Capacity, snap.Count);
        // Oldest 50 dropped → first remaining detail is "50".
        Assert.Equal("50", snap[0].Detail);
        Assert.Equal((Diagnostics.Capacity + 49).ToString(), snap[^1].Detail);
    }

    [Fact]
    public void Clear_EmptiesTheRing()
    {
        Diagnostics.Record("a", "1");
        Diagnostics.Clear();
        Assert.Empty(Diagnostics.Snapshot());
    }

    [Fact]
    public void FileLogging_Off_WritesNothing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"diag_off_{Guid.NewGuid():N}.log");
        Diagnostics.LogPathOverride = path;

        Diagnostics.Record("a", "1");

        Assert.False(File.Exists(path));
    }

    [Fact]
    public void FileLogging_On_AppendsContextAndDetail()
    {
        var path = Path.Combine(Path.GetTempPath(), $"diag_on_{Guid.NewGuid():N}.log");
        Diagnostics.LogPathOverride = path;
        Diagnostics.FileLoggingEnabled = true;
        try
        {
            Diagnostics.Swallow("CtxA", new IOException("disk full"));
            Diagnostics.Record("CtxB", "note");

            var text = File.ReadAllText(path);
            Assert.Contains("[CtxA]", text);
            Assert.Contains("disk full", text);
            Assert.Contains("[CtxB]", text);
            Assert.Equal(2, File.ReadAllLines(path).Length);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
