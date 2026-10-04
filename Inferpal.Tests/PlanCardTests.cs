#if WINDOWS
using System.Collections;
using System.Reflection;
#endif
using Inferpal.Models;
using Inferpal.Services.Presentation;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The agent's plan card in Visual Studio is drawn from the plan's Markdown (<see cref="AgentPlan.ToMarkdown"/>, the text a
/// session saves and an export writes), read back by <see cref="PlanCard"/>. Shown as a plain text block, that Markdown
/// puts "**🗂 Plan:**" on screen, asterisks included, under the card's own "Agent Plan" label.
/// </summary>
public class PlanCardTests
{
    private static AgentPlan SamplePlan() => new()
    {
        Goal  = "Add argument validation to OrderService.ApplyDiscount",
        Steps =
        [
            new AgentPlanStep { Index = 1, Description = "Read OrderService.cs",          Status = AgentStepStatus.Done },
            new AgentPlanStep { Index = 2, Description = "Add the guard clauses",          Status = AgentStepStatus.Active },
            new AgentPlanStep { Index = 3, Description = "Write the xUnit tests",          Status = AgentStepStatus.Failed },
            new AgentPlanStep { Index = 4, Description = "Update the README",              Status = AgentStepStatus.Skipped },
            new AgentPlanStep { Index = 5, Description = "Run the tests",                  Status = AgentStepStatus.Pending },
        ],
    };

    [Fact]
    public void TheCard_ReadsBackTheGoalAndEveryStepWithItsState()
    {
        var card = PlanCard.Read(SamplePlan().ToMarkdown());

        Assert.Equal("Add argument validation to OrderService.ApplyDiscount", card.Goal);
        Assert.Equal(
            new[]
            {
                new PlanCardStep("1. Read OrderService.cs", AgentStepStatus.Done),
                new PlanCardStep("2. Add the guard clauses", AgentStepStatus.Active),
                new PlanCardStep("3. Write the xUnit tests", AgentStepStatus.Failed),
                new PlanCardStep("4. Update the README", AgentStepStatus.Skipped),
                new PlanCardStep("5. Run the tests", AgentStepStatus.Pending),
            },
            card.Steps);
    }

    [Fact]
    public void LinesTheModelWroteOverSeveralLines_StayWithTheirGoalOrStep()
    {
        var plan = new AgentPlan
        {
            Goal  = "Fix the build\nthen run the tests",
            Steps = [new AgentPlanStep { Index = 1, Description = "Read the errors\nin Program.cs" }],
        };

        var card = PlanCard.Read(plan.ToMarkdown());

        Assert.Equal("Fix the build then run the tests", card.Goal);
        Assert.Equal(new[] { new PlanCardStep("1. Read the errors in Program.cs", AgentStepStatus.Pending) }, card.Steps);
    }

    [Fact]
    public void AnIconWithItsPresentationSelector_IsTheSameState()
    {
        var card = PlanCard.Read(AgentPlan.GoalPrefix + " Ship it\n\n✅️ 1. Build\r\n⏳ 2. Tag");

        Assert.Equal("Ship it", card.Goal);
        Assert.Equal(
            new[] { new PlanCardStep("1. Build", AgentStepStatus.Done), new PlanCardStep("2. Tag", AgentStepStatus.Pending) },
            card.Steps);
    }

    [Fact]
    public void TextThatIsNotAPlan_IsKeptAsTheGoal_AndNothingIsReadAsAStep()
    {
        var none = PlanCard.Read(null);
        Assert.Equal("", none.Goal);
        Assert.Empty(none.Steps);

        var card = PlanCard.Read("A plan written by hand");
        Assert.Equal("A plan written by hand", card.Goal);
        Assert.Empty(card.Steps);
    }

#if WINDOWS
    // ── The Visual Studio card: the view model's item, built by reflection (its base type is the SDK's) ──

    private static readonly Type ItemType =
        typeof(Inferpal.ToolWindow.VsThemeDetector).Assembly.GetType("Inferpal.ToolWindow.ChatMessageItem", throwOnError: true)!;

    private static object Factory(string name, params object?[] args) =>
        ItemType.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(null, args)!;

    private static object? Get(object target, string property) =>
        target.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target);

    private static List<(string Text, string State)> Rows(object item) =>
        ((IList)Get(item, "PlanSteps")!).Cast<object>().Select(r => ((string)Get(r, "Text")!, (string)Get(r, "State")!)).ToList();

    [Fact]
    public void InVisualStudio_ALivePlan_IsDrawnAsAGoalAndRows_AndAStepChangingState_RepaintsItsRow()
    {
        var plan = SamplePlan();
        var item = Factory("AgentPlanMsg", plan);

        Assert.Equal("Add argument validation to OrderService.ApplyDiscount", Get(item, "PlanGoal"));
        Assert.Equal(true, Get(item, "HasPlanGoal"));
        Assert.Equal(("2. Add the guard clauses", "Active"), Rows(item)[1]);

        var firstRow = ((IList)Get(item, "PlanSteps")!)[4];
        plan.Steps[4].Status = AgentStepStatus.Active;
        ItemType.GetMethod("RefreshPlan", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(item, [plan]);

        Assert.Equal(("5. Run the tests", "Active"), Rows(item)[4]);
        Assert.Same(firstRow, ((IList)Get(item, "PlanSteps")!)[4]);
        Assert.Equal(plan.ToMarkdown(), Get(item, "Content"));   // the session and the export still get the Markdown
    }

    [Fact]
    public void InVisualStudio_APlanRestoredWithASession_IsDrawnLikeALiveOne()
    {
        var live     = Factory("AgentPlanMsg", SamplePlan());
        var restored = Factory("FromSaved", "plan", SamplePlan().ToMarkdown(), "", false, "");

        Assert.Equal(Get(live, "PlanGoal"), Get(restored, "PlanGoal"));
        Assert.Equal(Rows(live), Rows(restored));
        Assert.Equal(5, Rows(restored).Count);
    }
#endif
}
