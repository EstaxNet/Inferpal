using System.IO;
using System.Text;
using Inferpal.Config;

namespace Inferpal.Services.Prompting;

/// <summary>Origin of one layer of the composed system prompt (for <c>/xray</c>).</summary>
internal enum PromptSectionKind { Base, Persona, Custom, Template, Pinned, ProjectContext, Memory, Notes, Rules }

/// <summary>One layer of the composed system prompt. <see cref="Content"/> includes the layer's own
/// leading separator so concatenating all sections reproduces the exact prompt text.
/// <see cref="Detail"/> carries the file name / rule count where relevant.</summary>
internal sealed record PromptSection(PromptSectionKind Kind, string? Detail, string Content);

/// <summary>
/// Builds the layered system prompt sent with every chat/agent request:
/// base prompt → persona snippet (active language) → user custom prompt → active
/// <c>/template</c> suffix → pinned files → project files (<c>.inferpal/context.md</c>,
/// <c>memory.md</c>, <c>notes.md</c>) → glob-scoped rules.
/// Extracted from the tool-window VM so the layering is unit-testable without VS.
/// </summary>
internal sealed class SystemPromptBuilder(InferpalConfig config)
{
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
        var sections = new List<PromptSection> { new(PromptSectionKind.Base, null, basePrompt) };

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

        foreach (var pinnedPath in PinnedFilesPolicy.ParseActive(config.PinnedContextFiles))
        {
            if (!File.Exists(pinnedPath)) continue;
            try
            {
                var pinnedContent = CapSection(File.ReadAllText(pinnedPath, Encoding.UTF8).Trim(),
                                               Path.GetFileName(pinnedPath));
                if (!string.IsNullOrEmpty(pinnedContent))
                    sections.Add(new(PromptSectionKind.Pinned, Path.GetFileName(pinnedPath),
                        "\n\n## Pinned: " + Path.GetFileName(pinnedPath) + "\n\n" + pinnedContent));
            }
            catch (Exception ex) { Diagnostics.Swallow($"SystemPromptBuilder.PinnedFile({Path.GetFileName(pinnedPath)})", ex); }
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
    /// This is the failure the repository has already paid for twice, one layer down: an oversized
    /// block makes the backend truncate the request <b>from the head</b>, which is exactly where the
    /// system prompt lives — so the section that grew silently evicts the instructions it was meant
    /// to add. <c>MaxToolResultCharsInContext</c> and <c>HistoryCompaction</c> bound the other two
    /// inputs for that reason; the system prompt was the one left open.
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
    private static void AddFileSection(List<PromptSection> sections, PromptSectionKind kind, string path, string header, string detail)
    {
        if (!File.Exists(path)) return;
        try
        {
            var text = CapSection(File.ReadAllText(path, Encoding.UTF8).Trim(), detail);
            if (!string.IsNullOrEmpty(text))
                sections.Add(new(kind, detail, "\n\n## " + header + "\n\n" + text));
        }
        catch (Exception ex) { Diagnostics.Swallow($"SystemPromptBuilder.FileSection({header})", ex); }
    }
}
