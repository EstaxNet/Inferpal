using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Inferpal.Services.Governance;

namespace Inferpal.Services.Prompting;

/// <summary>When a repository instruction applies.</summary>
internal enum RepoInstructionScope
{
    /// <summary>To every question.</summary>
    Always,

    /// <summary>When the active file matches one of its globs.</summary>
    Files,

    /// <summary>When the agent judges its description relevant — never by itself.</summary>
    OnDemand,

    /// <summary>Only when the user attaches it.</summary>
    Manual,

    /// <summary>Switched off by its own front matter (<c>paths: []</c>).</summary>
    Never,
}

/// <summary>What a reader could not take from an instruction file.</summary>
internal enum RepoInstructionNoteKind
{
    /// <summary>The file could not be read: its instructions are not applied.</summary>
    Unreadable,

    /// <summary>The file is not text.</summary>
    Binary,

    /// <summary>The file is larger than <see cref="RepoInstructionReader.MaxFileBytes"/>.</summary>
    TooLarge,

    /// <summary>An import whose file lies outside the search root: not read.</summary>
    ImportOutsideTheRepository,

    /// <summary>An import past <see cref="RepoInstructionReader.MaxImportHops"/>: not read.</summary>
    ImportTooDeep,

    /// <summary>A front-matter key the format defines and Inferpal does not apply (Continue's <c>regex</c>).</summary>
    KeyNotApplied,

    /// <summary>The front matter scopes the rule to no file at all.</summary>
    NeverApplies,
}

/// <summary>One thing not taken; <paramref name="Subject"/> = the file or the key it is about.</summary>
internal readonly record struct RepoInstructionNote(RepoInstructionNoteKind Kind, string Subject);

/// <summary>A file a CLAUDE.md imports, read.</summary>
internal sealed record RepoInstructionImport(string Path, string Text);

/// <summary>
/// One repository instruction file, read: its text without its front matter, when it applies, what it imports, and
/// what could not be taken from it.
/// </summary>
internal sealed record RepoInstruction(
    RepoInstructionSource Source,
    string Body,
    RepoInstructionScope Scope,
    IReadOnlyList<string> Globs,
    string? Description,
    IReadOnlyList<RepoInstructionImport> Imports,
    IReadOnlyList<RepoInstructionNote> Notes)
{
    /// <summary>
    /// Whether it applies to a question asked with <paramref name="activeRelativePath"/> open — relative to the search
    /// root, <c>/</c> or <c>\</c>; <c>null</c> = no active file. On-demand and manual instructions never apply by
    /// themselves, and an instruction with nothing read never does.
    /// </summary>
    public bool AppliesTo(string? activeRelativePath) =>
        Body.Length > 0 && Scope switch
        {
            RepoInstructionScope.Always => true,
            RepoInstructionScope.Files  => !string.IsNullOrEmpty(activeRelativePath)
                                           && Globs.Any(g => RulesService.GlobMatch(g, activeRelativePath)),
            _                           => false,
        };
}

/// <summary>
/// Reads the repository instruction files the discovery found, each as the tool that owns its format reads it.
/// </summary>
/// <remarks>
/// <para>
/// One front-matter reader and one glob dialect for the whole product (<see cref="RulesService"/>): <c>applyTo</c>,
/// <c>paths</c> and <c>globs</c> are lists written the three ways YAML writes one.
/// </para>
/// <para>
/// ⚠ The scope rules differ by tool and are followed, not merged: a Claude rule without <c>paths</c> applies always, a
/// Cursor rule without <c>globs</c> only on demand; Continue applies a rule without globs always unless
/// <c>alwaysApply: false</c>; Cline reads <c>paths: []</c> as "never". Applying one tool's rule by another's would
/// send instructions their authors scoped away — or keep back the ones they meant for every question.
/// </para>
/// </remarks>
internal static class RepoInstructionReader
{
    /// <summary>The largest file read as an instruction; a larger one is not, and is said.</summary>
    internal const int MaxFileBytes = 1024 * 1024;

    /// <summary>How deep CLAUDE.md imports go — Claude Code's own limit.</summary>
    internal const int MaxImportHops = 4;

    private static readonly TimeSpan RegexBudget = TimeSpan.FromSeconds(1);

    // `@path`, after a line start or a blank — never inside a word (an e-mail) nor after a quote. `\ ` escapes a space.
    private static readonly Regex ImportToken = new(@"(?<=^|\s)@((?:[^\s\\]|\\ )+)", RegexOptions.Multiline, RegexBudget);
    private static readonly Regex InlineCode = new(@"(`+)[^\n]*?\1", RegexOptions.None, RegexBudget);
    private static readonly Regex HtmlComment = new(@"<!--.*?-->", RegexOptions.Singleline, RegexBudget);

