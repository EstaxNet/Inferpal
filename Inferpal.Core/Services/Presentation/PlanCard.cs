using Inferpal.Models;

namespace Inferpal.Services.Presentation;

/// <summary>One row of the plan card: the step as written ("2. Update OrderService.cs") and its state.</summary>
internal sealed record PlanCardStep(string Text, AgentStepStatus Status);

/// <summary>
/// The agent's plan as the chat draws it: the goal, then one row per step with its state.
/// </summary>
/// <remarks>
/// Read back from the Markdown the plan bubble holds (<see cref="AgentPlan.ToMarkdown"/>) — the text a saved session
/// restores and an export writes — so a live card and a reloaded one go through the same reader. The card never shows
/// that Markdown as text: its <c>**</c> markers would appear as written, under a second "Plan:" label.
/// </remarks>
internal sealed record PlanCard(string Goal, IReadOnlyList<PlanCardStep> Steps)
{
    /// <summary>
    /// Reads a plan's Markdown. Never throws: a line that opens with no step icon continues the line before it (a goal
    /// or a step description the model wrote on several lines), so nothing written is dropped.
    /// </summary>
    public static PlanCard Read(string? markdown)
    {
        var goal  = new List<string>();
        var steps = new List<(List<string> Lines, AgentStepStatus Status)>();
        var lines = (markdown ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var n = 0; n < lines.Length; n++)
        {
            var line = lines[n].Trim();
            if (line.Length == 0) continue;
            if (n == 0 && line.StartsWith(AgentPlan.GoalPrefix, StringComparison.Ordinal))
                line = line[AgentPlan.GoalPrefix.Length..].Trim();
            if (StepOf(line) is { } step)
                steps.Add(([step.Text], step.Status));
            else if (steps.Count > 0)
                steps[^1].Lines.Add(line);
            else if (line.Length > 0)
                goal.Add(line);
        }
        return new PlanCard(
            string.Join(" ", goal),
            steps.Select(s => new PlanCardStep(string.Join(" ", s.Lines.Where(l => l.Length > 0)), s.Status)).ToList());
    }

    private static PlanCardStep? StepOf(string line)
    {
        foreach (var (icon, status) in AgentPlan.StepIcons)
        {
            if (!line.StartsWith(icon, StringComparison.Ordinal)) continue;
            var rest = line[icon.Length..].TrimStart('️').Trim();   // an emoji may carry its presentation selector
            return new PlanCardStep(rest, status);
        }
        return null;
    }
}
