using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Inferpal.Services.Execution;

/// <summary>
/// The files a write turns into the agent's <b>own future instructions</b>.
/// </summary>
/// <remarks>
/// <para>
/// <c>SystemPromptBuilder</c> injects <c>.inferpal/context.md</c>, <c>memory.md</c>, <c>notes.md</c>
/// and <c>rules/*.md</c> into the system prompt of <b>every later session</b>. Writing one of them
/// is therefore not an ordinary edit: it is the <i>persistence</i> half of a prompt-injection chain,
/// where the content can come from a web page, a file the model was asked to read, or an MCP server.
/// </para>
/// <para>
/// So are the files a repository writes for coding agents (<see cref="RepoInstructionFormats"/>: AGENTS.md, CLAUDE.md,
/// Copilot's, Cursor's, Cline's, Roo's, Continue's): read back by the next question's prompt, and by every other agent
/// the user runs in that repository. Recognised by the table's own reader — one list, the table's.
/// </para>
/// <para>
/// ⚠ <b>Every write path reaches them</b> — <c>update_memory</c>, <c>write_file</c>,
/// <c>apply_diff</c>, <c>apply_edits</c>, <c>delete_file</c>, <c>restore_file</c> and the caret
/// writes behind <c>EditorWriteGate</c> — and each is reachable unattended: an <c>allow</c> rule,
/// <c>SecurityAlertsDisabled</c>, or the realistic one, a single "Always" on <c>write_file</c>
/// clicked once and good for the rest of the session.
/// </para>
/// <para>
/// The answer is a <b>forced prompt, never a refusal</b>: writing memory or a rule is a legitimate,
/// useful thing for the agent to do. What must not happen is that it happens <i>silently</i>. Same
/// shape as <c>TestFileWriteGuard</c>, which already does this for the strictly less sensitive case
/// of a test file — and the reason this lives in <c>ApprovalServiceBase</c> next to
/// <see cref="PermissionPolicy.IsOpaqueExecution"/> rather than in a decorator each call site must
/// remember: a property held at the funnel cannot be forgotten by the eighth write path.
/// </para>
/// <para>
/// ⚠ <b>Bounds, stated rather than implied.</b> This matches a <i>path</i>, so it covers the tools
/// whose approval subject is a path. It does <b>not</b> cover a shell command that writes the same
/// file — <c>run_command</c>'s subject is the raw command line, and the repository is explicit that
/// text matching is an anti-accident guard, not a security boundary.
/// </para>
/// <para>
/// ⚠ <b>And the files that RESTRICT the agent</b>: <c>.inferpal/permissions.json</c> (the repository's deny rules) and
/// the ignore files (<c>.inferpalignore</c>, <c>.aiignore</c>…). Their content can only restrict, but a write REPLACES
/// it: <c>{"rules":[]}</c>, or an emptied ignore file, lifts every restriction the repository wrote, under an
/// "Always" given to <c>write_file</c>.
/// </para>
/// </remarks>
internal static class AgentInstructionFiles
{
    /// <summary>Directory holding everything the product reads back as instructions.</summary>
    private const string Dir = ".inferpal";

    /// <summary>The three files injected whole, by name.</summary>
    private static readonly string[] Injected = ["context.md", "memory.md", "notes.md"];

    /// <summary>Sub-directory whose <c>*.md</c> are injected, glob-scoped to the active file.</summary>
    private const string RulesDir = "rules";


