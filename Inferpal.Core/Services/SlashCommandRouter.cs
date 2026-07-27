using System.IO;
using Inferpal.Localization;

namespace Inferpal.Services;

// ── Routing results ──────────────────────────────────────────────────────────

/// <summary>What the chat VM must do for a parsed slash command (see <see cref="SlashCommandRouter.Route"/>).</summary>
internal abstract record SlashAction;

/// <summary>Show an informational bubble (usage error, /help, unknown command).</summary>
internal sealed record SlashInfoAction(string Message) : SlashAction;

/// <summary>Execute a registry tool directly and show its result (optionally attached as a context chip).</summary>
internal sealed record SlashToolAction(string Tool, object Args, string? AttachAs = null) : SlashAction;

/// <summary>Run a code action (/explain, /fix, …) on the active document or selection.</summary>
internal sealed record SlashCodeAction(SlashCodeActionKind Kind) : SlashAction;

/// <summary>Send the expanded text of a user-defined template as a normal chat prompt.</summary>
internal sealed record SlashPromptAction(string Prompt) : SlashAction;

/// <summary>Hand off to a stateful VM handler (session, config, background services…).</summary>
internal sealed record SlashDelegatedAction(SlashCommandId Id, string[] Parts) : SlashAction;

/// <summary>Code actions sharing the “grab active code → build prompt → send” shape.</summary>
internal enum SlashCodeActionKind { Explain, Fix, Review, Refactor, Test, Doc }

/// <summary>Commands whose execution needs VM state and therefore stays in the tool window.</summary>
internal enum SlashCommandId
{
    Clear, TestBuildBanner, Model, Tools, Export, Context, Memory, Index,
    Commit, CommitExec, FixBuild, History, PHistory, Models, AgentStep, Resume,
    Note, Notes, Snippets, Template, Docs, Check, Rules, Checks, Plan, Prompts,
    Hardware, Setup, Diagnostics, UndoRun,
}

/// <summary>User-defined prompt template (config <c>PromptTemplates</c>, one <c>/name=text</c> per line,
/// or a <c>.inferpal/prompts/*.md</c> file — see <see cref="PromptFilesService"/>).
/// <paramref name="Hint"/> overrides the truncated text in autocomplete when set.</summary>
internal sealed record UserSlashTemplate(string Name, string Text, string? Hint = null);

