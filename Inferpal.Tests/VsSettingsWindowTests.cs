using System.IO;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.ToolWindow;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Visual Studio's settings window, its form built as Visual Studio builds it (the theme subscription aside): what it shows
/// when shown again, and what a save leaves in it. Windows only: the form is the Visual Studio adapter's.
/// </summary>
[Collection(GlobalConfigPathCollection.Name)]
public sealed class VsSettingsWindowTests : IDisposable
{
    private readonly string? _previous = InferpalConfig.OverridePathForTests;
    private readonly string  _path     =
        Path.Combine(Path.GetTempPath(), "inferpal-tests", $"settings-window-{Guid.NewGuid():N}.json");

    public VsSettingsWindowTests()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        InferpalConfig.OverridePathForTests = _path;
    }

    public void Dispose()
    {
        InferpalConfig.OverridePathForTests = _previous;
        try { File.Delete(_path); } catch { /* best-effort cleanup */ }
    }

    /// <summary>Another editor saves: the file changes under the window.</summary>
    private void WriteAsAnotherEditor(Action<InferpalConfig> change)
    {
        var other = JsonSerializer.Deserialize<InferpalConfig>(File.ReadAllText(_path))!;
        change(other);
        File.WriteAllText(_path, JsonSerializer.Serialize(other));
    }

    private InferpalConfig OnDisk() => JsonSerializer.Deserialize<InferpalConfig>(File.ReadAllText(_path))!;

    /// <summary>⚠ A button's accessible name reads its first underscore as an access key ("Couper readfile" in UI
    /// Automation, the Tools page): doubled, the tool's name is announced as written.</summary>
    [Fact]
    public void AToolButtonsAccessibleName_KeepsTheToolsUnderscores()
    {
        var row = new ToolRowItem(new ToolListRow("search_in_files", "search_in_files", "1", "on", "on", false, true, "Couper"),
                                  string.Empty, "#000",
                                  new Microsoft.VisualStudio.Extensibility.UI.AsyncCommand((_, _) => Task.CompletedTask));

        Assert.Equal("Couper search__in__files", row.ActionName);
        Assert.Equal("search_in_files", row.Key);   // reference arm: the text shown keeps its single underscores
    }

    /// <summary>The window shown again takes what another editor saved since — with the four lists as an untouched form
    /// shows them: the form writes each list back in its own form (an empty server list reads <c>{}</c>), which is no edit
    /// of the person's — and leaves a form the person is editing as it is.</summary>
    [Fact]
    public async Task VisualStudiosSettingsWindow_ShownAgain_TakesWhatAnotherEditorSaved_UnlessTheFormIsBeingEdited()
    {
        new InferpalConfig { DefaultModel = "m", ContextWindowSize = 20000 }.Save();
        var config = InferpalConfig.Load();
        Assert.Equal("", config.McpServersJson);   // witness: the lists at their defaults, the case every new install is in
        await using var mcp = new Services.Mcp.McpToolService(config, new Approve());
        var form = new InferpalSettingsData(config, new FakeInferenceProvider(), null!, mcp);
        Assert.Equal("20000", form.ContextWindowSizeText);

        WriteAsAnotherEditor(c => c.ContextWindowSize = 37000);
        form.ShowAgain();
        Assert.Equal("37000", form.ContextWindowSizeText);

        // Reference arm: a list the person is typing in keeps the form as it is.
        form.PromptTemplates += "\n/x=y";
        WriteAsAnotherEditor(c => c.ContextWindowSize = 41000);
        form.ShowAgain();
        Assert.Equal("37000", form.ContextWindowSizeText);
        // The reload ran to its end: ShowAgain swallows what throws, and a box set early in it would read the same. (Only
        // this class shows the form again, and its tests run one at a time.)
        Assert.DoesNotContain(Services.Diagnostics.Snapshot(), e => e.Context == "Settings.ShowAgain");
    }

    /// <summary>A file the chat pins while the window is open is kept by a save — the save merges it in — and the form shows
    /// it after: left showing the list without it, the next save reads it as the person's removal and unpins it.</summary>
    [Fact]
    public async Task VisualStudiosSettingsWindow_Saved_ShowsWhatTheChatPinnedMeanwhile_SoTheNextSaveKeepsIt()
    {
        new InferpalConfig { DefaultModel = "m" }.Save();
        var config = InferpalConfig.Load();
        await using var mcp = new Services.Mcp.McpToolService(config, new Approve());
        var form = new InferpalSettingsData(config, new FakeInferenceProvider(), null!, mcp);

        config.PinnedContextFiles = "src/pinned.cs";   // the chat pins a file: the same instance, as in Visual Studio
        form.ContextWindowSizeText = "30000";
        await form.SaveCommand.ExecuteAsync(null, null!, CancellationToken.None);
        Assert.Equal(30000, OnDisk().ContextWindowSize);   // witness: the save ran
        Assert.Contains("src/pinned.cs", OnDisk().PinnedContextFiles, StringComparison.Ordinal);
        Assert.Contains("src/pinned.cs", form.PinnedContextFiles, StringComparison.Ordinal);

        form.ContextWindowSizeText = "31000";
        await form.SaveCommand.ExecuteAsync(null, null!, CancellationToken.None);
        Assert.Equal(31000, OnDisk().ContextWindowSize);
        Assert.Contains("src/pinned.cs", OnDisk().PinnedContextFiles, StringComparison.Ordinal);
    }

    /// <summary>A command row saved on its own merges with what the settings took since the window opened — a command
    /// another editor added, followed into the shared copy —, never writes the window's list whole over it. The same for a
    /// tool.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task VisualStudiosSettingsWindow_ARowSavedOnItsOwn_KeepsWhatAnotherEditorAddedSince(bool commands)
    {
        new InferpalConfig { DefaultModel = "m", PromptTemplates = "/a=A", CustomTools = "a=echo a" }.Save();
        var config = InferpalConfig.Load();
        await using var mcp = new Services.Mcp.McpToolService(config, new Approve());
        var form = new InferpalSettingsData(config, new FakeInferenceProvider(), null!, mcp);

        // Another editor adds one; the chat follows the file into the copy the window shares.
        if (commands) config.PromptTemplates = "/a=A\n/b=B"; else config.CustomTools = "a=echo a\nb=echo b";
        (commands ? form.SlashCommandRows : form.CustomToolRows)[0].Enabled = false;   // saved on its own, at once

        var onDisk = commands ? OnDisk().PromptTemplates : OnDisk().CustomTools;
        string[] expected = commands ? ["#/a=A", "/b=B"] : ["#a=echo a", "b=echo b"];
        Assert.Equal(expected, onDisk.Split('\n').Order(StringComparer.Ordinal));   // in either order
        Assert.Equal(2, (commands ? form.SlashCommandRows : form.CustomToolRows).Count);   // and the window shows it
    }

    /// <summary>Saved while the pinned-file editor is open, the form cannot show the file the chat pinned meanwhile: the
    /// edit committed after it must not read that file as the person's removal.</summary>
    [Fact]
    public async Task VisualStudiosSettingsWindow_SavedWithThePinnedEditorOpen_KeepsWhatTheChatPinned()
    {
        new InferpalConfig { DefaultModel = "m" }.Save();
        var config = InferpalConfig.Load();
        await using var mcp = new Services.Mcp.McpToolService(config, new Approve());
        var form = new InferpalSettingsData(config, new FakeInferenceProvider(), null!, mcp);

        await form.AddPinnedCommand.ExecuteAsync(null, null!, CancellationToken.None);
        Assert.True(form.IsEditingPinned);   // witness: the editor is open
        config.PinnedContextFiles = "src/pinned.cs";   // the chat pins a file
        form.ContextWindowSizeText = "30000";
        await form.SaveCommand.ExecuteAsync(null, null!, CancellationToken.None);
        Assert.Equal(30000, OnDisk().ContextWindowSize);

        form.EditPinnedPath = "src/mine.cs";
        await form.SavePinnedCommand.ExecuteAsync(null, null!, CancellationToken.None);

        Assert.Contains("src/mine.cs", OnDisk().PinnedContextFiles, StringComparison.Ordinal);   // witness: the edit landed
        Assert.Contains("src/pinned.cs", OnDisk().PinnedContextFiles, StringComparison.Ordinal);
    }

    private sealed class Approve : Services.Execution.IApprovalService
    {
        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct, string? subject = null,
                                               DiffInfo? diff = null, bool forcePrompt = false) => Task.FromResult(true);
    }
}
