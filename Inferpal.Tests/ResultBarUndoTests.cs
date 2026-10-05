using System;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  The Undo button of a result bar runs /undo-run itself — it is not the Send button.
//
//  It wrote "/undo-run" into the prompt box and called SendAsync, which is Stop while a turn runs:
//  a click during a follow-up stopped that follow-up, undid nothing, and replaced the draft (with an
//  approval card waiting, it answered nothing at all); idle, it still wiped the draft. Undoing while
//  a run is still writing waits for it, and says so.
// ──────────────────────────────────────────────────────────────────────────────────────────────
public class ResultBarUndoTests
{
    private static MethodDeclarationSyntax Method(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var path = Path.Combine(dir!.FullName, "Inferpal", "ToolWindow", "InferpalToolWindowData.Chrome.cs");
        var root = CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path).GetRoot();
        var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                         .SingleOrDefault(m => m.Identifier.Text == name);
        // Witness: the method this test reads still exists, under this name, in this file.
        Assert.True(method is not null, $"{name} was not found: this test would have measured nothing.");
        return method!;
    }

    /// <summary>The code of a method, comments left out (what it does, not what it says).</summary>
    private static string Code(MethodDeclarationSyntax method) =>
        string.Concat(method.DescendantTokens().Select(t => t.Text + " "));

    [Fact]
    public void TheBarsUndo_NeverGoesThroughThePromptBox()
    {
        var bar = Code(Method("InsertResultBar"));
        Assert.Contains("UndoFromResultBarAsync", bar, StringComparison.Ordinal);
        Assert.DoesNotContain("RunSuggestionAsync", bar, StringComparison.Ordinal);

        var undo = Code(Method("UndoFromResultBarAsync"));
        Assert.DoesNotContain("Prompt", undo, StringComparison.Ordinal);
        Assert.DoesNotContain("SendAsync", undo, StringComparison.Ordinal);
    }

    [Fact]
    public void WhileATurnRuns_TheUndoWaits_AndSaysSo()
    {
        var undo = Code(Method("UndoFromResultBarAsync"));

        var busy = undo.IndexOf("IsLoading", StringComparison.Ordinal);
        var said = undo.IndexOf("UndoRunWhileBusy", StringComparison.Ordinal);
        var run  = undo.IndexOf("HandleSlashCommandAsync", StringComparison.Ordinal);
        Assert.True(busy >= 0 && said > busy && run > said,
                    "The busy check, its notice, then the command — in that order.");
    }
}
