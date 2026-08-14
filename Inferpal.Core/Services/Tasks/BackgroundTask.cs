namespace Inferpal.Services.Tasks;

/// <summary>Lifecycle of a background agent task (ROADMAP 1.5.0 §9, pulled forward to 1.4.0).</summary>
internal enum BackgroundTaskState
{
    /// <summary>Submitted, waiting for the single GPU slot (and for the chat to go idle).</summary>
    Queued,
    /// <summary>Holding the slot, agent loop in flight.</summary>
    Running,
    /// <summary>Finished with an answer.</summary>
    Succeeded,
    /// <summary>The runner reported an error (network, backend, tool). The message is in <c>Error</c>.</summary>
    Failed,
    /// <summary>Cancelled by the user (<c>/task stop</c>) or by shutdown.</summary>
    Cancelled,
}

/// <summary>
/// Immutable view of a background task, safe to hand to a front-end: the queue mutates its own
/// state under a lock and never leaks the live object, so a VM can hold a snapshot across threads
/// (and across the Remote UI boundary) without tearing.
/// </summary>
/// <param name="QueuePosition">1-based rank among queued tasks; 0 once it is no longer waiting.</param>
/// <param name="ProposeWrites">
/// The task runs in <b>proposal mode</b> (roadmap §18): the editing tools are available to it, but
/// every change is recorded instead of applied. Chosen per task at submission (<c>/task propose</c>)
/// rather than by a setting — read-only stays the default because it is the form that asks nothing
/// of the user when it finishes.
/// </param>
/// <param name="Proposals">
/// Changes the task wanted to make, none of them applied. Reviewed when the report comes back and
/// applied one at a time through the ordinary approval prompt.
/// </param>
internal sealed record BackgroundTaskSnapshot(
    string              Id,
    string              Objective,
    BackgroundTaskState State,
    DateTimeOffset      CreatedAt,
    DateTimeOffset?     StartedAt,
    DateTimeOffset?     FinishedAt,
    string?             Result,
    string?             Error,
    IReadOnlyList<string> Steps,
    int                 QueuePosition,
    bool                ProposeWrites = false,
    IReadOnlyList<TaskProposal>? Proposals = null)
{
    /// <summary>Proposals, never null — the callers all enumerate it.</summary>
    internal IReadOnlyList<TaskProposal> PendingProposals => Proposals ?? [];

    /// <summary>True once the task reached a terminal state (nothing more will change).</summary>
    internal bool IsFinished => State is BackgroundTaskState.Succeeded
                                      or BackgroundTaskState.Failed
                                      or BackgroundTaskState.Cancelled;

    /// <summary>Wall-clock time the task actually ran, or null while still queued.</summary>
    internal TimeSpan? Duration =>
        StartedAt is null ? null : (FinishedAt ?? StartedAt) - StartedAt;
}
