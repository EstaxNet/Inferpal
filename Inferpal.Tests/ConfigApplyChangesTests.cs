using System.Text.Json;
using Inferpal.Config;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The Visual Studio settings window builds its values from the configuration it shares with the chat,
/// and its save rewrote everything it displayed: a /model or a pin made from the chat while the window
/// was open was silently reverted.
/// </summary>
public class ConfigApplyChangesTests
{
    private static InferpalConfig CopyOf(System.Text.Json.Nodes.JsonObject snapshot) =>
        JsonSerializer.Deserialize<InferpalConfig>(snapshot.ToJsonString())!;

    [Fact]
    public void AnEditorSave_KeepsWhatChangedElsewhere_AndAppliesWhatItChanged()
    {
        var live     = new InferpalConfig { DefaultModel = "opened", CustomSystemPrompt = "opened", PinnedContextFiles = "a.md" };
        var baseline = live.SnapshotNow();

        live.DefaultModel       = "picked-from-chat";   // elsewhere, window open
        live.PinnedContextFiles = "a.md\nb.md";

        var edited = CopyOf(baseline);
        edited.CustomSystemPrompt = "edited-in-window";

        live.ApplyChangesFrom(edited, baseline);

        Assert.Equal("picked-from-chat", live.DefaultModel);
        Assert.Equal("a.md\nb.md", live.PinnedContextFiles);
        Assert.Equal("edited-in-window", live.CustomSystemPrompt);
    }

    [Fact]
    public void ASettingBothSidesChanged_GoesToTheEditor()
    {
        // Witness: the merge does not cancel an explicit choice made in the window.
        var live     = new InferpalConfig { DefaultModel = "opened" };
        var baseline = live.SnapshotNow();
        live.DefaultModel = "picked-from-chat";

        var edited = CopyOf(baseline);
        edited.DefaultModel = "chosen-in-window";

        live.ApplyChangesFrom(edited, baseline);

        Assert.Equal("chosen-in-window", live.DefaultModel);
    }
}
