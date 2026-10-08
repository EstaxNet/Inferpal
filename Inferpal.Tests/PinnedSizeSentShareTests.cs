using System.IO;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Services.Presentation;
using Inferpal.Services.Prompting;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The Context page's size of a pinned file is what the prompt CARRIES of it — the file sections share one budget —
/// with the whole size beside it when the file is cut.
/// </summary>
/// <remarks>
/// ⚠ Estimated on the whole file, a large pinned log read "30.0k tokens" where each question carried about 2k, while the
/// usage bar and the X-Ray on the same page measured the cut text.
/// </remarks>
public sealed class PinnedSizeSentShareTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("inferpal-pin-size-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private string Pin(string name, string text)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, text);
        return path;
    }

    private static XRayPanelModel Prompt(InferpalConfig config) => XRayPanelPresenter.Build(
        new SystemPromptBuilder(config).BuildSections("base"), null, historyTokens: 0,
        contextWindow: config.ContextWindowSize, toolTokens: 0);

    private static string Words(int count) => string.Join(' ', Enumerable.Range(0, count).Select(i => $"line{i}"));

    [Fact]
    public void APinnedFileTheBudgetCuts_ShowsWhatGoes_AndItsWholeSize()
    {
        var big    = Pin("build.log", Words(20_000));                    // far past the default window's budget
        var config = new InferpalConfig { PinnedContextFiles = big };   // default window: 8 192 tokens
        var prompt = Prompt(config);

        var size = Assert.Single(SettingsWidgets.PinnedSizes([big], prompt)).Size;

        var sent  = prompt.Sections.Single(s => s.Id == "Pinned|" + big).Tokens;
        var whole = Services.Commands.XRayCommandHandler.EstimateTokens(File.ReadAllText(big));
        Assert.True(sent < whole, "the witness file is not cut: this test reads nothing");   // WITNESS
        Assert.Equal(Strings.PinnedFileTokensSent(SettingsWidgets.Amount(sent), SettingsWidgets.Amount(whole)), size);
    }

    /// <summary>Reference arms: a file the prompt carries whole shows its size; without a measured prompt, as before.</summary>
    [Fact]
    public void AFileCarriedWhole_ShowsItsSize()
    {
        var small  = Pin("notes.md", Words(50));
        var config = new InferpalConfig { PinnedContextFiles = small };
        var whole  = Services.Commands.XRayCommandHandler.EstimateTokens(File.ReadAllText(small));

        Assert.Equal(Strings.PinnedFileTokens(SettingsWidgets.Amount(whole)),
                     Assert.Single(SettingsWidgets.PinnedSizes([small], Prompt(config))).Size);
        Assert.Equal(Strings.PinnedFileTokens(SettingsWidgets.Amount(whole)),
                     Assert.Single(SettingsWidgets.PinnedSizes([small])).Size);
    }

    [Fact]
    public void BothEditors_ReadTheSizesAgainstThePromptAsSent()
    {
        var root = ConversationPersistenceSilenceTests.RepoRoot();
        var host = ConventionCoverageTests.CodeOnly(Path.Combine(root, "Inferpal.Host", "HostSettingsWidgets.cs"));
        Assert.Contains(".Where(l => l.Length > 0), prompt);", host);

        var vs = ConventionCoverageTests.CodeOnly(Path.Combine(root, "Inferpal", "ToolWindow", "InferpalSettingsData.Widgets.cs"));
        Assert.Contains("SettingsWidgets.PinnedSizes(PinnedFileRows.Select(r => r.Field1), _lastPrompt);", vs);
        Assert.Contains("_lastPrompt = xray;", vs);
    }
}
