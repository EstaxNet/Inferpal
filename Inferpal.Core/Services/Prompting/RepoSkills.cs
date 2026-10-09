using System.IO;
using System.Text.RegularExpressions;
using Inferpal.Services.Tools;

namespace Inferpal.Services.Prompting;

/// <summary>Whose a skill folder is: the repository's, read for everyone who opens it, or the user's own.</summary>
internal enum SkillScope { Repository, User }

/// <summary>One folder of skills: <c>&lt;Folder&gt;/&lt;name&gt;/SKILL.md</c>.</summary>
/// <param name="Folder">Relative to the repository's root, or to the user's home folder, with <c>/</c>.</param>
internal sealed record SkillLocation(RepoInstructionFamily Family, SkillScope Scope, string Folder);

/// <summary>A skill: what <c>/skill</c> lists and <c>read_skill_file</c> reads.</summary>
/// <param name="Name">The name it is invoked by: its front-matter name when it can be typed, else its folder's.</param>
/// <param name="Folder">The skill's folder, full path.</param>
/// <param name="Origin">The tool or standard it was written for, the same in every language.</param>
/// <param name="Shown">Where it lives, as a person reads it: <c>.claude/skills/pdf</c>, <c>~/.agents/skills/notes</c>.</param>
internal sealed record Skill(string Name, string Description, string Folder, string Origin, SkillScope Scope, string Shown)
{
    public string SkillFile => Path.Combine(Folder, RepoSkills.FileName);
}

/// <summary>Why a <c>SKILL.md</c> found is not in the catalog.</summary>
internal enum SkillSkipReason { NoDescription, Unreadable, LinkLeaves, Shadowed }

/// <param name="By">For <see cref="SkillSkipReason.Shadowed"/>, the skill that has the name (its <see cref="Skill.Shown"/>).</param>
internal sealed record SkillSkipped(string Shown, SkillSkipReason Reason, string? By = null);

/// <summary>The skills of a workspace and of its user, and the ones found and not loaded — with why.</summary>
internal sealed record SkillCatalog(IReadOnlyList<Skill> Skills, IReadOnlyList<SkillSkipped> Skipped)
{
    public static readonly SkillCatalog None = new([], []);

    public Skill? Find(string name) =>
        Skills.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The Agent Skills (<c>SKILL.md</c> folders) a repository or its user wrote for Copilot, Claude Code, Cline, Roo or
/// any agent of the standard: found, front matter read, collisions settled — the catalog behind <c>/skill</c> and
/// <c>read_skill_file</c>.
/// </summary>
/// <remarks>
/// <para>The repository's folders are read at its root (<see cref="RepoInstructionDiscovery.SearchRootFor"/>), the
/// user's at the home folder. A repository's skill wins over the user's of the same name (the standard's integration
/// guide); within one scope, the first folder of <see cref="Locations"/> wins — and the loser is named.</para>
/// <para>⚠ Lenient like the guide asks: a name that cannot be typed falls back to the folder's name; only a
/// <c>description</c> missing makes a <c>SKILL.md</c> no skill — it is what tells the model and the user what the skill
/// is for — and that is said.</para>
/// </remarks>
internal static class RepoSkills
{
    internal const string FileName = "SKILL.md";

    /// <summary>The files a skill's index lists, at most: a skill of hundreds of assets would fill the window.</summary>
    internal const int MaxIndexedFiles = 50;

    /// <summary>Past this size a <c>SKILL.md</c> is no skill (the standard asks for under 500 lines).</summary>
    private const long MaxFileBytes = RepoInstructionReader.MaxFileBytes;

