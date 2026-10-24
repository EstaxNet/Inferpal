using Inferpal.Localization;
using Inferpal.Services.Execution;

namespace Inferpal.Services.Presentation;

/// <summary>One file of the card: what going back does to it, said.</summary>
internal sealed record RewindCardLine(string Path, string Name, RewindFileKind Kind, string Label);

/// <summary>The card a question's "Go back here" opens under it, the same in both editors.</summary>
/// <param name="Question">The question as typed, without what went with it — what goes back in the message box.</param>
/// <param name="Refused">Nothing can be undone: <paramref name="Title"/> and <paramref name="Body"/> say why, and the card
/// offers the conversation only, or the runs' reviews.</param>
/// <param name="RunIds">The runs that followed the question, newest first: what Go back expects to undo, and their review
/// cards when refused.</param>
internal sealed record RewindCardModel(
    int Turn, string Question, string Title, string Body, IReadOnlyList<RewindCardLine> Files, string Note, bool Refused,
    IReadOnlyList<string> RunIds);

/// <summary>Builds <see cref="RewindCardModel"/> from <see cref="RewindPlan"/>.</summary>
internal static class RewindCard
{
    private const int QuestionPreview = 80;

    /// <summary>The question as typed, and the names of what went with it (attachments are not sent again).</summary>
    public static (string Text, IReadOnlyList<string> Names) Typed(string saved)
    {
        var (text, names) = Agent.ChatTurnPolicy.SplitBubbleText(saved);
        return (text.Trim(), names);
    }

    public static RewindCardModel Build(RewindPlan plan)
    {
        var question = Typed(plan.Question).Text;
        var shown    = question.ReplaceLineEndings(" ");
        if (shown.Length > QuestionPreview) shown = shown[..QuestionPreview].TrimEnd() + "…";
        var runIds = plan.Runs.Select(r => r.Id).ToList();
        if (plan.Refusal != RewindRefusal.None)
        {
            var name = plan.RefusedPath is { } p ? Path.GetFileName(p) : string.Empty;
            var (title, body) = plan.Refusal switch
            {
                RewindRefusal.ChangedSince   => (Strings.RewindRefusedChangedTitle, plan.ChangedAt is { } at
                                                    ? Strings.RewindRefusedChanged(name, at.ToString("HH:mm"))
                                                    : Strings.RewindRefusedGone(name)),
                RewindRefusal.ChangedBetween => (Strings.RewindRefusedChangedTitle, Strings.RewindRefusedBetween(name)),
                RewindRefusal.NotKept        => (Strings.RewindRefusedTitle, Strings.RewindRefusedNotKept(FileHistoryService.MaxRetainedRuns)),
                RewindRefusal.NoCopy         => (Strings.RewindRefusedTitle, Strings.RewindRefusedNoCopy(name)),
                RewindRefusal.Stale          => (Strings.RewindRefusedTitle, Strings.RewindPlanChanged),
                _                            => (Strings.RewindRefusedTitle, Strings.UndoRunWhileBusy),
            };
            return new RewindCardModel(plan.Turn, question, title, body, [], string.Empty, Refused: true, runIds);
        }
        var lines = plan.Files.Select(f => new RewindCardLine(f.Path, Path.GetFileName(f.Path), f.Kind, f.Kind switch
        {
            RewindFileKind.Deleted => Strings.RewindDeleted,
            RewindFileKind.Kept    => Strings.RewindKept,
            _                      => Strings.RewindPutBack,
        })).ToList();
        return new RewindCardModel(plan.Turn, question, Strings.RewindTitle(shown),
                                   plan.Runs.Count > 0 ? Strings.RewindLead(plan.Runs.Count) : Strings.RewindLeadNoRun,
                                   lines, Strings.RewindNote, Refused: false, runIds);
    }

    /// <summary>What the conversation says once it is back at the question — and that what went with it is not attached
    /// again: the question comes back as typed.</summary>
    public static string Done(RewindOutcome? outcome, int turn, string savedQuestion)
    {
        var done = outcome is null ? Strings.RewindDoneConversationOnly(turn) : Strings.RewindDone(turn, outcome.PutBack, outcome.Deleted);
        var names = Typed(savedQuestion).Names;
        return names.Count == 0 ? done : done + " " + Strings.RewindAttachmentsNotAgain(string.Join(", ", names));
    }
}
