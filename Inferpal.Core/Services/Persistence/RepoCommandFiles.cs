using System.IO;
using System.Text.RegularExpressions;
using Inferpal.Services.Prompting;
using Inferpal.Services.Tools;

namespace Inferpal.Services.Persistence;

/// <summary>One folder of commands a coding agent reads from a repository.</summary>
/// <param name="Location">Relative to the repository's root, with <c>/</c>.</param>
/// <param name="Extensions">The file endings it holds (<c>.prompt.md</c>); the rest of the folder is not commands.</param>
/// <param name="Recursive">Whether subfolders hold commands too (Claude Code: <c>frontend/component.md</c>).</param>
internal sealed record RepoCommandFormat(RepoInstructionFamily Family, string Location, IReadOnlyList<string> Extensions,
                                         bool Recursive = false);

/// <summary>What the repository's command folders hold, and what was found and not read.</summary>
/// <param name="LinksLeaving">Files (relative, with <c>/</c>) whose link leads out of the repository.</param>
/// <param name="Unreadable">Files (relative, with <c>/</c>) that could not be read.</param>
internal sealed record RepoCommandScan(IReadOnlyList<UserSlashTemplate> Commands, IReadOnlyList<string> LinksLeaving,
                                       IReadOnlyList<string> Unreadable)
{
    public static readonly RepoCommandScan None = new([], [], []);
}

/// <summary>
/// The commands a repository already wrote for Copilot (<c>.github/prompts/*.prompt.md</c>), Claude Code
/// (<c>.claude/commands/**/*.md</c>) and Continue (<c>.continue/prompts/*.prompt</c>, and <c>*.md</c> marked
/// <c>invokable</c>), read as slash commands: named as their tool names them, their variables mapped to <c>{args}</c>.
/// </summary>
/// <remarks>
/// <para>Read at the repository's root, like the instructions (<see cref="RepoInstructionDiscovery.SearchRootFor"/>):
/// Visual Studio's workspace is the solution's folder, usually <c>src/</c>, and <c>.github/</c> is not there.</para>
/// <para>⚠ A variable Inferpal does not fill (<c>${selection}</c>, <c>$0</c>, <c>{{prompt}}</c>) stays as written and is
/// named (<see cref="UserSlashTemplate.Unfilled"/>), and a Claude Code <c>!`command`</c> is never run before sending: it
/// becomes a request to the model, which runs it through <c>run_command</c> — under approval like any other command.</para>
/// </remarks>
internal static class RepoCommandFiles
{
    internal static readonly IReadOnlyList<RepoCommandFormat> Formats =
    [
        new(RepoInstructionFamily.Copilot,  ".github/prompts",   [".prompt.md"]),
        new(RepoInstructionFamily.Claude,   ".claude/commands",  [".md"], Recursive: true),
        new(RepoInstructionFamily.Continue, ".continue/prompts", [".prompt", ".md"]),
    ];

    /// <summary>Past this size a file is no command (a log, a dump) — the instruction reader's bound.</summary>
    private const long MaxFileBytes = RepoInstructionReader.MaxFileBytes;

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly Regex CopilotInput    = new(@"\$\{input:[^}]*\}", RegexOptions.None, MatchTimeout);
    private static readonly Regex CopilotVariable = new(@"\$\{[A-Za-z][^}]*\}", RegexOptions.None, MatchTimeout);
    private static readonly Regex ClaudeArguments = new(@"\$ARGUMENTS(?!\[)", RegexOptions.None, MatchTimeout);
    private static readonly Regex ClaudeUnfilled  = new(@"\$ARGUMENTS\[\d+\]|\$\d+\b|\$\{CLAUDE_[A-Z_]+\}", RegexOptions.None, MatchTimeout);
    private static readonly Regex ClaudeShell     = new(@"!`([^`\r\n]+)`", RegexOptions.None, MatchTimeout);
    private static readonly Regex ContinueInput   = new(@"\{\{\{\s*input\s*\}\}\}", RegexOptions.None, MatchTimeout);
    private static readonly Regex ContinueOther   = new(@"\{\{\{?[^{}]*\}?\}\}", RegexOptions.None, MatchTimeout);
    private static readonly Regex HeaderLine      = new(@"^\s*[A-Za-z_][\w-]*\s*:", RegexOptions.None, MatchTimeout);

