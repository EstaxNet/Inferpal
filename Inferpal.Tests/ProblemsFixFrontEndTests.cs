using System.IO;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// "Fix with Inferpal" from where problems are listed: Visual Studio's Error List menu and VS Code's quick fixes pass
/// the problems chosen there — and only those — to the Core's question.
/// </summary>
/// <remarks>What runs in devenv (the menu's placement, the list read on the UI thread) is held at the source: no test
/// drives an editor. The Core's half is tested where it runs (<see cref="FixProblemsTests"/>,
/// <c>HostServerTests.ProblemsFix_*</c>).</remarks>
public sealed class ProblemsFixFrontEndTests
{
    private static string Root => ConversationPersistenceSilenceTests.RepoRoot();

    [Fact]
    public void VisualStudiosCommand_SitsInTheErrorListMenu_AndSendsTheListsSelection()
    {
        var command = ConventionCoverageTests.CodeOnly(Path.Combine(Root, "Inferpal", "Commands", "FixErrorListCommand.cs"));

        // guidSHLMainMenu / IDM_VS_CTXT_ERRORLIST (vsshlids.h).
        var menus = ConventionCoverageTests.CodeOnly(Path.Combine(Root, "Inferpal", "Commands", "VsctMenus.cs"));
        Assert.Contains("ShlMainMenu = new(\"d309f791-903f-11d0-9efc-00a0c911004f\")", menus, StringComparison.Ordinal);
        Assert.Matches(@"ErrorListContext\s*=\s*0x0405;", menus);
        Assert.Contains("GroupPlacement.VsctParent(VsctMenus.ShlMainMenu, VsctMenus.ErrorListContext", command, StringComparison.Ordinal);
        // Asked, refused with its cause, the ignore files declared before the attachment reads a file, then sent with the
        // chat's tools (a model override would make it a tool-less code action).
        Assert.Matches(@"ErrorListSelectionSignal\.AskAsync\([\s\S]*?ProblemsFix\.Refusal\(answer[\s\S]*?ShowPromptAsync\(refusal"
                       + @"[\s\S]*?Exclusions\.Follow\(\)[\s\S]*?ProblemsFix\.NothingEditable\(answer!\.Problems, SolutionExtent\.ActiveRoot\(\)\)"
                       + @"[\s\S]*?ShowPromptAsync\(outOfReach[\s\S]*?ProblemsFix\.ForAsync\(answer\.Problems, _overlay"
                       + @"[\s\S]*?SetPendingPrompt\(question, modelOverride: null",
                       command);
        // The package serves the request: the editor listener alive alone says nothing about it.
        Assert.Contains("InProcAliveSignal.TryRead() is { HasPackage: true }", command, StringComparison.Ordinal);
    }

    [Fact]
    public void TheInProcessHalf_ReadsTheListsSelection_OnTheUiThread_AndAnswers()
    {
        var watcher = ConventionCoverageTests.CodeOnly(Path.Combine(Root, "Inferpal.InProc", "GhostText", "ErrorListSelectionWatcher.cs"));
        var package = ConventionCoverageTests.CodeOnly(Path.Combine(Root, "Inferpal.InProc", "GhostText", "GhostTextPackage.cs"));

        Assert.Matches(@"SwitchToMainThreadAsync\(\)[\s\S]*?Read\(ask\.Id\)[\s\S]*?ErrorListSelectionSignal\.Answer\(answer\)", watcher);
        Assert.Contains("foreach (var entry in list.TableControl.SelectedEntries)", watcher, StringComparison.Ordinal);
        Assert.Contains("ErrorListSelectionWatcher.Start()", package, StringComparison.Ordinal);
        Assert.Contains("ErrorListSelectionWatcher.Stop()", package, StringComparison.Ordinal);
    }

