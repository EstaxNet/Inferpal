using System.Runtime.Serialization;
using Inferpal.Localization;
using Inferpal.Services.Inference;
using Microsoft.VisualStudio.Extensibility.UI;

namespace Inferpal.ToolWindow;

/// <summary>
/// The reasoning effort of a question and of an agent run, on the Server and models page: four levels each, drawn as a
/// segmented switch.
/// </summary>
internal partial class InferpalSettingsData
{
    private string _reasoningEffortChat = "low", _reasoningEffortAgent = "server";
    // What the configuration holds, saved back as it is until a level is chosen (see Shown).
    private string _effortChatAsRead = "low", _effortAgentAsRead = "server";
    private string _labelReasoningEffortChat = "", _hintReasoningEffortChat = "", _labelReasoningEffortAgent = "",
                   _hintReasoningEffortAgent = "", _labelEffortServer = "", _labelEffortLow = "", _labelEffortMedium = "",
                   _labelEffortHigh = "";

    [DataMember] public string ReasoningEffortChat  { get => _reasoningEffortChat;  set => SetProperty(ref _reasoningEffortChat,  value); }
    [DataMember] public string ReasoningEffortAgent { get => _reasoningEffortAgent; set => SetProperty(ref _reasoningEffortAgent, value); }
    [DataMember] public AsyncCommand SetReasoningEffortChatCommand  { get; private set; } = null!;
    [DataMember] public AsyncCommand SetReasoningEffortAgentCommand { get; private set; } = null!;

    [DataMember] public string LabelReasoningEffortChat  { get => _labelReasoningEffortChat;  set => SetProperty(ref _labelReasoningEffortChat,  value); }
    [DataMember] public string HintReasoningEffortChat   { get => _hintReasoningEffortChat;   set => SetProperty(ref _hintReasoningEffortChat,   value); }
    [DataMember] public string LabelReasoningEffortAgent { get => _labelReasoningEffortAgent; set => SetProperty(ref _labelReasoningEffortAgent, value); }
    [DataMember] public string HintReasoningEffortAgent  { get => _hintReasoningEffortAgent;  set => SetProperty(ref _hintReasoningEffortAgent,  value); }
    [DataMember] public string LabelEffortServer { get => _labelEffortServer; set => SetProperty(ref _labelEffortServer, value); }
    [DataMember] public string LabelEffortLow    { get => _labelEffortLow;    set => SetProperty(ref _labelEffortLow,    value); }
    [DataMember] public string LabelEffortMedium { get => _labelEffortMedium; set => SetProperty(ref _labelEffortMedium, value); }
    [DataMember] public string LabelEffortHigh   { get => _labelEffortHigh;   set => SetProperty(ref _labelEffortHigh,   value); }

    /// <summary>The value a switch shows for <paramref name="setting"/>: the level it names, the server's for an empty one.</summary>
    /// <remarks>⚠ Only shown: the value is saved back as the file holds it (<c>Low</c>, <c>off</c>, empty) until a level is
    /// chosen. Rewritten, it would count as this window's change, and the save would overwrite what the other editor
    /// wrote in the shared file since.</remarks>
    private static string Shown(string? setting) => setting?.Trim().ToLowerInvariant() is { Length: > 0 } v ? v : "server";

    /// <summary>The switches' saved values and their commands; called by the constructor.</summary>
    private void InitReasoning(Config.InferpalConfig saved)
    {
        LoadReasoning(saved);
        SetReasoningEffortChatCommand  = new AsyncCommand((p, _) => RunOnVMContextAsync(() =>
        {
            if (p is not ("server" or "low" or "medium" or "high")) return;
            _effortChatAsRead = (string)p;
            if (ReasoningEffortChat == (string)p) RefreshUnsaved();   // "Low" in the file, "low" chosen: still a change
            else ReasoningEffortChat = (string)p;
        }));
        SetReasoningEffortAgentCommand = new AsyncCommand((p, _) => RunOnVMContextAsync(() =>
        {
            if (p is not ("server" or "low" or "medium" or "high")) return;
            _effortAgentAsRead = (string)p;
            if (ReasoningEffortAgent == (string)p) RefreshUnsaved();
            else ReasoningEffortAgent = (string)p;
        }));
    }

    /// <summary>Shows the levels <paramref name="config"/> holds, and keeps them as written for the save.</summary>
    private void LoadReasoning(Config.InferpalConfig config)
    {
        _effortChatAsRead    = config.ReasoningEffortChat ?? string.Empty;
        _effortAgentAsRead   = config.ReasoningEffortAgent ?? string.Empty;
        ReasoningEffortChat  = Shown(_effortChatAsRead);
        ReasoningEffortAgent = Shown(_effortAgentAsRead);
    }

    /// <summary>The two values Save writes: the file's own, until a level is chosen.</summary>
    private (string Chat, string Agent) ReasoningToSave => (_effortChatAsRead, _effortAgentAsRead);

    /// <summary>The switches' words, in the interface language; part of <see cref="ApplyLabels"/>.</summary>
    private void ApplyReasoningLabels()
    {
        LabelReasoningEffortChat  = Strings.LabelReasoningEffortChat;
        HintReasoningEffortChat   = Strings.HintReasoningEffortChat;
        LabelReasoningEffortAgent = Strings.LabelReasoningEffortAgent;
        HintReasoningEffortAgent  = Strings.HintReasoningEffortAgent;
        LabelEffortServer = Strings.ReasoningEffortServer;
        LabelEffortLow    = Strings.ReasoningEffortLow;
        LabelEffortMedium = Strings.ReasoningEffortMedium;
        LabelEffortHigh   = Strings.ReasoningEffortHigh;
    }
}
