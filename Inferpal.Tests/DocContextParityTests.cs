using System.IO;
using System.Text.RegularExpressions;
using Inferpal.Services;
using Inferpal.Services.CodeActions;
using Inferpal.Services.Prompting;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>/doc</c> gets the document's semantic context on <b>both</b> editors.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ <b>Measured</b>: the Visual Studio window appended <see cref="DocContextExtractor"/>'s block to
/// the docstring prompt and the host did not, so the same command on the same file produced a
/// measurably weaker docstring in VS Code — no namespace, no base type, no
/// <c>&lt;inheritdoc/&gt;</c> guidance, no interface contract — and nothing said so. The block is
/// not decorative: on a class implementing an interface it carries the members to inherit the
/// documentation from.
/// </para>
/// <para>
/// ⚠ The drift was possible because the (system, instruction) pair was PICKED by each front-end.
/// The Core now builds it, enrichment included, and the rule below is what keeps it that way — the
/// repository has already paid this exact shape twice (283 + 141 lines deduplicated in 2026-07).
/// </para>
/// <para>
/// ⚠ The kind still arrives at the host as a string, and an unknown one is still NAMED there rather
/// than degraded to "doc": the funnel takes the enum, the string stays judged where it arrives.
/// </para>
/// </remarks>
public sealed class DocContextParityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"doc-{Guid.NewGuid():N}");
    private readonly string _impl;

    public DocContextParityTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "IGreeter.cs"), """
            namespace App;
            public interface IGreeter
            {
                string Greet(string name);
                void Reset();
            }
            """);
        _impl = Path.Combine(_root, "LoudGreeter.cs");
        File.WriteAllText(_impl, """
            namespace App;
            public class LoudGreeter : BaseGreeter, IGreeter
            {
                public override string Greet(string name) => name.ToUpperInvariant();
                public void Reset() { }
            }
            """);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string Text() => File.ReadAllText(_impl);

    // ── What the funnel builds ───────────────────────────────────────────────

    [Fact]
    public async Task TheDocPrompt_CarriesTheSemanticContext_WhenAPathIsKnown()
    {
        // WITNESS: the extractor really produces something for this fixture. Without it, an
        // extractor that had gone silent would make every assertion below pass for nothing.
        var block = await DocContextExtractor.BuildContextBlockAsync(_impl, Text(), CancellationToken.None);
        Assert.Contains("IGreeter", block, StringComparison.Ordinal);

        var (system, instruction) = await InPlaceCodeActionPrompts.BuildAsync(
            SlashCodeActionKind.Doc, _impl, Text(), CancellationToken.None);

        Assert.Contains(block, system, StringComparison.Ordinal);
        Assert.Contains(InPlaceCodeActionPrompts.DocstringSystem, system, StringComparison.Ordinal);
        Assert.Equal(InPlaceCodeActionPrompts.DocstringInstruction, instruction);
    }

    /// <summary>
    /// Reference arm: no path (a caller holding text alone) is not an error, it is simply the
    /// unenriched prompt. Without this, a funnel that always appended something would pass above.
    /// </summary>
    [Fact]
    public async Task TheDocPrompt_WithoutAPath_IsTheBareOne()
    {
        var (system, _) = await InPlaceCodeActionPrompts.BuildAsync(
            SlashCodeActionKind.Doc, null, Text(), CancellationToken.None);

        Assert.Equal(InPlaceCodeActionPrompts.DocstringSystem, system);
    }

    /// <summary>Reference arm: the other two actions are not enriched — the block is about
    /// documentation, and adding it to a refactor would be prompt noise.</summary>
    [Theory]
    [InlineData((int)SlashCodeActionKind.Fix)]
    [InlineData((int)SlashCodeActionKind.Refactor)]
    public async Task TheOtherActions_AreNotEnriched(int kind)
    {
        var (system, _) = await InPlaceCodeActionPrompts.BuildAsync(
            (SlashCodeActionKind)kind, _impl, Text(), CancellationToken.None);

        Assert.DoesNotContain("Semantic context", system, StringComparison.Ordinal);
    }

    // ── And neither front-end picks the pair itself ──────────────────────────

    /// <summary>
    /// The rule that keeps the two editors together: the prompt pair is chosen in ONE place. A
    /// front-end that picks it again is how this defect was born — and it is not detectable by
    /// running either editor, since both answer.
    /// </summary>
    /// <remarks>
    /// ⚠ Read without comments: the remark right above quotes the very names the rule forbids.
    /// </remarks>
    [Theory]
    [InlineData("Inferpal.Host", "HostServer.cs")]
    [InlineData("Inferpal", "ToolWindow", "InferpalToolWindowData.SlashCommands.cs")]
    public void NeitherFrontEnd_PicksTheCodeActionPromptsItself(params string[] parts)
    {
        var code = ConventionCoverageTests.CodeOnly(Path.Combine(RepoRoot(), Path.Combine(parts)));

        // WITNESS: this really is a file that runs code actions.
        Assert.Contains("InPlaceCodeActionPrompts.BuildAsync", code, StringComparison.Ordinal);

        // The pair itself is never named outside the funnel.
        foreach (var picked in new[] { "DocstringSystem", "RefactorSystem", "FixSystem" })
            Assert.DoesNotContain(picked, code, StringComparison.Ordinal);
    }

    /// <summary>
    /// And the path really travels: without it the host holds text with no file, so the funnel
    /// hands back the bare prompt and the defect is intact with every call site looking right.
    /// </summary>
    [Fact]
    public void TheVsCodeAdapter_SendsTheDocumentPath()
    {
        var ts = SettingsSchemaDriftTests.NeutralizeTypeScriptComments(
            File.ReadAllText(Path.Combine(RepoRoot(), "vscode", "src", "chatViewProvider.ts")));

        var call = Regex.Match(ts, @"codeActionRun\(\s*\{[\s\S]{0,400}?\}\s*\)");
        Assert.True(call.Success, "codeActionRun is no longer called with an object literal — the rule reads nothing.");
        Assert.Contains("path:", call.Value, StringComparison.Ordinal);
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
