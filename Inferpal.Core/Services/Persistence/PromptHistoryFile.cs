namespace Inferpal.Services.Persistence;

/// <summary>
/// <c>prompt_history.json</c>: the prompts typed in the Visual Studio chat window, most recent last.
/// </summary>
/// <remarks>
/// A prompt is user content, so an unreadable file is set aside before the next save overwrites it:
/// the window then starts with an empty history, and its first prompt would otherwise replace them all.
/// </remarks>
internal static class PromptHistoryFile
{
    public static AppDataJsonFile<List<string>> Create() =>
        new("prompt_history.json", "PromptHistory", preserveUnreadable: true);

    /// <summary>Records a sent prompt: the file read again, the prompt appended, the file written.</summary>
    /// <remarks>
    /// ⚠ The file is shared by every Visual Studio window, and each one read it once, when it opened: written from that
    /// copy, every send erased the prompts the other window had added since. Read again just before: what it holds now
    /// is the list. An unreadable file keeps the window's own list (the save sets the file aside first).
    /// </remarks>
    public static void Append(AppDataJsonFile<List<string>> store, PromptHistoryNavigator history, string prompt)
    {
        var (onDisk, unreadable) = store.Read([]);
        if (!unreadable) history.Load(onDisk);
        if (history.Append(prompt)) store.Save([.. history.Entries]);
    }
}
