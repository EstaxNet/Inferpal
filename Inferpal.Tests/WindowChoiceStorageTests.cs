using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A VS Code window keeps its own model and mode — in VS Code's storage for the workspace, never in the workspace
/// settings, which are the repository's <c>.vscode/settings.json</c>: written there, they show in <c>git status</c>, go
/// into the review of the change and are committed by <c>/commit-exec</c>, and nothing says so. A setting already there
/// (written by hand, or by an earlier version) still applies until a choice is made in the window.
/// </summary>
/// <remarks>The extension does not run headless: its source is read, without its comments.</remarks>
public sealed class WindowChoiceStorageTests
{
    private static string Provider => WebviewRebuildTests.TsCode("chatViewProvider.ts");

    [Fact]
    public void TheWindowsModelAndMode_AreNeverWrittenToTheSettings()
    {
        var src = Directory.EnumerateFiles(Path.Combine(ConventionCoverageTests.RepoRoot(), "vscode", "src"), "*.ts",
                                           SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}test{Path.DirectorySeparatorChar}"))
            .ToList();
        Assert.True(src.Count > 10, "the extension's sources were not found: this rule would read nothing");   // WITNESS
        var code = string.Join("\n", src.Select(f => SettingsSchemaDriftTests.NeutralizeTypeScriptComments(File.ReadAllText(f))));
        Assert.DoesNotMatch(new Regex(@"\.update\(\s*'(model|agentMode)'"), code);
    }

    /// <summary>The window's pick wins while the setting is still the one it was made under; a setting changed since —
    /// even while the window was closed (a pull, an edit by hand) — takes over.</summary>
    [Fact]
    public void TheWindowsChoice_WinsUntilTheSettingChanges()
    {
        var model = WebviewRebuildTests.Body(Provider, "private windowModel(");
        Assert.Contains("workspaceState.get<WindowChoice<string>>(WINDOW_MODEL_KEY)", model, StringComparison.Ordinal);
        Assert.Contains("pick && pick.setting === setting ? pick.value : setting", model, StringComparison.Ordinal);
        var mode = WebviewRebuildTests.Body(Provider, "private windowAgentMode(");
        Assert.Contains("workspaceState.get<WindowChoice<boolean>>(WINDOW_AGENT_MODE_KEY)", mode, StringComparison.Ordinal);
        Assert.Contains("pick && pick.setting === setting ? pick.value : setting", mode, StringComparison.Ordinal);

        // Each pick is kept with the setting it was made under.
        Assert.Contains("{ value: model, setting }", WebviewRebuildTests.Body(Provider, "private async setWindowModel("), StringComparison.Ordinal);
        Assert.Contains("{ value: enabled, setting }", WebviewRebuildTests.Body(Provider, "private async setWindowAgentMode("), StringComparison.Ordinal);

        // Every reader goes through them: the setting is read nowhere else (the two followers aside — they ARE the
        // setting's change).
        var outside = Provider
            .Replace(model, string.Empty).Replace(mode, string.Empty)
            .Replace(WebviewRebuildTests.Body(Provider, "private async setWindowModel("), string.Empty)
            .Replace(WebviewRebuildTests.Body(Provider, "private async setWindowAgentMode("), string.Empty)
            .Replace(WebviewRebuildTests.Body(Provider, "async followModelSetting("), string.Empty)
            .Replace(WebviewRebuildTests.Body(Provider, "async followAgentModeSetting("), string.Empty);
        Assert.DoesNotContain("get<string>('model'", outside, StringComparison.Ordinal);
        Assert.DoesNotContain("get<boolean>('agentMode'", outside, StringComparison.Ordinal);
    }

    /// <summary>A setting changed by hand becomes the window's choice: without the follower, a switch made once in the
    /// chat wins over it for good.</summary>
    [Fact]
    public void ASettingChangedByHand_IsFollowed()
    {
        var listener = WebviewRebuildTests.TsCode("extension.ts");
        Assert.Contains("affectsConfiguration('inferpal.model')", listener, StringComparison.Ordinal);   // WITNESS
        Assert.Contains("affectsConfiguration('inferpal.agentMode')", listener, StringComparison.Ordinal);
        Assert.Contains("chatView.followAgentModeSetting()", listener, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("private async setupApplied(", "this.setWindowModel(model)")]
    [InlineData("case 'pickModel':", "this.setWindowModel(msg.model)")]
    [InlineData("private async setMode(", "this.setWindowAgentMode(agent)")]
    [InlineData("async followModelSetting(", "this.setWindowModel(configured)")]
    [InlineData("async followAgentModeSetting(", "this.setWindowAgentMode(configured)")]
    [InlineData("async configSaved(", "this.setWindowModel(cfg.defaultModel)")]
    [InlineData("async configSaved(", "this.setWindowAgentMode(cfg.agentModeEnabled)")]
    [InlineData("if (e.name === 'model' && e.value)", "this.setWindowModel(e.value)")]   // /model in the chat
    public void EveryChoiceMadeInTheWindow_IsKeptForIt(string site, string write)
    {
        var at = Provider.IndexOf(site, StringComparison.Ordinal);
        Assert.True(at >= 0, $"{site} not found: this rule would measure nothing");   // WITNESS
        // A method is read whole; a case or a branch, over its next lines.
        var region = site.EndsWith('(') ? WebviewRebuildTests.Body(Provider, site)
                                        : Provider.Substring(at, Math.Min(1500, Provider.Length - at));
        Assert.Contains(write, region, StringComparison.Ordinal);
    }
}
