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
}
