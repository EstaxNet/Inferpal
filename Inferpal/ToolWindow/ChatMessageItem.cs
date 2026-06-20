using System.Collections.ObjectModel;
using System.Runtime.Serialization;
using System.Threading;
using Microsoft.VisualStudio.Extensibility.UI;
using Inferpal.Localization;
using Inferpal.Services;

namespace Inferpal.ToolWindow;

[DataContract]
internal class ChatMessageItem : NotifyPropertyChangedObject
{
    private string          _content          = string.Empty;
    private string          _label            = string.Empty;
    private string          _btnFixWithAi     = string.Empty;
    private string          _fixErrorOutput   = string.Empty;
    private Action<string>? _onFixWithAi;
    private Action?         _onRestoreAll;
    private Action?         _onResume;
    private string          _btnResume       = string.Empty;
    private bool            _isResumable;
    private Action?         _onRegenerate;
    private string          _btnRegenerate   = string.Empty;
    private bool            _isRegeneratable;
    private bool            _isStreaming;
    private bool            _hasBlocks;
    private bool            _isExpanded       = true;
    private bool            _isFixable;
    private bool            _isRestorable;
    private bool            _isSearchDimmed;
    private string _bubbleBackground = "Transparent";
    private string _themeText        = "#D4D4D4";
    private string _themeSubtleText  = "#808080";
    private string _themeToolText    = "#9CDCFE";
    private string _themeCodeText    = "#CE9178";
    private string _themeCodeBg      = "#161616";
    private string _themeCodeBorder  = "#333333";

    [DataMember] public string Role      { get; set; } = string.Empty;
    [DataMember] public string ToolName  { get; set; } = string.Empty;
    [DataMember] public string Timestamp { get; set; } = string.Empty;
    [DataMember] public string Content          { get => _content;          set => SetProperty(ref _content,          value); }
    [DataMember] public string Label            { get => _label;            set => SetProperty(ref _label,            value); }
    [DataMember] public string BtnFixWithAi     { get => _btnFixWithAi;     set => SetProperty(ref _btnFixWithAi,     value); }
    [DataMember] public bool   HasBlocks        { get => _hasBlocks;        set => SetProperty(ref _hasBlocks,        value); }
    [DataMember] public bool   IsExpanded       { get => _isExpanded;       set => SetProperty(ref _isExpanded,       value); }
    [DataMember] public bool   IsFixable        { get => _isFixable;        set => SetProperty(ref _isFixable,        value); }
    [DataMember] public bool   IsRestorable     { get => _isRestorable;     set => SetProperty(ref _isRestorable,     value); }
    [DataMember] public bool   IsRegeneratable  { get => _isRegeneratable;  set => SetProperty(ref _isRegeneratable,  value); }
    [DataMember] public string BtnRegenerate    { get => _btnRegenerate;    set => SetProperty(ref _btnRegenerate,    value); }
    [DataMember] public bool   IsSearchDimmed   { get => _isSearchDimmed;   set => SetProperty(ref _isSearchDimmed,   value); }

    private string _btnRestoreAll = string.Empty;
    [DataMember] public string BtnRestoreAll    { get => _btnRestoreAll;    set => SetProperty(ref _btnRestoreAll,    value); }
    [DataMember] public bool   IsResumable      { get => _isResumable;      set => SetProperty(ref _isResumable,      value); }
    [DataMember] public string BtnResume        { get => _btnResume;        set => SetProperty(ref _btnResume,        value); }
    [DataMember] public string BubbleBackground { get => _bubbleBackground; set => SetProperty(ref _bubbleBackground, value); }
    [DataMember] public string ThemeText        { get => _themeText;        set => SetProperty(ref _themeText,        value); }
    [DataMember] public string ThemeSubtleText  { get => _themeSubtleText;  set => SetProperty(ref _themeSubtleText,  value); }
    [DataMember] public string ThemeToolText    { get => _themeToolText;    set => SetProperty(ref _themeToolText,    value); }
    [DataMember] public string ThemeCodeText    { get => _themeCodeText;    set => SetProperty(ref _themeCodeText,    value); }
    [DataMember] public string ThemeCodeBg      { get => _themeCodeBg;      set => SetProperty(ref _themeCodeBg,      value); }
    [DataMember] public string ThemeCodeBorder  { get => _themeCodeBorder;  set => SetProperty(ref _themeCodeBorder,  value); }

