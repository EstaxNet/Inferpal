using Inferpal.Services.Bench;
using Xunit;

namespace Inferpal.Tests;

// The context bench (roadmap §12): question generation from the workspace, grading, and — the
// part that matters — a verdict that refuses to flatter the feature it measures.
public class ContextBenchTests
{
    private static ScannedFile F(string path, params string[] symbols) => new(path, null, symbols, []);

    // ── Question generation ────────────────────────────────────────────────────

    [Fact]
    public void AmbiguousSymbols_AreNeverAsked()
    {
        // Declared in two files (partial class, or a test double named after its subject):
        // there is no single right answer, so grading it would measure the grader.
        var files = new List<ScannedFile>
        {
            F("A/Engine.cs", "EngineCore"),
            F("B/Engine.cs", "EngineCore"),
            F("C/Widget.cs", "WidgetFactory"),
        };

        var questions = ContextBenchTasks.Build(files);

        Assert.DoesNotContain(questions, q => q.Symbol == "EngineCore");
        Assert.Contains(questions, q => q.Symbol == "WidgetFactory");
    }

    [Fact]
    public void ShortSymbols_AreSkipped()
    {
        var files = new List<ScannedFile> { F("A/One.cs", "Job", "T", "PromptSection") };

        var questions = ContextBenchTasks.Build(files);

        Assert.Equal(["PromptSection"], questions.Select(q => q.Symbol));
    }

    [Fact]
    public void Questions_AreSpreadAcrossFolders()
    {
        // Ten candidates in one folder and one in another: taking the first N alphabetically
        // would ask about a single directory and call it a measure of the repository.
        var files = new List<ScannedFile>();
        for (var i = 0; i < 10; i++) files.Add(F($"Big/File{i}.cs", $"BigSymbol{i}"));
        files.Add(F("Small/Only.cs", "SmallSymbol"));

        var questions = ContextBenchTasks.Build(files, count: 4);

        Assert.Contains(questions, q => q.ExpectedPath.StartsWith("Small/", StringComparison.Ordinal));
    }

    [Fact]
    public void Generation_IsDeterministic()
    {
        var files = new List<ScannedFile>
        {
            F("B/Two.cs", "SecondType"), F("A/One.cs", "FirstType"), F("C/Three.cs", "ThirdType"),
        };
        var shuffled = new List<ScannedFile> { files[2], files[0], files[1] };

        // Both arms of a comparison must be asked exactly the same questions.
        Assert.Equal(ContextBenchTasks.Build(files).Select(q => q.Symbol),
                     ContextBenchTasks.Build(shuffled).Select(q => q.Symbol));
    }

    [Fact]
    public void EmptyWorkspace_YieldsNoQuestion() =>
        Assert.Empty(ContextBenchTasks.Build([]));

    [Fact]
    public void TypesNamedAfterTheirOwnFile_AreAvoided()
    {
        // `WorkspaceSymbolScanner` in `WorkspaceSymbolScanner.cs` is answered by one filename lookup — the ceiling
        // that made the first real measurement useless (6/6 in both arms, no room for a gain).
        var files = new List<ScannedFile>
        {
            F("A/WorkspaceSymbolScanner.cs", "WorkspaceSymbolScanner", "ScanBudget"),
            F("B/Helpers.cs",        "TokenEstimator"),
        };

        var questions = ContextBenchTasks.Build(files, count: 2);

        Assert.DoesNotContain(questions, q => q.Symbol == "WorkspaceSymbolScanner");
        Assert.Contains(questions, q => q.Symbol == "ScanBudget");
        Assert.Contains(questions, q => q.Symbol == "TokenEstimator");
    }

    [Fact]
    public void ConventionStrictRepository_TopsUpRatherThanReturningNothing()
    {
        // Every type in its own same-named file: excluding them all would leave no bench at all.
        var files = new List<ScannedFile>
        {
            F("A/FirstType.cs",  "FirstType"),
            F("B/SecondType.cs", "SecondType"),
        };

        var questions = ContextBenchTasks.Build(files, count: 2);

        Assert.Equal(2, questions.Count);
    }

