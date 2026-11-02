using System.Runtime.Serialization;
using Inferpal.Localization;
using Microsoft.VisualStudio.Extensibility.UI;

namespace Inferpal.ToolWindow;

/// <summary>
/// An API key of the settings form, masked once saved. Remote UI has no bindable <c>PasswordBox</c>, so a saved key shows
/// as dots until asked for, then read-only; a new one is typed in its place. The form's own string property stays the
/// value Save writes and the unsaved count compares: this item only says which box shows it.
/// </summary>
[DataContract]
internal sealed class SecretFieldItem : NotifyPropertyChangedObject
{
    private readonly Func<string?> _saved;
    private readonly Func<string?> _value;
    private readonly Action<string> _setValue;
    private bool   _isMasked, _isShown, _isEditing, _canChange, _canKeepSaved;
    private string _hint = string.Empty, _defaultHint = string.Empty,
                   _showLabel = string.Empty, _hideLabel = string.Empty, _changeLabel = string.Empty, _keepLabel = string.Empty,
                   _showName  = string.Empty, _hideName  = string.Empty, _changeName  = string.Empty, _keepName  = string.Empty;

    /// <param name="saved">The key as the form last read or saved it — what its unsaved count compares with.</param>
    /// <param name="value">The form's box.</param>
    /// <param name="setValue">Sets the form's box: emptied to type a new key, given back the saved one.</param>
    /// <param name="onForm">Runs a click on the form's own thread, like its other commands: Change and Keep set the box,
    /// whose role lists follow.</param>
    /// <remarks>Shows nothing until <see cref="Reset"/>: the form calls it once every box holds its key.</remarks>
    public SecretFieldItem(Func<string?> saved, Func<string?> value, Action<string> setValue, Func<Action, Task> onForm)
    {
        _saved    = saved;
        _value    = value;
        _setValue = setValue;
        ShowCommand   = new AsyncCommand((_, _) => onForm(Show));
        HideCommand   = new AsyncCommand((_, _) => onForm(Hide));
        ChangeCommand = new AsyncCommand((_, _) => onForm(Change));
        KeepCommand   = new AsyncCommand((_, _) => onForm(KeepSaved));
    }

    /// <summary>What stands for a saved key: a fixed width, never its length.</summary>
    [DataMember] public string Mask { get; } = "••••••••••••";

    [DataMember] public bool IsMasked     { get => _isMasked;     private set => SetProperty(ref _isMasked,     value); }
    [DataMember] public bool IsShown      { get => _isShown;      private set => SetProperty(ref _isShown,      value); }
    /// <summary>The editable box: no key saved, or a new one typed in place of the saved one.</summary>
    [DataMember] public bool IsEditing    { get => _isEditing;    private set => SetProperty(ref _isEditing,    value); }
    [DataMember] public bool CanChange    { get => _canChange;    private set => SetProperty(ref _canChange,    value); }
    /// <summary>A new key is being typed over a saved one, which can be taken back.</summary>
    [DataMember] public bool CanKeepSaved { get => _canKeepSaved; private set => SetProperty(ref _canKeepSaved, value); }
    [DataMember] public string Hint       { get => _hint;         private set => SetProperty(ref _hint,         value); }

    [DataMember] public string ShowLabel   { get => _showLabel;   private set => SetProperty(ref _showLabel,   value); }
    [DataMember] public string HideLabel   { get => _hideLabel;   private set => SetProperty(ref _hideLabel,   value); }
    [DataMember] public string ChangeLabel { get => _changeLabel; private set => SetProperty(ref _changeLabel, value); }
    [DataMember] public string KeepLabel   { get => _keepLabel;   private set => SetProperty(ref _keepLabel,   value); }
    /// <summary>Accessible names: three key fields share the buttons' words, the field's label tells them apart.</summary>
    [DataMember] public string ShowName    { get => _showName;    private set => SetProperty(ref _showName,    value); }
    [DataMember] public string HideName    { get => _hideName;    private set => SetProperty(ref _hideName,    value); }
    [DataMember] public string ChangeName  { get => _changeName;  private set => SetProperty(ref _changeName,  value); }
    [DataMember] public string KeepName    { get => _keepName;    private set => SetProperty(ref _keepName,    value); }

    [DataMember] public AsyncCommand ShowCommand   { get; }
    [DataMember] public AsyncCommand HideCommand   { get; }
    [DataMember] public AsyncCommand ChangeCommand { get; }
    [DataMember] public AsyncCommand KeepCommand   { get; }

    /// <summary>The words, in the interface language; <paramref name="defaultHint"/> is said while no key is saved.</summary>
    public void ApplyLabels(string fieldLabel, string defaultHint)
    {
        _defaultHint = defaultHint;
        ShowLabel    = Strings.KeyShow;
        HideLabel    = Strings.KeyHide;
        ChangeLabel  = Strings.KeyChange;
        KeepLabel    = Strings.KeyKeepSaved;
        ShowName     = $"{fieldLabel} — {Strings.KeyShow}";
        HideName     = $"{fieldLabel} — {Strings.KeyHide}";
        ChangeName   = $"{fieldLabel} — {Strings.KeyChange}";
        KeepName     = $"{fieldLabel} — {Strings.KeyKeepSaved}";
        Set(IsMasked, IsShown);
    }

    /// <summary>After the form opens, is cancelled or saved: masked when the box holds the saved key. A key typed while a
    /// save ran is not that one, and stays in its box: dots over it would hide an unsaved key.</summary>
    public void Reset() => Set(masked: HasSaved && (_value() ?? string.Empty).Trim() == (_saved() ?? string.Empty).Trim(), shown: false);

    /// <summary>The field is hidden (its role no longer runs on a server of its own): a key being changed out of sight
    /// goes back to the saved one — never erased by a Save that cannot show it — and a key shown is masked again.</summary>
    public void Hidden()
    {
        if (CanKeepSaved) KeepSaved();
        else MaskShown();
    }

    /// <summary>A key shown is masked again: the window is closing.</summary>
    public void MaskShown()
    {
        if (IsShown) Set(masked: true, shown: false);
    }

    internal void Show()  => Set(masked: false, shown: true);
    internal void Hide()  => Set(masked: true,  shown: false);

    /// <summary>The saved key leaves the box, never shown on the way: the new one is typed in an empty box.</summary>
    internal void Change()
    {
        _setValue(string.Empty);
        Set(masked: false, shown: false);
    }

    internal void KeepSaved()
    {
        _setValue(_saved() ?? string.Empty);
        Reset();
    }

    private bool HasSaved => !string.IsNullOrEmpty(_saved());

    private void Set(bool masked, bool shown)
    {
        IsMasked     = masked;
        IsShown      = shown;
        IsEditing    = !masked && !shown;
        CanChange    = masked || shown;
        CanKeepSaved = IsEditing && HasSaved;
        Hint         = masked ? Strings.HintKeySaved
                     : shown ? Strings.HintKeyShown
                     : CanKeepSaved ? Strings.HintKeyReplacing
                     : _defaultHint;
    }
}