/// <summary>
/// Pure parsing/routing for chat slash commands: tokenisation, usage validation, tool-argument
/// building, user-template expansion, and autocomplete matching. Extracted from the tool-window
/// VM so this logic is unit-testable without VS — execution of the resulting
/// <see cref="SlashAction"/> stays in the VM, which owns the services and UI.
/// </summary>
internal static class SlashCommandRouter
{
    /// <summary>Canonical built-in command list — single source for autocomplete (and docs).
    /// A property (not a cached array) so the localized hints follow a runtime language switch
    /// (<see cref="Strings.ApplyLanguage"/>).</summary>
    internal static (string Cmd, string Hint)[] BuiltInCommands =>
    [
        ("/explain",  Strings.SlashHintExplain),
        ("/fix",      Strings.SlashHintFix),
        ("/review",   Strings.SlashHintReview),
        ("/refactor", Strings.SlashHintRefactor),
        ("/test",     Strings.SlashHintTest),
        ("/doc",      Strings.SlashHintDoc),
        ("/clear",    Strings.SlashHintClear),
        ("/model",    Strings.SlashHintModel),
        ("/tools",    Strings.SlashHintTools),
        ("/export",   Strings.SlashHintExport),
        ("/restore",  Strings.SlashHintRestore),
        ("/help",     Strings.SlashHintHelp),
        ("/read",     Strings.SlashHintRead),
        ("/ls",       Strings.SlashHintLs),
        ("/grep",     Strings.SlashHintGrep),
        ("/run",      Strings.SlashHintRun),
        ("/fetch",    Strings.SlashHintFetch),
        ("/search-web",  Strings.SlashHintSearch),
        ("/search-code", Strings.SlashHintSearchCode),
        ("/commit",   Strings.SlashHintCommit),
        ("/git",      Strings.SlashHintGit),
        ("/map",      Strings.SlashHintMap),
        ("/solution", Strings.SlashHintSolution),
        ("/build",    Strings.SlashHintBuild),
        ("/fix-build",Strings.SlashHintFixBuild),
        ("/context",  Strings.SlashHintContext),
        ("/memory",   Strings.SlashHintMemory),
        ("/index",    Strings.SlashHintIndex),
        ("/history",  Strings.SlashHintHistory),
        ("/template", Strings.SlashHintTemplate),
        ("/diff",     Strings.SlashHintDiff),
        ("/check",    Strings.SlashHintCheck),
        ("/rules",    Strings.SlashHintRules),
        ("/checks",   Strings.SlashHintChecks),
        ("/snippets", Strings.SlashHintSnippets),
        ("/note",     Strings.SlashHintNote),
        ("/notes",    Strings.SlashHintNotes),
        ("/phistory", Strings.SlashHintPhistory),
        ("/models",     Strings.SlashHintModels),
        ("/agent-step", Strings.SlashHintAgentStep),
        ("/resume",     Strings.SlashHintResume),
        ("/plan",       Strings.SlashHintPlan),
        ("/prompts",    Strings.SlashHintPrompts),
        ("/hardware",   Strings.SlashHintHardware),
        ("/setup",      Strings.SlashHintSetup),
        ("/diagnostics", Strings.SlashHintDiagnostics),
        ("/undo-run",   Strings.SlashHintUndoRun),
    ];

