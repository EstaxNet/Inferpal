using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ VS Code's settings panel wrote the form into the configuration it compares against BEFORE the host answered. A
/// Save the host refused (a turn running, no host) still counted as done: "No unsaved changes", Save disabled, and
/// Cancel redrew the refused values as if saved — closing the panel then lost them. The edits are made on a copy, and a
/// refused Save puts the saved state back. These rules read the source.
/// </summary>
public class SettingsSaveRollbackTests
{
    [Fact]
    public void ASave_EditsACopy_KeepingTheSavedStateAside()
    {
        var save   = WebviewRebuildTests.Body(WebviewRebuildTests.TsCode("webview/settings.ts"), "function onSave(");
        var aside  = save.IndexOf("savedBeforeSave = config;", StringComparison.Ordinal);
        var copy   = save.IndexOf("config = JSON.parse(JSON.stringify(config))", StringComparison.Ordinal);
        var write  = save.IndexOf("config[field.key] =", StringComparison.Ordinal);
        Assert.True(write > 0, "the form no longer writes the configuration: the rule reads nothing");   // WITNESS
        Assert.True(aside >= 0 && copy > aside && write > copy, "the form is written into the saved configuration itself");
    }

    [Fact]
    public void ARefusedSave_PutsTheSavedStateBack_AndADoneOneForgetsIt()
    {
        var page  = WebviewRebuildTests.TsCode("webview/settings.ts");
        var error = page[page.IndexOf("case 'error':", StringComparison.Ordinal)..];
        error     = error[..error.IndexOf("break;", StringComparison.Ordinal)];
        Assert.Contains("msg.op === 'save'", error, StringComparison.Ordinal);
        Assert.Contains("config = savedBeforeSave;", error, StringComparison.Ordinal);
        Assert.Contains("refreshForm();", error, StringComparison.Ordinal);

        var done = page[page.IndexOf("case 'saveDone':", StringComparison.Ordinal)..];
        done     = done[..done.IndexOf("break;", StringComparison.Ordinal)];
        Assert.Contains("savedBeforeSave = null;", done, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePanel_TagsEveryFailureOfASave()
    {
        var panel = WebviewRebuildTests.TsCode("settingsPanel.ts");
        var save  = panel[panel.IndexOf("case 'save': {", StringComparison.Ordinal)..];
        save      = save[..save.IndexOf("private parseKeys(", StringComparison.Ordinal)];

        var errors = System.Text.RegularExpressions.Regex.Matches(save, @"type: 'error'").Count;
        var tagged = System.Text.RegularExpressions.Regex.Matches(save, @"type: 'error', op: 'save'").Count;
        Assert.True(errors >= 2, "the save's failure paths moved: the rule reads nothing");   // WITNESS
        Assert.Equal(errors, tagged);
    }
}
