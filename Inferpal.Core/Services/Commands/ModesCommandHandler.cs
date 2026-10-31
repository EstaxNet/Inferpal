using System.Text;
using Inferpal.Localization;

namespace Inferpal.Services.Commands;

/// <summary>
/// <c>/modes</c>: the modes found for this workspace — each with its description, its file, its model, its tools and what
/// its file sets that Inferpal does not carry — and the files set aside, each with why. The same text in both editors and
/// in an ACP client.
/// </summary>
internal static class ModesCommandHandler
{
    public static string Handle(ChatModeCatalog catalog)
    {
        var sb = new StringBuilder();
        if (catalog.Modes.Count == 0) sb.Append(ModePresenter.None);
        else
        {
            sb.Append(Strings.ModesHeader).Append("\n\n");
            foreach (var mode in catalog.Modes) sb.Append("- ").Append(Line(mode)).Append('\n');
        }
        if (catalog.Notes.Count > 0)
        {
            sb.Append("\n\n").Append(Strings.ModesSetAside).Append('\n');
            foreach (var note in catalog.Notes) sb.Append("- `").Append(note.Source).Append("`: ").Append(Note(note)).Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>One mode, on one line: name, description, file, model, tools, what is not carried.</summary>
    internal static string Line(ChatMode mode)
    {
        var parts = new List<string> { $"**{mode.Name}**" + (mode.Description.Length > 0 ? " — " + mode.Description : string.Empty) };
        parts.Add($"`{mode.Source}`");
        parts.Add(mode.Models.Count switch
        {
            0 => Strings.ModesModelChat,
            1 => Strings.ModesModel(mode.Models[0]),
            _ => Strings.ModesModelFirst(string.Join(", ", mode.Models)),
        });
        if (mode.Tools is not { } tools) parts.Add(Strings.ModesToolsAll);
        else
        {
            var words = tools.Words;
            parts.Add(words.Count == 0 ? Strings.ModesToolsNone : Strings.ModesTools(string.Join(", ", words)));
            if (tools.EditPatternText is { } pattern) parts.Add(Strings.ModesEditsOnly($"`{pattern}`"));
        }
        if (mode.NotCarried.Count > 0) parts.Add(Strings.ModesNotCarried(string.Join(", ", mode.NotCarried)));
        return string.Join(" · ", parts);
    }

    /// <summary>Why a file, or a mode in it, was set aside — in the person's language, a cause's own detail as it came.</summary>
    internal static string Note(ChatModeNote note) => note.Kind switch
    {
        ChatModeNoteKind.Unreadable          => Strings.ModeNoteUnreadable(note.Detail ?? string.Empty),
        ChatModeNoteKind.Duplicate           => Strings.ModeNoteDuplicate(note.Detail ?? string.Empty),
        ChatModeNoteKind.ReservedName        => Strings.ModeNoteReservedName(note.Detail ?? string.Empty),
        ChatModeNoteKind.SubAgentOnly        => Strings.ModeNoteSubAgentOnly,
        ChatModeNoteKind.NoSlug              => Strings.ModeNoteNoSlug,
        ChatModeNoteKind.LeavesTheRepository => Strings.ModeNoteLeaves,
        ChatModeNoteKind.Unlistable          => Strings.ModeNoteUnlistable(note.Detail ?? string.Empty),
        ChatModeNoteKind.NoModes             => Strings.ModeNoteNoModes,
        ChatModeNoteKind.RulesCut            => Strings.ModeNoteRulesCut(note.Detail ?? string.Empty),
        _                                    => note.Kind.ToString(),
    };
}