    /// <summary>Maps a raw <c>/command …</c> input to the action the VM must execute.</summary>
    /// <param name="prompt">Full prompt text, starting with <c>/</c>.</param>
    /// <param name="userTemplates">User templates checked as the fallback for unknown commands.</param>
    public static SlashAction Route(string prompt, IEnumerable<UserSlashTemplate> userTemplates)
    {
        var parts = prompt.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var cmd   = parts.Length > 0 ? parts[0].ToLowerInvariant() : "/";

        switch (cmd)
        {
            // ── Stateful commands — execution stays in the VM ─────────────────
            case "/clear":             return new SlashDelegatedAction(SlashCommandId.Clear,           parts);
            case "/test-build-banner": return new SlashDelegatedAction(SlashCommandId.TestBuildBanner, parts);
            case "/model":             return new SlashDelegatedAction(SlashCommandId.Model,           parts);
            case "/tools":             return new SlashDelegatedAction(SlashCommandId.Tools,           parts);
            case "/export":            return new SlashDelegatedAction(SlashCommandId.Export,          parts);
            case "/context":           return new SlashDelegatedAction(SlashCommandId.Context,         parts);
            case "/memory":            return new SlashDelegatedAction(SlashCommandId.Memory,          parts);
            case "/index":             return new SlashDelegatedAction(SlashCommandId.Index,           parts);
            case "/commit":            return new SlashDelegatedAction(SlashCommandId.Commit,          parts);
            case "/commit-exec":       return new SlashDelegatedAction(SlashCommandId.CommitExec,      parts);
            case "/fix-build":         return new SlashDelegatedAction(SlashCommandId.FixBuild,        parts);
            case "/history":           return new SlashDelegatedAction(SlashCommandId.History,         parts);
            case "/phistory":          return new SlashDelegatedAction(SlashCommandId.PHistory,        parts);
            case "/models":            return new SlashDelegatedAction(SlashCommandId.Models,          parts);
            case "/agent-step":        return new SlashDelegatedAction(SlashCommandId.AgentStep,       parts);
            case "/resume":            return new SlashDelegatedAction(SlashCommandId.Resume,          parts);
            case "/plan":              return new SlashDelegatedAction(SlashCommandId.Plan,            parts);
            case "/prompts":           return new SlashDelegatedAction(SlashCommandId.Prompts,         parts);
            case "/hardware":          return new SlashDelegatedAction(SlashCommandId.Hardware,        parts);
            case "/setup":             return new SlashDelegatedAction(SlashCommandId.Setup,           parts);
            case "/note":              return new SlashDelegatedAction(SlashCommandId.Note,            parts);
            case "/notes":             return new SlashDelegatedAction(SlashCommandId.Notes,           parts);
            case "/snippets":          return new SlashDelegatedAction(SlashCommandId.Snippets,        parts);
            case "/template":          return new SlashDelegatedAction(SlashCommandId.Template,        parts);
            case "/docs":              return new SlashDelegatedAction(SlashCommandId.Docs,            parts);
            case "/check":             return new SlashDelegatedAction(SlashCommandId.Check,           parts);
            case "/rules":             return new SlashDelegatedAction(SlashCommandId.Rules,           parts);
            case "/checks":            return new SlashDelegatedAction(SlashCommandId.Checks,          parts);
            case "/diagnostics":       return new SlashDelegatedAction(SlashCommandId.Diagnostics,     parts);
            case "/undo-run":          return new SlashDelegatedAction(SlashCommandId.UndoRun,         parts);

            // ── Code actions on the active document/selection ─────────────────
            case "/explain":           return new SlashCodeAction(SlashCodeActionKind.Explain);
            case "/fix":               return new SlashCodeAction(SlashCodeActionKind.Fix);
            case "/review":            return new SlashCodeAction(SlashCodeActionKind.Review);
            case "/refactor":          return new SlashCodeAction(SlashCodeActionKind.Refactor);
            case "/test":              return new SlashCodeAction(SlashCodeActionKind.Test);
            case "/doc":               return new SlashCodeAction(SlashCodeActionKind.Doc);

            // ── Direct tool invocations — usage checks + argument building ────
            case "/restore":
                if (parts.Length < 2) return new SlashInfoAction(Strings.SlashUsageRestore);
                return new SlashToolAction("restore_file", new { path = string.Join(" ", parts[1..]) });

            case "/read":
            {
                if (parts.Length < 2) return new SlashInfoAction(Strings.SlashUsage("/read <path>"));
                var p = string.Join(" ", parts[1..]);
                return new SlashToolAction("read_file", new { path = p }, AttachAs: Path.GetFileName(p));
            }

            case "/ls":
                if (parts.Length < 2) return new SlashInfoAction(Strings.SlashUsage("/ls <path> [pattern]"));
                return new SlashToolAction("list_files",
                    parts.Length >= 3 ? (object)new { path = parts[1], pattern = parts[2] }
                                      : new { path = parts[1] });

            case "/grep":
                if (parts.Length < 3) return new SlashInfoAction(Strings.SlashUsage("/grep <dir> <pattern> [file_pattern]"));
                return new SlashToolAction("search_in_files",
                    parts.Length >= 4 ? (object)new { path = parts[1], pattern = parts[2], file_pattern = parts[3] }
                                      : new { path = parts[1], pattern = parts[2] });

            case "/run":
                if (parts.Length < 2) return new SlashInfoAction(Strings.SlashUsage("/run <PowerShell command>"));
                return new SlashToolAction("run_command", new { command = string.Join(" ", parts[1..]) });

            case "/fetch":
                if (parts.Length < 2) return new SlashInfoAction(Strings.SlashUsage("/fetch <url>"));
                return new SlashToolAction("fetch_url", new { url = parts[1] });

            case "/search-web":
            case "/search":        // legacy alias
            case "/web_search":    // legacy alias
                if (parts.Length < 2) return new SlashInfoAction(Strings.SlashUsage("/search-web <query>"));
                return new SlashToolAction("web_search", new { query = string.Join(" ", parts[1..]) });

            case "/search-code":
            case "/codebase":
                if (parts.Length < 2) return new SlashInfoAction(Strings.SlashUsage("/search-code <query>"));
                return new SlashToolAction("search_codebase", new { query = string.Join(" ", parts[1..]) });

            case "/git":
                return new SlashToolAction("get_git_status",
                    parts.Length >= 2 ? (object)new { path = parts[1] } : new { });

            case "/diff":
            {
                // /diff [path] — attaches full diff as a context chip
                var diffPath = parts.Length >= 2 ? string.Join(" ", parts[1..]) : null;
                var diffArgs = diffPath is not null
                    ? (object)new { path = diffPath, include_diff = true }
                    : new { include_diff = true };
                return new SlashToolAction("get_git_status", diffArgs, AttachAs: "📊 git diff");
            }

            case "/map":
                // /map           → project-wide architecture map (namespaces, types, hotspots)
                // /map <path>    → call-graph for that specific file (analyze_code mode=callgraph)
                return parts.Length >= 2
                    ? new SlashToolAction("analyze_code", new { mode = "callgraph", path = string.Join(" ", parts[1..]) })
                    : new SlashToolAction("generate_project_map", new { });

            case "/solution":
                return new SlashToolAction("get_solution_info",
                    parts.Length >= 2 ? (object)new { path = parts[1] } : new { });

            case "/build":
                return new SlashToolAction("get_diagnostics",
                    parts.Length >= 2 ? (object)new { path = parts[1] } : new { });

            // ── Meta ──────────────────────────────────────────────────────────
            case "/help":
                return new SlashInfoAction(Strings.SlashHelpAll);

            default:
                // User-defined prompt templates, then the unknown-command help.
                var userTemplate = userTemplates.FirstOrDefault(t => t.Name == cmd);
                if (userTemplate is not null)
                {
                    var args = parts.Length > 1 ? string.Join(" ", parts[1..]) : "";
                    return new SlashPromptAction(userTemplate.Text.Replace("{args}", args));
                }
                return new SlashInfoAction(Strings.SlashHelp(cmd));
        }
    }

