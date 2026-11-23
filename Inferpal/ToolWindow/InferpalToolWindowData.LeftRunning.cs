using Inferpal.Services.Presentation;

namespace Inferpal.ToolWindow;

/// <summary>What a turn left running (pari 168): the background jobs it started that still run, said under its answer.</summary>
internal partial class InferpalToolWindowData
{
    /// <summary>Under the turn that ends, the card of the jobs it started after <paramref name="mark"/> and left running;
    /// nothing for a turn that ran no tools (no mark) or left nothing.</summary>
    private void ShowLeftRunning(int? mark)
    {
        if (mark is not { } since
            || LeftRunning.Card(LeftRunning.Since(_tools.Jobs, since), EditorName) is not { } card) return;
        InsertThemed(ChatMessageItem.JobsMsg(card, StopJobsAsync));
        ScrollToBottom();
    }

    /// <summary>Stops the jobs a card names, each one that has not ended on its own, off the window's context (a process
    /// tree is killed); the card's rows then say what was done.</summary>
    private async Task StopJobsAsync(ChatMessageItem card, IReadOnlyList<string> ids)
    {
        var jobs = _tools.Jobs;
        var done = await Task.Run(() => (IReadOnlyDictionary<string, (string Text, bool Done)>)ids.Distinct(StringComparer.Ordinal)
            .ToDictionary(id => id, id => LeftRunning.Outcome(jobs?.StopIfRunning(id) ?? (Services.Shell.BackgroundShellRegistry.StopOutcome.AlreadyEnded, null)), StringComparer.Ordinal));
        await RunOnVMContextAsync(() => card.JobsDone(done));
    }
}
