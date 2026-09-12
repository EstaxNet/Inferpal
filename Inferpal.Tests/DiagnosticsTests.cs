using System.IO;
using System.Linq;
using Inferpal.Services;
using System;
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

            // ⚠ One line per entry for a NOTE; an exception also carries its whole stack - the
            // file is turned on deliberately and has none of the ring's brevity constraint.
            // Counting the file's lines would amount to forbidding that stack.
            var noteLines = File.ReadAllLines(path).Count(l => l.Contains("[CtxB]"));
            Assert.Equal(1, noteLines);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
    // ── Where the exception was thrown ────────────────────────────────────────────

    [Fact]
    public void ASwallowedException_NamesWhereItWasThrown()
    {
        Diagnostics.Clear();

        try { ThrowsForTheTest(); }
        catch (Exception ex) { Diagnostics.Swallow("Settings.Save", ex); }

        var entry = Assert.Single(Diagnostics.Snapshot());
        // An NRE message names nothing: without the stack, a field report cannot be
        // diagnosed. Method names come from metadata, not from the PDB.
        Assert.Contains("NullReferenceException", entry.Detail);
        Assert.Contains("ThrowsForTheTest", entry.Detail);
    }

    private static void ThrowsForTheTest()
    {
        string? nothing = null;
        _ = nothing!.Length;
    }

    [Fact]
    public void AnExceptionWithNoStack_StillRecordsItsMessage()
    {
        Diagnostics.Clear();

        // Witness: an exception never thrown has no stack, and the line stays useful.
        Diagnostics.Swallow("Ctx", new InvalidOperationException("boom"));

        var entry = Assert.Single(Diagnostics.Snapshot());
        Assert.Equal("InvalidOperationException: boom", entry.Detail);
    }

    [Theory]
    // An async method carries its state machine: the most frequent shape here.
    [InlineData("   at Inferpal.ToolWindow.SettingsData.<SaveCoreAsync>d__12.MoveNext()", "SettingsData.SaveCoreAsync")]
    [InlineData("   at Inferpal.Services.Rag.ProjectIndexService.SaveAsync(CancellationToken ct)", "ProjectIndexService.SaveAsync")]
    [InlineData("   at Foo.Bar.Baz.Qux() in C:\\dev\\x.cs:line 42", "Baz.Qux")]
    [InlineData("", "")]
    public void AFrameIsCompactedToTypeAndMethod(string raw, string expected) =>
        Assert.Equal(expected, Diagnostics.CompactFrame(raw));
    // ── What the trace renders when the stack is twisted ──────────────────────
    //
    // Field measurement (issue #8, 2026-09-12): the SAME failure produced two different
    // lines, and one of them was "InferpalSettingsData. ← InferpalSettingsData. ←
    // --- End of stack trace from previous location ---" - two EMPTY names and a marker taken
    // for a frame. A thermometer that renders that is of no use.

    [Theory]
    // Closure class: the real name is the LAST bracketed group, not the first.
    [InlineData("   at Inferpal.ToolWindow.SettingsData.<>c__DisplayClass89_0.<SaveCoreAsync>b__0()",
                "SettingsData.SaveCoreAsync")]
    // Rethrow marker: not a frame.
    [InlineData("--- End of stack trace from previous location ---", "")]
    [InlineData("   --- End of inner exception stack trace ---", "")]
    public void ACompactedFrame_NeverRendersAnEmptyNameNorAMarker(string raw, string expected) =>
        Assert.Equal(expected, Diagnostics.CompactFrame(raw));

    [Fact]
    public void ThrownInsideALambda_TheTraceStillNamesTheMethod()
    {
        Diagnostics.Clear();

        try { RunLambdaThatThrows(); }
        catch (Exception ex) { Diagnostics.Swallow("Settings.Save", ex); }

        var entry = Assert.Single(Diagnostics.Snapshot());
        Assert.Contains("NullReferenceException", entry.Detail);
        // The owning method name survives even when the throw comes from a lambda.
        Assert.Contains("RunLambdaThatThrows", entry.Detail);
        Assert.DoesNotContain("---", entry.Detail);
    }

    private static void RunLambdaThatThrows()
    {
        Action a = () => { string? nothing = null; _ = nothing!.Length; };
        a();
    }

    [Fact]
    public void TheFileLog_CarriesTheWholeStack_WhereTheRingCannot()
    {
        // The ring ends up pasted into an issue: it stays short. The file is turned on
        // deliberately by the user - that is where a failure three frames cannot locate reads
        // in full.
        var path = Path.Combine(Path.GetTempPath(), "inferpal-diag-" + Guid.NewGuid().ToString("N") + ".log");
        Diagnostics.Clear();
        Diagnostics.LogPathOverride = path;
        Diagnostics.FileLoggingEnabled = true;
        try
        {
            try { RunLambdaThatThrows(); }
            catch (Exception ex) { Diagnostics.Swallow("Settings.Save", ex); }

            var log = File.ReadAllText(path);
            Assert.Contains("RunLambdaThatThrows", log);
            Assert.Contains("at ", log);   // the raw stack, not just the compacted line

            // Witness: a note with no exception writes no stack.
            Diagnostics.Record("Ctx", "just a note");
            var after = File.ReadAllText(path);
            Assert.Contains("just a note", after);
        }
        finally
        {
            Diagnostics.FileLoggingEnabled = false;
            Diagnostics.LogPathOverride = null;
            try { File.Delete(path); } catch { }
        }
    }
}
