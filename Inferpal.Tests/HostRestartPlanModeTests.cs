using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ VS Code's host is a separate process that restarts (a provider or URL saved in the settings, a crash followed by
/// Restart, a new workspace root) and a new host session starts in AGENT mode. The extension kept showing Plan: the
/// next question ran with the write tools under a switch that says read-only, and clicking Plan again did nothing,
/// since the switch already said so. Visual Studio has no separate host, so its plan mode cannot drift this way.
/// These rules read the source (the extension has no TypeScript test runner).
/// </summary>
public class HostRestartPlanModeTests
{
    private static string OnHostReady() =>
        WebviewRebuildTests.Body(WebviewRebuildTests.TsCode("chatViewProvider.ts"), "async onHostReady(");

    [Fact]
    public void AHostThatRestarted_IsPutBackInPlanMode()
    {
        var ready = OnHostReady();
        Assert.Contains("if (this.planMode) {", ready, StringComparison.Ordinal);
        Assert.Contains("this.planMode = await host.planMode(true);", ready, StringComparison.Ordinal);

        // Before the conversation is handed back: that path returns early, and the mode would never be restored.
        var restore = ready.IndexOf("host.planMode(true)", StringComparison.Ordinal);
        var rebuild = ready.IndexOf("host.sessionSave('last_session'", StringComparison.Ordinal);
        Assert.True(rebuild > 0, "the rebuild after a restart moved: the order is no longer measured");   // WITNESS
        Assert.True(restore < rebuild, "plan mode is restored after an early return can skip it");
    }

    [Fact]
    public void ARefusedRestore_ShowsTheModeTheHostReallyRuns_AndSaysSo()
    {
        var ready = OnHostReady();
        Assert.Contains("this.planMode = false;", ready, StringComparison.Ordinal);
        Assert.Contains("t('The assistant restarted and could not return to plan mode: the agent can edit files again.')",
                        ready, StringComparison.Ordinal);
    }
}
