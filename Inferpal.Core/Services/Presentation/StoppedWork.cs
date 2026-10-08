using Inferpal.Localization;
using Inferpal.Services.Tasks;

namespace Inferpal.Services.Presentation;

/// <summary>
/// What a stopping host takes with it — background tasks, background commands, a documentation indexing pass — said in
/// the conversation, or <c>null</c> when nothing runs.
/// </summary>
/// <remarks>
/// ⚠ All three live in the host process only, and VS Code restarts it (a server setting, the workspace root changed):
/// the task's report never came, the dev server started as <c>bg1</c> was gone, the docs source stayed at 0 pages — and
/// nothing said so. Ids start again in the new host, so an old <c>/task apply t1</c> reaches another task, or none.
/// </remarks>
internal static class StoppedWork
{
    internal static string? Notice(
        IReadOnlyList<BackgroundTaskSnapshot> tasks,
        IReadOnlyList<(string Id, string Command)> commands,
        string? indexingSite)
    {
        var items = new List<string>();
        foreach (var t in tasks.Where(t => t.State is BackgroundTaskState.Queued or BackgroundTaskState.Running))
            items.Add(Strings.StoppedWorkTask(t.Id, ChatTurnPolicy.OneLinePreview(t.Objective, 80)));
        foreach (var (id, command) in commands)
            items.Add(Strings.StoppedWorkCommand(id, ChatTurnPolicy.OneLinePreview(command, 80)));
        if (!string.IsNullOrEmpty(indexingSite))
            items.Add(Strings.StoppedWorkDocs(indexingSite));

        return items.Count == 0 ? null : Strings.StoppedWorkNotice(string.Join("\n", items.Select(i => "- " + i)));
    }
}
