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
    Hardware, Setup, Diagnostics, UndoRun, Replay, Xray, Bench, Arena, Tdd, Branch, Task,
    Onboard,
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
    /// <summary>Grouping of <see cref="Catalog"/>, in `/help` display order.</summary>
    internal enum SlashCategory
    {
        Meta, CodeActions, Files, Shell, Web, Git, Build,
        Knowledge, Sessions, Models, Agent, Governance, Transparency,
    }

    /// <summary>
    /// Canonical built-in command catalogue — the single source for the autocomplete popup
    /// (VS and, through <c>command/list</c>, VS Code), for <c>/help</c>, and for the docs.
    /// A property (not a cached array) so the localized hints follow a runtime language switch
    /// (<see cref="Strings.ApplyLanguage"/>).
    /// </summary>
    /// <remarks>
    /// Adding a command means adding a line here, and <c>SlashCommandCoverageTests</c> checks both
    /// directions: every entry must be routed by <see cref="Route"/>, and every command routed by
    /// <see cref="Route"/> must appear here. That second check is what caught <c>/docs</c>, which
    /// worked but was invisible in both autocompletes because it had never been listed.
    /// </remarks>
    internal static (string Cmd, string Hint, SlashCategory Category)[] Catalog =>
    [
        ("/clear",    Strings.SlashHintClear,    SlashCategory.Meta),
        ("/help",     Strings.SlashHintHelp,     SlashCategory.Meta),
        ("/model",    Strings.SlashHintModel,    SlashCategory.Meta),
        ("/tools",    Strings.SlashHintTools,    SlashCategory.Meta),
        ("/export",   Strings.SlashHintExport,   SlashCategory.Meta),
        ("/restore",  Strings.SlashHintRestore,  SlashCategory.Meta),

        ("/explain",  Strings.SlashHintExplain,  SlashCategory.CodeActions),
        ("/fix",      Strings.SlashHintFix,      SlashCategory.CodeActions),
        ("/review",   Strings.SlashHintReview,   SlashCategory.CodeActions),
        ("/refactor", Strings.SlashHintRefactor, SlashCategory.CodeActions),
        ("/test",     Strings.SlashHintTest,     SlashCategory.CodeActions),
        ("/doc",      Strings.SlashHintDoc,      SlashCategory.CodeActions),

        ("/read",     Strings.SlashHintRead,     SlashCategory.Files),
        ("/ls",       Strings.SlashHintLs,       SlashCategory.Files),
        ("/grep",     Strings.SlashHintGrep,     SlashCategory.Files),
        ("/diff",     Strings.SlashHintDiff,     SlashCategory.Files),

        ("/run",      Strings.SlashHintRun,      SlashCategory.Shell),

        ("/fetch",       Strings.SlashHintFetch,  SlashCategory.Web),
        ("/search-web",  Strings.SlashHintSearch, SlashCategory.Web),

        ("/commit",   Strings.SlashHintCommit,   SlashCategory.Git),
        ("/git",      Strings.SlashHintGit,      SlashCategory.Git),

        ("/build",     Strings.SlashHintBuild,    SlashCategory.Build),
        ("/fix-build", Strings.SlashHintFixBuild, SlashCategory.Build),
        ("/tdd",       Strings.SlashHintTdd,      SlashCategory.Build),
        ("/solution",  Strings.SlashHintSolution, SlashCategory.Build),
        ("/map",       Strings.SlashHintMap,      SlashCategory.Build),

        ("/context",     Strings.SlashHintContext,    SlashCategory.Knowledge),
        ("/memory",      Strings.SlashHintMemory,     SlashCategory.Knowledge),
        ("/note",        Strings.SlashHintNote,       SlashCategory.Knowledge),
        ("/notes",       Strings.SlashHintNotes,      SlashCategory.Knowledge),
        ("/index",       Strings.SlashHintIndex,      SlashCategory.Knowledge),
        ("/search-code", Strings.SlashHintSearchCode, SlashCategory.Knowledge),
        ("/docs",        Strings.SlashHintDocs,       SlashCategory.Knowledge),
        ("/onboard",     Strings.SlashHintOnboard,    SlashCategory.Knowledge),

        ("/history",  Strings.SlashHintHistory,  SlashCategory.Sessions),
        ("/phistory", Strings.SlashHintPhistory, SlashCategory.Sessions),
        ("/branch",   Strings.SlashHintBranch,   SlashCategory.Sessions),
        ("/template", Strings.SlashHintTemplate, SlashCategory.Sessions),
        ("/snippets", Strings.SlashHintSnippets, SlashCategory.Sessions),

        ("/models",   Strings.SlashHintModels,   SlashCategory.Models),
        ("/hardware", Strings.SlashHintHardware, SlashCategory.Models),
        ("/bench",    Strings.SlashHintBench,    SlashCategory.Models),
        ("/arena",    Strings.SlashHintArena,    SlashCategory.Models),
        ("/setup",    Strings.SlashHintSetup,    SlashCategory.Models),

        ("/agent-step", Strings.SlashHintAgentStep, SlashCategory.Agent),
        ("/resume",     Strings.SlashHintResume,    SlashCategory.Agent),
        ("/plan",       Strings.SlashHintPlan,      SlashCategory.Agent),
        ("/task",       Strings.SlashHintTask,      SlashCategory.Agent),

        ("/rules",    Strings.SlashHintRules,    SlashCategory.Governance),
        ("/checks",   Strings.SlashHintChecks,   SlashCategory.Governance),
        ("/check",    Strings.SlashHintCheck,    SlashCategory.Governance),
        ("/prompts",  Strings.SlashHintPrompts,  SlashCategory.Governance),

        ("/xray",        Strings.SlashHintXray,        SlashCategory.Transparency),
        ("/replay",      Strings.SlashHintReplay,      SlashCategory.Transparency),
        ("/undo-run",    Strings.SlashHintUndoRun,     SlashCategory.Transparency),
        ("/diagnostics", Strings.SlashHintDiagnostics, SlashCategory.Transparency),
    ];

    /// <summary>Flat view of <see cref="Catalog"/> for the autocomplete popup and `command/list`.</summary>
    internal static (string Cmd, string Hint)[] BuiltInCommands =>
        Catalog.Select(c => (c.Cmd, c.Hint)).ToArray();

    /// <summary>Localized section title of a category.</summary>
    internal static string CategoryTitle(SlashCategory category) => category switch
    {
        SlashCategory.Meta         => Strings.SlashCategoryMeta,
        SlashCategory.CodeActions  => Strings.SlashCategoryCodeActions,
        SlashCategory.Files        => Strings.SlashCategoryFiles,
        SlashCategory.Shell        => Strings.SlashCategoryShell,
        SlashCategory.Web          => Strings.SlashCategoryWeb,
        SlashCategory.Git          => Strings.SlashCategoryGit,
        SlashCategory.Build        => Strings.SlashCategoryBuild,
        SlashCategory.Knowledge    => Strings.SlashCategoryKnowledge,
        SlashCategory.Sessions     => Strings.SlashCategorySessions,
        SlashCategory.Models       => Strings.SlashCategoryModels,
        SlashCategory.Agent        => Strings.SlashCategoryAgent,
        SlashCategory.Governance   => Strings.SlashCategoryGovernance,
        _                          => Strings.SlashCategoryTransparency,
    };

    /// <summary>
    /// Renders `/help` from <see cref="Catalog"/>. Generated rather than written: the previous
    /// hand-maintained help text had silently drifted, missing ten shipped commands (`/tdd`,
    /// `/branch`, `/arena`, `/bench`, `/xray`, `/replay`, `/undo-run`, `/diagnostics`, `/task`,
    /// `/docs`) in all ten languages while claiming to list everything.
    /// </summary>
    internal static string BuildHelp()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var group in Catalog.GroupBy(c => c.Category).OrderBy(g => g.Key))
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.Append("**").Append(CategoryTitle(group.Key)).AppendLine("**");
            foreach (var (cmd, hint, _) in group)
                sb.Append("- `").Append(cmd).Append("` — ").AppendLine(hint);
        }
        return sb.ToString().TrimEnd();
    }

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
            case "/replay":            return new SlashDelegatedAction(SlashCommandId.Replay,          parts);
            case "/xray":              return new SlashDelegatedAction(SlashCommandId.Xray,            parts);
            case "/bench":
            case "/benchmark":         return new SlashDelegatedAction(SlashCommandId.Bench,           parts);
            case "/arena":             return new SlashDelegatedAction(SlashCommandId.Arena,           parts);
            case "/tdd":               return new SlashDelegatedAction(SlashCommandId.Tdd,             parts);
            case "/branch":            return new SlashDelegatedAction(SlashCommandId.Branch,          parts);
            case "/task":              return new SlashDelegatedAction(SlashCommandId.Task,            parts);
            case "/onboard":           return new SlashDelegatedAction(SlashCommandId.Onboard,         parts);

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
                return new SlashInfoAction(BuildHelp());

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
            .Select(t => (Cmd: t.Name, Hint: SlashTemplates.HintOf(t)));
        return BuiltInCommands
            .Concat(userCmds)
            .Where(c => c.Cmd.StartsWith(text, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }
}