    [DataMember] public ObservableCollection<MarkdownBlock> Blocks    { get; } = [];
    [DataMember] public ObservableCollection<DiffLine>      DiffLines { get; } = [];

    private bool _hasDiff;
    [DataMember] public bool HasDiff { get => _hasDiff; set => SetProperty(ref _hasDiff, value); }

    [DataMember] public AsyncCommand ToggleExpandCommand { get; }
    [DataMember] public AsyncCommand CopyCommand         { get; }
    [DataMember] public AsyncCommand FixWithAiCommand    { get; }
    [DataMember] public AsyncCommand RestoreAllCommand   { get; }
    [DataMember] public AsyncCommand ResumeCommand       { get; }
    [DataMember] public AsyncCommand RegenerateCommand   { get; }

    public ChatMessageItem()
    {
        ToggleExpandCommand = new AsyncCommand(ToggleExpandAsync);
        CopyCommand         = new AsyncCommand(CopyContentAsync);
        FixWithAiCommand    = new AsyncCommand(FixWithAiAsync);
        RestoreAllCommand   = new AsyncCommand(RestoreAllAsync);
        ResumeCommand       = new AsyncCommand(ResumeAsync);
        RegenerateCommand   = new AsyncCommand(RegenerateAsync);
    }

    private Task ToggleExpandAsync(object? _, CancellationToken ct)
    {
        if (Role == "tool")
            IsExpanded = !IsExpanded;
        return Task.CompletedTask;
    }

    internal void InitFixCallback(string fullErrorOutput, Action<string> onFixWithAi)
    {
        _fixErrorOutput = fullErrorOutput;
        _onFixWithAi    = onFixWithAi;
        BtnFixWithAi    = Strings.BtnFixWithAi;
        IsFixable       = true;
    }

    internal void InitRestoreCallback(Action onRestoreAll)
    {
        _onRestoreAll = onRestoreAll;
        BtnRestoreAll = Strings.BtnRestoreAll;
        IsRestorable  = true;
    }

    internal void InitRegenerateCallback(Action onRegenerate)
    {
        _onRegenerate   = onRegenerate;
        BtnRegenerate   = Strings.BtnRegenerate;
        IsRegeneratable = true;
    }

    /// <summary>Shows a "Resume" button on this bubble (used by the step-mode pause bubble).</summary>
    internal void InitResumeCallback(Action onResume)
    {
        _onResume   = onResume;
        BtnResume   = Strings.BtnResume;
        IsResumable = true;
    }

    private Task RestoreAllAsync(object? _, CancellationToken ct)
    {
        _onRestoreAll?.Invoke();
        return Task.CompletedTask;
    }

    private Task ResumeAsync(object? _, CancellationToken ct)
    {
        _onResume?.Invoke();
        return Task.CompletedTask;
    }

    private Task RegenerateAsync(object? _, CancellationToken ct)
    {
        _onRegenerate?.Invoke();
        return Task.CompletedTask;
    }

    internal void InitDiff(DiffInfo diff)
    {
        DiffLines.Clear();
        foreach (var line in DiffComputer.Compute(diff.OldText, diff.NewText))
            DiffLines.Add(line);
        HasDiff = DiffLines.Count > 0;
    }

    private Task FixWithAiAsync(object? _, CancellationToken ct)
    {
        if (_onFixWithAi is not null)
            _onFixWithAi(_fixErrorOutput);
        return Task.CompletedTask;
    }

