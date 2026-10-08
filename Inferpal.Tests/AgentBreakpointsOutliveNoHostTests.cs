using System.Text.RegularExpressions;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// VS Code: the breakpoints the assistant set go with the host that set them — on a restart and on a crash.
/// </summary>
/// <remarks>
/// ⚠ The host records which breakpoints its agent added (<c>AgentBreakpoints</c>) and removes them when the debugging
/// session ends — a record held in its process only. Restarted (a server setting) or crashed, the new host knew nothing
/// of them: <c>/debug stop</c> removed nothing, the editor saved them with the workspace, and the user's next session
/// stopped on lines they never chose. The bridge, shared by every host, records what it added and releases it when its
/// host goes away. These rules read the TypeScript source.
/// </remarks>
public class AgentBreakpointsOutliveNoHostTests
{
    [Fact]
    public void TheBridge_RecordsWhatItAddsForTheHost_AndReleasesIt()
    {
        var bridge = WebviewRebuildTests.TsCode("debugBridge.ts");

        var add = WebviewRebuildTests.Body(bridge, "async addBreakpoint(");
        var added   = add.IndexOf("vscode.debug.addBreakpoints([created]);", StringComparison.Ordinal);
        var tracked = add.IndexOf("this.agentBreakpointIds.add(created.id);", StringComparison.Ordinal);
        Assert.True(added > 0, "the bridge no longer adds the breakpoint it records: the rule reads nothing");   // WITNESS
        Assert.True(tracked > added, "a breakpoint added for the host is not recorded");

        // One the host removes itself is no longer the bridge's to release.
        Assert.Contains("this.agentBreakpointIds.delete(b.id);", WebviewRebuildTests.Body(bridge, "async removeBreakpoint("));

        var release = WebviewRebuildTests.Body(bridge, "releaseAgentBreakpoints(): number");
        Assert.Contains("vscode.debug.breakpoints.filter((b) => this.agentBreakpointIds.has(b.id))", release);
        Assert.Contains("vscode.debug.removeBreakpoints(doomed);", release);
    }

    [Fact]
    public void TheBreakpointsGo_WhenTheHostGoes_AndOnlyThen()
    {
        var extension = WebviewRebuildTests.TsCode("extension.ts");
        var core = WebviewRebuildTests.Body(extension, "async function startHostCore(");

        var stop    = core.IndexOf("await host.stop();", StringComparison.Ordinal);
        var release = core.IndexOf("debugBridge?.releaseAgentBreakpoints();", StringComparison.Ordinal);
        var crash   = core.IndexOf("onCrash: () => {", StringComparison.Ordinal);
        Assert.True(stop > 0 && crash > 0, "the host is no longer stopped or watched here: the rule reads nothing");   // WITNESS
        Assert.True(release > stop && release < crash, "a restarted host leaves its agent's breakpoints behind");
        Assert.True(core.IndexOf("debugBridge?.releaseAgentBreakpoints();", crash, StringComparison.Ordinal) > crash,
            "a crashed host leaves its agent's breakpoints behind");

        // Reference arm: a host still running keeps them — released at these two places only.
        Assert.Equal(2, Regex.Matches(extension, @"releaseAgentBreakpoints\(\)").Count);
    }
}
