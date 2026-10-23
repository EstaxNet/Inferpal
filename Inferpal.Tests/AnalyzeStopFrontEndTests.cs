using System.IO;
using System.Text.Json;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// "Analyze with Inferpal" on a debugger stop, from where the debugger shows it: Visual Studio's Debug menu and its call
/// stack and variables windows, VS Code's call stack and variables views — the Core's question, asked without tools.
/// </summary>
/// <remarks>The menus and the send are held at the source: no test drives an editor. The Core's half is tested where it
/// runs (<see cref="DebugStopAnalysisTests"/>, <c>HostServerTests.DebugAnalyze_*</c>).</remarks>
public sealed class AnalyzeStopFrontEndTests
{
    private static string Root => ConversationPersistenceSilenceTests.RepoRoot();

    [Fact]
    public void VisualStudiosCommand_SitsInTheDebugMenus_AndAsksWithoutTools()
    {
        var command = ConventionCoverageTests.CodeOnly(Path.Combine(Root, "Inferpal", "Commands", "AnalyzeStopCommand.cs"));
        var menus   = ConventionCoverageTests.CodeOnly(Path.Combine(Root, "Inferpal", "Commands", "VsctMenus.cs"));

        // guidSHLMainMenu / IDM_VS_MENU_DEBUG (vsshlids.h); guidVSDebugGroup / IDM_*_CONTEXT (VsDbgCmd.h); UICONTEXT_Debugging.
        Assert.Contains("ShlMainMenu = new(\"d309f791-903f-11d0-9efc-00a0c911004f\")", menus, StringComparison.Ordinal);
        Assert.Contains("VsDebugGroup = new(\"c9dd4a58-47fb-11d2-83e7-00c04f9902c1\")", menus, StringComparison.Ordinal);
        Assert.Contains("DebuggingContext = new(\"adfc4e61-0397-11d1-9f4e-00a0c911004f\")", menus, StringComparison.Ordinal);
        foreach (var (group, menu, id) in new[] { ("ShlMainMenu", "DebugMenu", "0x0089"), ("VsDebugGroup", "WatchContext", "0x0500"),
                                                  ("VsDebugGroup", "LocalsContext", "0x0501"), ("VsDebugGroup", "AutosContext", "0x0502"),
                                                  ("VsDebugGroup", "CallStackContext", "0x0504") })
        {
            Assert.Matches($@"{menu}\s*=\s*{id};", menus);
            Assert.Matches($@"GroupPlacement\.VsctParent\(VsctMenus\.{group}, VsctMenus\.{menu}, \w+\)\)\s*\{{\s*Children = \[GroupChild\.Command<AnalyzeStopCommand>\(\)\]",
                           command);
        }
        Assert.Contains("EnabledWhen = ActivationConstraint.UIContext(VsctMenus.DebuggingContext)", command, StringComparison.Ordinal);
        // The ignore files, then the stop sized for the code-actions model; a refusal said — "nothing paused" only when the
        // package that pushes the stop is there —, else a code action: the code-actions model, never null (a null model
        // is the chat with its tools, which could continue or step the program).
        Assert.Matches(@"WorkspaceExclusions\.Follow\(\)[\s\S]*?ModelRole\.CodeActions[\s\S]*?"
                       + @"DebugStopAnalysis\.ForAsync\([\s\S]*?CodeExcerpt\.BudgetFor\(window\)[\s\S]*?"
                       + @"analysis\.NothingPaused && Services\.Signals\.InProcAliveSignal\.TryRead\(\) is not \{ HasPackage: true \}[\s\S]*?"
                       + @"refusal = Strings\.AnalyzeStopNoInProc[\s\S]*?ShowPromptAsync\(refusal[\s\S]*?"
                       + @"SetPendingPrompt\(analysis\.Question, modelOverride: _config\.CodeActionsModel",
                       command);
    }

    [Fact]
    public void VsCode_OffersIt_InTheCallStackAndVariablesViews_AndThePalette_DuringASession()
    {
        using var package = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "vscode", "package.json")));
        var menus = package.RootElement.GetProperty("contributes").GetProperty("menus");

        string When(string menu) => menus.GetProperty(menu).EnumerateArray()
            .Single(m => m.GetProperty("command").GetString() == "inferpal.analyzeStop").GetProperty("when").GetString()!;

        Assert.Equal("debugState == 'stopped'", When("debug/callstack/context"));
        Assert.Equal("debugState == 'stopped'", When("debug/variables/context"));
        Assert.Equal("inDebugMode", When("commandPalette"));
    }

    [Fact]
    public void VsCode_AsksTheHost_AndSendsACodeAction()
    {
        var provider  = Path.Combine(Root, "vscode", "src", "chatViewProvider.ts");
        var extension = File.ReadAllText(Path.Combine(Root, "vscode", "src", "extension.ts"));

        Assert.Contains("registerCommand('inferpal.analyzeStop', () => chatView.analyzeStop())", extension, StringComparison.Ordinal);
        Assert.Contains("this.askWithChip((host) => host.debugAnalyze(this.model || undefined), 'analyze the debugger stop', true)",
                        ConversationPersistenceSilenceTests.TsMethodBody(provider, "analyzeStop(): Promise<void>"),
                        StringComparison.Ordinal);
        // The code-action flag reaches the turn: the code-actions model, without tools.
        Assert.Contains("this.send(ask.question, false, { prompt: ask.question, codeAction: true, query: ask.question })",
                        ConversationPersistenceSilenceTests.TsMethodBody(provider, "private async askWithChip("),
                        StringComparison.Ordinal);
    }
}
