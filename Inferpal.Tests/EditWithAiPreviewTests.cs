using System.IO;
using System.Linq;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Visual Studio: every edit the model writes into the active document goes through the inline diff preview when
/// "Show the change before applying it" is on — "Edit with AI" (Ctrl+Shift+I) included.
/// </summary>
/// <remarks>
/// The box lives in the settings section named after "Edit with AI", and that command was the one that applied past it:
/// the detour sat in <c>InPlaceCodeEdit</c> (the menu's Fix, Refactor and Add docs), while the command did its own
/// <c>EditAsync</c>. The property, read in the sources (the commands need a running Visual Studio): a file that turns a
/// model reply into an edit (<c>CodeActionPipeline</c>) and applies it (<c>EditAsync</c>) offers the preview first, by
/// the one detour.
/// </remarks>
public sealed class EditWithAiPreviewTests
{
    [Fact]
    public void EveryModelEditOfTheActiveDocument_OffersThePreview_BeforeItApplies()
    {
        var commands = Path.Combine(ConventionCoverageTests.RepoRoot(), "Inferpal", "Commands");
        var appliers = Directory.EnumerateFiles(commands, "*.cs")
            .Select(f => (File: Path.GetFileName(f), Code: ConventionCoverageTests.CodeOnly(f)))
            .Where(s => s.Code.Contains("CodeActionPipeline.", StringComparison.Ordinal)
                        && s.Code.Contains(".EditAsync(", StringComparison.Ordinal))
            .ToList();
        // WITNESS, under the real count (two: the menu's actions and Edit with AI): the reading is alive.
        Assert.True(appliers.Count >= 1, $"only {appliers.Count} file(s) apply a model edit: this test would measure nothing.");

        foreach (var (file, code) in appliers)
        {
            var preview = code.IndexOf("TryPreviewAsync(", StringComparison.Ordinal);
            Assert.True(preview >= 0, $"{file} applies a model edit without offering the preview.");
            Assert.True(preview < code.LastIndexOf(".EditAsync(", StringComparison.Ordinal),
                        $"{file} offers the preview only after applying.");
        }

        // One detour: the setting is read in one place.
        var detour = ConventionCoverageTests.CodeOnly(Path.Combine(commands, "InPlaceCodeEdit.cs"));
        Assert.Contains("internal static async Task<bool> TryPreviewAsync(", detour, StringComparison.Ordinal);
        Assert.Single(appliers, a => a.Code.Contains("InlineDiffPreviewEnabled", StringComparison.Ordinal));
    }
}
