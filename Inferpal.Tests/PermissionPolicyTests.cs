using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

// Pure permission engine: rule parsing (DSL + JSON overlay), allow/deny/prompt evaluation,
// first-match-wins ordering, tool scoping, and the non-bypassable hard denylist.
public class PermissionPolicyTests
{
    private static PermissionPolicy FromDsl(string dsl) =>
        new(PermissionPolicy.ParseRules(dsl));

    // ── Evaluation basics ───────────────────────────────────────────────────

    [Fact]
    public void NoRules_FallsBackToPrompt()
    {
        Assert.Equal(PermissionDecision.Prompt, PermissionPolicy.Empty.Evaluate("run_command", "dotnet build"));
    }

    [Fact]
    public void AllowRule_MatchingCommand_AutoApproves()
    {
        var policy = FromDsl(@"allow run_command ^\s*dotnet\b");
        Assert.Equal(PermissionDecision.Allow, policy.Evaluate("run_command", "dotnet test"));
    }

    [Fact]
    public void AllowRule_NonMatchingCommand_FallsBackToPrompt()
    {
        var policy = FromDsl(@"allow run_command ^\s*dotnet\b");
        Assert.Equal(PermissionDecision.Prompt, policy.Evaluate("run_command", "npm install"));
    }

    [Fact]
    public void DenyRule_Matching_Denies()
    {
        var policy = FromDsl(@"deny run_command Remove-Item");
        Assert.Equal(PermissionDecision.Deny, policy.Evaluate("run_command", "Remove-Item ./bin -Recurse"));
    }

    [Fact]
    public void Matching_IsCaseInsensitive()
    {
        var policy = FromDsl(@"allow run_command ^dotnet");
        Assert.Equal(PermissionDecision.Allow, policy.Evaluate("run_command", "DOTNET build"));
    }

    // ── Tool scoping ─────────────────────────────────────────────────────────

    [Fact]
    public void Rule_OnlyAppliesToItsTool()
    {
        var policy = FromDsl(@"allow run_command .");
        Assert.Equal(PermissionDecision.Allow,  policy.Evaluate("run_command", "anything"));
        Assert.Equal(PermissionDecision.Prompt, policy.Evaluate("write_file", "anything"));
    }

    [Fact]
    public void WildcardTool_AppliesToAnyTool()
    {
        var policy = FromDsl(@"deny * \.env$");
        Assert.Equal(PermissionDecision.Deny, policy.Evaluate("write_file", @"C:\proj\.env"));
        Assert.Equal(PermissionDecision.Deny, policy.Evaluate("delete_file", @"C:\proj\.env"));
    }

    [Fact]
    public void PathScopedAllow_MatchesAbsolutePathSubject()
    {
        var policy = FromDsl(@"allow write_file \.(cs|ts)$");
        Assert.Equal(PermissionDecision.Allow,  policy.Evaluate("write_file", @"C:\proj\src\Foo.cs"));
        Assert.Equal(PermissionDecision.Prompt, policy.Evaluate("write_file", @"C:\proj\app.csproj"));
    }

    // ── First match wins ──────────────────────────────────────────────────────

    [Fact]
    public void FirstMatchWins_DenyBeforeAllow()
    {
        var policy = FromDsl("deny run_command secret\nallow run_command .");
        Assert.Equal(PermissionDecision.Deny,  policy.Evaluate("run_command", "echo secret"));
        Assert.Equal(PermissionDecision.Allow, policy.Evaluate("run_command", "echo hello"));
    }

    [Fact]
    public void FirstMatchWins_AllowBeforeDeny()
    {
        var policy = FromDsl("allow run_command ^git status\ndeny run_command ^git");
        Assert.Equal(PermissionDecision.Allow, policy.Evaluate("run_command", "git status"));
        Assert.Equal(PermissionDecision.Deny,  policy.Evaluate("run_command", "git push --force"));
    }

    // ── Hard denylist (non-bypassable) ─────────────────────────────────────────

    [Theory]
    [InlineData("rm -rf /")]
    [InlineData("rm -rf ~")]
    [InlineData("rm --no-preserve-root -rf /tmp")]
    [InlineData(@"Remove-Item -Recurse -Force C:\")]
    [InlineData(@"Remove-Item -Force -Recurse 'D:\'")]
    [InlineData("mkfs.ext4 /dev/sda1")]
    [InlineData("Format-Volume -DriveLetter D")]
    [InlineData("format C:")]
    [InlineData("dd if=/dev/zero of=/dev/sda")]
    public void HardDenylist_BlocksCatastrophicCommands(string cmd)
    {
        Assert.True(PermissionPolicy.IsHardDenied(cmd));
        // Even an explicit allow rule cannot override the hard denylist.
        var policy = FromDsl(@"allow run_command .");
        Assert.Equal(PermissionDecision.Deny, policy.Evaluate("run_command", cmd));
    }

    [Theory]
    [InlineData("dotnet build")]
    [InlineData("rm -rf ./bin")]
    [InlineData("rm -rf node_modules")]
    [InlineData(@"Remove-Item -Recurse -Force .\obj")]
    [InlineData("git status")]
    public void HardDenylist_DoesNotBlockOrdinaryDevCommands(string cmd)
    {
        Assert.False(PermissionPolicy.IsHardDenied(cmd));
    }

    // ── Parsing robustness ──────────────────────────────────────────────────────

    [Fact]
    public void ParseRules_SkipsCommentsBlanksAndMalformedLines()
    {
        var rules = PermissionPolicy.ParseRules(
            "# a comment\n\nallow run_command ^dotnet\ngibberish line\nallow\nmaybe run_command x\n");
        // Only the one well-formed allow line survives ("maybe" is not allow/deny).
        Assert.Single(rules);
        Assert.Equal(PermissionDecision.Allow, rules[0].Decision);
    }

    [Fact]
    public void ParseLine_InvalidRegex_IsSkipped()
    {
        Assert.Null(PermissionPolicy.ParseLine(@"allow run_command ([unclosed"));
    }

    [Fact]
    public void ParseJsonOverlay_ReadsRulesArray()
    {
        var rules = PermissionPolicy.ParseJsonOverlay(
            """{ "rules": ["allow run_command ^dotnet", "deny * \\.env$"] }""");
        Assert.Equal(2, rules.Count);
        Assert.Equal(PermissionDecision.Allow, rules[0].Decision);
        Assert.Equal(PermissionDecision.Deny,  rules[1].Decision);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{ "rules": "not an array" }""")]
    public void ParseJsonOverlay_InvalidOrEmpty_ReturnsNoRules(string json)
    {
        Assert.Empty(PermissionPolicy.ParseJsonOverlay(json));
    }
}
