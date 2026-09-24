using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Inferpal.Config;

namespace Inferpal.Services.Prompting;

/// <summary>Origin of one layer of the composed system prompt (for <c>/xray</c>).</summary>
internal enum PromptSectionKind { Base, Persona, Custom, Template, Pinned, ProjectContext, Memory, Notes, Rules }

/// <summary>One layer of the composed system prompt. <see cref="Content"/> includes the layer's own
/// leading separator so concatenating all sections reproduces the exact prompt text.
/// <see cref="Detail"/> carries the file name / rule count where relevant.
/// <para>
/// ⚠ <see cref="Detail"/> is a <b>label</b>, not an identity — two pinned files can share a name,
/// and the persona's language and the rule count change with the active file. <see cref="Key"/>
/// carries the identity when the label is not one; <c>XRayPanelPresenter.SectionId</c> is what
/// reads it, and the user's on/off switch depends on that id not moving under it.
/// </para></summary>
internal sealed record PromptSection(PromptSectionKind Kind, string? Detail, string Content, string? Key = null);

/// <summary>
/// Builds the layered system prompt sent with every chat/agent request:
/// base prompt → persona snippet (active language) → user custom prompt → active
/// <c>/template</c> suffix → pinned files → project files (<c>.inferpal/context.md</c>,
/// <c>memory.md</c>, <c>notes.md</c>) → glob-scoped rules.
/// Extracted from the tool-window VM so the layering is unit-testable without VS.
/// </summary>
internal sealed class SystemPromptBuilder(InferpalConfig config, string? editorName = null)
{
    /// <summary>
    /// The editor and the shell, stated from what this process can actually observe — appended to
    /// the base layer so no new <c>/xray</c> section appears.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠ The base prompt is a localised resource shared by both front-ends, so it can assert
    /// neither the editor nor the shell: a VS Code user would be told the wrong editor and a
    /// Linux/macOS user the wrong shell. A model told it lives in Visual Studio answers with
    /// Solution Explorer and Rebuild Solution.
    /// </para>
    /// <para>
    /// The editor is <b>declared</b> by the front-end, never inferred — same rule as
    /// <c>SignalScope.DeclareNoVsInProcessPeer</c>, and for the same reason: a process that guesses
    /// its own role guesses wrong the day a third front-end appears. When no name is declared the
    /// line simply omits it rather than naming an editor at random.
    /// </para>
    /// </remarks>
    internal string EnvironmentFacts()
    {
        var (dialect, fileName) = Shell.ShellLauncher.Resolve();
        var shell = Shell.ShellLauncher.SpokenName(dialect, fileName);
        var editor = string.IsNullOrWhiteSpace(editorName) ? string.Empty : $"Editor: {editorName}. ";
        return $"\n\n{editor}Operating system: {RuntimeInformation.OSDescription}. "
             + $"The run_command shell is {shell}.";
    }

    /// <summary>
    /// Persona language of a file, from its extension; null when it is not a code file the persona knows
    /// (a Markdown file keeps the previous persona). Shared by both front-ends.
    /// </summary>
    public static string? LanguageOf(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".cs"     => "csharp",
            ".ts"     => "typescript",
            ".tsx"    => "typescript",
            ".js"     => "javascript",
            ".jsx"    => "javascript",
            ".py"     => "python",
            ".go"     => "go",
            ".java"   => "java",
            ".cpp"    => "cpp",
            ".c"      => "c",
            ".h"      => "cpp",
            ".hpp"    => "cpp",
            ".rs"     => "rust",
            ".fs"     => "fsharp",
            ".rb"     => "ruby",
            ".php"    => "php",
            ".swift"  => "swift",
            ".kt"     => "kotlin",
            ".razor"  => "razor",
            ".vue"    => "vue",
            _         => null,
        };

