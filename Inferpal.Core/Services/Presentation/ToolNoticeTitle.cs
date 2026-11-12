using Inferpal.Localization;

namespace Inferpal.Services.Presentation;

/// <summary>
/// The title a person reads for a notice the chat draws as a tool call. Two are written by the chat itself — the context
/// window's compaction and the session recap — and their internal names mean nothing to anyone; a tool keeps its
/// documented name.
/// </summary>
/// <remarks>⚠ The NAME is what a conversation saves, shared between the editors; the title is computed where it is shown —
/// set when a bubble is made, a reloaded conversation would show none.</remarks>
internal static class ToolNoticeTitle
{
    /// <summary>The notice of a compaction, or a truncation, of the conversation.</summary>
    public const string ContextCompact = "context_compact";

    /// <summary>The session recap written every few questions.</summary>
    public const string OodaRecap = "ooda_recap";

    /// <summary>Whether <paramref name="name"/> is one of the chat's own two notices: no call of the model, so no step of a
    /// run, no tool call counted, no failed step.</summary>
    public static bool IsChatNotice(string? name) => name is ContextCompact or OodaRecap;

    /// <summary>The title of the notice named <paramref name="name"/>: translated for the chat's own two, the name for a
    /// tool.</summary>
    public static string For(string? name) => name switch
    {
        ContextCompact => Strings.ToolTitleContextCompact,
        OodaRecap      => Strings.ToolTitleOodaRecap,
        _              => name ?? string.Empty,
    };
}
