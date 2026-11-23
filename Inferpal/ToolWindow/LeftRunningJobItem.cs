using System.Threading;
using System.Runtime.Serialization;
using Microsoft.VisualStudio.Extensibility.UI;

namespace Inferpal.ToolWindow;

/// <summary>
/// One row of the "still running after this run" card (pari 168): a background job the turn left running, its Stop, and
/// — once stopped, or found ended on its own — what was done.
/// </summary>
[DataContract]
internal sealed class LeftRunningJobItem : NotifyPropertyChangedObject
{
    public LeftRunningJobItem(Services.Presentation.LeftRunningJob job, string stop, Action<LeftRunningJobItem> act)
    {
        Id          = job.Id;
        Command     = job.Command;
        StopLabel   = stop;
        StopName    = $"{stop} {job.Id}";
        StopCommand = new AsyncCommand((_, _) => { act(this); return Task.CompletedTask; });
    }

    [DataMember] public string Id        { get; }
    [DataMember] public string Command   { get; }
    [DataMember] public string StopLabel { get; }
    /// <summary>The button's accessible name: its label and its job — two Stop buttons are not one.</summary>
    [DataMember] public string StopName  { get; }
    [DataMember] public AsyncCommand StopCommand { get; }

    private string _outcome = string.Empty;
    private bool _done;
    private int _stopping;

    /// <summary>What a Stop did ("Stopped", "Had already ended — exit code 1", "Could not be stopped…"); empty before.</summary>
    [DataMember] public string Outcome { get => _outcome; private set { SetProperty(ref _outcome, value); RaiseNotifyPropertyChangedEvent(nameof(HasOutcome)); } }
    [DataMember] public bool HasOutcome => _outcome.Length > 0;
    /// <summary>The job no longer runs: its Stop goes away. A kill that did not end it leaves Stop.</summary>
    [DataMember] public bool IsDone { get => _done; private set => SetProperty(ref _done, value); }

    /// <summary>Claims the row for one Stop at a time (any thread); false while one is under way or once it is done.</summary>
    internal bool TryBeginStop() => !_done && Interlocked.CompareExchange(ref _stopping, 1, 0) == 0;

    /// <summary>What the Stop did; the first one that ended the job is kept.</summary>
    internal void Said(string outcome, bool done)
    {
        Interlocked.Exchange(ref _stopping, 0);
        if (_done) return;
        Outcome = outcome;
        IsDone  = done;
    }
}
