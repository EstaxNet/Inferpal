using System.IO;
using Inferpal.Services.Presentation;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Esc twice in the composer stops a running turn, in both editors, as the Stop button's tooltip says ("Stop (Esc
/// twice)"); one press only arms, and Esc on a waiting approval card still denies it.
/// </summary>
public class DoubleEscapeTests
{
    private static readonly DateTime T0 = new(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void TheSecondPressWithinTheWindow_Stops_TheFirstOnlyArms()
    {
        var escape = new DoubleEscape();
        Assert.False(escape.Press(T0));
        Assert.True(escape.Press(T0 + TimeSpan.FromMilliseconds(400)));
        // Stopped: the next press starts over.
        Assert.False(escape.Press(T0 + TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public void ASecondPressTooLate_ArmsAgain_AndAReset_ForgetsTheFirst()
    {
        var escape = new DoubleEscape();
        escape.Press(T0);
        Assert.False(escape.Press(T0 + DoubleEscape.Window + TimeSpan.FromMilliseconds(1)));
        escape.Reset();
        Assert.False(escape.Press(T0 + DoubleEscape.Window + TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public void VisualStudio_EscInTheComposer_DeniesACard_ElseStopsOnTheSecondPress()
    {
        var root = ConversationPersistenceSilenceTests.RepoRoot();
        var chrome = ConventionCoverageTests.CodeOnly(Path.Combine(root, "Inferpal", "ToolWindow", "InferpalToolWindowData.Chrome.cs"));
        Assert.Contains("card.Answer(ApprovalDecision.Deny);", chrome, StringComparison.Ordinal);
        // From an empty prompt only, as Enter and as VS Code: Esc while typing a note never refuses the card.
        Assert.Contains("if (Prompt.Trim().Length > 0) return;", chrome, StringComparison.Ordinal);
        Assert.Contains("else if (IsLoading && _escape.Press(DateTime.UtcNow)) _currentCts?.Cancel();", chrome, StringComparison.Ordinal);
        var helpers = ConventionCoverageTests.CodeOnly(Path.Combine(root, "Inferpal", "ToolWindow", "InferpalToolWindowData.UiHelpers.cs"));
        Assert.Contains("_escape.Reset();", helpers, StringComparison.Ordinal);
        var construction = ConventionCoverageTests.CodeOnly(Path.Combine(root, "Inferpal", "ToolWindow", "InferpalToolWindowData.Construction.cs"));
        Assert.Contains("BtnCancel            = Strings.BtnStopTurn;", construction, StringComparison.Ordinal);
    }

    [Fact]
    public void VsCode_KeepsTheSameWindow_AndStopsOnTheSecondPress()
    {
        var path = Path.Combine(ConversationPersistenceSilenceTests.RepoRoot(), "vscode", "src", "webview", "main.ts");
        var src = SettingsSchemaDriftTests.NeutralizeTypeScriptComments(File.ReadAllText(path));
        Assert.Contains($"const DOUBLE_ESCAPE_MS = {(int)DoubleEscape.Window.TotalMilliseconds};", src, StringComparison.Ordinal);
        Assert.Contains("if (now - lastEscape <= DOUBLE_ESCAPE_MS) {", src, StringComparison.Ordinal);
        // Never while a card waits: Esc answers cards (Visual Studio denies one whatever is typed).
        Assert.Contains("} else if (busy && approvalCards.size === 0) {", src, StringComparison.Ordinal);
        // A press as a turn ended never counts in the next one, in either editor.
        Assert.Contains("lastEscape = busy === value ? lastEscape : 0;", src, StringComparison.Ordinal);
        Assert.Contains("cancelTitle: t('Stop (Esc twice)')",
                        File.ReadAllText(Path.Combine(ConversationPersistenceSilenceTests.RepoRoot(), "vscode", "src", "webview", "chatWebviewHtml.ts")),
                        StringComparison.Ordinal);
    }
}
