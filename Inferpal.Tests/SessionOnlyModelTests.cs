using System.IO;
using Inferpal.Config;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  A model set for the session only is never written by a later save of another setting.
//
//  VS Code gives the host its window's model at start (models/useForSession), in memory. Save()
//  writes what the copy changed since it was read — and that model was a change: a pin, /hardware,
//  /docs or the Agent switch wrote it into config.json, and Visual Studio started on it next time.
//  With no file yet (a fresh install), Save() wrote the whole instance, the model included.
// ──────────────────────────────────────────────────────────────────────────────────────────────
public sealed class SessionOnlyModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"session-model-{Guid.NewGuid():N}");
    private string ConfigFile => Path.Combine(_dir, "config.json");

    public SessionOnlyModelTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Theory]
    [InlineData(true)]    // config.json exists: the save merges into it
    [InlineData(false)]   // a fresh install: no file yet
    public void SavingAnotherSetting_LeavesTheSessionModelOutOfTheFile(bool fileExists)
    {
        if (fileExists) File.WriteAllText(ConfigFile, "{ \"defaultModel\": \"shared-model\" }");
        var config = new InferpalConfig { SavePathForTests = ConfigFile, DefaultModel = "shared-model" };

        config.UseDefaultModelForSession("window-model");
        config.AgentModeEnabled = !config.AgentModeEnabled;   // a pin, /hardware, the Agent switch: any other save
        config.Save();

        var saved = File.ReadAllText(ConfigFile);
        Assert.Contains("agentModeEnabled", saved);   // WITNESS: the save wrote the file
        Assert.DoesNotContain("window-model", saved);
        Assert.Equal("window-model", config.DefaultModel);   // the session keeps using it
    }

    [Fact]
    public void AModelThePersonPicksAfterwards_IsSaved()
    {
        // Reference arm: the session-only model is not a lock — a real pick is a change, and it is written.
        var config = new InferpalConfig { SavePathForTests = ConfigFile };
        config.UseDefaultModelForSession("window-model");

        config.DefaultModel = "picked-model";
        config.Save();

        Assert.Contains("picked-model", File.ReadAllText(ConfigFile));
    }
}
