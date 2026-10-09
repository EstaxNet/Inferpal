using System.Text;
using Inferpal.Localization;

namespace Inferpal.Services.Presentation;

/// <summary>One repository instruction file as the Context page and <c>/instructions</c> show it.</summary>
/// <param name="Family">The tool it is written for — a product name, the same in every language.</param>
/// <param name="File">Its path from the repository's root, with <c>/</c>.</param>
/// <param name="Sent">What the question sends of it, in tokens — the X-Ray's figure, not the file's size; "—" when nothing.</param>
/// <param name="IsSent">Whether the next question carries it.</param>
internal sealed record RepoInstructionRow(string Family, string File, string FullPath, string Scope, string Sent, string State, bool IsSent)
{
    /// <summary>The columns after the file on one line — how the settings pages show a row under its file name.</summary>
    public string Detail => string.Join(" · ", new[] { Family, Scope, Sent, State }.Where(p => p != RepoInstructionsReport.Nothing));
}

/// <summary>
/// The repository's instructions to coding agents, one row per file found: which tool, which file, when it applies,
/// what goes, and why not when it does not. One reader for the three screens (VS's Context page, VS Code's, and
/// <c>/instructions</c> in both chats).
/// </summary>
/// <remarks>
/// ⚠ Built from <see cref="RepoInstructionPlan"/> — the path the prompt itself takes — and from the X-Ray of that prompt:
/// a page that recomputed it from the files would show a cut file whole, or a file switched off as sent.
/// </remarks>
internal static class RepoInstructionsReport
{
    /// <summary>A cell with nothing to say.</summary>
    internal const string Nothing = "—";

    /// <summary>The rows, in the order the prompt carries them (root first, closest folder last); none without a plan.</summary>
    /// <param name="prompt">The X-Ray of the prompt the plan belongs to; <c>null</c> = not built yet, the whole text counted.</param>
    public static IReadOnlyList<RepoInstructionRow> Rows(RepoInstructionPlan? plan, XRayPanelModel? prompt)
    {
        if (plan is null) return [];
        var sent = plan.Composed.Sent.ToDictionary(s => s.Instruction.Source.Path, PathComparer.Default);
        var left = plan.Composed.Left.ToDictionary(l => l.Instruction.Source.Path, PathComparer.Default);
        var read = plan.Read.ToDictionary(r => r.Source.Path, PathComparer.Default);
        var off  = plan.FamilyOff.Select(s => s.Path).ToHashSet(PathComparer.Default);

        var rows = new List<RepoInstructionRow>();
        foreach (var source in plan.Discovery.Sources)
        {
            var family = RepoInstructionFormats.ProductName(source.Format.Family);
            var file   = plan.Relative(source.Path);
            if (off.Contains(source.Path))
            {
                rows.Add(new(family, file, source.Path, Nothing, Nothing, Strings.RepoInstructionStateFamilyOff, IsSent: false));
                continue;
            }

            var instruction = read[source.Path];
            var scope       = Scope(instruction);
            if (sent.TryGetValue(source.Path, out var going))
            {
                var section = prompt?.Sections.FirstOrDefault(s => s.Id == $"{PromptSectionKind.RepoInstructions}|{source.Path}");
                if (section is { Enabled: false })
                {
                    rows.Add(new(family, file, source.Path, scope, Nothing, Strings.RepoInstructionStateSwitchedOff, IsSent: false));
                    continue;
                }
                var whole  = Commands.XRayCommandHandler.EstimateTokens(going.Text);
                var tokens = section?.Tokens ?? whole;
                var cut    = section is not null && section.Content.Contains(" truncated to ", StringComparison.Ordinal);
                rows.Add(new(family, file, source.Path, scope,
                             cut ? Strings.PinnedFileTokensSent(SettingsWidgets.Amount(tokens), SettingsWidgets.Amount(whole))
                                 : Strings.PinnedFileTokens(SettingsWidgets.Amount(tokens)),
                             Strings.RepoInstructionStateSent, IsSent: true));
                continue;
            }

            var why = left[source.Path];
            rows.Add(new(family, file, source.Path, scope, Nothing, why.Reason switch
            {
                RepoInstructionLeftReason.Duplicate   => Strings.RepoInstructionStateDuplicate(plan.Relative(why.DuplicateOf!)),
                RepoInstructionLeftReason.OnDemand    => Strings.RepoInstructionStateOnDemand,
                RepoInstructionLeftReason.Manual      => Strings.RepoInstructionStateManual,
                RepoInstructionLeftReason.Never       => Strings.RepoInstructionStateNever,
                RepoInstructionLeftReason.NotThisFile => Strings.RepoInstructionStateNotThisFile,
                _                                     => Strings.RepoInstructionStateNotRead,
            }, IsSent: false));
        }
        return rows;
    }

    /// <summary>The <c>/instructions</c> answer: the rows as a table, or one sentence when the repository has none.</summary>
    public static string Markdown(IReadOnlyList<RepoInstructionRow> rows)
    {
        if (rows.Count == 0) return Strings.RepoInstructionsNone;
        var md = new StringBuilder()
            .Append("## ").AppendLine(Strings.RepoInstructionsTitle).AppendLine()
            .Append("| ").Append(Strings.RepoInstructionsColFamily).Append(" | ").Append(Strings.RepoInstructionsColFile)
            .Append(" | ").Append(Strings.RepoInstructionsColScope).Append(" | ").Append(Strings.RepoInstructionsColSent)
            .Append(" | ").Append(Strings.RepoInstructionsColState).AppendLine(" |")
            .AppendLine("|---|---|---|---|---|");
        foreach (var r in rows)
            md.Append("| ").Append(Cell(r.Family)).Append(" | `").Append(r.File.Replace("`", "'")).Append("` | ")
              .Append(Cell(r.Scope)).Append(" | ").Append(Cell(r.Sent)).Append(" | ").Append(Cell(r.State)).AppendLine(" |");
        return md.ToString().TrimEnd();
    }

    /// <summary>When the instruction applies, in the words of the page.</summary>
    private static string Scope(RepoInstruction instruction) => instruction.Body.Length == 0 ? Nothing : instruction.Scope switch
    {
        RepoInstructionScope.Always   => Strings.RepoInstructionScopeAlways,
        RepoInstructionScope.Files    => Strings.RepoInstructionScopeFiles(string.Join(", ", instruction.Globs)),
        RepoInstructionScope.OnDemand => Strings.RepoInstructionScopeOnDemand,
        RepoInstructionScope.Manual   => Strings.RepoInstructionScopeManual,
        _                             => Strings.RepoInstructionScopeNever,
    };

    // A cell is one line, and a pipe would end it.
    private static string Cell(string text) => text.Replace("|", "\\|").Replace('\n', ' ');
}
