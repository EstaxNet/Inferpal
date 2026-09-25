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
    /// <summary>
    /// The <c>toolName</c> a saved <c>assistant</c> message carries when it is a NOTICE — shown in the thread, never
    /// an answer the model gave: an end notice, a slash command's output, a failed save, a lost connection. Live, a
    /// turn keeps one answer; restored, a notice came back as another one. Carried in <c>toolName</c> because both
    /// editors render by role: a notice stays an assistant bubble on screen.
    /// </summary>
    public const string NoticeMarker = "notice";

    // ── /template presets ─────────────────────────────────────────────────────

    // A property, not a field: labels and greetings are read in the interface language of the moment.
    internal static SessionTemplate[] Templates =>
    [
        new("code-review",
            Strings.TemplateLabelCodeReview,
            "\n\n## Mode: Code Review\nFocus on code quality, readability, edge cases, security vulnerabilities, and SOLID violations. Always cite line numbers. Prefer concrete suggestions over abstract advice.",
            Strings.TemplateGreetingCodeReview),

        new("bug-hunt",
            Strings.TemplateLabelBugHunt,
            "\n\n## Mode: Bug Hunt\nYour goal is to find bugs, regressions, and subtle logic errors. Think like a QA engineer: trace execution paths, challenge assumptions, look for off-by-ones and null-deref risks.",
            Strings.TemplateGreetingBugHunt),

        new("architecture",
            Strings.TemplateLabelArchitecture,
            "\n\n## Mode: Architecture\nThink at the system level: dependencies, coupling, cohesion, scalability, and evolutionary design. Use diagrams (text-based) where helpful. Reference well-known architectural patterns.",
            Strings.TemplateGreetingArchitecture),

        new("refactoring",
            Strings.TemplateLabelRefactoring,
            "\n\n## Mode: Refactoring\nApply clean-code principles (DRY, SRP, YAGNI). Prefer small, safe, incremental steps. Show before/after diffs. Avoid speculative generality.",
            Strings.TemplateGreetingRefactoring),

        new("tests",
            Strings.TemplateLabelTests,
            "\n\n## Mode: Test Coverage\nFocus exclusively on test design: coverage gaps, boundary values, happy path and failure modes, mocking strategy, and assertion quality.",
            Strings.TemplateGreetingTests),
    ];

    /// <summary>Template lookup by id (caller lower-cases the user input).</summary>
    public static SessionTemplate? FindTemplate(string id) =>
        Templates.FirstOrDefault(t => t.Id == id);

    /// <summary>Markdown list shown by a bare <c>/template</c>.</summary>
    public static string FormatTemplateList()
    {
        var list = string.Join("\n", Templates.Select(t => $"- **{t.Id}** — {t.Label}"));
        return $"{Strings.TemplateListHeader}\n\n{list}\n\n{Strings.SlashUsage("/template <id>")}";
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
    /// Rebuilds the API history for a restored session: fresh system prompt, then the questions and the answers —
    /// the history the model had LIVE. Tool bubbles and UI-only roles stay on screen and out of it.
    /// </summary>
    /// <remarks>
    /// ⚠ Live, both front-ends keep a durable history of the question and the one answer shown: a run's tool results
    /// do not outlive the run (they bloat every following prompt, and a model shown its previous transcript replays
    /// its previous answer). The restore folded every saved tool output back in, so a reloaded conversation came back
    /// larger than anything the model had — measured on real sessions, 1.9× to 8.9× the live history, 31 359
    /// characters against 16 269 for the one Visual Studio reloads each time it opens — and compaction, which keeps
    /// the last turns WHOLE, kept their tool outputs too. The restored history is now the live one.
    /// ⚠ No tool result means no orphaned one either (<see cref="Agent.ToolBlockBoundary"/>): a saved transcript has no
    /// <c>tool_calls</c>, and a <c>tool</c> message without them is refused by every OpenAI-compatible server.
    /// </remarks>
    public static List<ChatMessageDto> BuildRestoredHistory(
        string systemPrompt, IEnumerable<SavedMessage> messages)
    {
        var history = new List<ChatMessageDto> { new("system", systemPrompt) };
        foreach (var m in messages)
        {
            if (m.Role == "user")
                history.Add(new ChatMessageDto(m.Role, m.Content));
            // A notice is an assistant bubble on screen, never an answer: live, a turn keeps one (see NoticeMarker).
            else if (m.Role == "assistant" && m.ToolName != NoticeMarker)
            {
                // The rule of the live history: a streamed bubble is saved with the model's inline reasoning, and
                // restored as is it handed the model its old chain of thought back.
                var answer = ChatTurnPolicy.ChoosePersistedAnswer(m.Content, null);
                if (answer.Length > 0)
                    history.Add(new ChatMessageDto("assistant", answer));
            }
            // Tool bubbles (and every UI-only role) are not part of the live history: see the remarks.
        }
        return history;
    }

    /// <summary>
    /// Whether the auto-save slot may be restored into the workspace open now.
    /// </summary>
    /// <remarks>
    /// <c>last_session</c> is ONE file under <c>%AppData%</c>, shared by both editors and every
    /// project: restored blindly, it brings another project's conversation — its transcript, and the
    /// tool results rebuilt into the model's history — into this one. It is refused only when both
    /// roots are known and differ: a file saved before the root was recorded, or a front-end that
    /// does not know its root yet, keeps the continuity it had.
    /// </remarks>
    public static bool AutoSaveBelongsHere(SessionData saved, string? currentRoot)
    {
        if (string.IsNullOrWhiteSpace(saved.WorkspaceRoot) || string.IsNullOrWhiteSpace(currentRoot))
            return true;

        return string.Equals(NormalizeRoot(saved.WorkspaceRoot), NormalizeRoot(currentRoot),
            PathComparer.Comparison);
    }

    private static string NormalizeRoot(string root)
    {
        try { root = System.IO.Path.GetFullPath(root); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.IO.PathTooLongException)
        {
            // An unreadable path is compared as written: it can only differ from a real root.
        }
        return root.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
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
        title = Regex.Replace(title, @"\s+", "_", RegexOptions.None, RegexBudget.Default);
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

    /// <summary>
    /// <paramref name="baseName"/> when no existing session carries it, otherwise the first free
    /// <c>_2</c>, <c>_3</c>… suffix.
    /// </summary>
    /// <remarks>
    /// For names the product CREATES — an archive on <c>/clear</c>, the parent <c>/branch</c> writes,
    /// the name <c>session/title</c> suggests. <see cref="SessionFileName"/> is minute-precise and the
    /// title comes from the first message, so the same question cleared and asked again within the
    /// minute produced the same name, and the save overwrote the first conversation. Re-saving an
    /// existing session is not a creation and keeps its name. Case-insensitive, like the file systems
    /// the names land on.
    /// </remarks>
    public static string UniqueSessionName(string baseName, IEnumerable<string> existingNames)
    {
        var taken = new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(baseName)) return baseName;

        for (var n = 2; ; n++)
        {
            var candidate = $"{baseName}_{n}";
            if (!taken.Contains(candidate)) return candidate;
        }
    }

    // ── /history rendering ────────────────────────────────────────────────────

    /// <summary>Compact relative age for session listings (<c>5m ago</c> … <c>2026-06-12</c>).</summary>
    public static string FormatAge(DateTime savedAtUtc, DateTime nowUtc)
    {
        // Doctrine: instants come from JSON (Kind depending on the file's Z suffix) as
        // much as from DateTime.UtcNow - normalize here rather than demand discipline from the
        // callers: a Local Kind shifted the age by the time zone, down to negative "-2h ago".
        if (savedAtUtc.Kind == DateTimeKind.Local) savedAtUtc = savedAtUtc.ToUniversalTime();
        if (nowUtc.Kind     == DateTimeKind.Local) nowUtc     = nowUtc.ToUniversalTime();

        var elapsed = nowUtc - savedAtUtc;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero; // clock/time zone - never negative
        if (elapsed.TotalMinutes < 60)  return Strings.AgeMinutesAgo((int)elapsed.TotalMinutes);
        if (elapsed.TotalHours   < 24)  return Strings.AgeHoursAgo((int)elapsed.TotalHours);
        if (elapsed.TotalDays    < 30)  return Strings.AgeDaysAgo((int)elapsed.TotalDays);
        return savedAtUtc.ToString("yyyy-MM-dd");
    }

    /// <summary>Markdown for <c>/history &lt;term&gt;</c> search hits.</summary>
    public static string FormatHistorySearch(string term, IReadOnlyList<SessionMatch> matches, DateTime nowUtc)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Strings.HistorySearchHeader(term, matches.Count));
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
        sb.AppendLine(Strings.HistoryListHeader(sessions.Count));
        sb.AppendLine();

        foreach (var s in sessions)
        {
            sb.AppendLine($"**{s.Name}**  ·  {FormatAge(s.SavedAt, nowUtc)}  ·  {Strings.HistoryMessageCount(s.MessageCount)}");
            if (!string.IsNullOrWhiteSpace(s.FirstUserPreview))
                sb.AppendLine($"  *\"{s.FirstUserPreview}\"*");
            sb.AppendLine();
        }

        sb.AppendLine("---");
        sb.AppendLine(Strings.HistorySearchHint);

        return sb.ToString().TrimEnd();
    }
}
