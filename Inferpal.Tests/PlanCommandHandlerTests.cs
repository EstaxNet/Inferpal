using System.IO;
using Inferpal.Localization;
using Inferpal.Services.Commands;
using Inferpal.Services.Persistence;
using Xunit;

namespace Inferpal.Tests;

// /plan (roadmap §17): the sub-command table both front-ends share, and the two things that must
// not drift — bare /plan still toggles plan mode, and nothing here executes a step.
public class PlanCommandHandlerTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "inferpal-plancmd-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static string[] Cmd(string line) => line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private PlanCommandHandler.PlanCommandResult Run(
        string line, string? lastAnswer = null, string? active = null, bool noRoot = false) =>
        PlanCommandHandler.Handle(noRoot ? null : _root, Cmd(line), lastAnswer, active);

    private const string Proposal = """
        # Port the semantic index

        Here is how I would do it:

        1. Read `CSharpSemanticIndex` and list what is C#-specific
        2. Add a TypeScript branch to `LspSemanticProvider`
        3. Wire it into `analyze_code`

        ```bash
        - not a step, this is a shell flag
        ```
        """;

    // ── The bare form is unchanged ─────────────────────────────────────────────

    [Fact]
    public void BarePlan_StillTogglesPlanMode()
    {
        // /plan has meant "toggle read-only plan mode" since 1.0; §17 adds sub-commands beside it
        // and must not repurpose the bare form under the user's feet.
        var result = Run("/plan");

        Assert.True(result.ToggleMode);
        Assert.Null(result.Message);
    }

    [Fact]
    public void TheToggle_DoesNotNeedAWorkspace()
    {
        // Plan mode is a session switch; requiring an open solution for it would be a regression.
        Assert.True(Run("/plan", noRoot: true).ToggleMode);
        Assert.False(Run("/plan list", noRoot: true).ToggleMode);
    }

    // ── save ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Save_TurnsTheLastAnswerIntoAFileAndMakesItActive()
    {
        var result = Run("/plan save port the index", Proposal);

        Assert.Equal("port-the-index", result.SetActivePlan);
        Assert.NotNull(result.OpenPath);
        Assert.True(File.Exists(result.OpenPath));

        var doc = PlanStore.Load(_root, "port-the-index");
        Assert.Equal(3, doc!.Steps.Count);
        Assert.Equal("port the index", doc.Title);   // the title is what the user typed, verbatim
    }

    [Fact]
    public void Save_IgnoresListsInsideFencedCode()
    {
        // A proposal almost always carries a snippet; a shell flag or a diff line reads exactly
        // like a bullet, and turning those into steps makes the very first save need cleaning up.
        Run("/plan save p", Proposal);

        Assert.DoesNotContain(PlanStore.Load(_root, "p")!.Steps,
                              s => s.Text.Contains("shell flag", StringComparison.Ordinal));
    }

    [Fact]
    public void Save_WithoutAName_TakesTheHeadingOfTheAnswer()
    {
        var result = Run("/plan save", Proposal);

        Assert.Equal("port-the-semantic-index", result.SetActivePlan);
    }

    [Fact]
    public void Save_WithoutStepsInTheAnswer_WritesNothing()
    {
        var result = Run("/plan save", "I would just change one line, no list here.");

        Assert.Null(result.SetActivePlan);
        Assert.Null(result.OpenPath);
        Assert.Empty(PlanStore.List(_root));
    }

    /// <summary>
    /// A GENERATED name never overwrites an existing plan. Without a name the plan takes the answer's
    /// heading — and models often open their plans with the same one: the second overwrote the first,
    /// ticked steps included, without a word.
    /// </summary>
    [Fact]
    public void Save_WithoutAName_DoesNotOverwriteAPlanWithTheSameHeading()
    {
        Run("/plan save", Proposal);
        Run("/plan done 1", active: "port-the-semantic-index");

        var second = Run("/plan save", Proposal);

        Assert.Equal(1, PlanStore.Load(_root, "port-the-semantic-index")!.DoneCount);
        Assert.Equal("port-the-semantic-index-2", second.SetActivePlan);
    }

    /// <summary>Boundary witness: a name the user typed is still a deliberate choice.</summary>
    [Fact]
    public void Save_WithAnExplicitName_KeepsThatName()
    {
        Run("/plan save alpha", Proposal);

        Assert.Equal("alpha", Run("/plan save alpha", Proposal).SetActivePlan);
    }

    /// <summary>
    /// The suffix survives truncation: a plan name is cut to 60 characters when the file is written, so
    /// a <c>-2</c> stuck onto a long heading would be dropped — and the second plan would land exactly on
    /// the first.
    /// </summary>
    [Fact]
    public void Save_WithoutAName_KeepsTheSuffix_WhenTheHeadingIsLong()
    {
        var longTitle = string.Join(' ', Enumerable.Repeat("refactor", 10));
        var answer    = new System.Text.RegularExpressions.Regex(@"^#+ .*$", System.Text.RegularExpressions.RegexOptions.Multiline)
                            .Replace(Proposal, "# " + longTitle, 1);

        var first  = Run("/plan save", answer).SetActivePlan;
        var second = Run("/plan save", answer).SetActivePlan;

        Assert.NotEqual(first, second);
        Assert.Equal(2, PlanStore.List(_root).Count);
    }

    // ── list / open ────────────────────────────────────────────────────────────

    [Fact]
    public void List_ShowsProgressPerPlan()
    {
        Run("/plan save alpha", Proposal);
        PlanStore.SetStepDone(_root, "alpha", 1, true);

        var message = Run("/plan list").Message;

        Assert.Contains("alpha", message);
        Assert.Contains("1/3", message);
    }

    // A write failure returned the same null as "step already in that state": `/plan done 1` on a
    // read-only plan (a file checked out of Perforce/TFVC) answered "step 1 is already done" while
    // nothing was ticked. The failure must surface, as it does for `/plan save`.
    [Fact]
    public void DoneOnAPlanThatCannotBeWritten_FailsInsteadOfClaimingTheStepIsAlreadyDone()
    {
        Run("/plan save alpha", Proposal);
        var path = PlanStore.PathFor(_root, "alpha");
        var dir  = PlanStore.DirectoryFor(_root);

        if (OperatingSystem.IsWindows())
        {
            // A file held open without FileShare.Delete cannot be replaced: the plan can still be read,
            // the atomic write fails.
            using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Assert.ThrowsAny<Exception>(() => Run("/plan done 1", active: "alpha"));
        }
        else
        {
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            try { Assert.ThrowsAny<Exception>(() => Run("/plan done 1", active: "alpha")); }
            finally { File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        }
        Assert.False(PlanStore.Load(_root, "alpha")!.Steps[0].Done);

        // Witness: the same command, file released, does tick the step — the path under test writes.
        Run("/plan done 1", active: "alpha");
        Assert.True(PlanStore.Load(_root, "alpha")!.Steps[0].Done);
    }

    // A plan that cannot be READ (locked by another process, permissions removed) is not a missing
    // plan: `/plan list` shows it, and `/plan open|next|done` answered "not found" — which sends the
    // user to check the spelling of a correct name. The cause must surface, as it does for writes.
    [Fact]
    public void APlanThatCannotBeRead_FailsInsteadOfBeingReportedMissing()
    {
        Run("/plan save alpha", Proposal);
        var path = PlanStore.PathFor(_root, "alpha");
        string[] commands = ["/plan open alpha", "/plan next alpha", "/plan done 1 alpha"];

        void AssertEachFailsWithTheReadError()
        {
            Assert.Contains("`alpha`", Run("/plan list").Message);
            foreach (var command in commands)
            {
                var ex = Record.Exception(() => Run(command));
                Assert.True(ex is IOException or UnauthorizedAccessException,
                            $"{command}: {ex?.GetType().Name ?? "no exception"}");
            }
        }

        if (OperatingSystem.IsWindows())
        {
            using var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            AssertEachFailsWithTheReadError();
        }
        else
        {
            File.SetUnixFileMode(path, UnixFileMode.None);
            try { AssertEachFailsWithTheReadError(); }
            finally { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        }

        // Witness: once the file is released the same commands succeed — and a name that really is
        // absent is still "not found", without an exception.
        Assert.Equal("alpha", Run("/plan open alpha").SetActivePlan);
        Assert.Equal(Strings.PlanNotFound("ghost"), Run("/plan open ghost").Message);
    }

    [Fact]
    public void AnUnknownWord_IsTreatedAsAPlanName()
    {
        Run("/plan save alpha", Proposal);

        var opened = Run("/plan alpha");

        Assert.Equal("alpha", opened.SetActivePlan);
        Assert.Contains("Add a TypeScript branch", opened.Message);
        Assert.Contains("not-a-plan", Run("/plan not-a-plan").Message);   // reported, not created
    }

    [Fact]
    public void OpeningNothingWithNoActivePlan_SaysSoInsteadOfGuessing()
    {
        Assert.Equal(Strings.PlanNoActive, Run("/plan open").Message);
        Assert.Equal(Strings.PlanNoActive, Run("/plan next").Message);
    }

    // ── done / undone / next ───────────────────────────────────────────────────

    [Fact]
    public void Done_TicksTheStepOfTheActivePlan()
    {
        Run("/plan save alpha", Proposal);

        var result = Run("/plan done 2", active: "alpha");

        Assert.Contains("Add a TypeScript branch", result.Message);
        Assert.True(PlanStore.Load(_root, "alpha")!.Steps[1].Done);
    }

    [Fact]
    public void Done_CanTargetAnotherPlanWithoutOpeningIt()
    {
        Run("/plan save alpha", Proposal);
        Run("/plan save beta", Proposal);

        Run("/plan done 1 alpha", active: "beta");

        Assert.True(PlanStore.Load(_root, "alpha")!.Steps[0].Done);
        Assert.False(PlanStore.Load(_root, "beta")!.Steps[0].Done);
    }

    [Fact]
    public void TickingATickedStep_ReadsAsAlreadyDone_NotAsAnError()
    {
        // "already done" and "no such step" are different answers: conflating them makes a normal
        // repetition look like a mistake.
        Run("/plan save alpha", Proposal);
        Run("/plan done 1", active: "alpha");

        Assert.Equal(Strings.PlanStepAlready(1, Strings.PlanStateDone),
                     Run("/plan done 1", active: "alpha").Message);
        Assert.Equal(Strings.PlanStepUnknown(9, 3), Run("/plan done 9", active: "alpha").Message);
    }

    [Fact]
    public void Undone_ReopensAStep()
    {
        Run("/plan save alpha", Proposal);
        Run("/plan done 1", active: "alpha");
        Run("/plan undone 1", active: "alpha");

        Assert.False(PlanStore.Load(_root, "alpha")!.Steps[0].Done);
    }

    [Fact]
    public void Next_WalksTheUnfinishedStepsThenReportsCompletion()
    {
        Run("/plan save alpha", Proposal);

        Assert.Contains("Read", Run("/plan next", active: "alpha").Message);
        Run("/plan done 1", active: "alpha");
        Assert.Contains("Add a TypeScript branch", Run("/plan next", active: "alpha").Message);

        Run("/plan done 2", active: "alpha");
        Run("/plan done 3", active: "alpha");
        Assert.Equal(Strings.PlanComplete("alpha"), Run("/plan next", active: "alpha").Message);
    }

    [Fact]
    public void DoneWithoutAStepNumber_ShowsTheUsage()
    {
        Assert.Equal(Strings.PlanUsage, Run("/plan done", active: "alpha").Message);
        Assert.Equal(Strings.PlanUsage, Run("/plan help").Message);
    }

    // ── The boundary the fiche locked ──────────────────────────────────────────

    [Fact]
    public void NoSubCommand_EverAsksToRunAnything()
    {
        // The result type carries a message, a mode toggle, an active plan and a file to open —
        // and deliberately no way to execute a step. Grouped approval is the §9 blank cheque, and
        // a plan file arrives with every clone. If a field for running steps is ever added, this
        // test is the place where that decision has to be argued.
        var fields = typeof(PlanCommandHandler.PlanCommandResult)
            .GetProperties().Select(p => p.Name).ToArray();

        Assert.Equal(["Message", "ToggleMode", "SetActivePlan", "OpenPath"], fields);
    }
}