    /// <summary>
    /// Parses the config's <c>PromptTemplates</c> text (one <c>/name=text</c> per line;
    /// <c>#</c> prefix = disabled entry; names are lower-cased and must start with <c>/</c>).
    /// </summary>
    public static IReadOnlyList<UserSlashTemplate> ParseUserTemplates(string? raw)
    {
        var result = new List<UserSlashTemplate>();
        foreach (var line in (raw ?? string.Empty)
                     .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith('#')) continue;   // '#' prefix = disabled entry
            var eq = line.IndexOf('=');
            if (eq <= 1) continue;
            var name = line[..eq].Trim().ToLowerInvariant();
            var text = line[(eq + 1)..].Trim();
            if (!name.StartsWith('/') || string.IsNullOrEmpty(text)) continue;
            result.Add(new UserSlashTemplate(name, text));
        }
        return result;
    }

    /// <summary>
    /// Autocomplete matches for the current prompt text: built-ins plus user templates whose
    /// command starts with the typed prefix. Empty unless the text is a spaceless <c>/prefix</c>.
    /// User-template hints are the template text, truncated for display.
    /// </summary>
    public static IReadOnlyList<(string Cmd, string Hint)> MatchCommands(
        string text, IEnumerable<UserSlashTemplate> userTemplates)
    {
        if (string.IsNullOrEmpty(text) || !text.StartsWith('/') || text.Contains(' '))
            return [];

        var userCmds = userTemplates
            .Select(t => (Cmd: t.Name, Hint: t.Hint ?? (t.Text.Length > 50 ? t.Text[..50] + "…" : t.Text)));
        return BuiltInCommands
            .Concat(userCmds)
            .Where(c => c.Cmd.StartsWith(text, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }
}