    private Task CopyContentAsync(object? _, CancellationToken ct)
    {
        var text = Content;
        var thread = new Thread(() =>
        {
            try { System.Windows.Clipboard.SetText(string.IsNullOrEmpty(text) ? " " : text); }
            catch { }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return Task.CompletedTask;
    }

    [DataMember]
    public bool IsStreaming
    {
        get => _isStreaming;
        set
        {
            SetProperty(ref _isStreaming, value);
            if (!value && Role == "assistant")
                ParseMarkdown();
        }
    }

    private void ParseMarkdown()
    {
        Blocks.Clear();
        var isDark = _themeCodeBg == "#161616";
        var tableBorder    = isDark ? "#3F3F46" : "#CCCCCC";
        var tableHeaderBg  = isDark ? "#1E1E1E" : "#F0F0F0";

        foreach (var b in MarkdownParser.Parse(Content))
        {
            b.ThemeText        = _themeText;
            b.ThemeCodeText    = _themeCodeText;
            b.ThemeCodeBg      = _themeCodeBg;
            b.ThemeCodeBorder  = _themeCodeBorder;
            b.ThemeTableBorder = tableBorder;
            foreach (var run in b.Inlines)
                run.Foreground = run.IsCode ? _themeCodeText : _themeText;
            foreach (var cell in b.Cells)
            {
                cell.ThemeText = _themeText;
                cell.ThemeBg   = cell.IsHeader ? tableHeaderBg : "Transparent";
            }
            Blocks.Add(b);
        }
        HasBlocks = Blocks.Count > 0;
    }

    private static string Now() => DateTime.Now.ToString("t", System.Globalization.CultureInfo.CurrentCulture);

    internal static ChatMessageItem UserMsg(string content) =>
        new() { Role = "user", Content = content, Label = "Vous", Timestamp = Now() };

    internal static ChatMessageItem AssistantMsg(string content = "")
    {
        var item = new ChatMessageItem { Role = "assistant", Content = content, Label = "Assistant", Timestamp = Now() };
        item.ParseMarkdown();
        return item;
    }

    internal static ChatMessageItem StreamingMsg(string? modelName = null) =>
        new() { Role = "assistant", Label = string.IsNullOrEmpty(modelName) ? "Assistant" : modelName, IsStreaming = true, Timestamp = Now() };

    internal static ChatMessageItem ToolMsg(string toolName, string content, bool expanded = false) =>
        new() { Role = "tool", ToolName = toolName, Content = content,
                Label = $"🔧 {toolName}", IsExpanded = expanded, Timestamp = Now() };

    internal static ChatMessageItem Anchor() =>
        new() { Role = "anchor" };

    /// <summary>
    /// Creates a live "what-is-the-agent-doing" notification bubble (Role = "status").
    /// The bubble is displayed in the chat during agent execution and removed once the
    /// final response is inserted.  Its <see cref="Content"/> is updated in place as
    /// the agent moves through planning / tool-calling / observation phases.
    /// </summary>
    internal static ChatMessageItem StatusMsg(string step) =>
        new() { Role = "status", Content = step, Label = string.Empty };

    /// <summary>
    /// Creates a live plan bubble (Role = "plan") for the autonomous agent mode.
    /// The content is set once and later updated in-place via <see cref="RefreshPlan"/>.
    /// </summary>
    internal static ChatMessageItem AgentPlanMsg(Inferpal.Models.AgentPlan plan) =>
        new() { Role = "plan", Label = Strings.AgentPlanLabel, Content = plan.ToMarkdown(), Timestamp = Now() };

    /// <summary>
    /// Regenerates the Markdown content of a plan bubble when any step status changes.
    /// Safe to call from any thread — the property setter fires INPC which VS Remote UI handles.
    /// </summary>
    internal void RefreshPlan(Inferpal.Models.AgentPlan plan) =>
        Content = plan.ToMarkdown();

    internal static ChatMessageItem FromSaved(string role, string content, string toolName, bool toolBubblesExpanded = false, string timestamp = "")
    {
        var item = new ChatMessageItem
        {
            Role       = role,
            Content    = content,
            ToolName   = toolName,
            Timestamp  = timestamp,
            IsExpanded = role != "tool" || toolBubblesExpanded,
            Label      = role switch
            {
                "user"      => "Vous",
                "assistant" => "Assistant",
                "tool"      => $"🔧 {toolName}",
                _           => role,
            },
        };
        if (role == "assistant")
            item.ParseMarkdown();
        return item;
    }
}
