using Inferpal.Localization;

namespace Inferpal.Services.Presentation;

/// <summary>
/// What the mode selector and the thread show of a mode, in the person's language — the same words in Visual Studio and,
/// through the host, in VS Code.
/// </summary>
internal static class ModePresenter
{
    /// <summary>The selector's second line: the model it answers with, what it may do, its file.</summary>
    /// <param name="answering">The model the mode answers with on this server (<see cref="ChatMode.PickModel"/>);
    /// <c>null</c> when it names none the server has, or none at all — the conversation's then.</param>
    public static string Summary(ChatMode mode, string? answering) =>
        $"{answering ?? Strings.ModePickerChatModel} · {What(mode)} · {mode.Source}";

    /// <summary>No mode found: where one goes, the person's own folder first — this machine's, as the catalog reads it.</summary>
    public static string None => Strings.ModesNone(ChatModes.UserDir);

    /// <summary>The segment's tooltip when no mode holds the conversation, one rule for Visual Studio and the host:
    /// <c>null</c> (not read yet) never says "no mode".</summary>
    public static string SegmentTip(int? found) => found switch
    {
        null  => Strings.ModesSegment,
        0     => None,
        { } n => Strings.ModesSegmentTip(n),
    };

    /// <summary>The line the thread shows where a mode starts: its name, and what it may do.</summary>
    public static string HeldLine(ChatMode mode) =>
        Strings.ModeHeldLine(mode.Name, mode.Tools is { MayWrite: false } ? Strings.ModeReadsOnly : Strings.ModeMayWrite);

    /// <summary>What a mode may do, in a few words: read only, or its tools (and the files its edits are limited to).</summary>
    internal static string What(ChatMode mode)
    {
        if (mode.Tools is not { } tools) return Strings.ModesToolsAll;
        if (!tools.MayWrite) return Strings.ModeReadsOnly;
        var words = Strings.ModesTools(string.Join(", ", tools.Words));
        return tools.EditPatternText is { } pattern ? $"{words} · {Strings.ModesEditsOnly(pattern)}" : words;
    }
}
