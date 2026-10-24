using System.IO;
using Inferpal.Localization;
using Inferpal.Services.CodeActions;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// "Fix with Inferpal" on chosen problems — Visual Studio's Error List, VS Code's quick fix: what reaches the chat (each
/// problem as a build prints it and the code around it), why nothing does, and the Error List's selection read across
/// the process boundary.
/// </summary>
[Collection(SignalCollection.Name)]
public sealed class FixProblemsTests : IDisposable
{
    private readonly SignalScratchDir _signals = new();

    public void Dispose() => _signals.Dispose();

    private static string LongFile(int lines) =>
        string.Join("\n", Enumerable.Range(1, lines).Select(i => $"    var line{i} = {i}; // {new string('x', 40)}"));

    // ── What reaches the chat ────────────────────────────────────────────────────

    /// <summary>A selection that mixes the solution's errors with a file elsewhere (another repository, an SDK file):
    /// that file is named, never read — its own ignore files are not the ones declared — and the attachment fits the
    /// window of the model that answers, the cut said on the chip.</summary>
    [Fact]
    public async Task AFileOutsideTheWorkspace_IsNamedNeverRead_AndTheAttachmentFitsTheWindow()
    {
        var root    = Directory.CreateTempSubdirectory("inferpal-fix-root").FullName;
        var outside = Directory.CreateTempSubdirectory("inferpal-fix-out").FullName;
        try
        {
            var inside = Path.Combine(root, "Foo.cs");
            var other  = Path.Combine(outside, "Other.cs");
            File.WriteAllText(inside, "class Foo { int InsideMarker; }\n");
            File.WriteAllText(other, "class Other { int OutsideMarker; }\n");
            IReadOnlyList<ProblemEntry> problems =
            [
                new(inside, 1, 1, "error", "CS0103", "inside"),
                new(other, 1, 1, "warning", "CS0168", "outside"),
            ];

            var (_, chip, content) = await ProblemsFix.ForAsync(problems, root, overlay: null, budgetChars: 60_000,
                                                                CancellationToken.None);
            var many = Enumerable.Range(0, 400)
                .Select(i => new ProblemEntry(inside, 1, 1, "error", "CS0029", $"{i}: " + new string('t', 200))).ToList();
            var (_, smallChip, small) = await ProblemsFix.ForAsync(many, root, overlay: null, budgetChars: 8_000,
                                                                   CancellationToken.None);

            Assert.Contains("InsideMarker", content);
            Assert.DoesNotContain("OutsideMarker", content);
            Assert.Contains(other, content);   // the problem itself stays listed, by its path
            Assert.Equal(Strings.FixProblemsChip(2), chip);
            Assert.True(small.Length <= 8_000 + 200, $"{small.Length} characters for a window of 8,000");
            Assert.NotEqual(Strings.FixProblemsChip(400), smallChip);   // the cut is said on the chip
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(outside, true);
        }
    }

    [Fact]
    public void EachProblem_IsWrittenAsABuildPrintsIt()
    {
        var content = FixPromptBuilder.ForProblems(
        [
            new("C:\\p\\Foo.cs", 12, 5, "error", "CS0103", "The name 'x' does not exist"),
            new("C:\\p\\App.csproj", 0, 0, "warning", "NU1903", "Package 'X' 1.0.0 has a known high severity vulnerability"),
            new(null, 0, 0, "error", null, "The command exited with code 1"),
            new("C:\\p\\app.ts", 3, 1, "error", "2304", "Cannot find name 'y'.", "ts"),
        ], _ => null);

        Assert.Contains("C:\\p\\Foo.cs(12,5): error CS0103: The name 'x' does not exist", content);
        Assert.Contains("C:\\p\\App.csproj : warning NU1903: Package 'X' 1.0.0 has a known high severity vulnerability", content);
        Assert.Contains("\nerror: The command exited with code 1", content);
        Assert.Contains("C:\\p\\app.ts(3,1): error 2304: Cannot find name 'y'. (ts)", content);
    }

    [Fact]
    public void TheCodeAroundAProblemDeepInALongFile_IsInTheAttachment()
    {
        // A .ts file: the build-output parser reads C# paths only, the entries carry their own site.
        var content = FixPromptBuilder.ForProblems(
            [new("C:\\p\\big.ts", 400, 1, "error", "2304", "Cannot find name 'y'.")],
            path => path == "C:\\p\\big.ts" ? LongFile(600) : null);

        Assert.Contains("### C:\\p\\big.ts", content);
        Assert.Contains("var line400 = 400;", content);
        Assert.Contains("var line390 = 390;", content);
        Assert.DoesNotContain("var line300 = 300;", content);   // a window, not the file's start
    }

