using System.IO;
using Inferpal.Models;
using Inferpal.Services.CodeActions;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The conversation, the same in VS Code as in Visual Studio: a writing step shows the change it made.
/// </summary>
public class ChatParityTests
{
    private static string VsCodeSource(params string[] parts) =>
        SettingsSchemaDriftTests.NeutralizeTypeScriptComments(
            File.ReadAllText(Path.Combine([ConversationPersistenceSilenceTests.RepoRoot(), "vscode", "src", .. parts])));

    // ── A writing step shows its change ───────────────────────────────────────

    /// <summary>The webview draws the change the host sends with the step — coloured lines, the +/− count and the
    /// button that opens it — and the extension hands it on from the notice to the step and to the saved item.</summary>
    [Fact]
    public void VsCode_DrawsTheChangeOfAWritingStep()
    {
        var provider = VsCodeSource("chatViewProvider.ts");
        Assert.Contains("toolDiff: tool.diff ?? undefined,", provider);
        Assert.Contains("diff: item.toolDiff,", provider);

        var webview = VsCodeSource("webview", "main.ts");
        Assert.Contains("toolDiff: msg.diff,", webview);
        Assert.Contains("body.appendChild(renderToolDiff(item.toolDiff));", webview);
        Assert.Contains("line.appendChild(diffStat(item.toolDiff!));", webview);
        // A step does not open by itself for its change: the setting that keeps steps folded is kept, as in Visual Studio.
        Assert.Contains("let open = expanded || item.hasErrors === true;", webview);
        Assert.Contains("post({ type: 'openApprovalDiff', text: item.toolDiff ?? '' })", webview);
    }

    // ── A build that failed has its banner ────────────────────────────────────

    /// <summary>A build task (its group) that fails draws the banner, a build that passes takes it down, a task stopped
    /// before it exited says nothing. The banner reads the Problems panel as it stands — at each change, at each redraw —,
    /// its errors once each, the first written as the model receives it; never a count frozen when the build failed.</summary>
    [Fact]
    public void VsCode_FollowsItsBuildTasks_WithALiveBanner()
    {
        var provider = VsCodeSource("chatViewProvider.ts");
        Assert.Contains("vscode.tasks.onDidEndTaskProcess((e) => {", provider);
        // A watch task stopped ends with a code: it was not a build that failed.
        Assert.Contains("if (e.exitCode === undefined || task.isBackground || !isBuildTask(task)) {", provider);
        Assert.Matches(@"if \(e\.exitCode === 0\) \{\s*this\.failedBuild = null;\s*this\.post\(\{ type: 'buildBanner', banner: null \}\);", provider);
        // Never over a later build's verdict.
        Assert.Matches(@"if \(seq === this\.buildSeq\) \{\s*this\.failedBuild = task\.name;\s*this\.postBuildBanner\(\);", provider);
        Assert.Contains("this.failedBuild === null ? null : { seq: this.buildSeq, ...buildBannerOf(this.failedBuild) }", provider);
        // Redrawn from the panel when it changes, and by a reloaded view.
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(provider, @"if \(this\.failedBuild !== null\) \{\s*this\.postBuildBanner\(\);").Count);
        Assert.Contains("await vscode.commands.executeCommand('workbench.actions.view.problems');", provider);

