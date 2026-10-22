using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Services.Inference;

namespace Inferpal.Services.Presentation;

/// <summary>A settings window's fields for the roles' own servers (<see cref="RoleServers"/>), as Save reads them.</summary>
/// <param name="CompletionPlace">Index of the selected entry in the completion's place list; -1 when none is.</param>
/// <param name="CompletionPlaceName">The selected entry's text (translated: read by its index first).</param>
/// <param name="CompletionKind">The selected kind of server; <c>null</c> = none selected, not set yet.</param>
internal sealed record RoleServerForm(
    int CompletionPlace, string? CompletionPlaceName, string CompletionUrl, string? CompletionKind, string CompletionKey,
    int SearchPlace, string? SearchPlaceName, string SearchUrl, string? SearchKind, string SearchKey);

/// <summary>How a settings window writes the roles' own servers: each place by its index (the places are translated), each
/// address and key as typed, each kind when one is selected. What the form cannot read keeps what is saved, and is named —
/// like every list of the window (<see cref="SettingsFallback.ResolveSelection"/>).</summary>
internal static class RoleServerSettings
{
    /// <summary>Where completions run, in the interface language.</summary>
    public static (string Code, string Name)[] CompletionPlaces =>
        [.. SettingsSchema.CompletionPlaces.Select(o => (o.Value, o.Display))];

    /// <summary>Where code-search embeddings run, in the interface language.</summary>
    public static (string Code, string Name)[] SearchPlaces =>
        [.. SettingsSchema.SearchPlaces.Select(o => (o.Value, o.Display))];

    private static (string Code, string Name)[] Providers =>
        [.. SettingsSchema.Providers.Select(o => (o.Value, o.Display))];

    /// <summary>Relabels <paramref name="list"/> in place and gives back the entry at the index <paramref name="selected"/>
    /// held — a text of the old language is found by its place in the old list, or in the new one.</summary>
    /// <remarks>⚠ A selection the box lost (the Selector writes null when its item is replaced) falls back to what is
    /// SAVED (<paramref name="saved"/>), never to the first entry: "The server above" would move the role back to the
    /// chat's server at the next Save.</remarks>
    public static string Relabel(IList<string> list, (string Code, string Name)[] options, string? selected,
                                  string? saved)
    {
        var index = selected is null ? -1 : list.IndexOf(selected);
        if (index < 0) index = Array.FindIndex(options, o => o.Name == selected);
        if (index < 0) index = Array.FindIndex(options, o => string.Equals(o.Code, saved?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (list.Count == 0)
            foreach (var (_, name) in options) list.Add(name);
        else
            for (var i = 0; i < options.Length && i < list.Count; i++)
                if (list[i] != options[i].Name) list[i] = options[i].Name;
        return options[index >= 0 && index < options.Length ? index : 0].Name;
    }

    /// <summary>What a saved place means: its option's code whatever its case, the chat's server for a value no option
    /// names (<see cref="RoleServers.Of"/> reads it so) — the unsaved count compares meanings, not spellings.</summary>
    public static string PlaceMeaning((string Code, string Name)[] options, string? saved) =>
        options.FirstOrDefault(o => string.Equals(o.Code, saved?.Trim(), StringComparison.OrdinalIgnoreCase)).Code ?? options[0].Code;

    /// <summary>The code of the place a list shows selected: by its index in the list, never by its translated text;
    /// <paramref name="current"/> when nothing in the list is selected.</summary>
    public static string PlaceCode(IList<string> list, (string Code, string Name)[] options, string? selected,
                                    string current, out bool matched) =>
        SettingsFallback.ResolveSelection(options, selected is null ? -1 : list.IndexOf(selected), selected, current, out matched);

    /// <summary>Writes <paramref name="form"/> into <paramref name="edited"/>; <paramref name="note"/> receives each read
    /// field (its text, whether it was applied, its label).</summary>
    public static void Write(RoleServerForm form, InferpalConfig edited, Action<string?, bool, Func<string>> note)
    {
        edited.InlineCompletionRunsOn = SettingsFallback.ResolveSelection(
            CompletionPlaces, form.CompletionPlace, form.CompletionPlaceName, edited.InlineCompletionRunsOn, out var cOk);
        note(form.CompletionPlaceName, cOk, () => $"{Strings.SettingsPageAutocomplete} — {Strings.LabelRunsOn}");
        edited.RagEmbeddingRunsOn = SettingsFallback.ResolveSelection(
            SearchPlaces, form.SearchPlace, form.SearchPlaceName, edited.RagEmbeddingRunsOn, out var sOk);
        note(form.SearchPlaceName, sOk, () => $"{Strings.SettingsPageSearch} — {Strings.LabelRunsOn}");

        // No kind selected = not set yet: the role speaks to its server as the chat's kind until its Test button says.
        if (form.CompletionKind is not null)
        {
            edited.InlineCompletionProvider = SettingsFallback.ResolveSelection(
                Providers, -1, form.CompletionKind, edited.InlineCompletionProvider, out var ckOk);
            note(form.CompletionKind, ckOk, () => Strings.LabelCompletionProvider);
        }
        if (form.SearchKind is not null)
        {
            edited.RagEmbeddingProvider = SettingsFallback.ResolveSelection(
                Providers, -1, form.SearchKind, edited.RagEmbeddingProvider, out var skOk);
            note(form.SearchKind, skOk, () => Strings.LabelSearchProvider);
        }

        edited.InlineCompletionBaseUrl = form.CompletionUrl;
        edited.InlineCompletionApiKey  = form.CompletionKey;
        edited.RagEmbeddingBaseUrl     = form.SearchUrl;
        edited.RagEmbeddingApiKey      = form.SearchKey;
    }
}
