using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Inferpal.Config;
using Inferpal.Host;
using Inferpal.Services.Presentation;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The sound Visual Studio plays when a long turn ends can be turned off, in the settings, beside how a run is shown —
/// and only where it plays: VS Code plays none, so its panel has no box for it.
/// </summary>
public sealed class RunEndSoundSettingTests
{
    private static string Source(params string[] parts) =>
        ConventionCoverageTests.CodeOnly(Path.Combine([ConversationPersistenceSilenceTests.RepoRoot(), .. parts]));

    [Fact]
    public void TheOnlyCallToTheSound_IsGuardedByTheSetting()
    {
        var turn  = Source("Inferpal", "ToolWindow", "InferpalToolWindowData.ChatTurn.cs");
        var calls = Regex.Matches(turn, @"NotifyAgentComplete\(\);");

        // Witness: the sound is still played from the turn — the rule reads a live call.
        Assert.Single(calls);
        Assert.Contains("if (_config.RunEndSoundEnabled && (DateTime.UtcNow - sendStart).TotalSeconds > 30)", turn,
                        StringComparison.Ordinal);
        Assert.True(turn.LastIndexOf("_config.RunEndSoundEnabled", calls[0].Index, StringComparison.Ordinal) > 0,
                    "the sound is played without reading the setting");
    }

    [Fact]
    public void AConfigurationWrittenBeforeTheSetting_KeepsTheSound()
    {
        Assert.True(JsonSerializer.Deserialize<InferpalConfig>("{}")!.RunEndSoundEnabled);

        var off = JsonSerializer.Deserialize<InferpalConfig>("""{ "runEndSoundEnabled": false }""")!;
        Assert.False(off.RunEndSoundEnabled);
        Assert.Contains("\"runEndSoundEnabled\":false", JsonSerializer.Serialize(off), StringComparison.Ordinal);
    }

    [Fact]
    public void TheBox_SitsWithTheAgentRuns_InVisualStudioOnly()
    {
        var page    = SettingsSchema.Tabs.Single(t => t.Key == "appearance");
        var section = page.Sections.Single(s => s.Fields.Any(f => f.Key == "runEndSoundEnabled"));
        Assert.Equal("SettingsSectionAgentRuns", section.Title);
        Assert.Equal(SettingsSchema.VisualStudioOnly, SettingsSchema.Field("runEndSoundEnabled").Only);

        // VS Code's panel is drawn from what the host serves: the box is not in it, its neighbour is.
        var served = new HostServer(_ => new FakeInferenceProvider(), () => new InferpalConfig())
            .SettingsSchemaModel().Tabs.SelectMany(t => t.Sections).SelectMany(s => s.Fields).Select(f => f.Key).ToList();
        Assert.DoesNotContain("runEndSoundEnabled", served);
        Assert.Contains("toolBubblesExpanded", served);

        // The Visual Studio window binds it, and counts it among its unsaved changes.
        var xaml = File.ReadAllText(Path.Combine(ConversationPersistenceSilenceTests.RepoRoot(),
                                                 "Inferpal", "ToolWindow", "InferpalSettingsContent.xaml"));
        Assert.Contains("IsChecked=\"{Binding RunEndSoundEnabled, Mode=TwoWay}\"", xaml, StringComparison.Ordinal);
        var pages = Source("Inferpal", "ToolWindow", "InferpalSettingsData.Pages.cs");
        Assert.Contains("nameof(RunEndSoundEnabled)", pages, StringComparison.Ordinal);
        Assert.Contains("(RunEndSoundEnabled.ToString(), saved.RunEndSoundEnabled.ToString())", pages, StringComparison.Ordinal);
    }
}
