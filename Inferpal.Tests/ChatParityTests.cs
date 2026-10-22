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
