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

    private const string DamsPass = "Authorization: Basic ZGFtczpwYXNz";   // dams:pass

    /// <summary>The proxy block opens on the method the saved headers are, and the fields write the header back: the
    /// person types a username and a password, never a Base64 line.</summary>
    [Fact]
    public async Task TheProxyBlock_OpensOnTheSavedMethod_AndItsFieldsWriteTheHeader()
    {
        new InferpalConfig { DefaultModel = "m", BaseUrl = "https://llm.home.example", RequestHeaders = DamsPass }.Save();
        var config = InferpalConfig.Load();
        await using var mcp = new Services.Mcp.McpToolService(config, new Approve());
        var form = new InferpalSettingsData(config, new FakeInferenceProvider(), null!, mcp);

        Assert.Equal(("basic", "dams", "pass"), (form.ProxyMethodSelected, form.ProxyUser, form.ProxyPassword));
        Assert.Equal("Authorization: Basic ••••••••", form.ProxySendsPreview);   // the secret never on screen
        Assert.False(form.ProxyHttp);

        form.ProxyUser = "ana";
        Assert.Equal("Authorization: Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("ana:pass")), form.RequestHeaders);
        await form.SaveCommand.ExecuteAsync(null, null!, CancellationToken.None);
        Assert.Equal(form.RequestHeaders, OnDisk().RequestHeaders);

        await form.SetProxyMethodCommand.ExecuteAsync("none", null!, CancellationToken.None);
        Assert.Equal("", form.RequestHeaders);
        Assert.False(form.ProxySends);
    }

    /// <summary>Trying another method never loses what was written by hand, and an address that comes back gives back the
    /// form's own headers — an edit not saved yet included.</summary>
    [Fact]
    public async Task TheProxyBlock_KeepsHandWrittenHeaders_AcrossMethods_AndAcrossAnAddressThatComesBack()
    {
        new InferpalConfig { DefaultModel = "m", BaseUrl = "https://llm.home.example", RequestHeaders = "X-Api-Key: abc" }.Save();
        var config = InferpalConfig.Load();
        await using var mcp = new Services.Mcp.McpToolService(config, new Approve());
        var form = new InferpalSettingsData(config, new FakeInferenceProvider(), null!, mcp);
        Assert.Equal("custom", form.ProxyMethodSelected);

        await form.SetProxyMethodCommand.ExecuteAsync("basic", null!, CancellationToken.None);
        Assert.Equal("", form.RequestHeaders);   // witness: another method, nothing typed
        await form.SetProxyMethodCommand.ExecuteAsync("custom", null!, CancellationToken.None);
        Assert.Equal("X-Api-Key: abc", form.RequestHeaders);

        form.ProxyRaw = "X-Api-Key: new";              // an edit not saved yet
        form.BaseUrl = "https://llm.home.exampl";      // a keystroke away: another address
        Assert.True(form.ProxyMovedVisible);
        form.BaseUrl = "https://llm.home.example";     // back
        Assert.False(form.ProxyMovedVisible);
        Assert.Equal("X-Api-Key: new", form.RequestHeaders);
    }

    /// <summary>Another address: the old proxy's credentials are not carried unless the person says it is the same proxy.</summary>
    [Theory]
    [InlineData("none", "")]
    [InlineData("keep", DamsPass)]
    public async Task AnotherAddress_LeavesTheOldProxysCredentials_UnlessKept(string choice, string saved)
    {
        new InferpalConfig { DefaultModel = "m", BaseUrl = "https://llm.home.example", RequestHeaders = DamsPass }.Save();
        var config = InferpalConfig.Load();
        await using var mcp = new Services.Mcp.McpToolService(config, new Approve());
        var form = new InferpalSettingsData(config, new FakeInferenceProvider(), null!, mcp);
        Assert.False(form.ProxyMovedVisible);   // witness: the saved address says nothing

        form.BaseUrl = "http://192.168.1.40:11434";
        Assert.True(form.ProxyMovedVisible);
        await form.SetProxyMovedCommand.ExecuteAsync(choice, null!, CancellationToken.None);
        await form.SaveCommand.ExecuteAsync(null, null!, CancellationToken.None);

        Assert.Equal("http://192.168.1.40:11434", OnDisk().BaseUrl);   // witness: the save ran
        Assert.Equal(saved, OnDisk().RequestHeaders);
        Assert.False(form.ProxyMovedVisible);   // saved: the block is gone
    }

    private sealed class Approve : Services.Execution.IApprovalService
    {
        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct, string? subject = null,
                                               DiffInfo? diff = null, bool forcePrompt = false) => Task.FromResult(true);
    }
}
