using System.Collections.ObjectModel;
using System.Runtime.Serialization;
using Microsoft.VisualStudio.Extensibility.UI;

namespace Inferpal.ToolWindow;

internal partial class ChatMessageItem
{
    // ── What a turn left running ("jobs", pari 168): its background jobs, each with Stop, Stop all from two. Screen only
    // (SessionManager.ScreenOnlyRoles): the jobs die with Visual Studio. Its title is CardTitle. ──
    private Services.Presentation.LeftRunningCard? _jobsCard;
    [DataMember] public ObservableCollection<LeftRunningJobItem> Jobs { get; } = [];
    private string _jobsNote = string.Empty;
    [DataMember] public string JobsNote { get => _jobsNote; private set => SetProperty(ref _jobsNote, value); }
    [DataMember] public string BtnStopAll { get; private set; } = string.Empty;
    private bool _hasStopAll;
    [DataMember] public bool HasStopAll { get => _hasStopAll; private set => SetProperty(ref _hasStopAll, value); }
    [DataMember] public AsyncCommand? StopAllCommand { get; private set; }

    /// <summary>The card of what a turn left running; <paramref name="stop"/> stops the jobs it is given, then hands what
    /// was done to each back through <see cref="JobsDone"/> on the window's context.</summary>
    internal static ChatMessageItem JobsMsg(Services.Presentation.LeftRunningCard card,
                                            Func<ChatMessageItem, IReadOnlyList<string>, Task> stop)
    {
        var item = new ChatMessageItem
        {
            Role       = "jobs",
            Timestamp  = Now(),
            CardTitle  = card.Title,
            BtnStopAll = card.StopAll,
            _jobsCard  = card,
        };
        foreach (var job in card.Jobs)
            item.Jobs.Add(new LeftRunningJobItem(job, card.Stop, row => { if (row.TryBeginStop()) _ = stop(item, [row.Id]); }));
        // A row being stopped is not stopped twice: a second kill would find it gone and say it had ended on its own.
        item.StopAllCommand = new AsyncCommand((_, _) =>
            stop(item, [.. item.Jobs.Where(j => !j.IsDone && j.TryBeginStop()).Select(j => j.Id)]));
        item.Refresh();
        return item;
    }

    /// <summary>What a Stop did to each job, by id: its row says it.</summary>
    internal void JobsDone(IReadOnlyDictionary<string, (string Text, bool Done)> done)
    {
        foreach (var row in Jobs)
            if (done.TryGetValue(row.Id, out var outcome)) row.Said(outcome.Text, outcome.Done);
        Refresh();
    }

    /// <summary>The note and Stop all follow the rows: once nothing of the card runs, it says so.</summary>
    private void Refresh()
    {
        var running = Jobs.Count(j => !j.IsDone);
        JobsNote   = running > 0 ? _jobsCard!.Notes[running - 1] : _jobsCard!.NoneNow;
        HasStopAll = running >= 2;
    }
}
