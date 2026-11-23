namespace Inferpal.Services.Execution;

/// <summary>One step of a run's plan as the run's file keeps it: its text, whether it was done, and the tool it planned —
/// what tells a step that writes or checks from one that reads, once it is resumed.</summary>
internal sealed record ResumeStep(string Text, bool Done, string? Tool = null);

/// <summary>The last check a run proved (tests, build): whether it passed and what it was.</summary>
internal sealed record ResumeCheck(bool Passed, string Summary);

/// <summary>
/// What a run carries to be picked up again after the closing cut it — the question as typed, the mode, the plan and which
/// steps were done, one line per action, the last check — kept in the run's file as the run goes.
/// </summary>
/// <remarks>
/// ⚠ The run's file is the only place that survives the cut: the conversation keeps nothing of a turn the editor closes on,
/// and an editor that is killed writes nothing at the end. So each field is written when it changes, never at the end.
/// ⚠ A run that resumes another carries what that one did (<see cref="CarriedFrom"/>): cut again, its own resume still
/// knows every action and file of the work, never only its own.
/// </remarks>
/// <param name="Actions">How many actions the run took, the ones <see cref="Steps"/> no longer lists included.</param>
/// <param name="EarlierFiles">The files the runs it resumes changed, as their records named them.</param>
/// <param name="ResumedFrom">The run this one resumes; <c>null</c> for a run started by a question.</param>
internal sealed record RunResume(string Question, string Mode, string? Goal, IReadOnlyList<ResumeStep> Plan,
                                 IReadOnlyList<string> Steps, ResumeCheck? Check, int Actions = 0,
                                 IReadOnlyList<string>? EarlierFiles = null, string? ResumedFrom = null)
{
    /// <summary>The most action lines kept: the latest, a long run's first actions matter least to what comes next.</summary>
    internal const int MaxSteps = 40;

    /// <summary>The longest action line, question or step text kept.</summary>
    internal const int MaxLine = 160;
    internal const int MaxQuestion = 4000;

    /// <summary>The most files carried from the runs a run resumes.</summary>
    internal const int MaxEarlierFiles = 200;

    /// <summary>A run started on <paramref name="question"/> in <paramref name="mode"/> (<c>agent</c>, <c>chat</c>).</summary>
    internal static RunResume Start(string question, string mode) =>
        new(Cut(question, MaxQuestion), mode, null, [], [], null);

    /// <summary>A run that resumes <paramref name="earlier"/> (run <paramref name="from"/>, which changed
    /// <paramref name="files"/>): its question, its actions, its last check and its files go on with it.</summary>
    internal RunResume CarriedFrom(RunResume earlier, string from, IEnumerable<string> files) =>
        this with
        {
            Question = earlier.Question,
            Steps = earlier.Steps,
            Actions = earlier.Actions,
            Check = earlier.Check,
            EarlierFiles = [.. (earlier.EarlierFiles ?? []).Concat(files).Distinct(PathComparer.Default).Take(MaxEarlierFiles)],
            ResumedFrom = from,
        };

    /// <summary>The same run with its plan as it now stands.</summary>
    internal RunResume WithPlan(string? goal, IEnumerable<ResumeStep> plan) =>
        this with { Goal = goal is null ? null : Cut(goal, MaxLine), Plan = [.. plan.Select(s => s with { Text = Cut(s.Text, MaxLine) })] };

    /// <summary>The same run with one more action line, the oldest dropped past <see cref="MaxSteps"/>; counted either way.</summary>
    internal RunResume WithStep(string line) =>
        this with { Steps = [.. Steps.Append(Cut(line, MaxLine)).TakeLast(MaxSteps)], Actions = Actions + 1 };

    /// <summary>The same run with its last check.</summary>
    internal RunResume WithCheck(bool passed, string summary) => this with { Check = new(passed, Cut(summary, MaxLine)) };

    /// <summary>How many plan steps were done; 0 without a plan.</summary>
    internal int DoneSteps => Plan.Count(s => s.Done);

    private static string Cut(string text, int max)
    {
        text = text.Replace("\r\n", "\n").Trim();
        if (text.Length <= max) return text;
        var keep = char.IsHighSurrogate(text[max - 1]) ? max - 1 : max;
        return text[..keep] + "…";
    }
}
