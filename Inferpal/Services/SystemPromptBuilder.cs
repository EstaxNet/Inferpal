using System.IO;
using System.Text;
using Inferpal.Config;

namespace Inferpal.Services;

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
    public string Build(
        string  basePrompt,
        string? language          = null,
        string? templateSuffix    = null,
        string? projectRoot       = null,
        string? activeFileRelPath = null)
    {
        var sb = new StringBuilder(basePrompt);

        if (config.PersonaAutoSwitch && !string.IsNullOrEmpty(language))
        {
            var snippet = PersonaSnippetFor(language);
            if (!string.IsNullOrEmpty(snippet))
                sb.Append("\n\n").Append(snippet);
        }

        var custom = config.CustomSystemPrompt?.Trim();
        if (!string.IsNullOrEmpty(custom))
            sb.Append("\n\n").Append(custom);

        if (!string.IsNullOrEmpty(templateSuffix))
            sb.Append(templateSuffix);

        foreach (var pinnedPath in PinnedFilesPolicy.ParseActive(config.PinnedContextFiles))
        {
            if (!File.Exists(pinnedPath)) continue;
            try
            {
                var pinnedContent = File.ReadAllText(pinnedPath, Encoding.UTF8).Trim();
                if (!string.IsNullOrEmpty(pinnedContent))
                    sb.Append("\n\n## Pinned: ").Append(Path.GetFileName(pinnedPath)).Append("\n\n").Append(pinnedContent);
            }
            catch (Exception ex) { Diagnostics.Swallow($"SystemPromptBuilder.PinnedFile({Path.GetFileName(pinnedPath)})", ex); }
        }

        if (projectRoot is not null)
        {
            AppendFileSection(sb, Path.Combine(projectRoot, ".inferpal", "context.md"), "Project context");
            AppendFileSection(sb, Path.Combine(projectRoot, ".inferpal", "memory.md"),  "Agent memory");
            AppendFileSection(sb, NotesStore.NotesPath(projectRoot),                       "Project notes");

            // Project rules (.inferpal/rules/*.md) — scoped by glob against the active file.
            try
            {
                var rules = RulesService.Load(Path.Combine(projectRoot, ".inferpal", "rules"));
                if (rules.Count > 0)
                {
                    var matched = rules.Where(r => RulesService.Matches(r, activeFileRelPath)).ToList();
                    if (matched.Count > 0)
                        sb.Append(RulesService.Render(matched));
                }
            }
            catch (Exception ex) { Diagnostics.Swallow("SystemPromptBuilder.Rules", ex); }
        }

        return sb.ToString();
    }

    /// <summary>Appends a <c>## header</c> section with the file's content; missing/empty/unreadable file ⇒ no-op.</summary>
    private static void AppendFileSection(StringBuilder sb, string path, string header)
    {
        if (!File.Exists(path)) return;
        try
        {
            var text = File.ReadAllText(path, Encoding.UTF8).Trim();
            if (!string.IsNullOrEmpty(text))
                sb.Append("\n\n## ").Append(header).Append("\n\n").Append(text);
        }
        catch (Exception ex) { Diagnostics.Swallow($"SystemPromptBuilder.FileSection({header})", ex); }
    }
}
