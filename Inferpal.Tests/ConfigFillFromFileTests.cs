using System.IO;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Services.Hardware;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// An automatic decision that fills a setting nobody set — a VRAM budget detected, a model adopted at a first run —
/// asks the FILE whether it is still unset, not this window's copy: a value the other window set since is taken, never
/// overwritten.
/// </summary>
/// <remarks>
/// ⚠ Each window of both editors reads <c>config.json</c> once. A plain <c>/hardware</c> in a second window saw a budget
/// of 0 after the user typed <c>/hardware 10</c> in the first, detected the whole GPU and wrote 24 over it; a first run
/// completing late in Visual Studio wrote its own pick over the model chosen in VS Code meanwhile.
/// </remarks>
[Collection(GlobalConfigPathCollection.Name)]
public class ConfigFillFromFileTests : IDisposable
{
    private readonly string? _previous = InferpalConfig.OverridePathForTests;
    private readonly string  _path     =
        Path.Combine(Path.GetTempPath(), "inferpal-tests", $"fill-{Guid.NewGuid():N}.json");

    public ConfigFillFromFileTests()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        InferpalConfig.OverridePathForTests = _path;
    }

    public void Dispose()
    {
        InferpalConfig.OverridePathForTests = _previous;
        try { File.Delete(_path); } catch { }
    }

    private void WriteAsTheOtherWindow(Action<InferpalConfig> change)
    {
        var other = JsonSerializer.Deserialize<InferpalConfig>(File.ReadAllText(_path))!;
        change(other);
        File.WriteAllText(_path, JsonSerializer.Serialize(other));
    }

    private InferpalConfig OnDisk() => JsonSerializer.Deserialize<InferpalConfig>(File.ReadAllText(_path))!;

    [Fact]
    public async Task ABudgetSetInAnotherWindow_IsTaken_NeverDetectedOver()
    {
        // A remote backend: whatever happens, this test never probes the machine's GPU.
        new InferpalConfig { BaseUrl = "http://192.0.2.1:11434", VramBudgetGb = 0 }.Save();
        var mine = InferpalConfig.Load();

        WriteAsTheOtherWindow(c => c.VramBudgetGb = 10);
        await HardwareProfile.EnsureBudgetAsync(mine, CancellationToken.None);

        Assert.Equal(10, mine.VramBudgetGb);
        Assert.Equal(10, OnDisk().VramBudgetGb);
    }

    [Fact]
    public void AModelChosenInAnotherWindow_IsTaken_AndALaterSaveLeavesItToTheFile()
    {
        new InferpalConfig().Save();
        var mine = InferpalConfig.Load();

        WriteAsTheOtherWindow(c => c.DefaultModel = "devstral");
        Assert.True(mine.FillFromFile(nameof(InferpalConfig.DefaultModel)));
        Assert.Equal("devstral", mine.DefaultModel);

        // Taken from the file, it is not this copy's change: the other window picking again later is not reverted.
        WriteAsTheOtherWindow(c => c.DefaultModel = "qwen3:8b");
        mine.AgentModel = "some-agent";
        mine.Save();
        Assert.Equal("qwen3:8b", OnDisk().DefaultModel);
    }

    /// <summary>Reference arm: a value this window set stands, whatever the file holds.</summary>
    [Fact]
    public void AValueSetHere_IsNotReplaced()
    {
        new InferpalConfig { DefaultModel = "mine" }.Save();
        var mine = InferpalConfig.Load();

        WriteAsTheOtherWindow(c => c.DefaultModel = "theirs");

        Assert.False(mine.FillFromFile(nameof(InferpalConfig.DefaultModel)));
        Assert.Equal("mine", mine.DefaultModel);
    }

    /// <summary>Reference arm: a file that holds the factory value too leaves the decision to be made.</summary>
    [Fact]
    public void AFileStillUnset_LeavesTheDecisionToBeMade()
    {
        new InferpalConfig().Save();
        var mine = InferpalConfig.Load();

        Assert.False(mine.FillFromFile(nameof(InferpalConfig.VramBudgetGb)));
        Assert.Equal(0, mine.VramBudgetGb);
    }

    [Fact]
    public void EveryAutomaticModelDecision_AsksTheFileFirst()
    {
        var root = ConversationPersistenceSilenceTests.RepoRoot();
        string Code(params string[] parts) => ConventionCoverageTests.CodeOnly(Path.Combine([root, .. parts]));

        var connection = Code("Inferpal", "ToolWindow", "InferpalToolWindowData.Connection.cs");
        var firstRun   = Code("Inferpal", "ToolWindow", "InferpalToolWindowData.Rag.cs");
        var host       = Code("Inferpal.Host", "HostServer.cs");

        foreach (var (name, code, decision) in new[]
                 {
                     ("adopt (Visual Studio)", connection, "ModelCatalog.FirstModelToAdopt(_config, listed)"),
                     ("first run (Visual Studio)", firstRun, "PickBestChatModel(chatModels)"),
                     ("adopt (VS Code host)", host, "ModelCatalog.FirstModelToAdopt(s.Config, listed)"),
                 })
        {
            var decides = code.IndexOf(decision, StringComparison.Ordinal);
            var asks    = code.IndexOf("FillFromFile(nameof(InferpalConfig.DefaultModel))", StringComparison.Ordinal);
            Assert.True(decides > 0, $"{name}: the decision moved — this rule reads nothing");   // WITNESS
            Assert.True(asks >= 0 && asks < decides, $"{name} decides on its own copy before asking the file");
        }
    }
}
