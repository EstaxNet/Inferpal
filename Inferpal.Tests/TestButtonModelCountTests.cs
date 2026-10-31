using System.IO;
using Inferpal.Localization;
using Inferpal.Services.Presentation;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A Test button that reaches a server says how many models it lists ("✓ LM Studio answered — 7 models installed"),
/// in the settings of both editors and for a role's own server, as the validated mockups draw it. Unlisted, it names
/// the server only: never a count it did not read.
/// </summary>
[Collection(CultureSerialCollection.Name)]
public class TestButtonModelCountTests
{
    private static T In<T>(string language, Func<T> read)
    {
        try { Strings.ApplyLanguage(language); return read(); }
        finally { Strings.ApplyLanguage(null); }
    }

    [Fact]
    public void TheLine_SaysTheServerAndItsCount_InTheInterfaceLanguage()
    {
        Assert.Equal("✓ LM Studio answered — 7 models installed", In("en", () => ConnectionStatusPresenter.Answered("LM Studio", 7)));
        Assert.Equal("✓ Ollama answered — 1 model installed", In("en", () => ConnectionStatusPresenter.Answered("Ollama", 1)));
        Assert.Equal("✓ vLLM ответил — установлено 22 модели", In("ru", () => ConnectionStatusPresenter.Answered("vLLM", 22)));
    }

    [Fact]
    public void Unlisted_NamesTheServerOnly() =>
        Assert.Equal("✓ Connected — LM Studio", In("en", () => ConnectionStatusPresenter.Answered("LM Studio", null)));

    /// <summary>A listing that failed comes back empty: zero is never said as a count.</summary>
    [Fact]
    public void AnEmptyListing_NamesTheServerOnly() =>
        Assert.Equal("✓ Connected — Ollama", In("en", () => ConnectionStatusPresenter.Answered("Ollama", 0)));

    [Fact]
    public void BothVisualStudioTestButtons_CountTheListingTheyRead()
    {
        var root = ConversationPersistenceSilenceTests.RepoRoot();
        var main = ConventionCoverageTests.CodeOnly(Path.Combine(root, "Inferpal", "ToolWindow", "InferpalSettingsData.cs"));
        Assert.Contains("ConnectionStatusPresenter.Answered(detectedName!, listed.Listed.Count)", main, StringComparison.Ordinal);
        var roles = ConventionCoverageTests.CodeOnly(Path.Combine(root, "Inferpal", "ToolWindow", "InferpalSettingsData.RoleServers.cs"));
        Assert.Contains("ConnectionStatusPresenter.Answered(answeredAs, models.Listed.Count)", roles, StringComparison.Ordinal);
        Assert.Contains("answeredAs: name", roles, StringComparison.Ordinal);
    }

    [Fact]
    public void TheVsCodePanel_ShowsTheHostsLine()
    {
        var path = Path.Combine(ConversationPersistenceSilenceTests.RepoRoot(), "vscode", "src", "webview", "settings.ts");
        var src = SettingsSchemaDriftTests.NeutralizeTypeScriptComments(File.ReadAllText(path));
        Assert.Contains("msg.ok ? (msg.answered ??", src, StringComparison.Ordinal);
    }
}
