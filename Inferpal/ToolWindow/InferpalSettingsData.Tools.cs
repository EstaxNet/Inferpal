using System.Collections.ObjectModel;
using System.Runtime.Serialization;
using Inferpal.Localization;
using Inferpal.Services.Presentation;
using Microsoft.VisualStudio.Extensibility.UI;

namespace Inferpal.ToolWindow;

/// <summary>
/// The Tools page's first block: every tool the chat's registry holds, what each weighs and what the next request does
/// with it, and a button that turns one off or back on (<c>disabledTools</c>) — the rows <c>/tools</c> prints, from
/// <see cref="ToolListPresenter"/>, read through the chat window.
/// </summary>
internal partial class InferpalSettingsData
{
    private string _labelSectionToolList = "", _descSectionToolList = "", _toolListSummary = "", _toolListHowTo = "",
                   _toolListMessage = "", _toolListNoChatText = "";
    private bool _hasToolList, _toolListNoChat, _hasToolListMessage;

    [DataMember] public string LabelSectionToolList { get => _labelSectionToolList; set => SetProperty(ref _labelSectionToolList, value); }
    [DataMember] public string DescSectionToolList  { get => _descSectionToolList;  set => SetProperty(ref _descSectionToolList,  value); }
    [DataMember] public bool   HasToolList          { get => _hasToolList;          set => SetProperty(ref _hasToolList,          value); }
    [DataMember] public bool   ToolListNoChat       { get => _toolListNoChat;       set => SetProperty(ref _toolListNoChat,       value); }
    [DataMember] public string ToolListNoChatText   { get => _toolListNoChatText;   set => SetProperty(ref _toolListNoChatText,   value); }
    [DataMember] public string ToolListSummary      { get => _toolListSummary;      set => SetProperty(ref _toolListSummary,      value); }
    [DataMember] public string ToolListHowTo        { get => _toolListHowTo;        set => SetProperty(ref _toolListHowTo,        value); }
    /// <summary>What the last button said ("Turned off…", or why nothing changed).</summary>
    [DataMember] public string ToolListMessage      { get => _toolListMessage;      set => SetProperty(ref _toolListMessage,      value); }
    [DataMember] public bool   HasToolListMessage   { get => _hasToolListMessage;   set => SetProperty(ref _hasToolListMessage,   value); }
    /// <summary>One row per tool, flat: a row that opens a group carries its title (Remote UI binds no nested lists here).</summary>
    [DataMember] public ObservableCollection<ToolRowItem> ToolRows { get; } = [];

    private void ApplyToolListLabels()
    {
        LabelSectionToolList = Strings.SettingsSectionToolList;
        DescSectionToolList  = Strings.SettingsSectionToolListDesc;
        ToolListNoChatText   = Strings.ToolListNoChat;
    }

    /// <summary>The settings were saved — by <c>/tools off</c> in the chat among others: a Tools page on screen reads its
    /// rows again, or it offers to turn off a tool that already is.</summary>
    private void OnConfigSavedForToolList()
    {
        if (_page == "tools") _ = RefreshToolListAsync();
    }

    private async Task RefreshToolListAsync()
    {
        if (_live?.ToolList is not { } read) return;
        try
        {
            var list = await read();
            await RunOnVMContextAsync(() => ApplyToolList(list));
        }
        catch (Exception ex) { Services.Diagnostics.Swallow("Settings.RefreshToolList", ex); }
    }

    private async Task SetToolAsync(string key, bool off)
    {
        if (_live?.SetTool is not { } set) return;
        try
        {
            var result = await set(key, off);
            await RunOnVMContextAsync(() =>
            {
                ApplyToolList(result?.List);
                ToolListMessage    = result?.Message ?? Strings.ToolListNoChat;
                HasToolListMessage = ToolListMessage.Length > 0;
            });
        }
        catch (Exception ex)
        {
            Services.Diagnostics.Swallow("Settings.SetTool", ex);
            await RunOnVMContextAsync(() =>
            {
                ToolListMessage    = Services.Diagnostics.RootMessage(ex);
                HasToolListMessage = true;
            });
        }
    }

    /// <summary>The rows of <paramref name="list"/>; <c>null</c> = no chat window to read them from, said.</summary>
    private void ApplyToolList(ToolListModel? list)
    {
        ToolListNoChat = list is null;
        HasToolList    = list is not null;
        ToolRows.Clear();
        if (list is null) return;
        ToolListSummary = list.Summary;
        ToolListHowTo   = list.HowTo;
        foreach (var group in list.Groups)
            for (var i = 0; i < group.Rows.Count; i++)
            {
                var row = group.Rows[i];
                var off = !row.Off;
                ToolRows.Add(new ToolRowItem(row, i == 0 ? group.Title : string.Empty, ColorFor(row.State),
                    new AsyncCommand((_, _) => SetToolAsync(row.Key, off))));
            }
    }

    /// <summary>The colour of a state, from the window's own palette (a theme fix reaches this page with the rest): the
    /// warning colour for a tool turned off, the link colour on demand.</summary>
    private string ColorFor(string state) => state switch
    {
        "off" or "offByEntry" => WarningForeground,
        "onDemand"            => LinkForeground,
        "notOffered"          => SubtleForeground,
        _                     => TextForeground,
    };
}

/// <summary>One tool of the Tools page, as Remote UI carries it.</summary>
[DataContract]
internal sealed class ToolRowItem : NotifyPropertyChangedObject
{
    public ToolRowItem(ToolListRow row, string groupTitle, string stateForeground, AsyncCommand toggle)
    {
        Key             = row.Key;
        Weight          = row.Weight;
        StateLabel      = row.StateLabel;
        StateForeground = stateForeground;
        CanToggle       = row.CanToggle;
        ActionLabel     = row.ActionLabel;
        ActionName      = $"{row.ActionLabel} {row.Key}";
        GroupTitle      = groupTitle;
        IsGroupStart    = groupTitle.Length > 0;
        ToggleCommand   = toggle;
    }

    [DataMember] public string Key             { get; }
    [DataMember] public string Weight          { get; }
    [DataMember] public string StateLabel      { get; }
    [DataMember] public string StateForeground { get; }
    [DataMember] public bool   CanToggle       { get; }
    [DataMember] public string ActionLabel     { get; }
    /// <summary>The button's name for a screen reader: the action and the tool it acts on.</summary>
    [DataMember] public string ActionName      { get; }
    [DataMember] public string GroupTitle      { get; }
    [DataMember] public bool   IsGroupStart    { get; }
    [DataMember] public AsyncCommand ToggleCommand { get; }
}
