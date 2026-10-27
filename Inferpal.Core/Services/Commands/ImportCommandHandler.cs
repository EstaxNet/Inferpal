using System.Globalization;
using System.IO;
using Inferpal.Localization;
using Inferpal.Services.Import;

namespace Inferpal.Services.Commands;

/// <summary>What <c>/import</c> says, and what the caller does with it.</summary>
/// <param name="Card">The preview, drawn as a card by the editors; <c>null</c> for an application or a refusal.</param>
/// <param name="SaveConfig">The settings changed in memory: the caller saves them (the handler never does, so its tests
/// stay away from the real settings).</param>
/// <param name="ServerChanged">The model server changed: it takes effect when Inferpal restarts.</param>
/// <param name="ModesWritten">Modes were written: the editors' mode selectors read them again.</param>
internal sealed record ImportCommandResult(string Message, ImportCard? Card = null, bool SaveConfig = false,
                                           bool ServerChanged = false, bool ModesWritten = false);

/// <summary>
/// <c>/import continue|roo [file] [apply [all|none|1,3,5]]</c> — logic shared by Visual Studio, VS Code and the agent
/// protocol. Without <c>apply</c> it previews and writes NOTHING; with it, it reads the source again and writes the items
/// chosen: those ticked by default, all of them, none, or the numbers given (the card sends the boxes the user left
/// ticked, with <c>#</c> and the fingerprint of what it showed).
/// </summary>
/// <remarks>⚠ Reading again rather than keeping the preview: no state lives between the two commands, so an apply typed
/// in another session, or after the file changed, applies what the file says now.</remarks>
internal static class ImportCommandHandler
{
    public static ImportCommandResult Handle(string[] parts, ImportContext context, string configFile, DateTime now)
    {
        ImportSource? source = parts.Length < 2 ? null : parts[1].ToLowerInvariant() switch
        {
            "continue" => ImportSource.Continue,
            "roo" or "roocode" or "roo-code" => ImportSource.Roo,
            _ => null,
        };
        if (source is not { } from) return new(Strings.ImportUsage);

        var applyAt = Array.FindIndex(parts, 2, p => p.Equals("apply", StringComparison.OrdinalIgnoreCase));
        var typed = string.Join(' ', parts[2..(applyAt < 0 ? parts.Length : applyAt)]).Trim().Trim('"', '\'');
        string file;
        if (typed.Length == 0)
        {
            var candidates = from == ImportSource.Continue ? ContinueImport.DefaultFiles(context.Home) : RooImport.DefaultFiles(context.Home);
            if (candidates.FirstOrDefault(File.Exists) is not { } found)
            {
                var message = Strings.ImportNoFile(ImportPreview.ToolName(from), string.Join(", ", candidates), ImportPreview.Keyword(from));
                return new(from == ImportSource.Roo ? message + "\n\n" + Strings.ImportRooExportHint : message);
            }
            file = found;
        }
        else
        {
            file = typed.StartsWith('~') ? context.Home + typed[1..] : typed;
            // ⚠ Against the workspace, never the process's folder: Visual Studio's extension host keeps the folder it
            // started in, and a path typed from the solution's root was "not found" right beside the file.
            file = Path.IsPathFullyQualified(file) || context.WorkspaceRoot is not { } root
                ? Path.GetFullPath(file)
                : Path.GetFullPath(file, root);
            if (!File.Exists(file)) return new(Strings.ImportFileMissing(file));
        }

        ImportPlan plan;
        try
        {
            plan = from == ImportSource.Continue ? ContinueImport.Read(file, context) : RooImport.Read(file, context);
        }
        catch (ImportReadException ex)
        {
            return new(Strings.ImportReadFailed(file, ex.Message));
        }

        if (applyAt < 0)
        {
            var card = ImportPreview.Card(plan);
            return new(ImportPreview.Markdown(card), card);
        }

        // A card's Apply carries the fingerprint of what it showed ("#…"): against another plan its numbers name other
        // items, so nothing is written. A selection typed without one reads the plan as it is now.
        var tokens = parts[(applyAt + 1)..];
        var shown = tokens.FirstOrDefault(t => t.StartsWith('#'));
        if (shown is not null && !string.Equals(shown[1..], ImportPreview.Fingerprint(plan), StringComparison.OrdinalIgnoreCase))
            return new(Strings.ImportCardStale(ImportPreview.Keyword(from)));
        var selection = string.Join(' ', tokens.Where(t => !t.StartsWith('#'))).Trim();
        if (Chosen(plan, selection, out var unknown) is not { } ids) return new(Strings.ImportSelectionUnknown(unknown!));
        var outcome = ImportApplier.Apply(plan, ids, context.Current, configFile, context.CommandsFolder, context.Modes, now);
        return new(ImportPreview.Outcome(outcome, context.CommandsFolder, context.Modes), null, outcome.ConfigChanged,
                   outcome.ServerChanged, outcome.Applied.Any(i => i.Kind == ImportItemKind.Mode));
    }

    /// <summary>The ids chosen by what follows <c>apply</c>; <c>null</c> with the number no item has.</summary>
    private static HashSet<string>? Chosen(ImportPlan plan, string selection, out string? unknown)
    {
        unknown = null;
        switch (selection.ToLowerInvariant())
        {
            case "":     return [.. plan.Items.Where(i => i.Checked).Select(i => i.Id)];
            case "all":  return [.. plan.Items.Select(i => i.Id)];
            case "none": return [];
        }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in selection.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n < 1 || n > plan.Items.Count)
            {
                unknown = token;
                return null;
            }
            ids.Add(plan.Items[n - 1].Id);
        }
        return ids;
    }
}