    [Fact]
    public void AWholeListSelected_KeepsItsErrors_AndCountsWhatItLeavesOut()
    {
        var problems = Enumerable.Range(1, FixPromptBuilder.MaxProblems + 50)
            .Select(i => new ProblemEntry(null, 0, 0, "warning", "CS8618", $"warning {i}"))
            .Append(new ProblemEntry(null, 0, 0, "error", "CS0103", "the one error"))
            .ToList();

        var content = FixPromptBuilder.ForProblems(problems, _ => null);

        Assert.Contains("error CS0103: the one error", content);
        Assert.Contains("(51 more problem(s) selected, not listed", content);
        Assert.Contains($"warning {FixPromptBuilder.MaxProblems - 1}\n", content + "\n");
        Assert.DoesNotContain($"warning {FixPromptBuilder.MaxProblems}\n", content + "\n");
    }

    [Fact]
    public void LongMessages_StopTheListAtItsBudget_AndTheAttachmentIsNeverCutAgain()
    {
        var problems = Enumerable.Range(1, 80)
            .Select(i => new ProblemEntry($"C:\\p\\F{i % 5}.cs", i, 1, "error", "CS0029", $"{i}: " + new string('t', 1_000)))
            .ToList();

        var content = FixPromptBuilder.ForProblems(problems, _ => new string('x', 4_000));

        Assert.Matches(@"^\(\d+ more problem\(s\) selected, not listed", content);
        Assert.InRange(content.Length, FixPromptBuilder.MaxProblemChars, Services.Agent.AttachmentCap.MaxChars);
    }

    [Fact]
    public async Task AProblemInAnUnsavedBuffer_IsShownWithTheBuffersCode_NotTheDisks()
    {
        var file = Path.Combine(_signals.Path, "app.ts");
        File.WriteAllText(file, "const old = 1;\n");
        var overlay = new Services.Editor.OpenDocumentOverlay();
        overlay.Set(file, "const typed = missing;\n");

        var (_, _, content) = await ProblemsFix.ForAsync(
            [new(file, 1, 15, "error", "2304", "Cannot find name 'missing'.")], _signals.Path, overlay, 60_000, CancellationToken.None);

        Assert.Contains("const typed = missing;", content);
        Assert.DoesNotContain("const old = 1;", content);
        // Witness: saved, the same file is read from disk.
        overlay.Set(file, "const old = 1;\n", unsaved: false);
        Assert.Contains("const old = 1;", (await ProblemsFix.ForAsync(
            [new(file, 1, 7, "error", "2304", "x")], _signals.Path, overlay, 60_000, CancellationToken.None)).Content);
    }

    [Fact]
    public void AFileThatCannotBeRead_IsNamed_NotDropped()
    {
        var content = FixPromptBuilder.ForProblems([new("C:\\p\\Gone.cs", 1, 1, "error", "CS1", "x")], _ => null);

        Assert.Contains("could not be read, not shown: C:\\p\\Gone.cs", content);
    }

    // ── The Error List's selection, across the process boundary ─────────────────

    [Fact]
    public async Task TheSelection_ComesBackToTheCommandThatAsked()
    {
        var problem = new ProblemEntry("C:\\p\\Foo.cs", 12, 5, "error", "CS0103", "x");
        var asking = ErrorListSelectionSignal.AskAsync(TimeSpan.FromSeconds(30), CancellationToken.None);

        ErrorListAsk? ask = null;
        for (var i = 0; i < 600 && ask is null; i++)
        {
            ask = ErrorListSelectionSignal.TryTake(null);
            if (ask is null) await Task.Delay(50);
        }
        Assert.NotNull(ask);
        ErrorListSelectionSignal.Answer(new ErrorListAnswer(ask!.Id, [problem]));

        var answer = await asking;
        Assert.NotNull(answer);
        Assert.Equal([problem], answer!.Problems);
    }

    [Fact]
    public async Task AnAnswerToAnotherRequest_IsNotTaken_AndTheUnansweredRequestIsWithdrawn()
    {
        var asking = ErrorListSelectionSignal.AskAsync(TimeSpan.FromMilliseconds(600), CancellationToken.None);
        ErrorListAsk? ask = null;
        for (var i = 0; i < 100 && ask is null; i++)
        {
            ask = ErrorListSelectionSignal.TryTake(null);
            if (ask is null) await Task.Delay(5);
        }
        Assert.NotNull(ask);   // witness: the request was there to answer
        ErrorListSelectionSignal.Answer(new ErrorListAnswer("another", [new(null, 0, 0, "error", null, "a")]));
        // The answer deleted the request; the command keeps waiting for ITS answer, which never comes.
        SignalFile.Write(ErrorListSelectionSignal.RequestPath, ask, "test");

        Assert.Null(await asking);
        Assert.False(File.Exists(ErrorListSelectionSignal.RequestPath));
    }

