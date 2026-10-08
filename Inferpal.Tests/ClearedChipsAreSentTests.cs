using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Visual Studio: a send that clears the chips waiting in the composer sends them — whatever path it came by.
/// </summary>
/// <remarks>
/// A template, <c>/explain</c>, <c>/review</c> and <c>/debug</c> reach <c>SendCoreAsync</c> with their own attachments
/// only (none, or the code), and <c>clearPrompt</c> emptied the chips: a file the user had attached for that question was
/// neither sent nor named under it, and gone from the composer. VS Code sends them. The property lives in the funnel, so
/// a fifth path inherits it. (The VM needs a live Visual Studio: read from its syntax tree.)
/// </remarks>
public sealed class ClearedChipsAreSentTests
{
    [Fact]
    public void ClearingTheChips_AddsThemToWhatIsSent_First()
    {
        var path = Path.Combine(ConversationPersistenceSilenceTests.RepoRoot(), "Inferpal", "ToolWindow",
                                "InferpalToolWindowData.ChatTurn.cs");
        var root = CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path).GetRoot();
        var send = root.DescendantNodes().OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.Text == "SendCoreAsync");

        var clearing = send.DescendantNodes().OfType<IfStatementSyntax>()
                           .SingleOrDefault(i => i.Condition.ToString() == "clearPrompt");
        Assert.True(clearing is not null, "the chips' clearing was not found: this test would have measured nothing.");

        var body  = clearing!.Statement.ToString();
        var clear = body.IndexOf("Attachments.Clear()", StringComparison.Ordinal);
        var added = body.IndexOf("attachments.Add(pending)", StringComparison.Ordinal);
        Assert.True(clear > 0, "the clearing no longer empties the chips: check what replaced it.");   // witness
        Assert.True(added >= 0 && added < clear, "the chips are cleared without being added to what is sent.");
        Assert.Contains("foreach (var pending in Attachments)", body, StringComparison.Ordinal);
    }

    [Fact]
    public void WhatIsSent_IsReadAfterTheMerge()
    {
        // The bubble's names and _lastSent (what Regenerate resends) are built from `attachments` AFTER the clearing:
        // merged before them, the chips are named under the question and resent with it.
        var path = Path.Combine(ConversationPersistenceSilenceTests.RepoRoot(), "Inferpal", "ToolWindow",
                                "InferpalToolWindowData.ChatTurn.cs");
        var text  = File.ReadAllText(path);
        var clear = text.IndexOf("Attachments.Clear();", StringComparison.Ordinal);
        Assert.True(clear > 0);
        Assert.True(text.IndexOf("_lastSent = (userItem, userText, oneTimeModel, attachments.ToList());", StringComparison.Ordinal) > clear);
        Assert.True(text.IndexOf("BuildBubbleText(", clear, StringComparison.Ordinal) > clear);
    }
}
