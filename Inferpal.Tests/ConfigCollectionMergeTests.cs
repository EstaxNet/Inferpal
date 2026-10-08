using System.IO;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Services.Docs;
using Inferpal.Services.Mcp;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A setting that holds a collection merges ELEMENT by element with what the other editor wrote: a pin, a docs source,
/// an MCP server, a permission rule added in one window survives a save of the same setting from the other.
/// </summary>
/// <remarks>
/// ⚠ A save lays over <c>config.json</c> only the keys its copy changed — and a list is one key. A window that pinned a
/// file wrote its whole list, and the file the other window had pinned since was gone; the same for <c>/docs add</c>,
/// an MCP server (its OAuth sign-in was then forgotten as "removed"), and a deny rule added in the other editor's
/// settings. "The other editor" rewrites the file behind the back of an instance this process loaded.
/// </remarks>
[Collection(GlobalConfigPathCollection.Name)]
public class ConfigCollectionMergeTests : IDisposable
{
    private readonly string? _previous = InferpalConfig.OverridePathForTests;
    private readonly string  _path     =
        Path.Combine(Path.GetTempPath(), "inferpal-tests", $"collections-{Guid.NewGuid():N}.json");

    public ConfigCollectionMergeTests()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        InferpalConfig.OverridePathForTests = _path;
    }

    public void Dispose()
    {
        InferpalConfig.OverridePathForTests = _previous;
        try { File.Delete(_path); } catch { }
    }

    private void WriteAsTheOtherEditor(Action<InferpalConfig> change)
    {
        var other = JsonSerializer.Deserialize<InferpalConfig>(File.ReadAllText(_path))!;
        change(other);
        File.WriteAllText(_path, JsonSerializer.Serialize(other));
    }

    private InferpalConfig OnDisk() => JsonSerializer.Deserialize<InferpalConfig>(File.ReadAllText(_path))!;

    private static string[] Lines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    [Fact]
    public void APinAddedHere_KeepsTheFileTheOtherWindowPinned()
    {
        new InferpalConfig { PinnedContextFiles = @"C:\src\shared.cs" }.Save();
        var mine = InferpalConfig.Load();

        WriteAsTheOtherEditor(c => c.PinnedContextFiles = "C:\\src\\shared.cs\nC:\\src\\theirs.cs");
        mine.PinnedContextFiles = "C:\\src\\shared.cs\nC:\\src\\mine.cs";
        mine.Save();

        Assert.Equal([@"C:\src\shared.cs", @"C:\src\theirs.cs", @"C:\src\mine.cs"], Lines(OnDisk().PinnedContextFiles));
    }

    [Fact]
    public void ARuleAddedHere_KeepsTheDenyRuleTheOtherEditorAdded_InItsPlace()
    {
        new InferpalConfig { PermissionRules = "allow read_file .*" }.Save();
        var mine = InferpalConfig.Load();

        WriteAsTheOtherEditor(c => c.PermissionRules = "deny run_command ^curl\nallow read_file .*");
        mine.PermissionRules = "allow read_file .*\nallow list_files .*";
        mine.Save();

        // First match wins: the other editor's rule keeps its place above.
        Assert.Equal(["deny run_command ^curl", "allow read_file .*", "allow list_files .*"], Lines(OnDisk().PermissionRules));
    }

    [Fact]
    public void ARuleRemovedHere_StaysRemoved_AndOneRemovedThereToo()
    {
        new InferpalConfig { PermissionRules = "allow a .*\nallow b .*\nallow c .*" }.Save();
        var mine = InferpalConfig.Load();

        WriteAsTheOtherEditor(c => c.PermissionRules = "allow a .*\nallow b .*");   // the other editor removed c
        mine.PermissionRules = "allow b .*\nallow c .*";                            // this one removed a
        mine.Save();

        Assert.Equal(["allow b .*"], Lines(OnDisk().PermissionRules));
    }

    [Fact]
    public void ADocsSourceAddedHere_KeepsTheOneAddedThere()
    {
        var react = new DocSite("react", "React", "https://react.dev/learn");
        var vue   = new DocSite("vue", "Vue", "https://vuejs.org/guide");
        new InferpalConfig { DocSitesJson = "" }.Save();
        var mine = InferpalConfig.Load();

        WriteAsTheOtherEditor(c => c.DocSitesJson = JsonSerializer.Serialize(new[] { vue }));
        mine.DocSitesJson = JsonSerializer.Serialize(new[] { react });
        mine.Save();

        Assert.Equal(["vue", "react"], DocSite.Parse(OnDisk().DocSitesJson).Select(s => s.Id));
    }

    [Fact]
    public void AnMcpServerToggledHere_KeepsTheServerAddedThere()
    {
        const string start = """{ "mcpServers": { "files": { "command": "npx", "disabled": false } } }""";
        new InferpalConfig { McpServersJson = start }.Save();
        var mine = InferpalConfig.Load();

        WriteAsTheOtherEditor(c => c.McpServersJson =
            """{ "mcpServers": { "files": { "command": "npx", "disabled": false }, "remote": { "url": "https://mcp.example/" } } }""");
        mine.McpServersJson = """{ "mcpServers": { "files": { "command": "npx", "disabled": true } } }""";
        mine.Save();

        var servers = McpServerConfig.Parse(OnDisk().McpServersJson);
        Assert.Equal(["files", "remote"], servers.Select(s => s.Name).Order(StringComparer.Ordinal));
        Assert.False(servers.Single(s => s.Name == "files").Enabled);   // this copy's toggle
    }

    /// <summary>Reference arm: an element both editors changed goes to the one saving last, like any setting.</summary>
    [Fact]
    public void AnElementBothChanged_GoesToTheLastSave()
    {
        new InferpalConfig { McpServersJson = """{ "files": { "command": "a" } }""" }.Save();
        var mine = InferpalConfig.Load();

        WriteAsTheOtherEditor(c => c.McpServersJson = """{ "files": { "command": "theirs" } }""");
        mine.McpServersJson = """{ "files": { "command": "mine" } }""";
        mine.Save();

        Assert.Equal("mine", McpServerConfig.Parse(OnDisk().McpServersJson).Single().Command);
    }

    /// <summary>Reference arm: a side that cannot be split is written whole, as every setting was.</summary>
    [Fact]
    public void AnUnreadableCollection_IsWrittenWhole()
    {
        new InferpalConfig { DocSitesJson = "[]" }.Save();
        var mine = InferpalConfig.Load();

        WriteAsTheOtherEditor(c => c.DocSitesJson = "not json");
        mine.DocSitesJson = """[{"id":"react","title":"React","startUrl":"https://react.dev/learn"}]""";
        mine.Save();

        Assert.Equal(mine.DocSitesJson, OnDisk().DocSitesJson);
    }

    [Fact]
    public void TheSettingsThatHoldACollection_AreDeclaredSo()
    {
        // Nominative: the six settings edited by adding and removing elements. The shape is declared on the property.
        Assert.Equal(
            ["customTools", "docSitesJson", "mcpServersJson", "permissionRules", "pinnedContextFiles", "promptTemplates"],
            InferpalConfig.CollectionSettings.Keys.Order(StringComparer.Ordinal));
    }
}
