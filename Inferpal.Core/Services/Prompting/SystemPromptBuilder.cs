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
        => string.Concat(BuildSections(basePrompt, language, templateSuffix, projectRoot, activeFileRelPath)
                         .Where(s => disabledSectionIds is null
                                     || !disabledSectionIds.Contains(Presentation.XRayPanelPresenter.SectionId(s)))
                         .Select(s => s.Content));

    /// <summary>
    /// Same layering as <see cref="Build"/>, one <see cref="PromptSection"/> per contributing layer —
    /// the <c>/xray</c> token breakdown reads these. Concatenating the sections' contents in order
    /// reproduces the exact prompt (each content carries its own leading separator).
    /// </summary>
    public IReadOnlyList<PromptSection> BuildSections(
        string  basePrompt,
        string? language          = null,
        string? templateSuffix    = null,
        string? projectRoot       = null,
        string? activeFileRelPath = null)
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

        var (pinned, overCap) = PinnedFilesPolicy.ParseActiveWithOverflow(config.PinnedContextFiles);

        // ⚠ Same silence as just below, for the other cause. The cap drops paths the user wrote in
        // the settings window — which caps nothing — and that the configuration keeps: they are
        // neither in the prompt nor in the 📌 chips (which read the same capped list), and nothing
        // said so. Once per path, like the missing one: this prompt is rebuilt on every
        // active-file change.
        foreach (var dropped in overCap)
            Diagnostics.DroppedLineOnce(
                "PinnedFiles", $"Pinned context file ignored (only the first {PinnedFilesPolicy.MaxPinned} are sent)",
                PinKey(dropped), dropped);

        foreach (var pinnedPath in pinned)
        {
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
                var pinnedContent = CapSection(File.ReadAllText(pinnedPath, Encoding.UTF8).Trim(),
                                               Path.GetFileName(pinnedPath));
                // The read goes through again: a later failure will say so again.
                Diagnostics.ForgetDroppedLine(PinContext, UnreadableKey(pinnedPath));
                if (!string.IsNullOrEmpty(pinnedContent))
                    // The label is the file name; the identity is the PATH — two pins can both be
                    // called README.md, and one switch would then turn both off.
                    sections.Add(new(PromptSectionKind.Pinned, Path.GetFileName(pinnedPath),
                        "\n\n## Pinned: " + Path.GetFileName(pinnedPath) + "\n\n" + pinnedContent,
                        Key: pinnedPath));
            }
            catch (Exception ex) { ReportUnreadablePinOnce(pinnedPath, ex); }
        }


        if (projectRoot is not null)
        {
            AddFileSection(sections, PromptSectionKind.ProjectContext, Path.Combine(projectRoot, ".inferpal", "context.md"), "Project context", ".inferpal/context.md");
            AddFileSection(sections, PromptSectionKind.Memory,         Path.Combine(projectRoot, ".inferpal", "memory.md"),  "Agent memory",    ".inferpal/memory.md");
            AddFileSection(sections, PromptSectionKind.Notes,          NotesStore.NotesPath(projectRoot),                       "Project notes",   ".inferpal/notes.md");

            // Project rules (.inferpal/rules/*.md) — scoped by glob against the active file.
            try
            {
                var rules = RulesService.Load(Path.Combine(projectRoot, ".inferpal", "rules"));
                if (rules.Count > 0)
                {
                    var matched = rules.Where(r => RulesService.Matches(r, activeFileRelPath)).ToList();
                    if (matched.Count > 0)
                        sections.Add(new(PromptSectionKind.Rules, matched.Count.ToString(),
                                         CapSection(RulesService.Render(matched), ".inferpal/rules")));
                }
            }
            catch (Exception ex) { Diagnostics.Swallow("SystemPromptBuilder.Rules", ex); }
        }

        return sections;
    }

    /// <summary>
    /// Ceiling on one file-backed prompt section (~8k tokens). Every section here comes from a
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

    /// <summary>Caps one section and says so in the prompt — a silent cut would make the model
    /// answer from half a rule without either party knowing.</summary>
    internal static string CapSection(string text, string what)
    {
        if (text.Length <= MaxFileSectionChars) return text;

        Diagnostics.Record("SystemPrompt",
            $"'{what}' is {text.Length} chars; truncated to {MaxFileSectionChars} for the system prompt.");
        return SafeTruncate.Truncate(text, MaxFileSectionChars)
             + $"\n\n[... {what} truncated to {MaxFileSectionChars} characters out of {text.Length} "
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
        Diagnostics.ForgetDroppedLine(PinContext, MissingKey(path));

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

    // ⚠ The two causes have DISJOINT keys, and that is not cosmetic: `ForgetMissingPin` runs on
    // every pass as soon as the file exists. A shared key would therefore be forgotten on every
    // rebuild, and "once" would become "every time" for the unreadable one — the defect just closed,
    // coming back by the other end.
    private static string MissingKey(string path)    => "missing:"    + PinKey(path);
    private static string UnreadableKey(string path) => "unreadable:" + PinKey(path);

    private static void AddFileSection(List<PromptSection> sections, PromptSectionKind kind, string path, string header, string detail)
    {
        if (!File.Exists(path)) return;
        try
        {
            var text = CapSection(File.ReadAllText(path, Encoding.UTF8).Trim(), detail);
            Diagnostics.ForgetDroppedLine(PromptFileContext, PinKey(path));
            if (!string.IsNullOrEmpty(text))
                sections.Add(new(kind, detail, "\n\n## " + header + "\n\n" + text));
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