    /// <summary>
    /// The active file relative to the project root, '/'-separated, as the glob-scoped rules match it; null
    /// without a root or a file, or for a file outside the root. Shared by both front-ends.
    /// </summary>
    public static string? RelativeActivePath(string? root, string? path)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root)) return null;
        try
        {
            var rel = Path.GetRelativePath(root, path);
            if (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel)) return null;
            return rel.Replace('\\', '/');
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return null; }
    }

    /// <summary>Persona snippet appended when persona auto-switch is on, keyed by editor language.</summary>
    internal static string PersonaSnippetFor(string language) => language switch
    {
        "csharp"     => "Active file: C# — favour idiomatic C#, LINQ, async/await, and .NET conventions.",
        "typescript" => "Active file: TypeScript — favour strict typing, modern ESNext idioms, and framework conventions when evident.",
        "javascript" => "Active file: JavaScript — favour modern ES2022+ idioms.",
        "python"     => "Active file: Python — favour idiomatic Python (PEP 8), type hints, and stdlib-first approaches.",
        "go"         => "Active file: Go — favour idiomatic Go: explicit error handling, small interfaces, goroutines when natural.",
        "rust"       => "Active file: Rust — respect ownership, favour safe code, and use standard Rust idioms.",
        "java"       => "Active file: Java — favour modern Java (17+) idioms, streams, and records.",
        "cpp"        => "Active file: C++ — favour modern C++17/20 idioms, RAII, and avoid undefined behaviour.",
        "fsharp"     => "Active file: F# — favour functional-first patterns, discriminated unions, and immutable data.",
        "razor"      => "Active file: Razor/Blazor — apply ASP.NET Core and Blazor component lifecycle conventions.",
        "vue"        => "Active file: Vue — favour Vue 3 Composition API and idiomatic TypeScript.",
        _            => string.Empty,
    };

    /// <summary>Assembles the full system prompt.</summary>
    /// <param name="basePrompt">Localised base system prompt (<c>Strings.SystemPrompt</c>).</param>
    /// <param name="language">Active document language for the persona snippet; null/empty to skip.</param>
    /// <param name="templateSuffix">Suffix of the active <c>/template</c>, appended verbatim; null/empty to skip.</param>
    /// <param name="projectRoot">Project root containing <c>.inferpal/</c>; null to skip the project layers.</param>
    /// <param name="activeFileRelPath">
    /// Active file relative to the root (forward slashes) used to scope rules by glob;
    /// null matches only <c>alwaysApply</c> / glob-less rules.
    /// </param>
    /// <param name="disabledSectionIds">
    /// Section ids (<see cref="Presentation.XRayPanelPresenter.SectionId"/>) switched off from the
    /// Context X-Ray panel — those layers are skipped; null/empty keeps everything.
    /// </param>
    public string Build(
        string  basePrompt,
        string? language          = null,
        string? templateSuffix    = null,
        string? projectRoot       = null,
        string? activeFileRelPath = null,
        IReadOnlySet<string>? disabledSectionIds = null)
        => string.Concat(BuildSections(basePrompt, language, templateSuffix, projectRoot, activeFileRelPath, disabledSectionIds)
                         .Where(s => disabledSectionIds is null
                                     || !disabledSectionIds.Contains(Presentation.XRayPanelPresenter.SectionId(s)))
                         .Select(s => s.Content));

    /// <summary>
    /// Same layering as <see cref="Build"/>, one <see cref="PromptSection"/> per contributing layer —
    /// the <c>/xray</c> token breakdown reads these. Concatenating the sections' contents in order
    /// reproduces the exact prompt (each content carries its own leading separator).
    /// </summary>
    /// <param name="disabledSectionIds">Sections switched off from the X-Ray panel: they are still returned (the panel
    /// lists them), but they take no part of the budget the file-backed sections share — switching one off is how
    /// the user makes room for the others.</param>
    public IReadOnlyList<PromptSection> BuildSections(
        string  basePrompt,
        string? language          = null,
        string? templateSuffix    = null,
        string? projectRoot       = null,
        string? activeFileRelPath = null,
        IReadOnlySet<string>? disabledSectionIds = null)
    {
        var sections = new List<PromptSection> { new(PromptSectionKind.Base, null, basePrompt + EnvironmentFacts()) };

        if (config.PersonaAutoSwitch && !string.IsNullOrEmpty(language))
        {
            var snippet = PersonaSnippetFor(language);
            if (!string.IsNullOrEmpty(snippet))
                sections.Add(new(PromptSectionKind.Persona, language, "\n\n" + snippet));
        }

        var custom = config.CustomSystemPrompt?.Trim();
        if (!string.IsNullOrEmpty(custom))
            sections.Add(new(PromptSectionKind.Custom, null, "\n\n" + custom));

        if (!string.IsNullOrEmpty(templateSuffix))
            sections.Add(new(PromptSectionKind.Template, null, templateSuffix));

        // File-backed layers are gathered whole, then share one budget before they are added (ShareBudget).
        var files = new List<FileLayer>();

        var (pinned, overCap) = PinnedFilesPolicy.ParseActiveWithOverflow(config.PinnedContextFiles);

        // ⚠ Same silence as just below, for the other cause. The cap drops paths the user wrote in
        // the settings window — which caps nothing — and that the configuration keeps: they are
        // neither in the prompt nor in the 📌 chips (which read the same capped list), and nothing
        // said so. Once per path, like the missing one: this prompt is rebuilt on every
        // active-file change.
        foreach (var dropped in overCap)
            Diagnostics.DroppedLineOnce(
                "PinnedFiles", $"Pinned context file ignored (only the first {PinnedFilesPolicy.MaxPinned} are sent)",
                OverCapKey(dropped), dropped);

        foreach (var pinnedPath in pinned)
        {
            // ⚠ This path is within the cap NOW. Unlike the other two causes, being over the cap
            // depends on the OTHER pins, not on this path: unpin one above it and it comes back,
            // pin another and it drops out again — under the very same key. Without this forget,
            // the second drop is silent for the life of the process, and the over-cap note is the
            // ONLY channel that says so (the 📌 chips read the same capped list).
            Diagnostics.Forget(PinContext, OverCapKey(pinnedPath));
            if (!File.Exists(pinnedPath))
            {
                // ⚠ A pinned file that is not there — mistyped path, file moved, disconnected
                // network drive — was skipped WITHOUT A WORD. The user pinned it so it travels with
                // every request, the 📌 chip still shows it (the chip comes from the settings, not
                // from the disk), and it is not there. Same class as the three settings parsers that
                // kept quiet about the lines they rejected.
                ReportMissingPinOnce(pinnedPath);
                continue;
            }
            ForgetMissingPin(pinnedPath);
            try
            {
                var pinnedContent = File.ReadAllText(pinnedPath, Encoding.UTF8).Trim();
                // The read goes through again: a later failure will say so again.
                Diagnostics.Forget(PinContext, UnreadableKey(pinnedPath));
                if (!string.IsNullOrEmpty(pinnedContent))
                    // The label is the file name; the identity is the PATH — two pins can both be
                    // called README.md, and one switch would then turn both off.
                    files.Add(new(PromptSectionKind.Pinned, Path.GetFileName(pinnedPath),
                        "\n\n## Pinned: " + Path.GetFileName(pinnedPath) + "\n\n", pinnedContent,
                        Path.GetFileName(pinnedPath), Key: pinnedPath));
            }
            catch (Exception ex) { ReportUnreadablePinOnce(pinnedPath, ex); }
        }


        if (projectRoot is not null)
        {
            AddFileSection(files, PromptSectionKind.ProjectContext, Path.Combine(projectRoot, ".inferpal", "context.md"), "Project context", ".inferpal/context.md");
            AddFileSection(files, PromptSectionKind.Memory,         Path.Combine(projectRoot, ".inferpal", "memory.md"),  "Agent memory",    ".inferpal/memory.md");
            AddFileSection(files, PromptSectionKind.Notes,          NotesStore.NotesPath(projectRoot),                       "Project notes",   ".inferpal/notes.md");

            // Project rules (.inferpal/rules/*.md) — scoped by glob against the active file.
            try
            {
                var rules = RulesService.Load(Path.Combine(projectRoot, ".inferpal", "rules"));
                if (rules.Count > 0)
                {
                    var matched = rules.Where(r => RulesService.Matches(r, activeFileRelPath)).ToList();
                    if (matched.Count > 0)
                        files.Add(new(PromptSectionKind.Rules, matched.Count.ToString(), "",
                                      RulesService.Render(matched), ".inferpal/rules"));
                }
            }
            catch (Exception ex) { Diagnostics.Swallow("SystemPromptBuilder.Rules", ex); }
        }

        sections.AddRange(ShareBudget(files, FileSectionsBudget(config.ContextWindowSize), disabledSectionIds));
        return sections;
    }

    /// <summary>A file-backed layer before the budget is applied: <see cref="Header"/> + the (capped) body.</summary>
    private sealed record FileLayer(
        PromptSectionKind Kind, string? Detail, string Header, string Body, string What, string? Key = null)
    {
        public PromptSection Section(string body) => new(Kind, Detail, Header + body, Key);
    }

    /// <summary>
    /// Characters the file-backed sections share, all of them together: one per token of the context window —
    /// about a quarter of it at four characters per token.
    /// </summary>
    /// <remarks>
    /// ⚠ The ceiling of a SINGLE section (<see cref="MaxFileSectionChars"/>) is not a budget: at the default window
    /// (8 192 tokens) one section at that ceiling fills the whole window, there are up to seven of them (three
    /// pins, context, memory, notes, rules), and in agent mode the tool definitions already take about 4 900 tokens
    /// of it. The request then overflows on every question — refused by LM Studio, cut at the head by Ollama,
    /// system prompt first — and compaction cannot help: the system prompt is never compacted. The window here is
    /// the configured one: the prompt is built before the turn knows which model will answer.
    /// </remarks>
    internal static int FileSectionsBudget(int contextWindow) =>
        contextWindow > 0 ? contextWindow : DefaultContextWindow;

    private const int DefaultContextWindow = 8_192;

    /// <summary>
    /// Shares <paramref name="budget"/> between sections of the given sizes, fairly: sections under an equal share
    /// keep all they have, the others split what is left equally — one large pinned file is cut, it does not cut
    /// the project's rules down to nothing. No section gets more than <see cref="MaxFileSectionChars"/>.
    /// </summary>
    internal static int[] Allot(IReadOnlyList<int> sizes, int budget)
    {
        var allotted  = new int[sizes.Count];
        var remaining = Math.Max(0, budget);
        var order     = Enumerable.Range(0, sizes.Count).OrderBy(i => sizes[i]).ToList();
        for (var k = 0; k < order.Count; k++)
        {
            var i = order[k];
            allotted[i] = Math.Min(Math.Min(sizes[i], MaxFileSectionChars), remaining / (order.Count - k));
            remaining  -= allotted[i];
        }
        return allotted;
    }

    // A section switched off is not sent, so it takes no share; it is still capped on its own, for the panel.
    private static IEnumerable<PromptSection> ShareBudget(
        List<FileLayer> files, int budget, IReadOnlySet<string>? disabledSectionIds)
    {
        var sent = Enumerable.Range(0, files.Count)
            .Where(i => disabledSectionIds is null
                        || !disabledSectionIds.Contains(Presentation.XRayPanelPresenter.SectionId(files[i].Section(""))))
            .ToList();
        var shares   = Allot(sent.Select(i => files[i].Body.Length).ToList(), budget);
        var allotted = files.Select(f => Math.Min(f.Body.Length, MaxFileSectionChars)).ToArray();
        for (var j = 0; j < sent.Count; j++) allotted[sent[j]] = shares[j];

        for (var i = 0; i < files.Count; i++)
            yield return files[i].Section(CapSection(files[i].Body, files[i].What, allotted[i]));
    }

    /// <summary>
    /// Ceiling on ONE file-backed prompt section (~8k tokens), under the budget they all share
    /// (<see cref="FileSectionsBudget"/>) — on its own it bounds nothing: at the default window one
    /// section at this ceiling fills the whole window. Every section here comes from a
    /// file this process does not control: <c>memory.md</c> is written by the agent itself,
    /// <c>notes.md</c> and <c>context.md</c> by the user, the rules by whoever authored the
    /// repository, and a pinned file is whatever the user pinned — a build log, a generated header.
    /// None of them were bounded.
    /// </summary>
    /// <remarks>
    /// An oversized block makes the backend truncate the request <b>from the head</b>, which is
    /// exactly where the system prompt lives — so a section that grows silently evicts the
    /// instructions it was meant to add. <c>MaxToolResultCharsInContext</c> and
    /// <c>HistoryCompaction</c> bound the other two inputs for the same reason.
    /// </remarks>
    internal const int MaxFileSectionChars = 32_000;

    /// <summary>Caps one section at its own ceiling (<see cref="MaxFileSectionChars"/>) and says so in the prompt.</summary>
    internal static string CapSection(string text, string what) => CapSection(text, what, MaxFileSectionChars);

    /// <summary>Caps one section at <paramref name="allotted"/> characters and says so in the prompt — a silent cut
    /// would make the model answer from half a rule without either party knowing.</summary>
    /// <remarks>⚠ The diagnostics note is said ONCE per condition, not once per build: the prompt is rebuilt on every
    /// question, so a capped pinned file wrote one entry per question and a long session flushed the ring — and the
    /// failure being looked for with it. The key IS the condition (size and share): when either moves, it is said
    /// again.</remarks>
    internal static string CapSection(string text, string what, int allotted)
    {
        if (text.Length <= allotted) return text;

        Diagnostics.RecordOnce("SystemPrompt",
            $"'{what}' is {text.Length} chars; truncated to {allotted} for the system prompt "
            + "(the prompt's files share a budget set by the context window).",
            $"{what}|{text.Length}|{allotted}");
        return SafeTruncate.Truncate(text, allotted)
             + $"\n\n[... {what} truncated to {allotted} characters out of {text.Length} "
             + "to keep the system prompt inside the context window]";
    }

    /// <summary>Adds a <c>## header</c> file-backed section; missing/empty/unreadable file ⇒ no-op.</summary>
    // ⚠ Once per path and per process, not once per build: the system prompt is rebuilt on EVERY
    // active-file change, so reporting on each pass would drown the diagnostics ring under the same
    // message — and a noisy channel stops being read. A path that comes back leaves the set: if the
    // file disappears again, we say so again.
    //
    // ⚠ The set of already-reported paths lives in Diagnostics.DroppedLineOnce, shared with the
    // other repeated parsers (CustomTools, UserTemplates); this site keeps only the casing of its
    // keys, which is its own: a file path.
    private static void ReportMissingPinOnce(string path) =>
        Diagnostics.DroppedLineOnce(PinContext, "Pinned context file not found", MissingKey(path), path);

    /// <summary>The path is back: the next time it goes missing will be reported again.</summary>
    private static void ForgetMissingPin(string path) =>
        Diagnostics.Forget(PinContext, MissingKey(path));

    /// <summary>
    /// ⚠ <b>A pinned file that is present but unreadable</b> — locked by another editor, permission
    /// denied, a network drive gone. Same consequence as a missing one (it is not in the prompt, the
    /// 📌 chip keeps showing it), and the same rule: reported ONCE, not once per rebuild, since this
    /// prompt is rebuilt on every change of active file.
    /// </summary>
    private static void ReportUnreadablePinOnce(string path, Exception ex) =>
        Diagnostics.RecordOnce(PinContext,
            $"Pinned context file could not be read, so it is NOT in the system prompt: {path} ({ex.Message})",
            UnreadableKey(path));

    private const string PinContext = "PinnedFiles";

    /// <summary>Paths compare case-insensitively — <c>C:\A.md</c> and <c>c:\a.md</c> are the same
    /// pinned file, while the shared key itself is ordinal.</summary>
    private static string PinKey(string path) => path.ToLowerInvariant();

    // ⚠ The THREE causes have DISJOINT keys, and that is not cosmetic: each forget runs on its own
    // pass condition, so a shared key would be freed by the wrong one and "once" would become
    // "every time" — the defect closed at one end, coming back at the other. The over-cap key was
    // the bare path while this note claimed there were two causes: an enumeration standing in for
    // a rule, one cause short.
    private static string MissingKey(string path)    => "missing:"    + PinKey(path);
    private static string UnreadableKey(string path) => "unreadable:" + PinKey(path);
    private static string OverCapKey(string path)    => "overcap:"    + PinKey(path);

    private static void AddFileSection(List<FileLayer> files, PromptSectionKind kind, string path, string header, string detail)
    {
        if (!File.Exists(path)) return;
        try
        {
            var text = File.ReadAllText(path, Encoding.UTF8).Trim();
            Diagnostics.Forget(PromptFileContext, PinKey(path));
            if (!string.IsNullOrEmpty(text))
                files.Add(new(kind, detail, "\n\n## " + header + "\n\n", text, detail));
        }
        catch (Exception ex)
        {
            // Same class and same remedy as the unreadable pinned file: those three files (project
            // context, memory, notes) are re-read on every rebuild of the prompt.
            Diagnostics.RecordOnce(PromptFileContext,
                $"{detail} could not be read, so it is NOT in the system prompt: {ex.Message}",
                PinKey(path));
        }
    }

    private const string PromptFileContext = "PromptFiles";
}
