using System.IO;
using System.Linq;
using Inferpal.Config;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// What happens when <c>config.json</c> exists but cannot be read - a write torn by a hard
/// shutdown, a disk error, one comma too many after a hand edit, a file held by another process.
/// </summary>
/// <remarks>
/// The fallback to factory defaults is the SAFE half: the product has to start. The defect was
/// elsewhere, and in two steps - a mute <c>catch { }</c>, so nobody could know the configuration
/// had not been read; then the next <c>Save()</c> (a setting, <c>/model</c>, <c>/hardware</c>,
/// <c>/docs</c>) writing those factory defaults OVER the file. The configuration was then no longer
/// ignored, it was <b>destroyed</b>: backends, per-role models, MCP servers, permission rules.
///
/// It is exactly the 1.6.8 F8 defect - "a typo did not merely get ignored, it DESTROYED the
/// setting" - at whole-file scale, and nobody had looked at that level.
/// </remarks>
[Collection(GlobalConfigPathCollection.Name)]
public class ConfigUnreadableFileTests : IDisposable
{
    private readonly string? _previous = InferpalConfig.OverridePathForTests;
    private readonly string  _dir      =
        Path.Combine(Path.GetTempPath(), "inferpal-tests", $"unreadable-{Guid.NewGuid():N}");
    private readonly string  _path;

    public ConfigUnreadableFileTests()
    {
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "config.json");
        InferpalConfig.OverridePathForTests = _path;
    }

    public void Dispose()
    {
        InferpalConfig.OverridePathForTests = _previous;
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>Writes a recognisable configuration, then tears it in half.</summary>
    private void WriteThenTear()
    {
        new InferpalConfig { DefaultModel = "the-users-model", AgentMaxIterations = 42 }.Save();
        var whole = File.ReadAllText(_path);
        Assert.Contains("the-users-model", whole);   // reference arm: it really is there
        File.WriteAllText(_path, whole[..(whole.Length / 2)]);  // a torn write
    }

    [Fact]
    public void AnUnreadableFile_FallsBackToDefaults_ButSaysSoInDiagnostics()
    {
        WriteThenTear();
        Diagnostics.Clear();

        var loaded = InferpalConfig.Load();

        // The fallback is the safe half: the product starts.
        Assert.Equal(new InferpalConfig().DefaultModel, loaded.DefaultModel);

        // But it is legible. Without this line, a user whose settings have "vanished" has nowhere
        // to look - and /diagnostics is precisely that place.
        var entry = Assert.Single(Diagnostics.Snapshot(), e => e.Context.Contains("InferpalConfig"));
        Assert.Contains("config.json", entry.Detail);
    }

    /// <summary>A MISSING file is the ordinary first-run case: it reports nothing. A channel that
    /// speaks on the ordinary path stops being read.</summary>
    [Fact]
    public void AMissingFile_IsNotAFailure_AndTracesNothing()
    {
        Diagnostics.Clear();

        var loaded = InferpalConfig.Load();

        Assert.Equal(new InferpalConfig().DefaultModel, loaded.DefaultModel);
        Assert.DoesNotContain(Diagnostics.Snapshot(), e => e.Context.Contains("InferpalConfig"));
    }

    /// <summary>
    /// A READABLE file can hold <c>null</c> for a text setting (a hand edit): System.Text.Json writes it as is
    /// into a non-nullable <c>string</c> property, and the failure surfaces far from its cause — every backend
    /// call does <c>BaseUrl.TrimEnd('/')</c>.
    /// </summary>
    [Fact]
    public void ANullTextSetting_FallsBackToItsDefault_AndSaysWhichOne()
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(
            System.Text.Json.JsonSerializer.Serialize(new InferpalConfig { DefaultModel = "the-users-model" }))!.AsObject();

        var nullability = new System.Reflection.NullabilityInfoContext();
        var nulled = 0;
        foreach (var prop in typeof(InferpalConfig).GetProperties())
        {
            if (prop.PropertyType != typeof(string) || !prop.CanWrite
                || nullability.Create(prop).WriteState != System.Reflection.NullabilityState.NotNull) continue;
            var key = prop.GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonPropertyNameAttribute), false)
                          .Cast<System.Text.Json.Serialization.JsonPropertyNameAttribute>().FirstOrDefault()?.Name ?? prop.Name;
            if (key == "defaultModel" || !json.ContainsKey(key)) continue;
            json[key] = null;
            nulled++;
        }
        Assert.True(nulled >= 10, $"only {nulled} text settings nulled — the rule measures nothing.");
        File.WriteAllText(_path, json.ToJsonString());
        Diagnostics.Clear();

        var loaded   = InferpalConfig.Load();
        var defaults = new InferpalConfig();

        Assert.Equal(defaults.BaseUrl, loaded.BaseUrl);
        foreach (var prop in typeof(InferpalConfig).GetProperties())
            if (prop.PropertyType == typeof(string) && prop.CanWrite
                && nullability.Create(prop).WriteState == System.Reflection.NullabilityState.NotNull
                && prop.Name != nameof(InferpalConfig.DefaultModel))
                Assert.True(prop.GetValue(loaded) is not null, $"{prop.Name} came out null.");

        // Reference arm: what was not null is kept, the file is not rejected wholesale.
        Assert.Equal("the-users-model", loaded.DefaultModel);
        var entry = Assert.Single(Diagnostics.Snapshot(), e => e.Context.Contains("InferpalConfig"));
        Assert.Contains("baseUrl", entry.Detail);
    }

    [Fact]
    public void TheNextSave_PreservesTheUnreadableFile_InsteadOfOverwritingIt()
    {
        WriteThenTear();
        var torn = File.ReadAllText(_path);

        // The user changes any setting at all: /model, /hardware, the panel...
        var cfg = InferpalConfig.Load();
        cfg.DefaultModel = "autre-chose";
        cfg.Save();

        // config.json now carries the factory values — expected, something had to be written.
        Assert.DoesNotContain("the-users-model", File.ReadAllText(_path));

        // What must NOT be true is that the old bytes are gone from the disk.
        var rescued = Directory.GetFiles(_dir)
            .Where(f => !string.Equals(f, _path, StringComparison.OrdinalIgnoreCase))
            .Select(File.ReadAllText)
            .ToList();

        Assert.Contains(rescued, text => text == torn);
    }

    /// <summary>The ordinary path leaves no residue: a READABLE file is not archived on every save,
    /// or the configuration folder would fill up with copies.</summary>
    [Fact]
    public void AReadableFile_IsOverwrittenWithoutLeavingACopy()
    {
        new InferpalConfig { DefaultModel = "avant" }.Save();

        var cfg = InferpalConfig.Load();
        cfg.DefaultModel = "apres";
        cfg.Save();

        Assert.Equal("apres", InferpalConfig.Load().DefaultModel);
        Assert.Single(Directory.GetFiles(_dir));
    }
}
