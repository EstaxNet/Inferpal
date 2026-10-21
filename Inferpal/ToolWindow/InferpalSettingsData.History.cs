using System.Runtime.Serialization;
using Inferpal.Localization;
using Microsoft.VisualStudio.Extensibility.UI;

namespace Inferpal.ToolWindow;

/// <summary>
/// The Agent page's switch for where the copies of the files the agent changes are kept: in the project, or in the
/// user's own application data, with nothing written into the project.
/// </summary>
internal partial class InferpalSettingsData
{
    private string _historyStorage = "project";
    /// <summary><c>project</c> or <c>user</c>: the segment the switch shows selected.</summary>
    [DataMember] public string HistoryStorage { get => _historyStorage; set => SetProperty(ref _historyStorage, value); }
    [DataMember] public AsyncCommand SetHistoryStorageCommand { get; private set; } = null!;

    private string _labelHistoryStorage = string.Empty, _hintHistoryStorage = string.Empty,
                   _labelHistoryStorageProject = string.Empty, _labelHistoryStorageUser = string.Empty;
    [DataMember] public string LabelHistoryStorage        { get => _labelHistoryStorage;        set => SetProperty(ref _labelHistoryStorage,        value); }
    [DataMember] public string HintHistoryStorage         { get => _hintHistoryStorage;         set => SetProperty(ref _hintHistoryStorage,         value); }
    [DataMember] public string LabelHistoryStorageProject { get => _labelHistoryStorageProject; set => SetProperty(ref _labelHistoryStorageProject, value); }
    [DataMember] public string LabelHistoryStorageUser    { get => _labelHistoryStorageUser;    set => SetProperty(ref _labelHistoryStorageUser,    value); }

    /// <summary>The switch's value as the configuration reads it: anything but <c>user</c> is the project.</summary>
    private static string ShownHistoryStorage(Config.InferpalConfig c) => c.HistoryOutsideProject ? "user" : "project";

    /// <summary>The switch's command and its saved value; called by the constructor.</summary>
    private void InitHistoryStorage(Config.InferpalConfig saved)
    {
        _historyStorage          = ShownHistoryStorage(saved);
        SetHistoryStorageCommand = new AsyncCommand((p, _) => RunOnVMContextAsync(() =>
        {
            if (p is "project" or "user") HistoryStorage = (string)p;
        }));
    }

    /// <summary>The switch's words, in the interface language; part of <see cref="ApplyLabels"/>.</summary>
    private void ApplyHistoryStorageLabels()
    {
        LabelHistoryStorage        = Strings.LabelHistoryStorage;
        HintHistoryStorage         = Strings.HintHistoryStorage;
        LabelHistoryStorageProject = Strings.HistoryStorageProject;
        LabelHistoryStorageUser    = Strings.HistoryStorageUser;
    }
}