    internal static readonly IReadOnlyList<SkillLocation> Locations =
    [
        new(RepoInstructionFamily.Copilot,  SkillScope.Repository, ".github/skills"),
        new(RepoInstructionFamily.Claude,   SkillScope.Repository, ".claude/skills"),
        new(RepoInstructionFamily.Agents,   SkillScope.Repository, ".agents/skills"),
        new(RepoInstructionFamily.Cline,    SkillScope.Repository, ".cline/skills"),
        new(RepoInstructionFamily.Roo,      SkillScope.Repository, ".roo/skills"),
        new(RepoInstructionFamily.Copilot,  SkillScope.User,       ".copilot/skills"),
        new(RepoInstructionFamily.Claude,   SkillScope.User,       ".claude/skills"),
        new(RepoInstructionFamily.Agents,   SkillScope.User,       ".agents/skills"),
    ];

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly Regex TypableName = new("^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.None, MatchTimeout);

    /// <summary>The tool a skill folder belongs to, as a person knows it. <c>.agents/skills</c> is the open standard's.</summary>
    internal static string OriginOf(RepoInstructionFamily family) =>
        family == RepoInstructionFamily.Agents ? "Agent Skills" : RepoInstructionFormats.ProductName(family);

    // Read on every request (read_skill_file's IsOffered): kept a few seconds, like the commands.
    private const long CacheTtlMs = 3000;
    private static readonly object _gate = new();
    private static string? _cachedKey;
    private static long _cachedAt;
    private static SkillCatalog _cached = SkillCatalog.None;

    /// <summary>The catalog of <paramref name="workspace"/> and of the user, for the families turned on.</summary>
    public static SkillCatalog Load(string? workspace, IReadOnlySet<RepoInstructionFamily> families, string? home = null)
    {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var key = workspace + "|" + home + "|" + string.Join(",", families.Order());
        lock (_gate)
        {
            var now = Environment.TickCount64;
            if (key == _cachedKey && now - _cachedAt < CacheTtlMs) return _cached;
            _cached    = Read(workspace, families, home);
            _cachedKey = key;
            _cachedAt  = now;
            return _cached;
        }
    }

    internal static void InvalidateCache()
    {
        lock (_gate) _cachedKey = null;
    }

    /// <summary>Same as <see cref="Load"/>, uncached.</summary>
    internal static SkillCatalog Read(string? workspace, IReadOnlySet<RepoInstructionFamily> families, string? home)
    {
        string? root = null;
        if (!string.IsNullOrWhiteSpace(workspace))
        {
            try
            {
                var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));
                if (Directory.Exists(full))
                    root = RepoInstructionDiscovery.SearchRootFor(full, GitProcess.WorkTreeOf(full), home);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
        }

        var skills = new List<Skill>();
        var skipped = new List<SkillSkipped>();
        foreach (var scope in new[] { SkillScope.Repository, SkillScope.User })
        foreach (var location in Locations.Where(l => l.Scope == scope && families.Contains(l.Family)))
        {
            var bound = scope == SkillScope.Repository ? root : home;
            if (string.IsNullOrEmpty(bound)) continue;
            var folder = Path.Combine(bound, location.Folder.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(folder)) continue;

            foreach (var dir in SubFolders(folder))
            {
                var file = Path.Combine(dir, FileName);
                if (!File.Exists(file)) continue;
                var shown = (scope == SkillScope.User ? "~/" : "") + location.Folder + "/" + Path.GetFileName(dir);
                try { PathSanitizer.AssertUnderRoot(file, folder); }
                catch (ArgumentException) { skipped.Add(new(shown, SkillSkipReason.LinkLeaves)); continue; }

                string text;
                try
                {
                    if (new FileInfo(file).Length > MaxFileBytes) { skipped.Add(new(shown, SkillSkipReason.Unreadable)); continue; }
                    text = TextFileEncoding.ReadText(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Diagnostics.Swallow("RepoSkills.Read", ex);
                    skipped.Add(new(shown, SkillSkipReason.Unreadable));
                    continue;
                }

                var (frontMatter, _) = Governance.RulesService.ParseFrontMatter(text);
                string? Field(string key) =>
                    frontMatter.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim().Trim('\'', '"') : null;
                if (Field("description") is not { } description) { skipped.Add(new(shown, SkillSkipReason.NoDescription)); continue; }

                var name = Field("name") is { } n && TypableName.IsMatch(n) ? n : Path.GetFileName(dir).ToLowerInvariant();
                if (skills.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) is { } winner)
                {
                    skipped.Add(new(shown, SkillSkipReason.Shadowed, winner.Shown));
                    continue;
                }
                skills.Add(new Skill(name, description, dir, OriginOf(location.Family), scope, shown));
            }
        }
        return new SkillCatalog(skills, skipped);
    }

    /// <summary>The body of a skill's <c>SKILL.md</c>, front matter out.</summary>
    internal static string Body(Skill skill) =>
        Governance.RulesService.ParseFrontMatter(TextFileEncoding.ReadText(skill.SkillFile)).Body.Trim();

    /// <summary>The skill's other files, relative to its folder with <c>/</c>, ordinal order — at most
    /// <see cref="MaxIndexedFiles"/>, and how many more there are.</summary>
    internal static (IReadOnlyList<string> Files, int Omitted) FilesOf(Skill skill)
    {
        List<string> all;
        try
        {
            all = WorkspaceScan.EnumerateAll(skill.Folder, "*")
                .Select(f => Path.GetRelativePath(skill.Folder, f).Replace('\\', '/'))
                .Where(f => !f.Equals(FileName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Swallow("RepoSkills.FilesOf", ex);
            return ([], 0);
        }
        return all.Count <= MaxIndexedFiles ? (all, 0) : (all.Take(MaxIndexedFiles).ToList(), all.Count - MaxIndexedFiles);
    }

    /// <summary>
    /// Whether a path, cut on its separators, is a file inside a skill folder of <see cref="Locations"/> — a write there
    /// changes what an agent is told to do or runs (<c>AgentInstructionFiles</c>). One list: the catalog's.
    /// </summary>
    internal static bool IsInASkill(IReadOnlyList<string> segments)
    {
        foreach (var location in Locations)
        {
            var parts = location.Folder.Split('/');
            for (var i = 0; i + parts.Length + 1 < segments.Count; i++)
                if (parts.Select((p, k) => string.Equals(p, segments[i + k], StringComparison.OrdinalIgnoreCase)).All(x => x))
                    return true;
        }
        return false;
    }

    private static IEnumerable<string> SubFolders(string folder)
    {
        try
        {
            return Directory.EnumerateDirectories(folder, "*", new EnumerationOptions { IgnoreInaccessible = true })
                            .OrderBy(d => d, StringComparer.Ordinal).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Swallow("RepoSkills.SubFolders", ex);
            return [];
        }
    }
}
