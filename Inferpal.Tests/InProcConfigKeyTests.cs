using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Inferpal.Config;
using Inferpal.GhostText;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  Visual Studio's ghost text follows its three settings.
//
//  The in-process reader looked up "InlineCompletionEnabled", "…Mode" and "…Model" — the C# property
//  names — in a config.json written with camelCase JSON names, by a case-sensitive lookup. None ever
//  matched: turned off, ghost text kept requesting; a small completion model was never used (the chat
//  model answered); the Fast and High-accuracy modes never applied. VS Code was not affected.
// ──────────────────────────────────────────────────────────────────────────────────────────────
public class InProcConfigKeyTests
{
    [Theory]
    [InlineData(nameof(InferpalConfig.InlineCompletionEnabled), InProcConfig.KeyEnabled)]
    [InlineData(nameof(InferpalConfig.InlineCompletionMode),    InProcConfig.KeyMode)]
    [InlineData(nameof(InferpalConfig.InlineCompletionModel),   InProcConfig.KeyModel)]
    public void TheInProcReader_LooksUpTheKeyTheConfigIsWrittenWith(string property, string key)
    {
        var written = typeof(InferpalConfig).GetProperty(property)!
            .GetCustomAttribute<JsonPropertyNameAttribute>()?.Name;

        Assert.NotNull(written);   // WITNESS: the property carries the name the file uses
        Assert.Equal(written, key);
    }

    [Fact]
    public void TheInProcReader_FollowsWhatTheSettingsSave()
    {
        var saved = JsonSerializer.Serialize(new InferpalConfig
        {
            InlineCompletionEnabled = false, InlineCompletionMode = "Fast", InlineCompletionModel = "mellum",
        });

        var read = InProcConfig.Parse(saved);

        Assert.False(read.Enabled);
        Assert.Equal("Fast", read.Mode);
        Assert.Equal("mellum", read.Model);
    }

    [Fact]
    public void AKeyTheFileDoesNotHold_KeepsItsDefault()
    {
        // Reference arm: InferpalConfig only writes what was touched; absent keys are the defaults.
        var read = InProcConfig.Parse("{}");

        Assert.True(read.Enabled);
        Assert.Equal("Default", read.Mode);
        Assert.Null(read.Model);
    }
}
