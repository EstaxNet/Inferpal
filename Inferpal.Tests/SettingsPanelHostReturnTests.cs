using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The VS Code settings panel opened while the host is starting, stopped or waiting for a folder loads its form when the
/// host comes back, and says "starting" while it starts.
/// </summary>
/// <remarks>
/// ⚠ The form was asked for once, on the webview's 'ready'. Opened during the start (the panel is one command away at
/// activation), it said "Inferpal host is not running — use Restart Host": a remedy that kills the start in progress.
/// And whatever the cause, it stayed on that message once the host was up, until closed and reopened. A form already
/// shown is never reloaded — it may hold unsaved edits. These rules read the source: the panel is a VS Code webview.
/// </remarks>
public class SettingsPanelHostReturnTests
{
    [Fact]
    public void ThePanel_LoadsItsForm_WhenTheHostComesBack_NeverOverAFormAlreadyShown()
    {
        // The form's loading lives in the bridge shared with the browser settings window; the panel is its VS Code shell.
        var bridge = WebviewRebuildTests.TsCode("settingsBridge.ts");
        var panel  = WebviewRebuildTests.TsCode("settingsPanel.ts");

        // The webview's single 'ready' and the host's return go through the same load.
        var onMessage = WebviewRebuildTests.Body(bridge, "async onMessage(");
        Assert.Contains("case 'ready':\n        await this.load();", onMessage.Replace("\r\n", "\n"), StringComparison.Ordinal);

        var ready = WebviewRebuildTests.Body(panel, "static onHostReady(");
        Assert.Contains("SettingsPanel.hostStarting = false;", ready, StringComparison.Ordinal);
        Assert.Contains("bridge.loadIfNotLoaded();", ready, StringComparison.Ordinal);
        var ifNotLoaded = WebviewRebuildTests.Body(bridge, "loadIfNotLoaded(): void");
        Assert.Contains("if (!this.loaded) {", ifNotLoaded, StringComparison.Ordinal);   // reference arm: unsaved edits
        Assert.Contains("void this.load();", ifNotLoaded, StringComparison.Ordinal);

        // "Loaded" is set only once the form was sent, and only for a form the webview can render.
        var load = WebviewRebuildTests.Body(bridge, "private async loadCore(");
        var init = load.IndexOf("this.shell.post({ type: 'init',", StringComparison.Ordinal);
        var loaded = load.IndexOf("this.loaded = (schema?.tabs.length ?? 0) > 0;", StringComparison.Ordinal);
        Assert.True(init > 0, "the bridge no longer sends the form: the rule reads nothing");   // WITNESS
        Assert.True(loaded > init, "the bridge counts itself loaded before its form was sent");
    }

    [Fact]
    public void WhileTheHostStarts_ThePanelSaysStarting_NotRestart()
    {
        var load = WebviewRebuildTests.Body(WebviewRebuildTests.TsCode("settingsBridge.ts"), "private async loadCore(");
        var starting = load.IndexOf("if (this.shell.hostStarting()) {", StringComparison.Ordinal);
        var restart = load.IndexOf("this.shell.unavailableMessage()", StringComparison.Ordinal);
        Assert.True(restart > 0, "the bridge no longer names an unavailable host: the rule reads nothing");   // WITNESS
        Assert.True(starting > 0 && starting < restart, "a start in progress is told to restart");

        // VS Code's shell answers "starting" from the activator's flag, in words that do not say restart.
        var panel = WebviewRebuildTests.TsCode("settingsPanel.ts");
        Assert.Contains("hostStarting: () => SettingsPanel.hostStarting,", panel, StringComparison.Ordinal);
        Assert.Contains("the settings load as soon as it is ready.", panel, StringComparison.Ordinal);
    }

    [Fact]
    public void TheActivator_TellsThePanel_WhereTheStartIs()
    {
        var core = WebviewRebuildTests.Body(WebviewRebuildTests.TsCode("extension.ts"), "async function startHostCore(");
        var starting = core.IndexOf("SettingsPanel.onHostStarting();", StringComparison.Ordinal);
        var spawn = core.IndexOf("await client.start();", StringComparison.Ordinal);
        var ready = core.IndexOf("SettingsPanel.onHostReady();", StringComparison.Ordinal);
        var failed = core.IndexOf("SettingsPanel.onHostStartFailed();", StringComparison.Ordinal);
        Assert.True(spawn > 0, "the activator no longer starts the host here: the rule reads nothing");   // WITNESS
        Assert.True(starting >= 0 && starting < spawn, "the panel is not told a start began");
        // After the router settings are pushed: the form opens on the config the host will run with.
        Assert.True(ready > core.IndexOf("await pushModelRouterSettings(log);", StringComparison.Ordinal) && ready > spawn,
            "the panel is not told the host is up, or is told before its config is final");
        Assert.True(failed > ready, "the panel is not told the start failed");
    }
}
