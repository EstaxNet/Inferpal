using System.IO;
using System.Text.Json;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A depth the model asked for and did not get.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ <b>Both analysis tools clamped it with a bare <c>Math.Clamp</c>.</b> A call graph requested at
/// depth 7 and rendered at 3 simply stops, and a tree that stops reads as "the graph ends here" —
/// the conclusion every capped scan in that folder declares through <c>ScanCoverage</c>. A clamp is
/// the worse of the two: a scan cap is a limit of the world, a clamp overrides an instruction.
/// </para>
/// <para>
/// ⚠ The <b>lower</b> bound bites the same way: <c>analyze_code mode='impact'</c> raises
/// <c>depth: 0</c> to 1, turning "direct dependants only" into a transitive report, under a
/// question that asked for direct ones.
/// </para>
/// <para>
/// And the sweep that found it came back EMPTY on everything else it looked at — the names and the
/// types declared to the model match what the tools read, across 28 schemas. What it found is this
/// one, plus a structural weakness beside it: the facade's prose quoted the sub-tools' defaults as
/// literals, from two files away. It interpolates them now, the form <c>run_tests</c> already used.
/// </para>
/// </remarks>
public class ClampedDepthTests
{
    private static JsonElement Raw(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>A file the call-graph analyser really finds methods in — otherwise the tool
    /// returns "no method detected" before the report is assembled, and this class would measure
    /// its own fixture.</summary>
    private const string Fixture = """
        namespace T;

        public class A
        {
            public void M()
            {
                N();
            }

            public void N()
            {
            }
        }
        """;

    // ── The decision, in isolation ────────────────────────────────────────────

    [Fact]
    public void AValueAboveTheRange_IsUsedClamped_AndBothNumbersAreNamed()
    {
        var (value, notice) = ClampedArgument.Read(Raw("""{"depth":7}"""), "depth", 2, 1, 3);

        Assert.Equal(3, value);
        Assert.NotNull(notice);
        Assert.Contains("7", notice!, StringComparison.Ordinal);   // what was asked
        Assert.Contains("3", notice!, StringComparison.Ordinal);   // what was used
    }

    [Fact]
    public void AValueBelowTheRange_IsAlsoNamed()
    {
        var (value, notice) = ClampedArgument.Read(Raw("""{"depth":0}"""), "depth", 2, 1, 3);

        Assert.Equal(1, value);
        Assert.NotNull(notice);
    }

    /// <summary>Reference arms: an in-range value and an ABSENT one say nothing. Nothing was asked
    /// for in the second case, and a note on every ordinary call is what gets the real ones
    /// skipped.</summary>
    [Theory]
    [InlineData("""{"depth":2}""")]
    [InlineData("""{}""")]
    public void AnHonouredRequest_SaysNothing(string json)
    {
        var (_, notice) = ClampedArgument.Read(Raw(json), "depth", 2, 1, 3);

        Assert.Null(notice);
    }

    /// <summary>The string-wrapped form a small local model emits is read like any other argument,
    /// and clamping it says so just the same.</summary>
    [Fact]
    public void AStringWrappedValue_IsReadAndReported()
    {
        var (value, notice) = ClampedArgument.Read(Raw("""{"depth":"9"}"""), "depth", 2, 1, 3);

        Assert.Equal(3, value);
        Assert.NotNull(notice);
    }

    // ── And the report really carries it ──────────────────────────────────────

    /// <summary>
    /// The half that makes the decision visible: the notice reaches the model, ABOVE the report it
    /// qualifies. A caveat under a result is read after the result has been believed.
    /// </summary>
    [Fact]
    public async Task AnalyzeCode_WithADepthItCannotHonour_SaysSoAboveTheReport()
    {
        var dir  = Directory.CreateTempSubdirectory("inferpal-depth").FullName;
        var file = Path.Combine(dir, "A.cs");
        await File.WriteAllTextAsync(file, Fixture);

        try
        {
            var tool   = new AnalyzeCodeTool(() => dir);
            var answer = await tool.ExecuteAsync(
                Raw($$"""{"mode":"impact","path":{{JsonSerializer.Serialize(file)}},"depth":9}"""),
                CancellationToken.None);

            Assert.StartsWith("Note: 'depth' was 9", answer, StringComparison.Ordinal);
            Assert.Contains($"depth={AnalyzeImpactTool.MaxAllowedDepth}", answer, StringComparison.Ordinal);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    /// <summary>Reference arm: an ordinary call carries no note at all.</summary>
    [Fact]
    public async Task AnalyzeCode_WithADepthItHonours_SaysNothingAboutIt()
    {
        var dir  = Directory.CreateTempSubdirectory("inferpal-depth").FullName;
        var file = Path.Combine(dir, "A.cs");
        await File.WriteAllTextAsync(file, Fixture);

        try
        {
            var answer = await new AnalyzeCodeTool(() => dir).ExecuteAsync(
                Raw($$"""{"mode":"impact","path":{{JsonSerializer.Serialize(file)}},"depth":2}"""),
                CancellationToken.None);

            Assert.DoesNotContain("Note: 'depth'", answer, StringComparison.Ordinal);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    /// <summary>
    /// The facade STATES these numbers to the model. Interpolated from the constants, the sentence
    /// cannot outlive a change to them — written as literals, it would keep promising the old ones.
    /// </summary>
    [Fact]
    public void TheFacadeStatesTheDefaultsItsSubToolsActuallyUse()
    {
        var schema = JsonSerializer.Serialize(new AnalyzeCodeTool(() => null).Parameters);

        Assert.Contains($"default {TraceDependencyTool.DefaultDepth};", schema, StringComparison.Ordinal);
        Assert.Contains($"default {AnalyzeImpactTool.DefaultDepth})", schema, StringComparison.Ordinal);
        Assert.Contains($"{AnalyzeImpactTool.MinAllowedDepth}-{AnalyzeImpactTool.MaxAllowedDepth}",
                        schema, StringComparison.Ordinal);
    }
}
