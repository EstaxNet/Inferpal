using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// VS Code: the explicitly-set utility model and auto mode are kept in step with the shared config by what changed since
/// both last AGREED — a value picked in Visual Studio is followed, not set back — and a failure to keep them in step is
/// said, not only logged.
/// </summary>
/// <remarks>
/// ⚠ At every host start the VS Code value was pushed over the shared config as it stood: a copy of what the user last
/// set in VS Code, so a utility model picked in Visual Studio since was set back without a word. And when VS Code refused
/// to write its setting (a settings file with unsaved changes), the panel's choice was undone at the next start, with a
/// log line for all notice. These rules read the TypeScript source.
/// </remarks>
public class ModelRouterSyncTests
{
    private static string Router() => WebviewRebuildTests.TsCode("modelRouterSettings.ts");

    [Fact]
    public void TheSync_FollowsAChangeMadeElsewhere_AndPushesOnlyAChangeMadeHere()
    {
        var sync = WebviewRebuildTests.Body(Router(), "export async function syncModelRouterSettings(");

        var follows = sync.IndexOf("} else if (agreed[key] !== undefined && mine === agreed[key] && shared !== undefined) {", StringComparison.Ordinal);
        var update  = sync.IndexOf("await config.update(key, shared, target);", StringComparison.Ordinal);
        var pushes  = sync.IndexOf("cfg[key] = mine;", StringComparison.Ordinal);
        Assert.True(pushes > 0, "the push moved — the rule reads nothing");   // WITNESS
        Assert.True(follows > 0 && update > follows && pushes > update, "a value changed elsewhere is pushed over");
        Assert.Contains("await recordAgreed(now);", sync);

        // The panel's follow records what both now hold, or the next start would see it as a change made here.
        var follow = WebviewRebuildTests.Body(Router(), "export async function followModelRouterSettings(");
        Assert.Contains("await recordAgreed({ utilityModel: cfg.utilityModel });", follow);
        Assert.Contains("await recordAgreed({ modelRouterAuto: cfg.modelRouterAuto });", follow);
    }

    [Fact]
    public void AFailureToKeepThemInStep_IsSaid()
    {
        var router = Router();
        Assert.Contains("void vscode.window.showWarningMessage(", WebviewRebuildTests.Body(router, "function sayNotKept("));
        foreach (var fn in new[] { "export async function syncModelRouterSettings(", "export async function followModelRouterSettings(" })
            Assert.Contains("sayNotKept(err, log);", WebviewRebuildTests.Body(router, fn));
    }

    [Fact]
    public void TheExtension_SyncsThroughTheAgreedValues()
    {
        var extension = WebviewRebuildTests.TsCode("extension.ts");
        Assert.Contains("initModelRouterSync(context.workspaceState);", WebviewRebuildTests.Body(extension, "export async function activate("));
        Assert.Contains("await syncModelRouterSettings(host, log);", WebviewRebuildTests.Body(extension, "async function pushModelRouterSettings("));
    }
}
