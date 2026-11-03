using System.Runtime.Serialization;
using Microsoft.VisualStudio.Extensibility.UI;

namespace Inferpal.ToolWindow;

/// <summary>
/// One background task in the notice of a start ("away"): what happened to it, and its button — Retry for one the
/// closing stopped (with the command it runs), Open the report for one never read.
/// </summary>
[DataContract]
internal sealed class AwayTaskItem
{
    public AwayTaskItem(Services.Presentation.AwayTask task, string button, Action act)
    {
        Text        = task.Text;
        ButtonLabel = button;
        ButtonName  = $"{button} {task.Id}";
        CommandText = task.Retry ? $"/task retry {task.Id}" : string.Empty;
        HasCommand  = task.Retry;
        var acted = false;   // Retry runs the task once, however many clicks
        ActCommand  = new AsyncCommand((_, _) =>
        {
            if (!task.Retry || !acted) act();
            acted = true;
            return Task.CompletedTask;
        });
    }

    [DataMember] public string Text        { get; }
    [DataMember] public string ButtonLabel { get; }
    /// <summary>The button's accessible name: its label and its task — two Retry buttons are not one.</summary>
    [DataMember] public string ButtonName  { get; }
    [DataMember] public string CommandText { get; }
    [DataMember] public bool   HasCommand  { get; }
    [DataMember] public AsyncCommand ActCommand { get; }
}
