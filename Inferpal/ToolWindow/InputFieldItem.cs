using System.Collections.ObjectModel;
using System.Runtime.Serialization;
using Microsoft.VisualStudio.Extensibility.UI;

namespace Inferpal.ToolWindow;

/// <summary>One field of an input card (an MCP server's form): its label, what it holds, what to fix in it.</summary>
/// <remarks>Enter in a box is the field's own command: a binding inside the fields' nested template cannot reach the
/// card's. Esc is bound on the card itself, which every key typed in it reaches.</remarks>
[DataContract]
internal sealed class InputFieldItem : NotifyPropertyChangedObject
{
    private string _text;
    private bool   _flag;

    public InputFieldItem(InputFieldModel field, string cardId, Action send)
    {
        Key        = field.Key;
        Kind       = field.Kind;
        Label      = field.Label;
        IsRequired = field.Required;
        Hint       = field.Hint;
        HasHint    = field.Hint.Length > 0;
        Error      = field.Error;
        HasError   = field.Error.Length > 0;
        Warns      = field.Warns;
        IsTextBox  = field.Kind is "text" or "number";
        IsBox      = field.Kind == "boolean";
        IsSegmented = field.Kind == "choice" && field.Buttons;
        IsRadioList = field.Kind == "choice" && !field.Buttons;
        IsChoices  = field.Kind == "choices";
        RequiredMark = field.Required ? " *" : string.Empty;
        _text      = field.Text;
        _flag      = field.Flag;
        // One group per field of one card: two cards, or two fields, never share their radio buttons.
        var group  = cardId + "/" + field.Key;
        foreach (var o in field.Options)
            Options.Add(new InputOptionItem(o, group));
        SendCommand   = new AsyncCommand((_, _) => { send();   return Task.CompletedTask; });
    }

    /// <summary>The schema's key, sent with the value.</summary>
    internal string Key { get; }

    [DataMember] public string Kind       { get; }
    [DataMember] public string Label      { get; }
    [DataMember] public bool   IsRequired { get; }
    [DataMember] public string Hint       { get; }
    [DataMember] public bool   HasHint    { get; }
    [DataMember] public string Error      { get; }
    [DataMember] public bool   HasError   { get; }
    /// <summary>The field reads like a secret: its hint says a server must not ask for one.</summary>
    [DataMember] public bool   Warns      { get; }
    [DataMember] public bool   IsTextBox  { get; }
    [DataMember] public bool   IsBox      { get; }
    /// <summary>One choice among few short ones: buttons side by side, one pressed.</summary>
    [DataMember] public bool   IsSegmented { get; }
    /// <summary>One choice among many, or long ones: radio buttons, one under the other.</summary>
    [DataMember] public bool   IsRadioList { get; }
    [DataMember] public bool   IsChoices  { get; }
    /// <summary>" *" after the label of a field the server requires; empty otherwise.</summary>
    [DataMember] public string RequiredMark { get; }
    [DataMember] public string Text { get => _text; set => SetProperty(ref _text, value); }
    [DataMember] public bool   Flag { get => _flag; set => SetProperty(ref _flag, value); }
    [DataMember] public ObservableCollection<InputOptionItem> Options { get; } = [];
    [DataMember] public AsyncCommand SendCommand   { get; }

    /// <summary>The value as the card holds it now.</summary>
    internal Services.Mcp.McpInputValue Value() => Kind switch
    {
        "boolean" => new Services.Mcp.McpInputValue(Flag: Flag),
        "choice"  => new Services.Mcp.McpInputValue(Text: Options.FirstOrDefault(o => o.IsSelected)?.Value),
        "choices" => new Services.Mcp.McpInputValue(Picks: [.. Options.Where(o => o.IsSelected).Select(o => o.Value)]),
        _         => new Services.Mcp.McpInputValue(Text: Text),
    };
}

/// <summary>One choice of an input card's field.</summary>
[DataContract]
internal sealed class InputOptionItem : NotifyPropertyChangedObject
{
    private bool _isSelected;

    public InputOptionItem(InputOptionModel option, string group)
    {
        Value       = option.Value;
        Label       = option.Label;
        Group       = group;
        _isSelected = option.Selected;
    }

    /// <summary>The value sent when this choice is picked.</summary>
    internal string Value { get; }

    [DataMember] public string Label { get; }
    /// <summary>The radio group of a one-choice field.</summary>
    [DataMember] public string Group { get; }
    [DataMember] public bool   IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
}