    /// <summary>Every source of <paramref name="discovery"/>, read, in its order.</summary>
    public static IReadOnlyList<RepoInstruction> ReadAll(RepoInstructionDiscovery discovery) =>
        discovery.SearchRoot is { } root ? discovery.Sources.Select(s => Read(s, root)).ToList() : [];

    /// <summary>Reads <paramref name="source"/>, whose repository starts at <paramref name="searchRoot"/>.</summary>
    public static RepoInstruction Read(RepoInstructionSource source, string searchRoot)
    {
        var notes = new List<RepoInstructionNote>();
        var text  = ReadFile(source.Path, notes);
        if (text is null) return new RepoInstruction(source, string.Empty, RepoInstructionScope.Always, [], null, [], notes);

        var format = source.Format;
        var (scope, globs, description, body) = format.ScopeKey is null
            ? (RepoInstructionScope.Always, (IReadOnlyList<string>)[], (string?)null, text)
            : Scoped(format, text, notes);

        var imports = new List<RepoInstructionImport>();
        if (format.Family == RepoInstructionFamily.Claude && format.Role == RepoInstructionRole.Context)
        {
            body = WithoutHtmlComments(body);
            var seen = new HashSet<string>(PathComparer.Default) { Path.GetFullPath(source.Path) };
            Import(body, source.Path, searchRoot, hop: 1, seen, imports, notes);
        }

        return new RepoInstruction(source, body.Trim(), scope, globs, description, imports, notes);
    }

    /// <summary>The scope the format's own front matter gives, and the text after it.</summary>
    private static (RepoInstructionScope, IReadOnlyList<string>, string?, string) Scoped(
        RepoInstructionFormat format, string text, List<RepoInstructionNote> notes)
    {
        var (fm, body) = RulesService.ParseFrontMatter(text);
        var description = fm.TryGetValue("description", out var d) && !string.IsNullOrWhiteSpace(d) ? d.Trim() : null;
        var hasKey = fm.TryGetValue(format.ScopeKey!, out var raw);
        var globs  = hasKey ? RulesService.SplitGlobs(raw) : [];
        bool? always = fm.TryGetValue("alwaysApply", out var a) ? a.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) : null;

        RepoInstructionScope scope;
        switch (format.Family)
        {
            case RepoInstructionFamily.Cursor:
                scope = always == true ? RepoInstructionScope.Always
                      : globs.Count > 0 ? RepoInstructionScope.Files
                      : description is not null ? RepoInstructionScope.OnDemand
                      : RepoInstructionScope.Manual;
                break;

            case RepoInstructionFamily.Continue:
                if (fm.ContainsKey("regex")) notes.Add(new RepoInstructionNote(RepoInstructionNoteKind.KeyNotApplied, "regex"));
                scope = always == true ? RepoInstructionScope.Always
                      : globs.Count > 0 ? RepoInstructionScope.Files
                      : always == false ? (description is not null ? RepoInstructionScope.OnDemand : RepoInstructionScope.Manual)
                      : RepoInstructionScope.Always;
                break;

            case RepoInstructionFamily.Copilot:   // applyTo
                scope = globs.Count > 0 ? RepoInstructionScope.Files
                      : description is not null ? RepoInstructionScope.OnDemand
                      : RepoInstructionScope.Manual;
                break;

            default:                              // paths: Claude Code's rules, Cline's
                scope = !hasKey ? RepoInstructionScope.Always
                      : globs.Count > 0 ? RepoInstructionScope.Files
                      : RepoInstructionScope.Never;
                if (scope == RepoInstructionScope.Never)
                    notes.Add(new RepoInstructionNote(RepoInstructionNoteKind.NeverApplies, format.ScopeKey!));
                break;
        }

