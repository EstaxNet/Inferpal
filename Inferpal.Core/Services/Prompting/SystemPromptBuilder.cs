using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Inferpal.Config;
using Inferpal.Localization;

namespace Inferpal.Services.Prompting;

/// <summary>Origin of one layer of the composed system prompt (for <c>/xray</c>).</summary>
internal enum PromptSectionKind { Base, Persona, Custom, Template, Pinned, ProjectContext, Memory, Notes, Rules, RepoInstructions, Skills }

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
/// <param name="contextWindow">The window the prompt is sent into, when the front-end knows a smaller one than the
/// configured window — the one the server really loaded (<c>ContextWindowInUse</c>). 0 = the configured one.</param>
/// <param name="workspaceRoot">The root the file tools confine to, stated to the model; null or empty when none is
/// pinned. ⚠ Never a guessed root (the view model's <c>FindProjectRoot</c> falls back to the process's directory):
/// a root the tools refuse is worse than none.</param>
/// <param name="foldersOutOfReach">Folders open in the editor that the root does not hold — the other folders of a
/// multi-root VS Code workspace. Stated with the root: unnamed, the model looks for their code under the root, finds
/// nothing and concludes it does not exist.</param>
internal sealed class SystemPromptBuilder(InferpalConfig config, string? editorName = null, int contextWindow = 0,
                                          string? workspaceRoot = null, IReadOnlyList<string>? foldersOutOfReach = null)
{
    /// <summary>
    /// The editor and the shell, stated from what this process can actually observe — appended to
    /// the base layer so no new <c>/xray</c> section appears.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠ The base prompt is one text shared by both front-ends, so it can assert
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
    /// <para>
    /// ⚠ The workspace root is a fact of the same kind. Without it, a workspace with no .NET solution gives the
    /// model no absolute path anywhere — the solution block is the only other place one appears — and a model
    /// that wants one invents it (<c>/home/user/…</c>): refused as outside the workspace, then found again with
    /// <c>pwd</c>, a round or more lost on every such task.
    /// </para>
    /// </remarks>
    internal string EnvironmentFacts()
    {
        var (dialect, fileName) = Shell.ShellLauncher.Resolve();
        var shell = Shell.ShellLauncher.SpokenName(dialect, fileName);
        var editor = string.IsNullOrWhiteSpace(editorName) ? string.Empty : $"Editor: {editorName}. ";
        var root = string.IsNullOrWhiteSpace(workspaceRoot) ? string.Empty : $" The workspace root is {workspaceRoot}.";
        if (root.Length > 0 && foldersOutOfReach is { Count: > 0 })
            root += $" Other folders open in the editor are outside it, so the tools cannot read, search or edit them: "
                  + $"{string.Join(", ", foldersOutOfReach)}.";
        return $"\n\n{editor}Operating system: {RuntimeInformation.OSDescription}. "
             + $"The run_command shell is {shell}.{root}{ReplyLanguage(Strings.UiCulture)}";
    }

    /// <summary>
    /// The line that names the reply language when the interface is not in English; empty in English, where the base
    /// prompt's "respond in the same language as the user" is the whole rule.
    /// </summary>
    /// <remarks>
    /// ⚠ The model-facing prompts are English only (<see cref="ModelPrompts"/>): the interface language reaches the
    /// model through this line, never through a translated prompt. The user writing in another language still wins.
    /// </remarks>
    internal static string ReplyLanguage(System.Globalization.CultureInfo culture)
    {
        var language = culture.TwoLetterISOLanguageName;
        if (string.IsNullOrEmpty(language) || language is "en" or "iv") return string.Empty;   // "iv": invariant culture
        var name = System.Globalization.CultureInfo.GetCultureInfo(language).EnglishName;
        return $" The user's interface language is {name}: reply in {name} unless the user writes in another language.";
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
    /// <param name="basePrompt">Base system prompt (<c>ModelPrompts.SystemPrompt</c>, English; the reply language is
    /// stated by <see cref="EnvironmentFacts"/>).</param>
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
    /// <param name="activeFilePath">The active file, absolute — what scopes the repository's instructions (the
    /// AGENTS.md down to its folder, the rules its globs name). ⚠ Not <paramref name="activeFileRelPath"/>: a file
    /// outside the root (a test project beside Visual Studio's solution folder) has none, and its closest AGENTS.md
    /// would be skipped.</param>
    public string Build(
        string  basePrompt,
        string? language          = null,
        string? templateSuffix    = null,
        string? projectRoot       = null,
        string? activeFileRelPath = null,
        IReadOnlySet<string>? disabledSectionIds = null,
        string? activeFilePath    = null)
        => string.Concat(BuildSections(basePrompt, language, templateSuffix, projectRoot, activeFileRelPath, disabledSectionIds,
                                       activeFilePath)
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
        IReadOnlySet<string>? disabledSectionIds = null,
        string? activeFilePath    = null)
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
                var pinnedContent = Tools.TextFileEncoding.ReadText(pinnedPath).Trim();
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

        // The repository's own instructions to coding agents — after the pins, before .inferpal/, which wins a conflict.
        AddRepoInstructions(files, RepoInstructions(projectRoot, activeFileRelPath, activeFilePath), disabledSectionIds);
        AddSkillsCatalog(files, string.IsNullOrEmpty(workspaceRoot) ? projectRoot : workspaceRoot);

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

        // ⚠ The window the prompt is SENT into: configured 100 000 with a model loaded at 8 192, a budget read from the
        // setting kept every request over the loaded window — the files are the one part compaction never shrinks.
        sections.AddRange(ShareBudget(files, FileSectionsBudget(contextWindow > 0 ? contextWindow : config.ContextWindowSize),
                                      disabledSectionIds));
        return sections;
    }

    /// <summary>The repository's instructions for the question these inputs describe — the inputs <see cref="BuildSections"/>
    /// takes, so a screen built from it shows what the prompt sends.</summary>
    public RepoInstructionPlan? RepoInstructions(string? projectRoot, string? activeFileRelPath = null, string? activeFilePath = null) =>
        PlanRepoInstructions(config, string.IsNullOrEmpty(workspaceRoot) ? projectRoot : workspaceRoot,
                             activeFilePath ?? (projectRoot is not null && activeFileRelPath is not null
                                                    ? Path.Combine(projectRoot, activeFileRelPath) : null));

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
    /// system prompt first — and compaction cannot help: the system prompt is never compacted. The window is the one
    /// the front-end passes (the smaller of the configured and the loaded one, once a turn has measured it), else
    /// the configured one.
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
            yield return files[i].Section(CapSection(files[i].Body, files[i].What, allotted[i], KeepsItsEnd(files[i].Kind)));
    }

    /// <summary>
    /// A file written by APPENDING keeps its end when it is cut: <c>memory.md</c> (<c>update_memory</c> appends) and
    /// <c>notes.md</c> (<c>/note</c> appends) put their newest entries last.
    /// </summary>
    /// <remarks>⚠ Cut from the end like the others, the notes just written are the ones the next session never sees —
    /// while the tool that wrote them answered "saved". A rule, a pinned file or the project context keep their head:
    /// their structure starts there.</remarks>
    internal static bool KeepsItsEnd(PromptSectionKind kind) => kind is PromptSectionKind.Memory or PromptSectionKind.Notes;

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
    internal static string CapSection(string text, string what, int allotted, bool keepEnd = false)
    {
        if (text.Length <= allotted) return text;

        Diagnostics.RecordOnce("SystemPrompt",
            $"'{what}' is {text.Length} chars; truncated to {allotted} for the system prompt "
            + "(the prompt's files share a budget set by the context window).",
            $"{what}|{text.Length}|{allotted}");
        if (!keepEnd)
            return SafeTruncate.Truncate(text, allotted)
                 + $"\n\n[... {what} truncated to {allotted} characters out of {text.Length} "
                 + "to keep the system prompt inside the context window]";

        // The most recent part, from the start of a line: half an entry reads as a whole one.
        var start = text.Length - Math.Max(0, allotted);
        if (start < text.Length && char.IsLowSurrogate(text[start])) start++;
        var lineStart = text.IndexOf('\n', start);
        if (lineStart >= 0 && lineStart + 1 < text.Length) start = lineStart + 1;
        return $"[... the earliest {start} characters of {what} omitted to keep the system prompt inside the context "
             + $"window — its most recent {text.Length - start} follow]\n\n" + text[start..];
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
            var text = Tools.TextFileEncoding.ReadText(path).Trim();   // hand-written: may be in the legacy code page
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

    /// <summary>
    /// The repository's own instructions to coding agents that this question sends — AGENTS.md, Copilot's, Claude
    /// Code's, Cursor's, Cline's, Roo's, Continue's: one file layer per source, under one heading that says which wins.
    /// </summary>
    /// <remarks>
    /// <para>
    /// No budget of their own: they share the one the prompt's files share (<see cref="ShareBudget"/>), keep their head
    /// when cut, and say the cut with its numbers like every other file. A source switched off in the X-Ray hands the
    /// heading to the next one sent.
    /// </para>
    /// <para>
    /// What cannot be read, or is not taken, is said once in /diagnostics, not per build: the prompt is rebuilt on every
    /// question.
    /// </para>
    /// </remarks>
    /// <summary>
    /// The skills' catalog of the automatic mode (<c>skillsAutoMode</c>, off by default): each skill's name and
    /// description, and how to load one — the model picks the skill a request needs.
    /// </summary>
    /// <remarks>
    /// The text is the one the triggering probe measured (<c>docs/probes/skills/auto-mode.md</c>: the right skill 8/10
    /// and 10/10, none loaded wrongly in 20). It shares the budget of the prompt's files and says a cut like them: the
    /// catalog of a large repository (microsoft/vscode: 49 skills) must not overflow a small window in silence.
    /// </remarks>
    private void AddSkillsCatalog(List<FileLayer> files, string? root)
    {
        if (!config.SkillsAutoMode) return;
        var catalog = RepoSkills.Load(root, RepoInstructionFormats.Families(config.RepoInstructionFamilies).On);
        if (catalog.Skills.Count == 0) return;
        files.Add(new FileLayer(PromptSectionKind.Skills, catalog.Skills.Count.ToString(CultureInfo.InvariantCulture),
                                "\n\n", ModelPrompts.SkillsCatalog(catalog.Skills.Select(s => (s.Name, s.Description)).ToList()),
                                "the skills' catalog", Key: "skills"));
    }

    private static void AddRepoInstructions(List<FileLayer> files, RepoInstructionPlan? plan, IReadOnlySet<string>? disabledSectionIds)
    {
        if (plan is null) return;
        var headed = false;
        foreach (var sent in plan.Composed.Sent)
        {
            var relative = plan.Relative(sent.Instruction.Source.Path);
            var layer    = new FileLayer(PromptSectionKind.RepoInstructions, relative, string.Empty, sent.Text, relative,
                                         Key: sent.Instruction.Source.Path);
            var isSent   = disabledSectionIds is null
                           || !disabledSectionIds.Contains(Presentation.XRayPanelPresenter.SectionId(layer.Section("")));
            var heading  = isSent && !headed
                ? "\n\n" + ModelPrompts.RepoInstructionsHeading + "\n\n" + ModelPrompts.RepoInstructionsIntro
                : string.Empty;
            headed |= isSent;
            files.Add(layer with { Header = heading + "\n\n### " + relative + "\n\n" });
        }
    }

    /// <summary>
    /// What the repository's instructions are for a question asked from <paramref name="root"/> with
    /// <paramref name="activeFile"/> open: found, read, composed — the families the setting turns off kept apart.
    /// <c>null</c> without a root. ⚠ The ONE path from the files to the prompt: the Context page and
    /// <c>/instructions</c> read this, so they show what the prompt sends.
    /// </summary>
    /// <remarks>What cannot be read, or is not taken, is said once in /diagnostics, not per build: the prompt is rebuilt
    /// on every question.</remarks>
    internal static RepoInstructionPlan? PlanRepoInstructions(InferpalConfig config, string? root, string? activeFile)
    {
        if (string.IsNullOrEmpty(root)) return null;
        try
        {
            var discovery = RepoInstructionDiscovery.Discover(root, activeFile);
            if (discovery.SearchRoot is not { } searchRoot) return null;
            foreach (var unseen in discovery.Unseen)
                Diagnostics.RecordOnce(RepoInstructionsContext, Describe(unseen), $"{unseen.Reason}|{PinKey(unseen.Path)}");

            var (families, unknown) = RepoInstructionFormats.Families(config.RepoInstructionFamilies);
            foreach (var name in unknown)
                Diagnostics.DroppedLineOnce(RepoInstructionsContext,
                    "Unknown family in repoInstructionFamilies, ignored (agents, copilot, claude, cursor, cline, roo, continue)",
                    "family:" + name.ToLowerInvariant(), name);

            var off  = discovery.Sources.Where(s => !families.Contains(s.Format.Family)).ToList();
            var read = discovery.Sources.Where(s => families.Contains(s.Format.Family))
                                        .Select(s => RepoInstructionReader.Read(s, searchRoot)).ToList();
            foreach (var instruction in read)
            {
                foreach (var note in instruction.Notes)
                    Diagnostics.RecordOnce(RepoInstructionsContext, Describe(instruction, note),
                                           $"{note.Kind}|{PinKey(note.Subject)}");
                // Read whole now: the next time it cannot be, it is said again — the condition is the file's content.
                if (instruction.Body.Length > 0)
                    foreach (var kind in new[] { RepoInstructionNoteKind.Unreadable, RepoInstructionNoteKind.Binary,
                                                 RepoInstructionNoteKind.TooLarge })
                        Diagnostics.Forget(RepoInstructionsContext, $"{kind}|{PinKey(instruction.Source.Path)}");
            }
            return new RepoInstructionPlan(discovery, read, off,
                                           RepoInstructionComposition.Compose(read, RelativeActivePath(searchRoot, activeFile)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Diagnostics.Swallow("SystemPromptBuilder.RepoInstructions", ex);
            return null;
        }
    }

    private const string RepoInstructionsContext = "RepoInstructions";

    private static string Describe(RepoInstructionUnseen unseen) => unseen.Reason switch
    {
        RepoInstructionUnseenReason.LinkLeavesTheRepository =>
            $"Repository instruction file not read: it is a link whose target lies outside the repository ({unseen.Path}).",
        RepoInstructionUnseenReason.Capped =>
            $"{unseen.Count} more repository instruction files in {unseen.Path} were not read "
            + $"(only the first {RepoInstructionDiscovery.MaxFilesPerFormat} are).",
        RepoInstructionUnseenReason.LinkNotFollowed =>
            $"Repository instruction folder not read: it is a link, not followed ({unseen.Path}).",
        _ => $"Repository instruction folder could not be listed, so its files are not read ({unseen.Path}).",
    };

    private static string Describe(RepoInstruction instruction, RepoInstructionNote note) => note.Kind switch
    {
        RepoInstructionNoteKind.Unreadable => $"Repository instruction file could not be read, so it is NOT sent: {note.Subject}",
        RepoInstructionNoteKind.Binary     => $"Repository instruction file is not text, so it is not sent: {note.Subject}",
        RepoInstructionNoteKind.TooLarge   =>
            $"Repository instruction file is larger than {RepoInstructionReader.MaxFileBytes / 1024} KB, so it is not sent: {note.Subject}",
        RepoInstructionNoteKind.ImportOutsideTheRepository =>
            $"{instruction.Source.Path} imports a file outside the repository, which is not read: {note.Subject}",
        RepoInstructionNoteKind.ImportTooDeep =>
            $"{instruction.Source.Path} imports past {RepoInstructionReader.MaxImportHops} levels; not read: {note.Subject}",
        RepoInstructionNoteKind.KeyNotApplied =>
            $"'{note.Subject}' in {instruction.Source.Path} is not applied by Inferpal; the rule is scoped by its other keys.",
        _ => $"{instruction.Source.Path} scopes itself to no file ({note.Subject}: []), so it is never sent.",
    };
}