    /// <summary>
    /// Does this approval subject aim at a file that becomes the system prompt? The subject may
    /// carry several paths (<c>apply_edits</c> joins them with newlines) — one is enough.
    /// </summary>
    internal static bool Targets(string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject)) return false;

        // Every run of consecutive parts, the whole line included: `rules/style,v2.md` is ONE file name, which its parts
        // alone never name — and a line may join it to another path with the same separator.
        foreach (var token in subject.Split('\n').SelectMany(Runs))
        {
            // Separators are normalised BEFORE any path reading, for the reason TestFileWriteGuard
            // documents: under POSIX a backslash is an ordinary character, and the subject is
            // written by a model — or by an MCP server — so it may carry either form on either
            // platform.
            var path = token.Trim().Replace('\\', '/');
            if (path.Length == 0) continue;

            var raw = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            // ⚠ Read as written AND as resolved: `.inferpal/./memory.md` and `.inferpal/x/../memory.md` write memory.md,
            // and an MCP tool's subject carries the path raw. Either reading that lands on the system prompt asks.
            if (IsInstructionFile([.. raw.Select(AsWritten)]) || IsInstructionFile(Resolved(raw))) return true;
            // ⚠ A Windows short name (`INFERP~1`) is `.inferpal` too, and only the file system knows it: an MCP tool's
            // subject is raw. Rooted, it is read in its long form.
            if (LongForm(token.Trim()) is { } full && IsInstructionFile(Resolved(full.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))))
                return true;
        }
        return false;
    }

    /// <summary>A rooted Windows path holding a short name, in its long form; <c>null</c> for any other.</summary>
    private static string? LongForm(string path)
    {
        if (!OperatingSystem.IsWindows() || !path.Contains('~') || !Path.IsPathRooted(path)) return null;
        try { return Path.GetFullPath(path); }   // .NET expands the short names of what exists
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException) { return null; }
    }

    /// <summary>Each run of consecutive <c>;</c>/<c>,</c>-separated parts of <paramref name="line"/>, joined as written.</summary>
    private static IEnumerable<string> Runs(string line)
    {
        var pieces = Regex.Split(line, "(?<=[;,])", RegexOptions.None, RegexBudget.Default);   // each keeps its separator
        if (pieces.Length > 64) { yield return line; foreach (var p in pieces) yield return p.TrimEnd(';', ','); yield break; }
        for (var i = 0; i < pieces.Length; i++)
        {
            var run = new StringBuilder();
            for (var j = i; j < pieces.Length; j++) yield return run.Append(pieces[j]).ToString().TrimEnd(';', ',');
        }
    }

    private static bool IsInstructionFile(string[] segments)
    {
        if (segments.Length == 0) return false;

        // The files that restrict the agent: the repository's deny rules, and the ignore files (the names the settings
        // give; anywhere — one prompt more, never one less).
        if (segments.Length >= 2 && segments[^2].Equals(Dir, StringComparison.OrdinalIgnoreCase)
            && segments[^1].Equals("permissions.json", StringComparison.OrdinalIgnoreCase))
            return true;
        if (Governance.ContentExclusions.IsIgnoreFileName(segments[^1])) return true;

        // A file the repository gives agents as instructions — AGENTS.md, CLAUDE.md, Copilot's, Cursor's… The
        // table's own reader, never a second list: a row added there is a write that asks here.
        if (RepoInstructionFormats.AnyEndsAt(segments)) return true;

        // A file of a skill — its SKILL.md, a script it runs, a template it fills: the catalog's own folders.
        if (RepoSkills.IsInASkill(segments)) return true;

        // A mode's file — its instructions become the system prompt when it is picked: the reader's own folders.
        if (ChatModes.IsModeFile(segments)) return true;

        for (int i = 0; i < segments.Length - 1; i++)
        {
            if (!segments[i].Equals(Dir, StringComparison.OrdinalIgnoreCase)) continue;

            // .inferpal/<name>
            var rest = segments[(i + 1)..];
            if (rest.Length == 1 && Injected.Contains(rest[0], StringComparer.OrdinalIgnoreCase))
                return true;

            // .inferpal/rules/**/*.md — a rule file anywhere under the rules directory.
            if (rest.Length >= 2
                && rest[0].Equals(RulesDir, StringComparison.OrdinalIgnoreCase)
                && Path.GetExtension(rest[^1]).Equals(".md", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>The segments with <c>.</c> dropped and <c>..</c> taking its parent off, each as Windows writes it.</summary>
    private static string[] Resolved(IEnumerable<string> raw)
    {
        var kept = new List<string>();
        foreach (var segment in raw)
        {
            if (segment.TrimEnd(' ') == "..") { if (kept.Count > 0) kept.RemoveAt(kept.Count - 1); continue; }
            var written = AsWritten(segment);
            if (written.Length > 0) kept.Add(written);   // ".", "..." name the folder they are in
        }
        return [.. kept];
    }

    /// <summary>The name of the file a path segment writes, as Windows resolves it.</summary>
    /// <remarks>
    /// ⚠ Windows writes <c>memory.md::$DATA</c> (the file's own data stream), <c>memory.md.</c> and <c>memory.md </c>
    /// (trailing dots and spaces dropped) to <c>memory.md</c> itself: compared as written, each wrote the system prompt
    /// with no prompt forced. Read the same way everywhere — on a system where they are other files, the cost is one
    /// prompt more, never one less.
    /// </remarks>
    internal static string AsWritten(string segment)
    {
        if (segment.EndsWith("::$DATA", StringComparison.OrdinalIgnoreCase)) segment = segment[..^"::$DATA".Length];
        return segment.TrimEnd('.', ' ');
    }
}
