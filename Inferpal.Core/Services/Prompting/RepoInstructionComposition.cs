using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Inferpal.Services.Prompting;

/// <summary>Why a repository instruction read is not in the prompt of this question.</summary>
internal enum RepoInstructionLeftReason
{
    /// <summary>Its text is one already sent (blanks aside), or it only imports one.</summary>
    Duplicate,

    /// <summary>Loaded when the agent judges its description relevant — no such path yet.</summary>
    OnDemand,

    /// <summary>Attached by the user only.</summary>
    Manual,

    /// <summary>Switched off by its own front matter (<c>paths: []</c>).</summary>
    Never,

    /// <summary>Scoped to files the active one is not — or there is no active file.</summary>
    NotThisFile,

    /// <summary>Nothing could be read from it (binary, too large, unreadable, empty).</summary>
    NothingRead,
}

/// <summary>An instruction left out of this question's prompt, and why; <paramref name="DuplicateOf"/> = the source it
/// repeats.</summary>
internal sealed record RepoInstructionLeft(RepoInstruction Instruction, RepoInstructionLeftReason Reason, string? DuplicateOf = null);

/// <summary>An instruction sent, with the text the prompt carries: its body, then the imports no other source sends.</summary>
internal sealed record RepoInstructionSent(RepoInstruction Instruction, string Text);

/// <summary>
/// The repository's instructions for one question: what was found, read, composed — and the sources of the families the
/// setting turns off, found but not read. Built by <c>SystemPromptBuilder.PlanRepoInstructions</c>, the one path from the
/// files to the prompt, and read by the prompt and by every screen that shows it.
/// </summary>
internal sealed record RepoInstructionPlan(
    RepoInstructionDiscovery Discovery,
    IReadOnlyList<RepoInstruction> Read,
    IReadOnlyList<RepoInstructionSource> FamilyOff,
    RepoInstructionComposition Composed)
{
    /// <summary><paramref name="path"/> relative to the search root, with <c>/</c>.</summary>
    public string Relative(string path) =>
        Path.GetRelativePath(Discovery.SearchRoot!, path).Replace('\\', '/');
}

/// <summary>
/// Which repository instructions a question sends, and the text of each — in the discovery's order, the root first and
/// the closest folder last.
/// </summary>
/// <remarks>
/// ⚠ Sources add up, none replaces another; but the SAME text twice is noise that costs twice in a shared budget:
/// a <c>CLAUDE.md</c> copied from, linked to or importing <c>AGENTS.md</c> is common, and is sent once — the first, in
/// the table's order. What is left out says why: on demand and manual instructions have no way in yet.
/// </remarks>
internal sealed record RepoInstructionComposition(IReadOnlyList<RepoInstructionSent> Sent, IReadOnlyList<RepoInstructionLeft> Left)
{
    // The texts are the repository's: bounded like every pattern matching input nobody here controls.
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);
    private static readonly Regex Blanks = new(@"\s+", RegexOptions.None, MatchTimeout);
    private static readonly Regex OnlyImports = new(@"^(@\S+\s*)+$", RegexOptions.None, MatchTimeout);

    /// <summary>
    /// Composes the instructions <paramref name="read"/> for a question asked with <paramref name="activeRelativePath"/>
    /// open — relative to the search root, <c>null</c> = none.
    /// </summary>
    public static RepoInstructionComposition Compose(IReadOnlyList<RepoInstruction> read, string? activeRelativePath)
    {
        var sent  = new List<RepoInstruction>();
        var left  = new List<RepoInstructionLeft>();
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);   // normalized text → the source that sent it

        foreach (var instruction in read)
        {
            if (instruction.Body.Length == 0) { left.Add(new(instruction, RepoInstructionLeftReason.NothingRead)); continue; }
            if (!instruction.AppliesTo(activeRelativePath))
            {
                left.Add(new(instruction, instruction.Scope switch
                {
                    RepoInstructionScope.OnDemand => RepoInstructionLeftReason.OnDemand,
                    RepoInstructionScope.Manual   => RepoInstructionLeftReason.Manual,
                    RepoInstructionScope.Never    => RepoInstructionLeftReason.Never,
                    _                             => RepoInstructionLeftReason.NotThisFile,
                }));
                continue;
            }
            if (texts.TryGetValue(Normalized(instruction.Body), out var first))
            {
                left.Add(new(instruction, RepoInstructionLeftReason.Duplicate, first));
                continue;
            }
            texts[Normalized(instruction.Body)] = instruction.Source.Path;
            sent.Add(instruction);
        }

        // A file that only imports what another source sends is that source again.
        var paths = sent.Select(s => s.Source.Path).ToHashSet(PathComparer.Default);
        foreach (var instruction in sent.ToList())
        {
            if (!IsOnlyImports(instruction.Body) || instruction.Imports.Count == 0) continue;
            var repeated = instruction.Imports.FirstOrDefault(m => paths.Contains(m.Path)
                                                                 || texts.ContainsKey(Normalized(m.Text)));
            if (repeated is null || instruction.Imports.Any(m => !paths.Contains(m.Path) && !texts.ContainsKey(Normalized(m.Text))))
                continue;
            sent.Remove(instruction);
            paths.Remove(instruction.Source.Path);
            left.Add(new(instruction, RepoInstructionLeftReason.Duplicate,
                         paths.Contains(repeated.Path) ? repeated.Path : texts[Normalized(repeated.Text)]));
        }

        var order = read.Select((r, i) => (r, i)).ToDictionary(x => x.r, x => x.i);
        return new RepoInstructionComposition(
            sent.Select(i => new RepoInstructionSent(i, Render(i, paths, texts))).ToList(),
            left.OrderBy(l => order[l.Instruction]).ToList());
    }

    /// <summary>The body, then each import that no sent source already carries, with where it comes from.</summary>
    private static string Render(RepoInstruction instruction, HashSet<string> sentPaths, Dictionary<string, string> sentTexts)
    {
        var text = new StringBuilder(instruction.Body);
        var root = Path.GetDirectoryName(instruction.Source.Path)!;
        foreach (var import in instruction.Imports)
        {
            if (import.Text.Length == 0 || sentPaths.Contains(import.Path) || sentTexts.ContainsKey(Normalized(import.Text)))
                continue;
            text.Append("\n\n(imported from ")
                .Append(Path.GetRelativePath(root, import.Path).Replace('\\', '/'))
                .Append(")\n\n")
                .Append(import.Text);
        }
        return text.ToString();
    }

    private static string Normalized(string text)
    {
        try { return Blanks.Replace(text, " ").Trim(); }
        catch (RegexMatchTimeoutException) { return text; }
    }

    private static bool IsOnlyImports(string body)
    {
        try { return OnlyImports.IsMatch(body.Trim()); }
        catch (RegexMatchTimeoutException) { return false; }
    }
}
