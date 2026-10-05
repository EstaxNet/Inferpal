using System.IO;
using Inferpal.Services.Presentation;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ The Visual Studio settings window counted a field as changed when its box differed from the saved value TRIMMED —
/// trimmed on one side only. A custom prompt saved with a final line break never matched its own box: "1 unsaved
/// change" each time the window opened and after every save. Blanks around a value count on neither side.
/// </summary>
public class UnsavedCountTests
{
    [Theory]
    [InlineData("Answer in French.\n", "Answer in French.\n", true)]   // the prompt as saved, final line break included
    [InlineData("Answer in French.", "Answer in French.\n", true)]
    [InlineData("  20 ", "20", true)]
    [InlineData("Answer in German.", "Answer in French.", false)]       // reference arm: a real change still counts
    [InlineData("", "x", false)]
    public void ABox_ShowsWhatIsSaved_WhateverTheBlanksAround(string form, string saved, bool same) =>
        Assert.Equal(same, SettingsFallback.SameAsSaved(form, saved));

    [Fact]
    public void TheVisualStudioWindow_CountsThroughTheRule()
    {
        var pages = ConventionCoverageTests.CodeOnly(Path.Combine(ConversationPersistenceSilenceTests.RepoRoot(),
                                                                   "Inferpal", "ToolWindow", "InferpalSettingsData.Pages.cs"));
        Assert.Contains("fields.Count(f => !SettingsFallback.SameAsSaved(f.Form, f.Saved))", pages, StringComparison.Ordinal);
        Assert.Contains("(CustomSystemPrompt ?? string.Empty, saved.CustomSystemPrompt)", pages, StringComparison.Ordinal);   // witness
    }
}
