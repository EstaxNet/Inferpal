using System.Text;
using Inferpal.Localization;
using Inferpal.Models;

namespace Inferpal.Services.Execution;

/// <summary>
/// A run the closing cut, picked up again: the bubble the person sees (a question and what goes with it), the question the
/// model reads, the plan the orchestrator resumes, and what changed since the cut. Built once, in the Core, for Visual
/// Studio, the host and ACP.
/// </summary>
/// <param name="Bubble">The bubble: "Resume the run cut at …", and its chips (<see cref="ChatTurnPolicy.BuildBubbleText"/>).</param>
/// <param name="Prompt">The question the model reads, in English: the request, what was done, what to do now.</param>
/// <param name="Question">The request as the person typed it: what the turn's context is searched with.</param>
/// <param name="Plan">The plan with its finished steps marked; <c>null</c> for a run without one (Chat mode).</param>
/// <param name="Agent">The run was an Agent-mode run: it is resumed in that mode.</param>
/// <param name="ChangedSince">The files changed since the cut, as the notice names them.</param>
/// <param name="RunId">The run resumed.</param>
/// <param name="Carried">What it carried: the next run starts from it (<see cref="RunResume.CarriedFrom"/>).</param>
/// <param name="Files">Every file the work changed, the runs it resumes included (full paths).</param>
internal sealed record RunResumeTurn(string Bubble, string Prompt, string Question, AgentPlan? Plan, bool Agent,
                                     IReadOnlyList<string> ChangedSince, string RunId, RunResume Carried, IReadOnlyList<string> Files)
{
    /// <summary>
    /// The turn that resumes <paramref name="run"/>; <c>null</c> when it carries nothing to resume (a run of a slash
    /// command, or one written before runs kept their question).
    /// </summary>
    /// <param name="changedSince">Whether a file changed since the run — the judgement <c>/undo-run</c> makes
    /// (<see cref="FileHistoryService.ChangedSinceRun"/>), so the card and the undo never disagree.</param>
    internal static RunResumeTurn? Of(HistoryRun run, Func<string, bool> changedSince, string? root, DateTime now)
    {
        if (run.Resume is not { } resume) return null;
        var files = Presentation.RunSummary.Files(run).Select(f => f.Path)
            .Concat(resume.EarlierFiles ?? []).Distinct(PathComparer.Default).ToList();
        var changed = files.Where(changedSince).Select(f => Named(f, root)).ToList();
        var time = Presentation.AwayNotice.Time(run.LastChange ?? run.StartedAt, now);

        var chips = new List<string>();
        if (resume.Plan.Count > 0) chips.Add(Strings.ResumeChipPlan(resume.DoneSteps, resume.Plan.Count));
        chips.Add(Strings.ResumeChipFiles(files.Count));
        if (resume.Check is { } check) chips.Add(check.Passed ? Strings.ResumeChipCheckPassed : Strings.ResumeChipCheckFailed);
        chips.AddRange(changed.Select(Strings.ResumeChipChangedSince));
        var bubble = ChatTurnPolicy.BuildBubbleText(Strings.ResumeRunQuestion(time), chips);

        var plan = resume.Plan.Count == 0 || resume.Mode != "agent" ? null : new AgentPlan
        {
            Goal = resume.Goal ?? string.Empty,
            Steps = [.. resume.Plan.Select((s, i) => new AgentPlanStep
            {
                Index = i + 1,
                Description = s.Text,
                ToolHint = s.Tool,
                Status = s.Done ? AgentStepStatus.Done : AgentStepStatus.Pending,
            })],
        };
        // A run whose editor was killed never noted what each file held at its end: whether one changed since is unknown.
        var known = run.AfterHashes.Count > 0;
        return new(bubble, BuildPrompt(resume, files.Select(f => Named(f, root)).ToList(), changed, time, known), resume.Question, plan,
                   resume.Mode == "agent", changed, run.Id, resume, files);
    }

    /// <summary>What the away notice says of a run it can resume: what goes with it, its last check, what changed since.</summary>
    internal static string CardText(HistoryRun run, IReadOnlyList<string> changedSince)
    {
        if (run.Resume is not { } resume) return string.Empty;
        var text = new StringBuilder(resume.Plan.Count > 0
            ? Strings.AwayResumePlan(resume.DoneSteps, resume.Plan.Count)
            : Strings.AwayResumeNoPlan(Math.Max(resume.Actions, resume.Steps.Count)));
        if (resume.Check is { } check)
            text.Append(' ').Append(check.Summary == "tests"
                ? check.Passed ? Strings.AwayResumeTestsPassed : Strings.AwayResumeTestsFailed
                : check.Passed ? Strings.AwayResumeBuildPassed : Strings.AwayResumeBuildFailed);
        if (changedSince.Count > 0) text.Append(' ').Append(Strings.AwayResumeChangedSince(string.Join(", ", changedSince)));
        return text.ToString();
    }

    /// <summary>The question the model reads to resume: in English, as every instruction to the model.</summary>
    /// <remarks>⚠ The request and the plan were written by the person and the model: quoted as what was asked and planned,
    /// never as instructions of the product. ⚠ Each file is read again before it is changed: the run's memory of it is gone,
    /// and the person may have changed it.</remarks>
    internal static string BuildPrompt(RunResume resume, IReadOnlyList<string> files, IReadOnlyList<string> changed, string time,
                                       bool changesKnown = true)
    {
        var sb = new StringBuilder();
        sb.Append("This run was cut when the editor closed (its last change at ").Append(time)
          .Append("). Pick it up where it stopped and finish the request.\n\n");
        sb.Append("The request, as the person typed it:\n").Append(resume.Question).Append("\n\n");
        if (resume.Plan.Count > 0)
        {
            sb.Append("The plan, with what was done before the cut:\n");
            for (var i = 0; i < resume.Plan.Count; i++)
                sb.Append(i + 1).Append(". [").Append(resume.Plan[i].Done ? "done" : "not done").Append("] ").Append(resume.Plan[i].Text)
                  .Append(resume.Plan[i].Tool is { Length: > 0 } tool ? $" (tool: {tool})" : "").Append('\n');
            sb.Append('\n');
        }
        if (resume.Steps.Count > 0)
        {
            var total = Math.Max(resume.Actions, resume.Steps.Count);
            // ⚠ A cut list is said as one: told "do not redo what is done", the model must know it does not see all of it.
            sb.Append(total > resume.Steps.Count
                ? $"What the run already did — its last {resume.Steps.Count} of {total} actions (oldest first):\n"
                : "What the run already did (oldest first):\n");
            foreach (var step in resume.Steps) sb.Append("- ").Append(step).Append('\n');
            sb.Append('\n');
        }
        sb.Append("Files it changed: ").Append(string.Join(", ", files)).Append(".\n");
        if (changed.Count > 0)
            sb.Append("Changed since the cut, by the person: ").Append(string.Join(", ", changed))
              .Append(". Keep their change: build on it, never write over it.\n");
        if (!changesKnown)
            sb.Append("The editor stopped before it could note what each file held: any of them may have changed since.\n");
        if (resume.Check is { } check)
            sb.Append("Its last check: the ").Append(check.Summary).Append(check.Passed ? " passed.\n" : " failed.\n");
        sb.Append("\nDo not redo what is done. Read again every file before you change it: what the run knew of it is gone. ")
          .Append(resume.Plan.Count > 0 ? "Go on from the first step not done." : "Go on from the last action.");
        return sb.ToString();
    }

    private static string Named(string path, string? root)
    {
        var named = Commands.UndoRunCommandHandler.Relativise(path, root);
        return System.IO.Path.IsPathRooted(named) ? named : named.Replace('\\', '/');
    }
}
