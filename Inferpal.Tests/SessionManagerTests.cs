using System;
using System.Collections.Generic;
using System.Linq;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

// Covers the pure session logic extracted from the tool-window VM: snapshot building,
// restored-history rebuilding, title/file naming, /history markdown rendering, and the
// /template presets. Persistence (ConversationStore) and the LLM title call are not tested here.
public class SessionManagerTests
{
    // ── BuildSnapshot ──────────────────────────────────────────────────────────

    [Fact]
    public void BuildSnapshot_DropsAnchors_AndPreservesOrder()
    {
        var snapshot = SessionManager.BuildSnapshot(
        [
            ("anchor",    "",       "",     ""),
            ("user",      "hello",  "",     "10:00"),
            ("assistant", "hi",     "",     "10:01"),
            ("anchor",    "",       "",     ""),
        ]);

        Assert.Equal(2, snapshot.Count);
        Assert.Equal(["user", "assistant"], snapshot.Select(m => m.Role));
        Assert.Equal(["hello", "hi"], snapshot.Select(m => m.Content));
    }

    [Fact]
    public void BuildSnapshot_NullsEmptyToolNameAndTimestamp()
    {
        var snapshot = SessionManager.BuildSnapshot(
        [
            ("tool", "result", "read_file", "10:02"),
            ("user", "hello",  "",          ""),
        ]);

        Assert.Equal("read_file", snapshot[0].ToolName);
        Assert.Equal("10:02",     snapshot[0].Timestamp);
        Assert.Null(snapshot[1].ToolName);
        Assert.Null(snapshot[1].Timestamp);
    }

    // ── BuildRestoredHistory ───────────────────────────────────────────────────

    [Fact]
    public void BuildRestoredHistory_SystemPromptFirst_ThenConversationalRoles()
    {
        var history = SessionManager.BuildRestoredHistory("SYS",
        [
            new("user",      "q"),
            new("assistant", "a"),
            new("tool",      "result", "read_file"),
        ]);

        Assert.Equal(4, history.Count);
        Assert.Equal("system", history[0].Role);
        Assert.Equal("SYS",    history[0].Content);
        Assert.Equal(["user", "assistant", "tool"], history.Skip(1).Select(m => m.Role));
    }

    [Fact]
    public void BuildRestoredHistory_DropsUiOnlyRoles()
    {
        var history = SessionManager.BuildRestoredHistory("SYS",
        [
            new("info",  "banner"),
            new("error", "boom"),
            new("user",  "q"),
        ]);

        Assert.Equal(2, history.Count);
        Assert.Equal("user", history[1].Role);
    }

    // ── Title & file naming ────────────────────────────────────────────────────

    [Theory]
    [InlineData("Fix the login bug",        "Fix_the_login_bug")]
    [InlineData("  \"Refactor: god class\"", "Refactor_god_class")]
    [InlineData("Très bonne idée !",         "Très_bonne_idée")]
    public void SanitizeTitle_StripsPunctuation_AndJoinsWithUnderscores(string raw, string expected) =>
        Assert.Equal(expected, SessionManager.SanitizeTitle(raw, "fallback"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!! ???")]
    public void SanitizeTitle_FallsBackWhenNothingPrintableRemains(string raw) =>
        Assert.Equal("fallback", SessionManager.SanitizeTitle(raw, "fallback"));

    [Fact]
    public void MakeSnippet_Takes35Chars_AndReplacesNonAlphanumerics()
    {
        var snippet = SessionManager.MakeSnippet("Fix the login bug: NPE in AuthService.ValidateToken");
        Assert.Equal("Fix_the_login_bug__NPE_in_AuthServi", snippet);
    }

    [Fact]
    public void MakeSnippet_EmptyContent_UsesLocalizedDefault()
    {
        var snippet = SessionManager.MakeSnippet("");
        Assert.False(string.IsNullOrWhiteSpace(snippet));
    }

    [Fact]
    public void SessionFileName_IsSortableTimestampPlusTitle()
    {
        var now = new DateTime(2026, 6, 12, 9, 5, 0);
        Assert.Equal("2026-06-12_0905_My_Title", SessionManager.SessionFileName(now, "My_Title"));
    }

    // ── FormatAge ──────────────────────────────────────────────────────────────

    private static readonly DateTime Now = new(2026, 6, 12, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(5,      "5m ago")]
    [InlineData(59,     "59m ago")]
    [InlineData(60,     "1h ago")]
    [InlineData(60*23,  "23h ago")]
    [InlineData(60*24,  "1d ago")]
    [InlineData(60*24*29, "29d ago")]
    public void FormatAge_RelativeBuckets(int minutesAgo, string expected) =>
        Assert.Equal(expected, SessionManager.FormatAge(Now.AddMinutes(-minutesAgo), Now));

    [Fact]
    public void FormatAge_Over30Days_FallsBackToDate() =>
        Assert.Equal("2026-05-01", SessionManager.FormatAge(new DateTime(2026, 5, 1, 8, 0, 0, DateTimeKind.Utc), Now));

    // ── /history rendering ─────────────────────────────────────────────────────

    [Fact]
    public void FormatHistorySearch_RendersHeaderSessionsAndSnippets()
    {
        var md = SessionManager.FormatHistorySearch("vulkan",
            [new SessionMatch("2026-06-10_0900_Ollama_setup", Now.AddHours(-2), ["…forced the vulkan backend…"])],
            Now);

        Assert.Contains("## History: \"vulkan\" — 1 session(s)", md);
        Assert.Contains("### 2026-06-10_0900_Ollama_setup  *(2h ago)*", md);
        Assert.Contains("  > …forced the vulkan backend…", md);
    }

    [Fact]
    public void FormatHistoryList_RendersCountPreviewsAndSearchHint()
    {
        var md = SessionManager.FormatHistoryList(
        [
            new SessionSummary("2026-06-11_1500_Fix_bug", Now.AddDays(-1), 12, "fix the login bug"),
            new SessionSummary("2026-06-10_0900_No_preview", Now.AddDays(-2), 3, ""),
        ], Now);

        Assert.Contains("## Saved sessions (2)", md);
        Assert.Contains("**2026-06-11_1500_Fix_bug**  ·  1d ago  ·  12 messages", md);
        Assert.Contains("  *\"fix the login bug\"*", md);
        Assert.Contains("`/history <term>`", md);
    }

    // ── /template presets ──────────────────────────────────────────────────────

    [Fact]
    public void Templates_HaveUniqueIds_AndNonEmptyParts()
    {
        Assert.Equal(SessionManager.Templates.Length,
                     SessionManager.Templates.Select(t => t.Id).Distinct().Count());
        Assert.All(SessionManager.Templates, t =>
        {
            Assert.False(string.IsNullOrWhiteSpace(t.Label));
            Assert.StartsWith("\n\n## Mode:", t.SystemSuffix);
            Assert.False(string.IsNullOrWhiteSpace(t.Greeting));
        });
    }

    [Fact]
    public void FindTemplate_KnownId_ReturnsIt_UnknownReturnsNull()
    {
        Assert.NotNull(SessionManager.FindTemplate("bug-hunt"));
        Assert.Null(SessionManager.FindTemplate("nope"));
    }

    [Fact]
    public void FormatTemplateList_ListsEveryTemplateIdWithUsage()
    {
        var md = SessionManager.FormatTemplateList();
        Assert.All(SessionManager.Templates, t => Assert.Contains($"- **{t.Id}** — {t.Label}", md));
        Assert.Contains("Usage: `/template <id>`", md);
    }
}
