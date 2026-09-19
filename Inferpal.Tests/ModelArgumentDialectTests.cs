using System.IO;
using System.Text.Json;
using Inferpal.Localization;
using Inferpal.Services.Execution;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// An argument written by the MODEL is read with the same tolerance everywhere, and what cannot be
/// read is <b>named</b> rather than defaulted in silence.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ <see cref="ToolArgs"/> exists so that <c>"top_k": "5"</c> and <c>"refresh": "true"</c> — the
/// string-wrapped JSON a small local model emits constantly — mean what they say. Four tools read
/// an argument through <c>JsonElement.TryGetProperty</c> instead: a second dialect, accepting
/// strictly less and saying nothing about what it dropped. <c>generate_project_map</c> answered a
/// <c>"refresh": "true"</c> with the CACHED map — the model asks for a fresh scan after creating
/// files, gets the old picture, and concludes its files are not there.
/// </para>
/// <para>
/// ⚠ And <c>apply_edits</c> answered <i>"apply_edits requires at least one edit"</i> for an
/// <c>edits</c> that was not an array — "you sent none" for a payload that was badly shaped, which
/// sends the model round the loop resending it. That is the distinction the repository draws at the
/// call funnel (<i>"no arguments" is not "unreadable arguments"</i>) and did not draw for this one
/// argument, on the tool whose whole point is that a malformed call never becomes another operation.
/// </para>
/// <para>
/// ⚠ Rule 7 could not see either: it forbids the reads that THROW, and these degrade quietly. Its
/// own comment already names the shape — <i>"a sound rule, carried by an ENUMERATION"</i>.
/// </para>
/// </remarks>
public sealed class ModelArgumentDialectTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"args-{Guid.NewGuid():N}");

    public ModelArgumentDialectTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    // ── The two dialects, side by side ───────────────────────────────────────

    /// <summary>
    /// The defect in one line: the same argument, read two ways, gives two answers. Driving
    /// <c>generate_project_map</c> end to end would have measured the MACHINE — its root comes from
    /// <c>ActiveSolutionSignal</c>, a machine-wide file an open Visual Studio writes.
    /// </summary>
    [Fact]
    public void ABooleanTheModelWroteAsAString_MeansWhatItSays()
    {
        var args = Args("""{"refresh":"true"}""");

        Assert.True(args.Bool("refresh", false));

        // The raw dialect, kept here as the measurement: it drops the string form and says nothing.
        Assert.False(args.TryGetProperty("refresh", out var raw) && raw.ValueKind == JsonValueKind.True);
    }

    /// <summary>Reference arm: nothing became tolerant that should not be — a value that is neither
    /// a boolean nor a boolean-shaped string keeps the caller's default.</summary>
    [Theory]
    [InlineData("""{"refresh":"yes"}""")]
    [InlineData("""{"refresh":1}""")]
    [InlineData("{}")]
    public void AValueThatIsNotABoolean_KeepsTheDefault(string json) =>
        Assert.False(Args(json).Bool("refresh", false));

    [Fact]
    public void PresenceIsAskedOfTheFunnel_SoTheRuleNeedsNoExemption()
    {
        // "absent → the configured default, present → clamp what was asked" is why tools reached
        // for TryGetProperty in the first place; it is expressible now.
        Assert.True(Args("""{"top_k":"3"}""").Has("top_k"));
        Assert.False(Args("{}").Has("top_k"));
        Assert.Equal(3, Args("""{"top_k":"3"}""").Int("top_k", 7));
    }

    // ── apply_edits: absent is not malformed ─────────────────────────────────

    private ApplyEditsTool NewApplyEdits() =>
        new(new NoopApproval(), new FileHistoryService(), () => _root);

    [Theory]
    [InlineData("""{"edits":"[{\"path\":\"a.cs\"}]"}""")]   // the double-encoded list a small model sends
    [InlineData("""{"edits":{"path":"a.cs"}}""")]           // one edit, not wrapped in a list
    [InlineData("""{"edits":42}""")]
    public async Task AMalformedEditsList_IsNamedAsMalformed_NotAsEmpty(string json)
    {
        var answer = await NewApplyEdits().ExecuteAsync(Args(json), CancellationToken.None);

        Assert.NotEqual(Strings.ApplyEditsEmpty, answer);
        // The model must be able to tell "I sent none" from "the shape was wrong".
        Assert.Contains("edits", answer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("array", answer, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Reference arms: genuinely absent, and genuinely empty, keep the sentence they had.
    /// Without them, naming everything "malformed" would pass the test above.</summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("""{"edits":[]}""")]
    public async Task AnAbsentOrEmptyEditsList_KeepsSayingItIsEmpty(string json)
    {
        var answer = await NewApplyEdits().ExecuteAsync(Args(json), CancellationToken.None);

        Assert.Equal(Strings.ApplyEditsEmpty, answer);
    }

    // ── And one dialect, not two ─────────────────────────────────────────────

    /// <summary>
    /// The rule that keeps it closed. Rule 7 forbids the reads that THROW; this forbids the second
    /// DIALECT, which is what actually bit: a raw <c>TryGetProperty</c> accepts strictly less than
    /// <see cref="ToolArgs"/> and drops the rest in silence.
    /// </summary>
    /// <remarks>⚠ Zero exemptions, and that is why <c>ToolArgs.Has</c> exists: testing for presence
    /// was the one legitimate raw use, so the funnel had to offer it.</remarks>
    [Fact]
    public void NoToolReadsTheModelsArgumentsThroughARawTryGetProperty()
    {
        var tools = Path.Combine(RepoRoot(), "Inferpal.Core", "Services", "Tools");

        // WITNESS: the funnel really offers what the tools needed, and the scan really reads files.
        Assert.Contains("public static bool Has(", File.ReadAllText(Path.Combine(tools, "ToolArgs.cs")),
                        StringComparison.Ordinal);
        var files = Directory.GetFiles(tools, "*.cs");
        Assert.True(files.Length >= 25, $"only {files.Length} tool source(s) read — the rule scans nothing.");

        var offenders = files
            .Where(f => Path.GetFileName(f) != "ToolArgs.cs")
            .Where(f => ConventionCoverageTests.CodeOnly(f).Contains("args.TryGetProperty", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(offenders.Count == 0,
            "These tools read the model's arguments through a raw TryGetProperty — a second dialect "
            + "that accepts less than ToolArgs and drops the rest in silence: "
            + string.Join(", ", offenders));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
