namespace Inferpal.Services;

/// <summary>
/// Recall history for the prompt box: the submitted-prompt list plus the up/down navigation
/// state machine (current index + the in-progress draft stashed on the first ↑ step).
/// </summary>
/// <remarks>
/// Extracted from the tool-window view-model so the off-by-one-prone navigation arithmetic is
/// unit-testable in isolation. Pure C# — persistence (JSON file I/O) and the WPF <c>Prompt</c>
/// binding stay in the VM, which feeds the current text in and writes the returned text back.
/// </remarks>
internal sealed class PromptHistoryNavigator
{
    private readonly List<string> _entries = new();
    private readonly int          _max;

    private int    _index = -1;             // -1 = not navigating
    private string _draft = string.Empty;   // in-progress text, restored when stepping past the newest entry

    public PromptHistoryNavigator(int max = 50) => _max = max;

    /// <summary>Most-recent-last list, for the <c>/phistory</c> listing and persistence.</summary>
    public IReadOnlyList<string> Entries => _entries;

    /// <summary>↑ is available whenever there is at least one entry.</summary>
    public bool CanUp => _entries.Count > 0;

    /// <summary>↓ is available only while navigating (index has stepped off the live draft).</summary>
    public bool CanDown => _index >= 0;

    /// <summary>True once ↑ has stepped into the history (used by the VM to gate the edit-resets-nav guard).</summary>
    public bool IsNavigating => _index >= 0;

    /// <summary>Replaces the list from persisted entries (whitespace-only skipped, capped to max, newest kept).</summary>
    public void Load(IEnumerable<string> entries)
    {
        _entries.Clear();
        foreach (var e in entries.TakeLast(_max))
            if (!string.IsNullOrWhiteSpace(e))
                _entries.Add(e);
        ResetNavigation();
    }

    /// <summary>
    /// Records a submitted prompt (dedups the top entry, evicts oldest past max) and resets
    /// navigation. Returns <c>true</c> when the list changed and the caller should persist.
    /// </summary>
    public bool Append(string prompt)
    {
        var changed = ChatTurnPolicy.AppendPromptHistory(_entries, prompt, _max);
        ResetNavigation();
        return changed;
    }

    /// <summary>Drops navigation state. Call when the user edits the prompt or sends a turn.</summary>
    public void ResetNavigation()
    {
        _index = -1;
        _draft = string.Empty;
    }

    /// <summary>
    /// Steps to an older entry and returns the text to display. On the first step the live
    /// <paramref name="currentText"/> is stashed so ↓ can restore it. Clamps at the oldest entry.
    /// </summary>
    public string Up(string currentText)
    {
        if (_entries.Count == 0) return currentText;
        if (_index == -1) _draft = currentText;
        _index = Math.Min(_index + 1, _entries.Count - 1);
        return _entries[_entries.Count - 1 - _index];
    }

    /// <summary>
    /// Steps to a newer entry and returns the text to display; stepping past the newest entry
    /// restores the stashed draft. A no-op (returns <paramref name="currentText"/>) when not navigating.
    /// </summary>
    public string Down(string currentText)
    {
        if (_index < 0) return currentText;
        _index--;
        var text = _index >= 0 ? _entries[_entries.Count - 1 - _index] : _draft;
        if (_index < 0) _draft = string.Empty;
        return text;
    }
}
