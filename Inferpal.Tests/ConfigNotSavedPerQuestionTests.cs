using System;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  A question does not save the configuration.
//
//  The Visual Studio window saved config.json at every question, with nothing changed: the file's
//  stamp moved, so the ghost-text sidecar was killed and started again at the next completion — and
//  the process-wide FIM caches went with it — while Saved re-measured the model's window and rebuilt
//  the pinned chips. Every setting the window changes saves itself.
// ──────────────────────────────────────────────────────────────────────────────────────────────
public class ConfigNotSavedPerQuestionTests
{
    [Fact]
    public void SendingAQuestion_DoesNotSaveTheConfiguration()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var path = Path.Combine(dir!.FullName, "Inferpal", "ToolWindow", "InferpalToolWindowData.ChatTurn.cs");
        var root = CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetRoot();

        var send = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                       .SingleOrDefault(m => m.Identifier.Text == "SendCoreAsync");
        Assert.True(send is not null, "SendCoreAsync was not found: this test would measure nothing.");

        // Code, not comments: the remark that explains the rule names the call it forbids.
        var calls = send!.DescendantNodes().OfType<InvocationExpressionSyntax>()
                         .Select(i => i.Expression.ToString())
                         .ToList();
        Assert.True(calls.Count > 20, $"Only {calls.Count} call(s) read in SendCoreAsync: the scan is dead.");
        Assert.DoesNotContain("_config.Save", calls);
    }
}
