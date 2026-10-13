using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ Visual Studio: the chat's model button names the configured chat model, whoever changed it.
/// </summary>
/// <remarks>
/// The label was written only by the gestures of the chat window itself (the picker, <c>/model</c>, <c>/onboard apply</c>,
/// the first run). A chat model chosen in the settings window, or written by <c>/import … apply</c>, changed the model
/// every question went to and left the previous name on the button. Every save now reads the label back from the
/// configuration. These rules read the source (the window's view model needs Visual Studio).
/// </remarks>
public sealed class ModelButtonFollowsTheSettingTests
{
    private static string ToolWindow(string file) =>
        ConventionCoverageTests.CodeOnly(Path.Combine(ConventionCoverageTests.RepoRoot(), "Inferpal", "ToolWindow", file));

    [Fact]
    public void ASave_ReadsTheModelButtonBackFromTheConfiguration()
    {
        Assert.Contains("_config.Saved                   += OnConfigSaved;", ToolWindow("InferpalToolWindowData.Construction.cs"),
                        StringComparison.Ordinal);   // WITNESS: the save reaches the chat window

        var connection = ToolWindow("InferpalToolWindowData.Connection.cs");
        var saved = connection[connection.IndexOf("private void OnConfigSaved()", StringComparison.Ordinal)..];
        saved = saved[..saved.IndexOf("_ = RemeasureContextWindowAsync();", StringComparison.Ordinal)];
        Assert.Contains("ActiveModelLabel = _config.DefaultModel;", saved, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryWriterOfTheLabel_WritesTheConfiguredModel()
    {
        // The read-back above is right only while the label IS the configured model: a writer that put something else
        // there (a session-only model) would be undone by the next save of any setting. Each writer sets DefaultModel to
        // the same value first, or reads it.
        var dir = Path.Combine(ConventionCoverageTests.RepoRoot(), "Inferpal", "ToolWindow");
        var writers = Directory.EnumerateFiles(dir, "InferpalToolWindowData*.cs")
            .SelectMany(f => Regex.Matches(ConventionCoverageTests.CodeOnly(f), @"ActiveModelLabel\s*=\s*([^;)]+)")
                                  .Select(m => (File: Path.GetFileName(f), Value: m.Groups[1].Value.Trim())))
            .ToList();
        Assert.True(writers.Count >= 6, $"only {writers.Count} writers of the label found: the scan measures nothing.");   // WITNESS

        foreach (var (file, value) in writers)
        {
            if (value == "_config.DefaultModel") continue;
            var source = ConventionCoverageTests.CodeOnly(Path.Combine(dir, file));
            Assert.True(source.Contains($"_config.DefaultModel = {value};", StringComparison.Ordinal)
                        || source.Contains($"NewDefaultModel is {{ }} {value}", StringComparison.Ordinal)
                        || source.Contains($"_config.DefaultModel = {value} ", StringComparison.Ordinal),
                        $"{file}: the label is set to '{value}', which is not shown to be the configured model.");
        }
    }
}