    [Fact]
    public async Task ACancelledCommand_WithdrawsItsRequest()
    {
        using var cancel = new CancellationTokenSource();
        var asking = ErrorListSelectionSignal.AskAsync(TimeSpan.FromSeconds(30), cancel.Token);
        for (var i = 0; i < 600 && !File.Exists(ErrorListSelectionSignal.RequestPath); i++) await Task.Delay(5);
        Assert.True(File.Exists(ErrorListSelectionSignal.RequestPath));   // witness: the request was there

        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => asking);
        Assert.False(File.Exists(ErrorListSelectionSignal.RequestPath));
    }

    [Fact]
    public void AnOldRequest_IsNotServed()
    {
        var now = DateTimeOffset.UtcNow;
        SignalFile._nowOverride = () => now;
        SignalFile.Write(ErrorListSelectionSignal.RequestPath,
                         new ErrorListAsk("1", SignalFile.CurrentPid, now.ToUnixTimeMilliseconds()), "test");

        SignalFile._nowOverride = () => now + ErrorListSelectionSignal.MaxAge + TimeSpan.FromSeconds(1);
        Assert.Null(ErrorListSelectionSignal.TryTake(null));
        SignalFile._nowOverride = () => now + TimeSpan.FromSeconds(1);
        Assert.NotNull(ErrorListSelectionSignal.TryTake(null));   // witness: the same request, while it is fresh
        Assert.Null(ErrorListSelectionSignal.TryTake("1"));       // and served once
    }
}

/// <summary>What "Fix with Inferpal" says, in the interface language: the question, its chip, and why nothing is sent.</summary>
[Collection(CultureSerialCollection.Name)]
public sealed class FixProblemsWordsTests
{
    [Fact]
    public void TheQuestionAndItsChip_CountTheProblems()
    {
        var previous = Strings.OverrideCulture?.Name;
        try
        {
            Strings.ApplyLanguage("en");
            var (question, chip, content) = ProblemsFix.For(
                [new("C:\\p\\A.cs", 1, 1, "error", "CS1", "a"), new("C:\\p\\B.cs", 2, 1, "warning", "CS2", "b")], _ => null);

            Assert.Equal("Fix these problems.", question);
            Assert.Equal("2 problems", chip);
            Assert.Contains("C:\\p\\B.cs(2,1): warning CS2: b", content);
            Assert.Equal("Fix this problem.", ProblemsFix.For([new(null, 0, 0, "error", null, "a")], _ => null).Question);
        }
        finally { Strings.ApplyLanguage(previous); }
    }

    [Fact]
    public void TheQuestion_SaysThisForOne_AndTheseForMore_InEveryLanguage()
    {
        var previous = Strings.OverrideCulture?.Name;
        try
        {
            // Russian counts 21 like 1 for a noun after its number; "this" or "these" is about one problem or more.
            Strings.ApplyLanguage("ru");
            Assert.Equal(Strings.FixProblemsQuestion(2), Strings.FixProblemsQuestion(21));
            Assert.NotEqual(Strings.FixProblemsQuestion(1), Strings.FixProblemsQuestion(21));
        }
        finally { Strings.ApplyLanguage(previous); }
    }

    [Fact]
    public void ProblemsOnlyInFilesTheAgentCannotEdit_AreRefusedBeforeTheQuestionGoes()
    {
        var root    = Path.Combine(Path.GetTempPath(), "inferpal-fix-root");
        var inside  = new ProblemEntry(Path.Combine(root, "src", "A.cs"), 1, 1, "error", "CS1", "a");
        var outside = new ProblemEntry(Path.Combine(Path.GetTempPath(), "elsewhere", "B.cs"), 1, 1, "error", "CS1", "b");

        Assert.Equal(Strings.FixProblemsOutside, ProblemsFix.NothingEditable([outside], root));
        Assert.Equal(Strings.FixProblemsOutside, ProblemsFix.NothingEditable([outside], root: null));
        // One problem the agent can fix is enough; one naming no file may be fixed anywhere.
        Assert.Null(ProblemsFix.NothingEditable([outside, inside], root));
        Assert.Null(ProblemsFix.NothingEditable([outside, new(null, 0, 0, "error", "MSB1009", "no project")], root));
    }

    [Fact]
    public void NothingReachesTheChat_AndTheCauseIsSaid()
    {
        Assert.Equal(Strings.FixProblemsNoInProc, ProblemsFix.Refusal(null, inProcLoaded: false));
        Assert.Equal(Strings.FixProblemsNoAnswer, ProblemsFix.Refusal(null, inProcLoaded: true));
        Assert.Equal(Strings.FixProblemsNoSelection, ProblemsFix.Refusal(new ErrorListAnswer("1", []), true));
        Assert.Contains("the Error List service is unavailable",
            ProblemsFix.Refusal(new ErrorListAnswer("1", [], "the Error List service is unavailable"), true));
        // Witness: problems chosen, nothing to refuse.
        Assert.Null(ProblemsFix.Refusal(new ErrorListAnswer("1", [new(null, 0, 0, "error", null, "a")]), true));
    }
}
