using System.IO;
using Inferpal.Localization;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The texts the connection and the first run show name the CONFIGURED backend, as the "cannot reach" message
/// already did. "✓ Reconnected to Ollama", a Retry button that offered to "check connection to Ollama", and a first run
/// that told an LM Studio with nothing downloaded "Ollama is running … ollama pull llama3.1" — a command of another
/// server, for a model the documentation no longer recommends — were said to every user who is not on Ollama.
/// </summary>
// Switches Strings.OverrideCulture (process-wide): serialized collection.
[Collection(CultureSerialCollection.Name)]
public class ConnectionTextsNameTheBackendTests
{
    private static readonly string[] Cultures = ["en", "fr", "de", "es", "it", "ja", "ko", "pl", "ru", "zh-CN"];

    private static void InEveryLanguage(Action<string> check)
    {
        var previous = Strings.OverrideCulture?.Name;
        try
        {
            foreach (var culture in Cultures)
            {
                Strings.ApplyLanguage(culture);
                check(culture);
            }
        }
        finally { Strings.ApplyLanguage(previous); }
    }

    [Fact]
    public void TheConnectionTexts_NameTheBackendTheyAreGiven_InEveryLanguage() => InEveryLanguage(culture =>
    {
        foreach (var text in new[]
                 {
                     Strings.MsgHeartbeatRestored("LM Studio"),
                     Strings.TooltipRetryConnection("LM Studio"),
                     Strings.MsgFirstRunNoModels("LM Studio", canPull: true),
                     Strings.MsgFirstRunBackendDown("http://localhost:1234", "LM Studio"),
                 })
            Assert.True(text.Contains("LM Studio", StringComparison.Ordinal), $"{culture}: {text}");

        foreach (var text in new[]
                 {
                     Strings.MsgHeartbeatRestored("LM Studio"), Strings.TooltipRetryConnection("LM Studio"),
                     Strings.MsgFirstRunNoModels("LM Studio", canPull: true), Strings.MsgNoUrl,
                 })
            Assert.False(text.Contains("Ollama", StringComparison.Ordinal), $"{culture}: {text}");
    });

    [Fact]
    public void TheFirstRun_RecommendsNoModelOfItsOwn_AndPointsAtTheMeasuredOnes() => InEveryLanguage(culture =>
    {
        var text = Strings.MsgFirstRunNoModels("Ollama", canPull: true);

        Assert.DoesNotContain("llama3.1", text);
        Assert.Contains("docs/models.md#by-graphics-card", text);
        Assert.Contains("/models pull", text);
    });

    [Fact]
    public void TheDownloadHint_IsOnlyGivenWhereTheBackendCanDownload()
    {
        // Reference arm: an OpenAI-compatible server has no download command; offering one is a remedy that cannot work.
        Assert.DoesNotContain("/models pull", Strings.MsgFirstRunNoModels("OpenAI-compatible", canPull: false));
    }

    [Theory]
    [InlineData("Inferpal", "ToolWindow", "InferpalToolWindowData.Connection.cs")]
    [InlineData("Inferpal.Host", "HostServer.cs")]
    public void BothFrontEnds_SayWhichBackendCameBack(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var code = ConventionCoverageTests.CodeOnly(Path.Combine(dir!.FullName, Path.Combine(parts)));

        var at = code.IndexOf("Strings.MsgHeartbeatRestored(", StringComparison.Ordinal);
        Assert.True(at >= 0, "the restored notice is gone from this front-end");          // WITNESS
        Assert.Contains("DisplayName(", code.Substring(at, 160), StringComparison.Ordinal);
    }
}
