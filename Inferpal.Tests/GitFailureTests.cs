using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services;
using Inferpal.Services.Commands;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A git command that <b>failed</b> is not a git command that found nothing.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ <b>The funnel hands back the exit code and every caller but one dropped it.</b>
/// <see cref="GitProcess"/> returns <c>(Output, ExitCode)</c> precisely so "no git here" can be told
/// apart from "nothing changed" — its own remark says so — and
/// <c>CommitCommandHandler.ExecuteAsync</c> is the single place that looks. The readers did not, and
/// they did not even fail the same way:
/// </para>
/// <list type="bullet">
///   <item><c>get_git_status</c> keeps <b>stdout only</b>, so a refusal arrives as the empty string
///         and is rendered as <c>(empty)</c> / <c>(no commits)</c> / <c>(no branches)</c> /
///         <c>(nothing to diff)</c> — a complete, fabricated report of a pristine repository.</item>
///   <item><c>/commit</c>, <c>/check</c> and the <c>/onboard context</c> brief use the combined
///         output, where stderr is appended — so git's <c>fatal:</c> line becomes the diff the model
///         is asked to describe, review, or take as the project's recent history.</item>
/// </list>
/// <para>
/// ⚠ <b>Measured</b>, on a folder holding an empty <c>.git</c> directory — which is what a partial
/// clone, a repository git refuses for dubious ownership, or a half-deleted worktree looks like from
/// outside: <c>git status</c> exits 128 with everything on stderr, <c>git diff --stat</c> exits 129,
/// and <c>get_git_status</c> answered with the four empty-state lines above and nothing else.
/// </para>
/// <para>
/// ⚠ The reference arm is a <b>fresh</b> repository, where git legitimately exits non-zero:
/// <c>log</c> and <c>diff HEAD</c> both answer 128 because <c>HEAD</c> does not exist yet. "Non-zero
/// means broken" would turn that normal state into an alarm, which is why the gate is on
/// <c>git status</c> — the command whose success proves git runs and the repository is readable.
/// </para>
/// </remarks>
public sealed class GitFailureTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"gitfail-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); } catch { }
    }

    // ── Scripted git, for the handlers ────────────────────────────────────────

    /// <summary>git that refuses everything, the way a repository git will not open does.</summary>
    private static GitRunner RefusingGit(string message = "fatal: detected dubious ownership in repository at 'C:/dev/App'")
        => (_, _) => Task.FromResult((message, 128));

    /// <summary>git that runs and has nothing to report — the state that must stay distinguishable.</summary>
    private static GitRunner CleanGit => (_, _) => Task.FromResult((string.Empty, 0));

    private static FakeInferenceProvider Answering(string text) => new()
    {
        ChatResult = new ChatTurnResult(text, [], 0, 0),
    };

    // ── /commit ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Propose_WhenGitRefuses_NamesTheFailure_AndNeverAsksTheModel()
    {
        var client = Answering("should never be asked");

        var result = await CommitCommandHandler.ProposeAsync(
            client, new InferpalConfig(), RefusingGit(), null, CancellationToken.None);

        Assert.Contains("dubious ownership", result.Message ?? "", StringComparison.Ordinal);
        Assert.Null(result.Proposal);
        // ⚠ The half that costs the most: asked anyway, the model writes a commit message about
        // git's error text, and the user is one click from committing under it.
        Assert.Empty(client.AgentRuns);
    }

    [Fact]
    public async Task Propose_WhenGitRunsAndTheTreeIsClean_StillSaysNothingToCommit()
    {
        // REFERENCE ARM: exit 0 with empty output is the legitimate "nothing changed", and it must
        // not become a failure message.
        var result = await CommitCommandHandler.ProposeAsync(
            Answering("x"), new InferpalConfig(), CleanGit, null, CancellationToken.None);

        Assert.Equal(Strings.CommitNothingToCommit, result.Message);
    }

    // ── /check ────────────────────────────────────────────────────────────────

    private string RootWithOneCheck()
    {
        var root = Path.Combine(_base, "checked");
        Directory.CreateDirectory(Path.Combine(root, ".inferpal", "checks"));
        File.WriteAllText(Path.Combine(root, ".inferpal", "checks", "naming.md"),
            "---\ndescription: Naming\n---\nNames must be explicit.\n");
        return root;
    }

    [Fact]
    public async Task Check_WhenGitRefuses_NamesTheFailure_AndNeverAsksTheModel()
    {
        var client = Answering("should never be asked");

        var result = await CheckCommandHandler.HandleAsync(
            client, new InferpalConfig(), RootWithOneCheck(), ["/check"],
            RefusingGit(), null, CancellationToken.None);

        Assert.Contains("dubious ownership", result.Message ?? "", StringComparison.Ordinal);
        Assert.Empty(client.ChatModels);
    }

    [Fact]
    public async Task Check_WhenGitRunsAndTheTreeIsClean_StillSaysThereIsNothingToReview()
    {
        // REFERENCE ARM.
        var client = Answering("should never be asked");

        var result = await CheckCommandHandler.HandleAsync(
            client, new InferpalConfig(), RootWithOneCheck(), ["/check"],
            CleanGit, null, CancellationToken.None);

        Assert.Equal(Strings.CheckNoDiff, result.Message);
    }

    // ── /onboard context ──────────────────────────────────────────────────────

    [Fact]
    public async Task RepoBrief_WhenGitRefuses_DoesNotPassTheErrorOffAsTheProjectHistory()
    {
        // ⚠ This brief is written into `.inferpal/context.md`, i.e. into the system prompt of every
        // following session. Its own summary says "what a repository contains is a fact, and a fact
        // the model has to guess is a fact it can get wrong" — and it was handing the model a
        // `fatal:` line under the heading "Recent commit subjects".
        var root = Path.Combine(_base, "brief");
        Directory.CreateDirectory(root);

        var brief = await OnboardCommandHandler.BuildRepoBriefAsync(root, RefusingGit(), CancellationToken.None);

        var history = brief.Split("## Recent commit subjects").Skip(1).FirstOrDefault() ?? "";
        var first   = history.Split('\n').Select(l => l.Trim()).First(l => l.Length > 0);
        // git's words may appear — ATTRIBUTED to git and marked unavailable. What must never happen
        // is the bare `fatal:` line sitting where a commit subject goes.
        Assert.StartsWith("(unavailable", first, StringComparison.Ordinal);
        Assert.Contains("dubious ownership", first, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepoBrief_WhenGitAnswers_StillCarriesTheCommitSubjects()
    {
        // REFERENCE ARM: the section must not disappear for everyone.
        var root = Path.Combine(_base, "brief-ok");
        Directory.CreateDirectory(root);
        GitRunner git = (_, _) => Task.FromResult(("Add the thing\nFix the other thing", 0));

        var brief = await OnboardCommandHandler.BuildRepoBriefAsync(root, git, CancellationToken.None);

        Assert.Contains("## Recent commit subjects", brief, StringComparison.Ordinal);
        Assert.Contains("Add the thing", brief, StringComparison.Ordinal);
    }

    // ── get_git_status, against the real git ──────────────────────────────────

    /// <summary>
    /// git really runs here. Without this witness every assertion below would pass on a machine with
    /// no git at all — measuring the fixture instead of the product.
    /// </summary>
    private static void AssertGitRuns()
    {
        int exit;
        try
        {
            using var p = Process.Start(new ProcessStartInfo("git", "--version")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            })!;
            p.WaitForExit(10_000);
            exit = p.ExitCode;
        }
        catch (Exception ex)
        {
            Assert.Fail($"git is not runnable here, so this test is UNDECIDED, not green: {ex.Message}");
            return;
        }
        Assert.True(exit == 0, "`git --version` failed: this test is UNDECIDED, not green.");
    }

    private static async Task<string> StatusOfAsync(string root)
    {
        var tool = new GetGitStatusTool(new NullEditorSurface(), () => root);
        using var args = JsonDocument.Parse(JsonSerializer.Serialize(new { path = root }));
        return await tool.ExecuteAsync(args.RootElement, CancellationToken.None);
    }

    [Fact]
    public async Task GetGitStatus_OnARepositoryGitRefuses_SaysSo_InsteadOfReportingAPristineOne()
    {
        AssertGitRuns();

        // A `.git` folder is all `FindGitRoot` looks for, so the tool commits to "this is a
        // repository" — and git then refuses it. A partial clone and a dubious-ownership refusal
        // look exactly like this from here.
        var root = Path.Combine(_base, "broken");
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        File.WriteAllText(Path.Combine(root, "a.txt"), "hello");

        var report = await StatusOfAsync(root);

        Assert.Contains("not a git repository", report, StringComparison.OrdinalIgnoreCase);
        // Assertions POSITIVES on what must no longer be claimed would pass on a build where the
        // whole report vanished, so the fabricated lines are named explicitly too.
        Assert.DoesNotContain("(no commits)", report, StringComparison.Ordinal);
        Assert.DoesNotContain("(nothing to diff)", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetGitStatus_OnAFreshRepository_StillReportsItsRealState()
    {
        AssertGitRuns();

        // REFERENCE ARM, and the reason the gate is on `status`: here `git log` and
        // `git diff --stat HEAD` BOTH exit 128 — HEAD does not exist yet — while the repository is
        // perfectly healthy. Measured, on this fixture.
        var root = Path.Combine(_base, "fresh");
        Directory.CreateDirectory(root);
        Assert.True(RunGit("init -q .", root), "`git init` failed: this test is UNDECIDED, not green.");
        File.WriteAllText(Path.Combine(root, "a.txt"), "hello");
        Assert.True(RunGit("add a.txt", root), "`git add` failed: this test is UNDECIDED, not green.");

        var report = await StatusOfAsync(root);

        Assert.Contains("=== git status ===", report, StringComparison.Ordinal);
        Assert.Contains("a.txt", report, StringComparison.Ordinal);     // git really answered
        Assert.Contains("(no commits)", report, StringComparison.Ordinal);
        // The template up to its detail placeholder — comparing the whole formatted sentence would
        // pass trivially, since the detail we would have to guess is git's own wording.
        Assert.DoesNotContain(FailurePrefix, report, StringComparison.Ordinal);
    }

    /// <summary>The localized failure sentence up to where git's own words start.</summary>
    private static string FailurePrefix =>
        Strings.GitCommandFailed("status", "").Split('')[0];

    private static bool RunGit(string args, string workDir)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("git", args)
            {
                WorkingDirectory = workDir,
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            })!;
            p.WaitForExit(20_000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }
}