    [Fact]
    public void VsCode_OffersTheFix_AmongTheQuickFixesOfEveryDiagnostic()
    {
        var provider  = File.ReadAllText(Path.Combine(Root, "vscode", "src", "problemsFix.ts"));
        var extension = File.ReadAllText(Path.Combine(Root, "vscode", "src", "extension.ts"));

        Assert.Contains("new vscode.CodeAction(t('Fix with Inferpal'), vscode.CodeActionKind.QuickFix)", provider, StringComparison.Ordinal);
        Assert.Contains("action.diagnostics = [...context.diagnostics]", provider, StringComparison.Ordinal);
        // Outside the workspace the tools write nothing: not offered there.
        Assert.Contains("!vscode.workspace.getWorkspaceFolder(document.uri)", provider, StringComparison.Ordinal);
        Assert.Contains("arguments: [document.uri, [...context.diagnostics]]", provider, StringComparison.Ordinal);
        Assert.Contains("registerCodeActionsProvider({ scheme: 'file' }, new FixProblemsProvider()", extension, StringComparison.Ordinal);
        Assert.Contains("chatView.fixProblems(diagnostics.map((d) => problemEntry(at, d)))", extension, StringComparison.Ordinal);
        // Run without the quick fix's arguments (a key): the problems at the cursor, or a sentence — never a throw.
        Assert.Contains("if (!uri || !Array.isArray(diagnostics))", extension, StringComparison.Ordinal);
        Assert.Contains("t('No problem at the cursor.')", extension, StringComparison.Ordinal);
    }

    [Fact]
    public void VsCode_AsksTheHost_ThenSendsItsQuestionWithTheChip_NeverDuringATurn()
    {
        var provider = Path.Combine(Root, "vscode", "src", "chatViewProvider.ts");
        // The quick fix asks the host for its question, answered with the chat's tools (fixing is editing).
        Assert.Contains("this.askWithChip((host) => host.problemsFix(problems), 'fix problems', false)",
                        ConversationPersistenceSilenceTests.TsMethodBody(provider, "fixProblems(problems: ProblemEntry[])"),
                        StringComparison.Ordinal);
        var body = ConversationPersistenceSilenceTests.TsMethodBody(provider, "private async askWithChip(");

        var busy   = body.IndexOf("if (this.busy)", StringComparison.Ordinal);
        var asks   = body.IndexOf("ask = await build(host)", StringComparison.Ordinal);
        var again  = body.IndexOf("if (this.busy)", asks, StringComparison.Ordinal);
        var chip   = body.IndexOf("this.addChip(ask.chip, ask.content)", StringComparison.Ordinal);
        var aside  = body.IndexOf("const draft = this.pendingAttachments.filter((a) => !a.auto);", StringComparison.Ordinal);
        var send   = body.IndexOf("this.send(ask.question", StringComparison.Ordinal);
        var back   = body.IndexOf("this.pendingAttachments = [...draft, ...this.pendingAttachments.filter((a) => a !== chip)];",
                                  StringComparison.Ordinal);
        // Refused before and after the host's answer (a turn begun meanwhile); the draft's chips set aside, so the
        // gesture's chip goes alone; and given back after — with a gesture's chip the question did not take taken back:
        // left in the composer, it rode the next question.
        Assert.True(busy >= 0 && asks > busy && again > asks && aside > again && chip > aside && send > chip && back > send, body);
        Assert.Contains("} finally {", body[send..back], StringComparison.Ordinal);
        Assert.Contains("this.hostForGesture()", body, StringComparison.Ordinal);
        Assert.Contains("this.gestureFailed(", body, StringComparison.Ordinal);
        // The host's refusal (no problem the agent can edit, nothing paused) is said, and nothing is sent.
        Assert.Matches(@"if \(ask\.refusal\) \{\s*void vscode\.window\.showWarningMessage\(ask\.refusal\);\s*return;", body);
        Assert.InRange(body.IndexOf("if (ask.refusal)", StringComparison.Ordinal), asks, chip);
    }
}