        // `**` scopes to every file: that is always, an active file or not.
        if (scope == RepoInstructionScope.Files && globs.All(g => g is "**" or "**/*")) scope = RepoInstructionScope.Always;
        return (scope, globs, description, body);
    }

    /// <summary>The file's text, or <c>null</c> with the reason in <paramref name="notes"/>.</summary>
    private static string? ReadFile(string path, List<RepoInstructionNote> notes)
    {
        try
        {
            if (new FileInfo(path).Length > MaxFileBytes)
            {
                notes.Add(new RepoInstructionNote(RepoInstructionNoteKind.TooLarge, path));
                return null;
            }
            var bytes = File.ReadAllBytes(path);
            if (Tools.TextFileEncoding.IsBinary(bytes))
            {
                notes.Add(new RepoInstructionNote(RepoInstructionNoteKind.Binary, path));
                return null;
            }
            return Tools.TextFileEncoding.Decode(bytes).Replace("\r\n", "\n").Replace('\r', '\n');
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            notes.Add(new RepoInstructionNote(RepoInstructionNoteKind.Unreadable, path));
            return null;
        }
    }

    /// <summary>
    /// Reads the files <paramref name="text"/> imports (<c>@path</c>), depth first, each once — Claude Code's rules:
    /// relative to the importing file, never in code, at most <see cref="MaxImportHops"/> deep, and never outside the
    /// repository (a repository-authored import of <c>~/.ssh/…</c> would put that file in front of the model).
    /// </summary>
    private static void Import(string text, string importer, string root, int hop, HashSet<string> seen,
                               List<RepoInstructionImport> imports, List<RepoInstructionNote> notes)
    {
        foreach (var target in ImportTargets(text, Path.GetDirectoryName(importer)!))
        {
            if (!seen.Add(target)) continue;
            try { Tools.PathSanitizer.AssertUnderRoot(target, root); }
            catch (ArgumentException)
            {
                notes.Add(new RepoInstructionNote(RepoInstructionNoteKind.ImportOutsideTheRepository, target));
                continue;
            }
            if (hop > MaxImportHops)
            {
                notes.Add(new RepoInstructionNote(RepoInstructionNoteKind.ImportTooDeep, target));
                continue;
            }

            var imported = ReadFile(target, notes);
            if (imported is null) continue;
            imported = WithoutHtmlComments(imported);
            imports.Add(new RepoInstructionImport(target, imported.Trim()));
            Import(imported, target, root, hop + 1, seen, imports, notes);
        }
    }

    /// <summary>The existing files <paramref name="text"/> names with <c>@</c>, outside code, in order.</summary>
    private static IEnumerable<string> ImportTargets(string text, string folder)
    {
        List<string> tokens;
        try
        {
            var prose = InlineCode.Replace(OutsideCode(text, keep: false), " ");
            tokens = ImportToken.Matches(prose).Select(m => m.Groups[1].Value.Replace("\\ ", " ")).ToList();
        }
        catch (RegexMatchTimeoutException) { yield break; }

        foreach (var token in tokens)
        {
            // A sentence may end on the path: "see @docs/style.md." — the file is the one without the full stop.
            foreach (var candidate in new[] { token, token.TrimEnd('.', ',', ';', ':', '!', '?', ')') }.Distinct())
            {
                string full;
                try
                {
                    var expanded = candidate.StartsWith("~/", StringComparison.Ordinal)
                        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), candidate[2..])
                        : candidate;
                    full = Path.GetFullPath(Path.IsPathRooted(expanded) ? expanded : Path.Combine(folder, expanded));
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
                if (File.Exists(full)) { yield return full; break; }
            }
        }
    }

    /// <summary><paramref name="text"/> without its HTML comments — those in fenced code are part of the code.</summary>
    private static string WithoutHtmlComments(string text)
    {
        try { return OutsideCode(text, keep: true, transform: prose => HtmlComment.Replace(prose, string.Empty)); }
        catch (RegexMatchTimeoutException) { return text; }
    }

    /// <summary>
    /// <paramref name="text"/> with its fenced code blocks kept as they are (<paramref name="keep"/>) or blanked, and
    /// <paramref name="transform"/> applied to the prose between them.
    /// </summary>
    private static string OutsideCode(string text, bool keep, Func<string, string>? transform = null)
    {
        var result = new StringBuilder(text.Length);
        var prose  = new StringBuilder();
        string? fence = null;
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.TrimStart();
            var opens   = trimmed.StartsWith("```", StringComparison.Ordinal) ? "```"
                        : trimmed.StartsWith("~~~", StringComparison.Ordinal) ? "~~~" : null;
            if (fence is null && opens is not null)
            {
                result.Append(transform is null ? prose.ToString() : transform(prose.ToString()));
                prose.Clear();
                fence = opens;
                result.Append(keep ? line : string.Empty).Append('\n');
                continue;
            }
            if (fence is not null)
            {
                if (trimmed.StartsWith(fence, StringComparison.Ordinal)) fence = null;
                result.Append(keep ? line : string.Empty).Append('\n');
                continue;
            }
            prose.Append(line).Append('\n');
        }
        result.Append(transform is null ? prose.ToString() : transform(prose.ToString()));
        return result.ToString().TrimEnd('\n');
    }
}
