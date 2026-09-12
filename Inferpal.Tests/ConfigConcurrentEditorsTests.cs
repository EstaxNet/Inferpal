using System.IO;
using System.Text.Json;
using Inferpal.Config;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Visual Studio and VS Code share one <c>config.json</c>, and each keeps its own copy in memory: a
/// save from one editor wrote that whole copy back and reverted what the other had changed since.
/// </summary>
/// <remarks>
/// A model picked in VS Code, a backend switched, a deny rule added — gone at the next
/// <c>/model</c> or settings save made in Visual Studio, with nothing said. "The other editor" is
/// simulated by rewriting the file behind the back of an instance this process loaded.
/// </remarks>
[Collection(GlobalConfigPathCollection.Name)]
public class ConfigConcurrentEditorsTests : IDisposable
{
    private readonly string? _previous = InferpalConfig.OverridePathForTests;
    private readonly string  _path     =
        Path.Combine(Path.GetTempPath(), "inferpal-tests", $"editors-{Guid.NewGuid():N}.json");

    public ConfigConcurrentEditorsTests()
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

    [Fact]
    public void ASave_KeepsWhatTheOtherEditorChangedMeanwhile()
    {
        new InferpalConfig { DefaultModel = "a", PermissionRules = "" }.Save();
        var mine = InferpalConfig.Load();

        WriteAsTheOtherEditor(c => c.PermissionRules = "deny run_command ^curl");
        mine.DefaultModel = "b";
        mine.Save();

        Assert.Equal("b", OnDisk().DefaultModel);
        Assert.Equal("deny run_command ^curl", OnDisk().PermissionRules);
    }

    /// <summary>Reference arm: a setting both editors changed goes to the one saving last.</summary>
    [Fact]
    public void ASettingBothEditorsChanged_GoesToTheLastSave()
    {
        new InferpalConfig { DefaultModel = "a" }.Save();
        var mine = InferpalConfig.Load();

        WriteAsTheOtherEditor(c => c.DefaultModel = "theirs");
        mine.DefaultModel = "mine";
        mine.Save();

        Assert.Equal("mine", OnDisk().DefaultModel);
    }

    [Fact]
    public void ALaterSave_StillLeavesTheOtherEditorsValue()
    {
        new InferpalConfig { DefaultModel = "a", AgentModel = "x" }.Save();
        var mine = InferpalConfig.Load();

        WriteAsTheOtherEditor(c => c.AgentModel = "y");
        mine.DefaultModel = "b";
        mine.Save();
        mine.Save();

        Assert.Equal("y", OnDisk().AgentModel);
    }
}