    // The scan runs on every keystroke of the slash autocomplete: kept a few seconds, like the prompt files.
    private const long CacheTtlMs = 3000;
    private static readonly object _gate = new();
    private static string? _cachedKey;
    private static long _cachedAt;
    private static RepoCommandScan _cached = RepoCommandScan.None;

    /// <summary>The commands of the repository holding <paramref name="workspace"/>, for the families turned on.</summary>
    public static RepoCommandScan Load(string? workspace, IReadOnlySet<RepoInstructionFamily> families)
    {
        if (string.IsNullOrWhiteSpace(workspace)) return RepoCommandScan.None;
        var key = workspace + "|" + string.Join(",", families.Order());
        lock (_gate)
        {
            var now = Environment.TickCount64;
            if (key == _cachedKey && now - _cachedAt < CacheTtlMs) return _cached;
            _cached    = Read(workspace, families);
            _cachedKey = key;
            _cachedAt  = now;
            return _cached;
        }
    }

    /// <summary>Drops the cache (with <see cref="PromptFilesService.InvalidateCache"/>).</summary>
    internal static void InvalidateCache()
    {
        lock (_gate) _cachedKey = null;
    }

    /// <summary>Same as <see cref="Load"/>, uncached.</summary>
    internal static RepoCommandScan Read(string workspace, IReadOnlySet<RepoInstructionFamily> families)
    {
        string root;
        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));
            if (!Directory.Exists(full)) return RepoCommandScan.None;
            root = RepoInstructionDiscovery.SearchRootFor(full, GitProcess.WorkTreeOf(full),
                                                          Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return RepoCommandScan.None; }

        var commands = new List<UserSlashTemplate>();
        var leaving = new List<string>();
        var unreadable = new List<string>();
        foreach (var format in Formats.Where(f => families.Contains(f.Family)))
        {
            var folder = Path.Combine(root, format.Location.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(folder)) continue;
            foreach (var file in Files(format, folder))
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                try { PathSanitizer.AssertUnderRoot(file, root); }
                catch (ArgumentException)
                {
                    leaving.Add(relative);
                    Diagnostics.RecordOnce("RepoCommands", $"Command file not read: its link leads out of the repository ({relative})",
                                           "leaves|" + relative);
                    continue;
                }

                string text;
                try
                {
                    if (new FileInfo(file).Length > MaxFileBytes) { unreadable.Add(relative); continue; }
                    text = TextFileEncoding.ReadText(file);
                    if (LinkWrittenAsText(file, text) is { } target)
                    {
                        try { PathSanitizer.AssertUnderRoot(target, root); }
                        catch (ArgumentException) { leaving.Add(relative); continue; }
                        if (new FileInfo(target).Length > MaxFileBytes) { unreadable.Add(relative); continue; }
                        text = TextFileEncoding.ReadText(target);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    unreadable.Add(relative);
                    Diagnostics.Swallow("RepoCommandFiles.Read", ex);
                    continue;
                }

                if (Parse(format, folder, file, relative, text) is { } command) commands.Add(command);
            }
        }
        return new RepoCommandScan(commands, leaving, unreadable);
    }

    /// <summary>The command a file makes in its tool; <c>null</c> when it makes none (empty, or a Continue note that is
    /// not <c>invokable</c>).</summary>
    internal static UserSlashTemplate? Parse(RepoCommandFormat format, string folder, string file, string relative, string text)
    {
        var (frontMatter, body) = format.Family == RepoInstructionFamily.Continue
            ? ContinueHeader(text)
            : Governance.RulesService.ParseFrontMatter(text);
        string? Field(string key) =>
            frontMatter.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim().Trim('\'', '"') : null;

        if (format.Family == RepoInstructionFamily.Continue && file.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(Field("invokable"), "true", StringComparison.OrdinalIgnoreCase))
            return null;

        body = body.Trim();
        if (body.Length == 0) return null;

        var unfilled = new List<string>();
        var notRun = new List<string>();
        string name;
        switch (format.Family)
        {
            case RepoInstructionFamily.Copilot:
                name = OneWord(Field("name")) ?? StripEnding(Path.GetFileName(file), format);
                body = CopilotInput.Replace(body, "{args}");
                unfilled.AddRange(CopilotVariable.Matches(body).Select(m => m.Value));
                break;
            case RepoInstructionFamily.Claude:
                // Claude Code names a command by its path under the folder, subfolders joined by ':'.
                name = string.Join(":", StripEnding(Path.GetRelativePath(folder, file), format)
                                            .Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries));
                unfilled.AddRange(ClaudeUnfilled.Matches(body).Select(m => m.Value));
                body = ClaudeArguments.Replace(body, "{args}");
                notRun.AddRange(ClaudeShell.Matches(body).Select(m => m.Groups[1].Value));
                body = ClaudeShell.Replace(body, m => ModelPrompts.RepoCommandNotRun(m.Groups[1].Value));
                break;
            default:
                name = OneWord(Field("name")) ?? StripEnding(Path.GetFileName(file), format);
                body = ContinueInput.Replace(body, "{args}");
                unfilled.AddRange(ContinueOther.Matches(body).Select(m => m.Value));
                break;
        }

        return new UserSlashTemplate("/" + CommandName(name), body, Field("description"),
                                     RepoInstructionFormats.ProductName(format.Family), relative,
                                     unfilled.Distinct(StringComparer.Ordinal).ToList(),
                                     notRun.Distinct(StringComparer.Ordinal).ToList());
    }

    /// <summary>
    /// The file a git symbolic link points to, when the link was checked out as a text file holding its target —
    /// what git does without <c>core.symlinks</c>, the default of Git for Windows; <c>null</c> for any other file.
    /// </summary>
    /// <remarks>⚠ Read as is, the command sent the model a path (<c>../../.clinerules/workflows/release.md</c>) instead
    /// of the text its team wrote. A one-line text with no blank that names an existing file is that target.</remarks>
    internal static string? LinkWrittenAsText(string file, string text)
    {
        var target = text.Trim();
        if (target.Length == 0 || target.Length > 260 || target.Any(char.IsWhiteSpace)
            || Path.IsPathRooted(target) || !target.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            var full = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, target));
            return File.Exists(full) ? full : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    /// <summary>A name as typed after <c>/</c>: lower case (the router lower-cases what is typed), blanks made dashes.</summary>
    private static string CommandName(string name) =>
        string.Join("-", name.Trim().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>A front-matter name that can be typed — one word; <c>null</c> otherwise (the file's name is used).</summary>
    private static string? OneWord(string? name) =>
        name is { Length: > 0 } && !name.Any(char.IsWhiteSpace) ? name : null;

    private static string StripEnding(string fileName, RepoCommandFormat format)
    {
        foreach (var ending in format.Extensions)
            if (fileName.EndsWith(ending, StringComparison.OrdinalIgnoreCase)) return fileName[..^ending.Length];
        return fileName;
    }

    /// <summary>The command files of a folder, ordinal order — Claude Code's subfolders included.</summary>
    private static List<string> Files(RepoCommandFormat format, string folder)
    {
        try
        {
            var all = format.Recursive
                ? WorkspaceScan.EnumerateAll(folder, "*")
                : Directory.EnumerateFiles(folder, "*", new EnumerationOptions { IgnoreInaccessible = true });
            return all.Where(f => format.Extensions.Any(e => f.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
                      .OrderBy(f => f, StringComparer.Ordinal)
                      .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Swallow("RepoCommandFiles.Files", ex);
            return [];
        }
    }

    /// <summary>
    /// A Continue prompt's header: a front matter between <c>---</c> lines, or — the <c>.prompt</c> format — header
    /// lines ended by a <c>---</c> line with no opening one.
    /// </summary>
    private static (Dictionary<string, string> FrontMatter, string Body) ContinueHeader(string text)
    {
        if (text.TrimStart().StartsWith("---", StringComparison.Ordinal)) return Governance.RulesService.ParseFrontMatter(text);
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var end = Array.FindIndex(lines, l => l.Trim() == "---");
        if (end <= 0 || !lines[..end].Where(l => l.Trim().Length > 0).All(l => HeaderLine.IsMatch(l)))
            return (new Dictionary<string, string>(), text);
        return Governance.RulesService.ParseFrontMatter("---\n" + string.Join('\n', lines[..end]) + "\n" + string.Join('\n', lines[end..]));
    }
}
