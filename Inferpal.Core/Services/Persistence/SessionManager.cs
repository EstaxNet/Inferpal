using System.Text;
using System.Text.RegularExpressions;
using Inferpal.Localization;
using Inferpal.Models;

namespace Inferpal.Services.Persistence;

/// <summary>Preset chat mode applied via <c>/template</c>: system-prompt suffix + greeting.</summary>
internal sealed record SessionTemplate(string Id, string Label, string SystemSuffix, string Greeting);

/// <summary>
/// Pure session logic extracted from the tool-window VM: snapshot building, restored-history
/// rebuilding, title/file naming, <c>/history</c> markdown rendering, and the <c>/template</c>
/// presets. Persistence stays in <see cref="ConversationStore"/>; UI state and the LLM title
/// call stay in the VM.
/// </summary>
internal static class SessionManager
{
    // ── /template presets ─────────────────────────────────────────────────────

    internal static readonly SessionTemplate[] Templates =
    [
        new("code-review",
            "Code Review",
            "\n\n## Mode: Code Review\nFocus on code quality, readability, edge cases, security vulnerabilities, and SOLID violations. Always cite line numbers. Prefer concrete suggestions over abstract advice.",
            "**Code Review mode** active. Share a file or selection and I'll analyse it for quality, security, and design issues."),

        new("bug-hunt",
            "Bug Hunt",
            "\n\n## Mode: Bug Hunt\nYour goal is to find bugs, regressions, and subtle logic errors. Think like a QA engineer: trace execution paths, challenge assumptions, look for off-by-ones and null-deref risks.",
            "**Bug Hunt mode** active. Describe the issue or share the code — I'll trace the execution and identify the root cause."),

        new("architecture",
            "Architecture",
            "\n\n## Mode: Architecture\nThink at the system level: dependencies, coupling, cohesion, scalability, and evolutionary design. Use diagrams (text-based) where helpful. Reference well-known architectural patterns.",
            "**Architecture mode** active. Describe the system or share the project map — I'll analyse the design and propose improvements."),

        new("refactoring",
            "Refactoring",
            "\n\n## Mode: Refactoring\nApply clean-code principles (DRY, SRP, YAGNI). Prefer small, safe, incremental steps. Show before/after diffs. Avoid speculative generality.",
            "**Refactoring mode** active. Share the code and I'll suggest safe, incremental improvements."),

        new("tests",
            "Test Coverage",
            "\n\n## Mode: Test Coverage\nFocus exclusively on test design: coverage gaps, boundary values, happy path and failure modes, mocking strategy, and assertion quality.",
            "**Test Coverage mode** active. Share the code under test and I'll design a comprehensive test suite."),
    ];

    /// <summary>Template lookup by id (caller lower-cases the user input).</summary>
    public static SessionTemplate? FindTemplate(string id) =>
        Templates.FirstOrDefault(t => t.Id == id);

    /// <summary>Markdown list shown by a bare <c>/template</c>.</summary>
    public static string FormatTemplateList()
    {
        var list = string.Join("\n", Templates.Select(t => $"- **{t.Id}** — {t.Label}"));
        return $"## Available templates\n\n{list}\n\nUsage: `/template <id>`";
    }

    // ── Snapshots & restore ───────────────────────────────────────────────────

    /// <summary>
    /// Maps the chat list to persistable messages: drops UI anchors, nulls out empty
    /// tool names/timestamps so they stay out of the JSON.
    /// </summary>
    public static List<SavedMessage> BuildSnapshot(
        IEnumerable<(string Role, string Content, string ToolName, string Timestamp)> messages) =>
        messages
            .Where(m => m.Role != "anchor")
            .Select(m => new SavedMessage(
                m.Role, m.Content,
                string.IsNullOrEmpty(m.ToolName)  ? null : m.ToolName,
                string.IsNullOrEmpty(m.Timestamp) ? null : m.Timestamp))
            .ToList();