        var bridge = VsCodeSource("editorBridge.ts");
        Assert.Contains("return task.group?.id === vscode.TaskGroup.Build.id;", bridge);
        Assert.Contains("if (d.severity === vscode.DiagnosticSeverity.Error && !seen.has(key)) {", bridge);
        // One error = one place and one code: MSBuild's console appends the project to the matcher's message.
        Assert.Contains("const key = `${rel}|${d.range.start.line}|${d.range.start.character}|${code !== undefined ? String(code) : d.message}`;", bridge);
        Assert.Contains("firstError: first ? problemLine(first.rel, first.d) : null", bridge);
        // The model receives the panel in the same lines.
        Assert.Contains("(d.severity === vscode.DiagnosticSeverity.Error ? errors : warnings).push(problemLine(rel, d));", bridge);
        Assert.Contains("problems: panelErrors().length", provider);
    }

    /// <summary>Fix them is offered only with errors to send, refused during a turn before anything is attached, and the
    /// banner goes only once the errors are sent; the welcome screen's own banner gives way to it.</summary>
    [Fact]
    public void VsCode_BuildBannersFixThem_OnlyWhenItCanSend()
    {
        var webview = VsCodeSource("webview", "main.ts");
        Assert.Matches(@"if \(banner\.errors > 0\) \{\s*const fix = document\.createElement\('button'\);", webview);
        Assert.Contains("fix.addEventListener('click', () => post({ type: 'fixBuild', text: t('welcomeFixPrompt') }));", webview);
        Assert.Contains("if (problems > 0 && buildBannerEl.hidden) {", webview);
        Assert.Contains("post({ type: 'dismissBuildBanner' });", webview);
        // Closed, it stays closed for that build — a redraw already on its way does not reopen it.
        Assert.Matches(@"function renderBuildBanner\(banner: WvBuildBanner \| null\): void \{\s*if \(banner && banner\.seq === buildBannerClosed\) \{\s*return;", webview);
        Assert.Contains("buildBannerClosed = banner.seq;", webview);
        // No error in the panel: the task's output says why, and nothing offers to send or show what is not there.
        Assert.Contains("t(banner.errors > 0 ? 'buildFailedFrom' : 'buildFailedFromNoErrors', banner.task)", webview);

        var provider = VsCodeSource("chatViewProvider.ts");
        var fix = provider[provider.IndexOf("case 'fixBuild': {", StringComparison.Ordinal)..];
        Assert.True(fix.IndexOf("if (this.busy) {", StringComparison.Ordinal) < fix.IndexOf("await this.resolveMention('problems');", StringComparison.Ordinal),
                    "the chip is attached before a running turn refuses the request");
        Assert.Matches(@"if \(msg\.type === 'fixBuild'\) \{\s*this\.failedBuild = null;\s*this\.post\(\{ type: 'buildBanner', banner: null \}\);\s*\}\s*await this\.send\(msg\.text\);", fix);
    }
}

public partial class HostServerTests
{
    /// <summary>A call that wrote reaches VS Code with its change, in the lines the approval card showed — what Visual
    /// Studio draws in its tool bubble; a call that wrote nothing carries none.</summary>
    [Fact]
    public async Task ChatTool_CarriesTheChangeAWritingCallMade()
    {
        using var h = CreateHarness();
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        h.Fake.OnAgentRun = (history, _, reported, _) =>
        {
            reported?.Invoke(new ToolExecution("read_file", "{\"path\":\"a.cs\"}", "int a = 1;"));
            reported?.Invoke(new ToolExecution("write_file", "{\"path\":\"a.cs\"}", "Written.",
                Diff: new DiffInfo("int a = 1;\nint b = 2;\n", "int a = 1;\nint b = 3;\n", "a.cs")));
            return Task.FromResult(new AgentResult("Done.", [], history));
        };

        await h.Client.InvokeWithParameterObjectAsync<Host.ChatSendResult>("chat/send", new { prompt = "change b", agentMode = false })
            .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        // Notifications are one-way: a round-trip request guarantees they were dispatched.
        await h.Client.InvokeWithParameterObjectAsync<string[]>("models/list", new { })
            .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        Assert.Null(Assert.Single(h.Target.ToolNotes, n => n.Name == "read_file").Diff);
        var written = Assert.Single(h.Target.ToolNotes, n => n.Name == "write_file");
        Assert.Equal(DiffComputer.ComputeText("int a = 1;\nint b = 2;\n", "int a = 1;\nint b = 3;\n", int.MaxValue), written.Diff);
        Assert.Contains("-int b = 2;", written.Diff, StringComparison.Ordinal);
        Assert.Contains("+int b = 3;", written.Diff, StringComparison.Ordinal);
    }
}
