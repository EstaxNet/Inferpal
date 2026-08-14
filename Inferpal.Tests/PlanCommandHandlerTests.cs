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
