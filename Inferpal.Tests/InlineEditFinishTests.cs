using System.IO;
using Inferpal.Services.CodeActions;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// What a model reply becomes before it is applied — for <b>every</b> in-place action, "Edit with
/// AI" included.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ <b>Measured</b>: the Visual Studio "Edit with AI" command did
/// <c>Reindent(Clean(reply))</c> and applied the result. On a selection carrying its line ending —
/// <c>"    return 1;\r\n"</c>, the shape a click in the margin plus Shift+Down produces — it came
/// back as <c>"    return 1;"</c>. Two consequences, and the second is the worse one: the next line
/// moves up against the one just edited, and the unchanged echo no longer recognises itself, so the
/// "nothing to change" reply is applied as an edit.
/// </para>
/// <para>
/// ⚠ The repair for that exact gesture had been written a year of tours ago — in
/// <c>CodeActionPipeline</c>, whose comment cites this very gesture — and the command never went
/// through it. A fix that closes the instance you saw leaves the class alive; here the fix's own
/// comment named the site that did not get it.
/// </para>
/// <para>
/// The command keeps what is genuinely its own: with no selection it edits the caret's LINE, where
/// the slash commands rewrite the whole file. That is why it calls the funnel rather than the whole
/// pipeline — the funnel decides the text and the verdict, not where they land.
/// </para>
/// </remarks>
public class InlineEditFinishTests
{
    private const string Model  = "m";
    private const string Server = "http://localhost:11434";

    private static CodeActionRun Finish(string reply, string original, string doc, bool reindent = true) =>
        CodeActionPipeline.Finish(reply, original, doc, reindent, Model, Server, cutAtLimit: false);

    // ── The line ending the selection carried ────────────────────────────────

    [Fact]
    public void ASelectionThatCarriedItsLineBreak_GetsItBack()
    {
        const string original = "    return 1;\r\n";
        const string doc      = "class C\r\n{\r\n    return 1;\r\n}\r\n";

        var run = Finish("    return 2;", original, doc);

        Assert.Equal(CodeActionOutcome.Edited, run.Outcome);
        Assert.Equal("    return 2;\r\n", run.EditedCode);
    }

    [Fact]
    public void AnUnchangedEcho_OfSuchASelection_IsNothingToChange()
    {
        // THE point: without the line break restored first, the echo differs from the original by
        // one byte and is applied — an invisible edit that eats the line ending.
        const string original = "    return 1;\r\n";
        const string doc      = "class C\r\n{\r\n    return 1;\r\n}\r\n";

        var run = Finish("    return 1;", original, doc);

        Assert.Equal(CodeActionOutcome.NoChangeNeeded, run.Outcome);
    }

    /// <summary>Reference arm: a selection with no line ending gets none added.</summary>
    [Fact]
    public void ASelectionWithoutALineBreak_GetsNoneAdded()
    {
        var run = Finish("return 2;", "return 1;", "class C { return 1; }");

        Assert.Equal(CodeActionOutcome.Edited, run.Outcome);
        Assert.Equal("return 2;", run.EditedCode);
    }

    // ── The other three steps the command was missing ────────────────────────

    [Fact]
    public void TheDocumentsOwnLineEndings_AreWhatIsWrittenBack()
    {
        // The model answers in LF; a CRLF document gets CRLF back — which is also what makes the
        // identity check above exact.
        var run = Finish("if (a)\n{\n}", "if (b)\r\n{\r\n}", "if (b)\r\n{\r\n}\r\n", reindent: false);

        Assert.Equal(CodeActionOutcome.Edited, run.Outcome);
        Assert.DoesNotContain("\n", run.EditedCode!.Replace("\r\n", ""), StringComparison.Ordinal);
    }

    [Fact]
    public void TheNoChangeSentinel_IsHonoured()
    {
        var run = Finish(CodeActionSentinel.Token, "    return 1;\r\n", "class C { }");

        Assert.Equal(CodeActionOutcome.NoChangeNeeded, run.Outcome);
    }

    [Fact]
    public void AnEmptyReply_FailsWithACauseThatNamesTheModel()
    {
        var run = Finish("   ", "    return 1;\r\n", "class C { }");

        Assert.Equal(CodeActionOutcome.Failed, run.Outcome);
        Assert.Contains(Model, run.FailureDetail!, StringComparison.Ordinal);
        Assert.Contains(Server, run.FailureDetail!, StringComparison.Ordinal);
    }

    // ── And no second, incomplete reader ─────────────────────────────────────

    /// <summary>
    /// The rule that keeps this closed: a caller that applies a model reply goes through the funnel.
    /// Reaching for <c>Clean</c> or <c>Reindent</c> on its own is how the command drifted, and it is
    /// invisible from either editor — both of them apply something.
    /// </summary>
    /// <remarks>⚠ Read without comments: the remarks in this very file quote both names.</remarks>
    [Theory]
    [InlineData("Inferpal", "Commands", "InlineEditSelectionCommand.cs")]
    [InlineData("Inferpal", "ToolWindow", "InferpalToolWindowData.SlashCommands.cs")]
    public void NoCallerRebuildsThePostProcessing(params string[] parts)
    {
        var code = ConventionCoverageTests.CodeOnly(Path.Combine(RepoRoot(), Path.Combine(parts)));

        // WITNESS: this really is a file that turns a model reply into an edit.
        Assert.True(code.Contains("CodeActionPipeline.Finish", StringComparison.Ordinal)
                 || code.Contains("InPlaceCodeEdit.RunAsync", StringComparison.Ordinal),
                    "this file no longer applies a code action — the rule reads nothing.");

        Assert.DoesNotContain("InlineEditReindenter.", code, StringComparison.Ordinal);
        Assert.DoesNotContain("InlineEditResponse.Clean", code, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
