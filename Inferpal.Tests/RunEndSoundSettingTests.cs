using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Inferpal.Config;
using Inferpal.Host;
using Inferpal.Services.Presentation;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The end of a long turn is announced — Visual Studio plays a sound, VS Code shows a notification while its chat is out
/// of sight — after the same time, and one box in the settings, beside how a run is shown, turns both off.
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
        Assert.Contains("if (Services.Agent.ChatTurnPolicy.AnnouncesEnd(_config, DateTime.UtcNow - sendStart))",
                        turn, StringComparison.Ordinal);
        Assert.True(turn.LastIndexOf("ChatTurnPolicy.AnnouncesEnd(_config", calls[0].Index, StringComparison.Ordinal) > 0,
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

    /// <summary>VS Code announces the end the same way, after the same time: a notification, only while its chat is out
    /// of sight, turned off by the same setting — read where it reads its other chat settings.</summary>
    [Fact]
    public void VsCode_AnnouncesTheEndOfALongRun_AfterTheSameTime_UnderTheSameSetting()
    {
        var provider = SettingsSchemaDriftTests.NeutralizeTypeScriptComments(File.ReadAllText(
            Path.Combine(ConversationPersistenceSilenceTests.RepoRoot(), "vscode", "src", "chatViewProvider.ts")));

        // A turn the host answered: the host decides (ChatTurnPolicy.AnnouncesEnd, the setting as the file holds it now),
        // on the turn's whole time, and the result carries it.
        var host = Source("Inferpal.Host", "HostServer.cs");
        Assert.Contains("AnnounceEnd = Services.Agent.ChatTurnPolicy.AnnouncesEnd(s.Config, clock.Elapsed),", host, StringComparison.Ordinal);
        Assert.Matches(@"this\.turnEnded\(result\.error \? 'failed' : result\.cancelled \? 'stopped' : 'normal'\);\s*this\.announceRunEnd\(result\.announceEnd === true,",
                       provider);
        // A request that failed — the host may be gone: the same time, the setting as last read.
        var seconds = Regex.Match(provider, @"const LONG_RUN_SECONDS = (\d+);");
        Assert.True(seconds.Success, "the threshold is gone from the extension");
        Assert.Equal(Services.Agent.ChatTurnPolicy.LongRunSeconds, int.Parse(seconds.Groups[1].Value));
        Assert.Contains("this.announceRunEnd(startedAt > 0 && this.runEndNotice && Date.now() - startedAt > LONG_RUN_SECONDS * 1000, 'failed', '');",
                        provider);
        Assert.Equal(2, Regex.Matches(provider, @"this\.runEndNotice = cfg\.runEndSoundEnabled !== false;").Count);
        // Only while the chat is out of sight; Show the answer through the view's command, which recreates a closed view.
        Assert.Contains("if (vscode.window.state.focused && this.view?.visible) {", provider);
        Assert.Contains("void vscode.commands.executeCommand('inferpal.chat.focus');", provider);
    }

    [Fact]
    public void TheBox_SitsWithTheAgentRuns_InBothEditors()
    {
        var page    = SettingsSchema.Tabs.Single(t => t.Key == "appearance");
        var section = page.Sections.Single(s => s.Fields.Any(f => f.Key == "runEndSoundEnabled"));
        Assert.Equal("SettingsSectionAgentRuns", section.Title);
        // The hint says the time in words: it is the time both editors wait.
        Assert.Contains(Services.Agent.ChatTurnPolicy.LongRunSeconds.ToString(), Inferpal.Localization.Strings.HintRunEndSound,
                        StringComparison.Ordinal);

        // VS Code's panel is drawn from what the host serves: the box is in it, beside its neighbour.
        var served = new HostServer(_ => new FakeInferenceProvider(), () => new InferpalConfig())
            .SettingsSchemaModel().Tabs.SelectMany(t => t.Sections).SelectMany(s => s.Fields).Select(f => f.Key).ToList();
        Assert.Contains("runEndSoundEnabled", served);
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
