using Inferpal.Services;
using Inferpal.Services.Mcp;
using Inferpal.Services.Presentation;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The two structured editors of the Tools page, shared by both settings windows: the MCP server cards and the
/// approval rules table. Each says what the plain text editor could not: why a server does not run, and which
/// written rule takes no part in the decision.
/// </summary>
public class SettingsStructuredEditorsTests
{
    private static McpServerConfig Stdio(string name, bool enabled = true, params string[] args) =>
        new(name, "npx", args, new Dictionary<string, string>(), Enabled: enabled);

    private static McpServerConfig Http(string name) =>
        new(name, null, [], new Dictionary<string, string>(), Url: "https://example.test/mcp");

    // ── MCP server cards ────────────────────────────────────────────────────

    [Fact]
    public void EachServer_GetsTheStateItsStatusReports_AndAFailureCarriesItsCause()
    {
        var cards = McpServerCards.Build(
            [Stdio("github", true, "-y", "@modelcontextprotocol/server-github"), Http("tickets"), Stdio("database"), Stdio("idle"), Stdio("off", enabled: false)],
            [
                new McpServerStatus("github", Connected: true, ToolCount: 26, Error: null),
                new McpServerStatus("tickets", Connected: false, ToolCount: 0, Error: null, AuthRequired: true),
                new McpServerStatus("database", Connected: false, ToolCount: 0, Error: "exited with code 1 — DATABASE_URL is not set"),
            ]);

        Assert.Equal(["github", "tickets", "database", "idle", "off"], cards.Select(c => c.Name));
        Assert.Equal(
            [McpCardState.Connected, McpCardState.NeedsSignIn, McpCardState.Failed, McpCardState.NotStarted, McpCardState.Off],
            cards.Select(c => c.State));

        Assert.Equal(("stdio", "npx -y @modelcontextprotocol/server-github"), (cards[0].Transport, cards[0].Target));
        Assert.Contains("26", cards[0].StatusText);
        Assert.Equal(("HTTP", "https://example.test/mcp"), (cards[1].Transport, cards[1].Target));
        Assert.Equal("exited with code 1 — DATABASE_URL is not set", cards[2].Cause);
        Assert.Null(cards[0].Cause);                       // reference arm: a working server says nothing more
    }

    [Fact]
    public void AnEntryRejectedBeforeItBecameAServer_StillGetsACard()
    {
        // A misspelt key ("cmd") never becomes a config: its only trace is the status the service keeps.
        var cards = McpServerCards.Build([], [new McpServerStatus("typo", false, 0, "it declares neither a \"command\" (stdio) nor a \"url\" (HTTP)")]);

        var card = Assert.Single(cards);
        Assert.Equal(McpCardState.Failed, card.State);
        Assert.Contains("neither", card.Cause);
    }

    [Fact]
    public void AnArgumentWithASpace_IsQuoted_SoTheLineReadsAsTyped()
    {
        var card = Assert.Single(McpServerCards.Build([Stdio("fs", true, "-y", @"C:\My Projects")], []));
        Assert.Equal("npx -y \"C:\\My Projects\"", card.Target);
    }

    [Fact]
    public void TheSummary_CountsWhatNeedsAttention_OnlyWhenSomethingDoes()
    {
        var healthy = McpServerCards.Build([Stdio("a"), Stdio("b")],
            [new McpServerStatus("a", true, 1, null), new McpServerStatus("b", true, 2, null)]);
        var broken = McpServerCards.Build([Stdio("a"), Stdio("b")],
            [new McpServerStatus("a", true, 1, null), new McpServerStatus("b", false, 0, "boom")]);

        Assert.Equal(Inferpal.Localization.Strings.McpCardsSummary(2), McpServerCards.Summary(healthy));
        Assert.Equal(Inferpal.Localization.Strings.McpCardsSummaryAttention(2, 1), McpServerCards.Summary(broken));
    }

    // ── Approval rules table ────────────────────────────────────────────────

    private const string Overlay = """{ "rules": ["deny run_command git push", "allow read_file .*", "deny * ("] }""";

    [Fact]
    public void TheTable_ListsEveryWrittenRule_InEvaluationOrder_AndSaysWhichOnesDoNotApply()
    {
        var machine = "allow run_command ^dotnet (build|test)\\b\n\n# a comment\ndeny write_file *.env";
        var table = ApprovalRuleTable.Build(Overlay, machine);

        Assert.False(table.TeamUnusable);
        Assert.Equal(
            [RuleSource.Team, RuleSource.Team, RuleSource.Team, RuleSource.Machine, RuleSource.Machine],
            table.Rows.Select(r => r.Source));
        Assert.Equal(
            [RuleStatus.InForce, RuleStatus.IgnoredAllow, RuleStatus.Unreadable, RuleStatus.InForce, RuleStatus.Unreadable],
            table.Rows.Select(r => r.Status));

        // The machine rows point at their line, so the editor deletes the right one; team rows are not editable.
        Assert.Equal([-1, -1, -1, 0, 3], table.Rows.Select(r => r.MachineLine));
        Assert.Equal("^dotnet (build|test)\\b", table.Rows[3].Pattern);
        Assert.Equal("deny write_file *.env", table.Rows[4].Pattern); // the unreadable line is shown whole, as written
        Assert.NotNull(table.Rows[1].NoteText);
        Assert.Null(table.Rows[0].NoteText);                         // reference arm: a rule in force says nothing
    }

    [Fact]
    public void ATeamFileThatIsNotJson_IsSaid_NotShownAsEmpty()
    {
        Assert.True(ApprovalRuleTable.Build("{ rules: [", null).TeamUnusable);
        Assert.True(ApprovalRuleTable.Build("{ \"deny\": [] }", null).TeamUnusable);
        Assert.False(ApprovalRuleTable.Build(null, null).TeamUnusable);           // no file is not an unreadable file
        Assert.False(ApprovalRuleTable.Build("{ \"rules\": [] }", null).TeamUnusable);
    }

    [Fact]
    public void TheTable_WritesNothingToDiagnostics_ItIsRedrawnOnEveryEdit()
    {
        var marker = "deny * (" + Guid.NewGuid().ToString("N");
        var before = Diagnostics.Snapshot().Count(e => e.Detail.Contains(marker, StringComparison.Ordinal));

        ApprovalRuleTable.Build($$"""{ "rules": ["{{marker}}", "allow x y"] }""", marker);

        Assert.Equal(before, Diagnostics.Snapshot().Count(e => e.Detail.Contains(marker, StringComparison.Ordinal)));
        // Witness: the reading that DOES record still records the same line.
        Services.Execution.PermissionPolicy.ReadOverlay($$"""{ "rules": ["{{marker}}"] }""");
        Assert.Contains(Diagnostics.Snapshot(), e => e.Detail.Contains(marker, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true, "run_command", "^dotnet test", "allow run_command ^dotnet test")]
    [InlineData(false, "", "\\.env$", "deny * \\.env$")]
    [InlineData(false, "write_file", "(", null)]
    [InlineData(true, "run_command", "", null)]
    public void ANewRule_IsWrittenOnlyIfItWouldBeRead(bool allow, string tool, string pattern, string? expected) =>
        Assert.Equal(expected, ApprovalRuleTable.NewRuleLine(allow, tool, pattern));
}