    // ── Grading ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Core/Services/Engine.cs")]
    [InlineData("`Core/Services/Engine.cs`")]
    [InlineData("Core\\Services\\Engine.cs")]
    [InlineData("It is declared in Core/Services/Engine.cs.")]
    [InlineData("<think>let me search</think>Core/Services/Engine.cs")]
    public void Grading_IsLenientAboutShape(string answer)
    {
        var q = new ContextBenchQuestion("Engine", "Core/Services/Engine.cs");

        Assert.True(ContextBenchTasks.Score(q, answer));
    }

    [Theory]
    [InlineData("Core/Services/Other.cs")]
    [InlineData("I could not find it.")]
    [InlineData("")]
    [InlineData(null)]
    // The bare file name is the important one: in C# `Engine` lives in `Engine.cs`, so accepting it
    // would let a model score without reading anything. The first real run did exactly that —
    // 3/6 correct with zero tool calls — which is what exposed the flaw.
    [InlineData("Engine.cs")]
    [InlineData("It is in Engine.cs somewhere.")]
    public void Grading_IsStrictAboutTheFile(string? answer)
    {
        var q = new ContextBenchQuestion("Engine", "Core/Services/Engine.cs");

        Assert.False(ContextBenchTasks.Score(q, answer));
    }

    // ── Verdict — the anti-flattery rules ──────────────────────────────────────

    private static ContextBenchArm Arm(string label, int correct, int calls, int tokens) =>
        new(label, correct, 8, calls, tokens);

    [Fact]
    public void FewerCorrectAnswers_IsARegression_WhateverTheTokensSay()
    {
        // The trap: a model that gives up sooner makes fewer tool calls and burns fewer tokens.
        var off = Arm("map off", correct: 6, calls: 30, tokens: 40_000);
        var on  = Arm("map on",  correct: 3, calls: 8,  tokens: 12_000);

        Assert.Equal(ContextBenchVerdict.Regression, ContextBenchReport.Judge(off, on));
        Assert.Contains("Set `someSetting` to false", ContextBenchReport.Render(off, on, "someSetting"));
    }

    [Fact]
    public void CheaperAndAtLeastAsCorrect_Pays()
    {
        var off = Arm("map off", correct: 6, calls: 30, tokens: 40_000);
        var on  = Arm("map on",  correct: 6, calls: 12, tokens: 28_000);

        Assert.Equal(ContextBenchVerdict.Pays, ContextBenchReport.Judge(off, on));
    }

    [Fact]
    public void SameAccuracyAndMoreExpensive_DoesNotPay()
    {
        // The honest negative result: the map costs its ~800 tokens a turn and buys nothing here.
        var off = Arm("map off", correct: 6, calls: 20, tokens: 30_000);
        var on  = Arm("map on",  correct: 6, calls: 19, tokens: 36_000);

        Assert.Equal(ContextBenchVerdict.DoesNotPay, ContextBenchReport.Judge(off, on));
        Assert.Contains("does not pay", ContextBenchReport.Render(off, on, "someSetting"));
    }

    [Fact]
    public void MoreCorrectButMoreExpensive_IsATradeoffNotAWin()
    {
        var off = Arm("map off", correct: 4, calls: 30, tokens: 30_000);
        var on  = Arm("map on",  correct: 7, calls: 25, tokens: 36_000);

        Assert.Equal(ContextBenchVerdict.Tradeoff, ContextBenchReport.Judge(off, on));
    }

    [Fact]
    public void TinyTokenDifferences_AreNoiseNotAVictory()
    {
        var off = Arm("map off", correct: 6, calls: 20, tokens: 30_000);
        var on  = Arm("map on",  correct: 6, calls: 20, tokens: 29_700);   // 1 % — within noise

        Assert.Equal(ContextBenchVerdict.DoesNotPay, ContextBenchReport.Judge(off, on));
    }

    [Fact]
    public void NothingMeasured_IsInconclusive() =>
        Assert.Equal(ContextBenchVerdict.Inconclusive,
            ContextBenchReport.Judge(new("map off", 0, 0, 0, 0), new("map on", 0, 0, 0, 0)));
}
