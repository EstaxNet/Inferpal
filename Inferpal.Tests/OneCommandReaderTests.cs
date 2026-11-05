using System.IO;
using System.Text.Json;
using Inferpal.Services.Execution;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ One reader of a command line (<c>CommandLine</c>), for every control that reads one — the rules, the package check, the
/// rule a card proposes, the person's own shell tools. Two readers that split the same line differently let a command
/// through the one that does not see it; and a refusal judges every reading of the line, never one.
/// </summary>
public sealed class OneCommandReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "inferpal-tests", "one-reader-" + Guid.NewGuid().ToString("N")[..8]);

    public OneCommandReaderTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "package.json"), "{ \"name\": \"app\", \"version\": \"1.0.0\" }");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* cleanup */ }
    }

    private static JsonElement Args(object o) => JsonSerializer.SerializeToElement(o);

    private static PermissionPolicy Rules(string text) => new(PermissionPolicy.ParseRules(text));

    /// <summary>⚠ A PowerShell hashtable's value is a statement: <c>git status @{a=Remove-Item src}</c> runs the removal, and
    /// a rule allowing <c>git status</c> must not approve it.</summary>
    [Theory]
    [InlineData("git status @{a=ni MARK}")]
    [InlineData("git status { ni MARK }")]
    public void AStatementInBraces_IsNotApprovedByAPrefixRule(string line)
    {
        var rules = Rules("allow run_command ^git status");
        Assert.Equal(PermissionDecision.Allow, rules.Evaluate("run_command", "git status"));   // reference arm
        Assert.NotEqual(PermissionDecision.Allow, rules.Evaluate("run_command", line));
    }

    /// <summary>A brace is not plain under PowerShell even when every command inside is allowed; under a POSIX shell it runs
    /// nothing (<c>HEAD@{1}</c>, <c>@{u}</c>), and <c>${env:X}</c> is a variable.</summary>
    [Fact]
    public void ABrace_HidesACommand_UnderPowerShellOnly()
    {
        Assert.True(CommandLine.Hides("git status @{a=git status}", posix: false));
        Assert.False(CommandLine.Hides("git reset --hard HEAD@{1}", posix: true));
        Assert.False(CommandLine.Hides("git log @{u}..", posix: true));
        Assert.False(CommandLine.Hides("dotnet test ${env:CONFIG}", posix: false));
    }

    /// <summary>A separator inside brackets belongs to what they hold: cut there, the bracket never closes and the first
    /// command inside is read by nobody.</summary>
    [Theory]
    [InlineData("echo $(rm -rf src; true)", true)]
    [InlineData("echo $(rm -rf src; true)", false)]
    [InlineData("x=$(rm -rf src && echo ok)", true)]
    [InlineData("(cd a; rm -rf src)", true)]
    [InlineData("echo `rm -rf src; true`", true)]
    [InlineData("@{a=1;b=rm -rf src}", false)]
    [InlineData("if ($x) { rm -rf src; ls }", false)]
    public void ACommandInBracketsBesideAnother_IsAStepOfItsOwn(string line, bool posix) =>
        Assert.Contains(CommandLine.Steps(line, posix), s => s.Trim() == "rm -rf src");

    /// <summary>A bracket that never closes holds nothing: the line reads as it did, and goes to the card.</summary>
    [Fact]
    public void AStrayBracket_LosesNoStep()
    {
        Assert.Contains(CommandLine.Steps("echo ( ; rm -rf src", posix: true), s => s.Trim() == "rm -rf src");
        Assert.True(CommandLine.Hides("echo ( ; rm -rf src", posix: true));
    }

    /// <summary>⚠ The package check reads every command the line runs — one inside a subexpression, a grouping, a hashtable,
    /// backticks or a process substitution too —, an invented package installed there is checked like one at the top.</summary>
    [Theory]
    [InlineData("git status $(npm install left-pad-58)")]
    [InlineData("git status @(npm install left-pad-58)")]
    [InlineData("git status (npm install left-pad-58)")]
    [InlineData("git status @{a=npm install left-pad-58}")]
    [InlineData("git status <(npm install left-pad-58)")]
    [InlineData("git status \"$(npm install left-pad-58)\"")]
    public void AnInstallHiddenInsideTheLine_IsSeenByThePackageCheck(string line)
    {
        Assert.Contains(PackageCheck.Added("run_command", Args(new { command = "npm install left-pad-58" }), _root),
                        p => p.Name == "left-pad-58");   // reference arm: at the top level
        Assert.Contains(PackageCheck.Added("run_command", Args(new { command = line }), _root), p => p.Name == "left-pad-58");
    }

    /// <summary>Each shell's escapes read as it reads them: a POSIX backslash-quote and backticks, a PowerShell backtick escape
    /// — none of them hides the command after it.</summary>
    [Theory]
    [InlineData("git status \\' $(npm install left-pad-58) \\'", true)]
    [InlineData("git status `npm install left-pad-58`", true)]
    [InlineData("npm run x \"`t$(npm install left-pad-58)\"", false)]
    [InlineData("Write-Host \"done`n\"; git status $(npm install left-pad-58)", false)]
    public void AnEscape_DoesNotHideTheCommandAfterIt(string line, bool posix) =>
        Assert.Contains(CommandLine.Steps(line, posix), s => s.Trim() == "npm install left-pad-58");

    /// <summary>The commands a subexpression runs come before the step that holds it, in the order the shell runs them:
    /// a <c>cd</c> after the line does not move where its install is asked from.</summary>
    [Fact]
    public void AnInstallInASubexpression_IsAskedFromWhereItRuns()
    {
        Directory.CreateDirectory(Path.Combine(_root, "web"));
        var added = PackageCheck.Added("run_command", Args(new { command = "npm test $(npm install left-pad-58) && cd web" }), _root);
        Assert.Equal(_root, Assert.Single(added, p => p.Name == "left-pad-58").Directory);
    }

    /// <summary>A subexpression that changes where packages come from changes it for the install that holds it: that install
    /// is not judged against the default registry.</summary>
    [Fact]
    public void ASubexpressionThatChangesTheSources_RunsBeforeTheInstallThatHoldsIt()
    {
        Assert.Contains(PackageCheck.Added("run_command", Args(new { command = "npm install left-pad-58 $(echo x)" }), _root),
                        p => p.Name == "left-pad-58");   // reference arm: a subexpression that changes nothing
        Assert.DoesNotContain(PackageCheck.Added("run_command",
                                  Args(new { command = "npm install left-pad-58 $(npm config set registry https://corp.example/)" }), _root),
                              p => p.Name == "left-pad-58");
    }

    /// <summary>A folder changed inside a subexpression stays in a POSIX subshell and not in PowerShell: where the rest of
    /// the line runs is not known, and nothing after it is asked from a guessed folder.</summary>
    [Fact]
    public void AFolderChangedInsideASubexpression_IsNotGuessed()
    {
        Directory.CreateDirectory(Path.Combine(_root, "web"));
        var added = PackageCheck.Added("run_command", Args(new { command = "npm test $(cd web) ; npm install left-pad-58" }), _root);
        Assert.DoesNotContain(added, p => p.Directory == Path.Combine(_root, "web"));
    }

    /// <summary>A deny rule sees a command inside a subexpression: it runs first, whatever the line starts with.</summary>
    [Fact]
    public void ADenyRule_SeesTheCommandInsideASubexpression()
    {
        var rules = Rules("deny run_command ^rm\nallow run_command ^git status");
        Assert.Equal(PermissionDecision.Deny, rules.Evaluate("run_command", "rm -rf x"));   // reference arm
        Assert.Equal(PermissionDecision.Deny, rules.Evaluate("run_command", "git status $(rm -rf x)"));
    }

    /// <summary>
    /// ⚠ A refusal judges every reading of the line: a command a quote seems to hide — a string across two lines, a comment
    /// holding a quote, an escaped quote, a command another shell reads from a quoted argument — is still denied, and an
    /// allow rule never approves it.
    /// </summary>
    [Theory]
    [InlineData("git status \"x\ngit status\" ; rm -rf src ; \"x\"")]
    [InlineData("git status <# \" #> ; rm -rf src")]
    [InlineData("git status \\\"; rm -rf src; \\\"")]
    [InlineData("git status `\"; rm -rf src; git status `\"")]
    [InlineData("bash -c \"cd build && rm -rf ../src\"")]
    [InlineData("git status --% ' | rm -rf src #'")]
    public void ACommandAQuoteSeemsToHide_IsStillDenied_AndNeverAllowed(string line)
    {
        Assert.Equal(PermissionDecision.Deny, Rules("deny run_command ^rm").Evaluate("run_command", line));
        Assert.NotEqual(PermissionDecision.Allow, Rules("allow run_command ^git status\nallow run_command ^bash").Evaluate("run_command", line));
    }

    /// <summary>A line of brackets that never close is read in linear time: the reader stops at the first one, and the card
    /// (<c>Hides</c>) holds the line.</summary>
    [Fact]
    public void ALineOfUnclosedBrackets_IsReadFast()
    {
        var line = "git status " + new string('(', 60_000);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        CommandLine.Steps(line, posix: false);
        CommandLine.Steps(line, posix: true);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"read in {clock.Elapsed}");
        Assert.True(CommandLine.Hides(line, posix: false));
    }
}
