using System.Runtime.Serialization;
using Microsoft.VisualStudio.Extensibility.UI;
using Inferpal.Localization;

namespace Inferpal.ToolWindow;

internal partial class ChatMessageItem
{
    // ── The tool-off notice (pari 163): under the question that calls for tools the person turned off ──
    [DataMember] public string BtnToolOffOnce   { get; private set; } = string.Empty;
    [DataMember] public string BtnToolOffOn     { get; private set; } = string.Empty;
    [DataMember] public string BtnToolOffNotNow { get; private set; } = string.Empty;
    [DataMember] public AsyncCommand? ToolOffOnceCommand   { get; private set; }
    [DataMember] public AsyncCommand? ToolOffOnCommand     { get; private set; }
    [DataMember] public AsyncCommand? ToolOffNotNowCommand { get; private set; }

    /// <summary>
    /// The tool-off notice: <paramref name="finding"/>'s sentence and its three gestures — use the tools for this question,
    /// turn them back on, or not now. One gesture, then the card's buttons are spent (<see cref="CopyCanAct"/>).
    /// </summary>
    internal static ChatMessageItem ToolOffMsg(Services.Presentation.ToolOffFinding finding, Action once, Action on, Action notNow)
    {
        var item = new ChatMessageItem
        {
            Role = "tooloff", CardTitle = Plain(finding.Text),
            BtnToolOffOnce = Strings.ToolOffUseOnce, BtnToolOffOn = Strings.ToolOffTurnOn, BtnToolOffNotNow = Strings.ToolOffNotNow,
        };
        item.ToolOffOnceCommand = item.CopyGesture(once);
        item.ToolOffOnCommand = item.CopyGesture(on);
        item.ToolOffNotNowCommand = item.CopyGesture(notNow);
        return item;
    }
}