    /// <summary>
    /// Rebuilds the API history for a restored session: fresh system prompt, then every
    /// conversational message. Tool results are included so the model has the full context
    /// required to continue reasoning after the restore; UI-only roles are dropped.
    /// </summary>
    public static List<ChatMessageDto> BuildRestoredHistory(
        string systemPrompt, IEnumerable<SavedMessage> messages)
    {
        var history = new List<ChatMessageDto> { new("system", systemPrompt) };
        history.AddRange(messages
            .Where(m => m.Role is "user" or "assistant" or "tool")
            .Select(m => new ChatMessageDto(m.Role, m.Content)));
        return history;
    }

    // ── Title & file naming ───────────────────────────────────────────────────

    /// <summary>System prompt for the LLM-generated session title (the call stays in the VM).</summary>
    public const string TitleSystemPrompt =
        "Summarize the following message in 4 to 5 words maximum as a short descriptive title. Reply with ONLY the title words, no punctuation, no quotes, no explanation. Use the same language as the message.";

    /// <summary>
    /// File-safe title from the raw LLM output: keeps letters/digits, collapses everything
    /// else, joins words with <c>_</c>. Falls back when nothing printable remains.
    /// </summary>
    public static string SanitizeTitle(string raw, string fallback)
    {
        var title = new string(raw.Trim()
            .Select(c => char.IsLetterOrDigit(c) || c == ' ' ? c : ' ')
            .ToArray()).Trim();
        title = Regex.Replace(title, @"\s+", "_");
        return string.IsNullOrWhiteSpace(title) ? fallback : title;
    }

    /// <summary>Fallback title: first 35 chars of the first user message, file-safe.</summary>
    public static string MakeSnippet(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return Strings.DefaultSessionSnippet;
        var s = new string(content.Take(35)
            .Select(c => char.IsLetterOrDigit(c) || c == ' ' ? c : '_')
            .ToArray()).Trim().Replace(' ', '_');
        return string.IsNullOrWhiteSpace(s) ? Strings.DefaultSessionSnippet : s;
    }

    /// <summary>Sortable file name for a named save: <c>2026-06-12_0930_My_Title</c>.</summary>
    public static string SessionFileName(DateTime localNow, string title) =>
        $"{localNow:yyyy-MM-dd_HHmm}_{title}";

    // ── /history rendering ────────────────────────────────────────────────────

    /// <summary>Compact relative age for session listings (<c>5m ago</c> … <c>2026-06-12</c>).</summary>
    public static string FormatAge(DateTime savedAtUtc, DateTime nowUtc)
    {
        var elapsed = nowUtc - savedAtUtc;
        if (elapsed.TotalMinutes < 60)  return $"{(int)elapsed.TotalMinutes}m ago";
        if (elapsed.TotalHours   < 24)  return $"{(int)elapsed.TotalHours}h ago";
        if (elapsed.TotalDays    < 30)  return $"{(int)elapsed.TotalDays}d ago";
        return savedAtUtc.ToString("yyyy-MM-dd");
    }

    /// <summary>Markdown for <c>/history &lt;term&gt;</c> search hits.</summary>
    public static string FormatHistorySearch(string term, IReadOnlyList<SessionMatch> matches, DateTime nowUtc)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## History: \"{term}\" — {matches.Count} session(s)");
        sb.AppendLine();

        foreach (var m in matches)
        {
            sb.AppendLine($"### {m.Name}  *({FormatAge(m.SavedAt, nowUtc)})*");
            foreach (var snip in m.Snippets)
                sb.AppendLine($"  > {snip}");
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>Markdown for the bare <c>/history</c> session list.</summary>
    public static string FormatHistoryList(IReadOnlyList<SessionSummary> sessions, DateTime nowUtc)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## Saved sessions ({sessions.Count})");
        sb.AppendLine();

        foreach (var s in sessions)
        {
            sb.AppendLine($"**{s.Name}**  ·  {FormatAge(s.SavedAt, nowUtc)}  ·  {s.MessageCount} messages");
            if (!string.IsNullOrWhiteSpace(s.FirstUserPreview))
                sb.AppendLine($"  *\"{s.FirstUserPreview}\"*");
            sb.AppendLine();
        }

        sb.AppendLine("---");
        sb.AppendLine("→ `/history <term>` to search in session content");

        return sb.ToString().TrimEnd();
    }
}
