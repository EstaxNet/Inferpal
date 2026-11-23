using System.Net;
using System.Net.Http;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A CI run reads as what failed in it — a GitHub Actions run or job, a GitLab pipeline or job, the checks of a pull
/// request — by the forge's public API: failed jobs, failed steps, annotations, the end of a GitLab log; a GitHub log said
/// unreadable without an account. Shapes as github.com and gitlab.com answer without an account.
/// </summary>
public class TicketReaderCiTests
{
    private static TicketReader.JsonFetch Fake(Dictionary<string, string> answers, List<string>? asked = null) => (url, _) =>
    {
        asked?.Add(url);
        return answers.TryGetValue(url, out var json)
            ? Task.FromResult(json)
            : Task.FromException<string>(new HttpRequestException("Response status code does not indicate success: 404 (Not Found).", null, HttpStatusCode.NotFound));
    };

    private static readonly Func<string, bool> NoRule = _ => false;

    [Theory]
    [InlineData("https://github.com/o/r/actions/runs/45161811070", "https://api.github.com/repos/o/r/actions/runs/45161811070", false)]
    [InlineData("https://github.com/o/r/actions/runs/45161811070/job/137649043061", "https://api.github.com/repos/o/r/actions/jobs/137649043061", true)]
    [InlineData("https://github.com/o/r/actions/runs/1/attempts/2", "https://api.github.com/repos/o/r/actions/runs/1/attempts/2", false)]
    [InlineData("https://gitlab.com/gitlab-org/cli/-/pipelines/3101070325", "https://gitlab.com/api/v4/projects/gitlab-org%2Fcli/pipelines/3101070325", false)]
    [InlineData("https://gitlab.com/gitlab-org/cli/-/jobs/18266709879", "https://gitlab.com/gitlab-org/cli/-/jobs/18266709879.json", true)]
    public void ACiAddress_NamesItsRun_ByItsShape(string url, string api, bool job)
    {
        var run = TicketReader.ParseRun(url)!;
        Assert.Equal((api, job), (run.Api, run.IsJob));
    }

    [Theory]
    [InlineData("https://github.com/o/r/actions")]
    [InlineData("https://github.com/o/r/actions/workflows/ci.yml")]
    [InlineData("https://example.com/o/r/actions/runs/1")]
    public void AnyOtherAddress_IsNoRun(string url) => Assert.Null(TicketReader.ParseRun(url));

    private const string GitHubRun = """
        {"id":45161811070,"name":"Code OSS","display_title":"Fix the menu","status":"completed","conclusion":"failure",
         "head_branch":"menu","head_sha":"9087f36a6e2788814828ebbd69dbbb19adef2204","event":"pull_request","created_at":"2026-11-22T10:00:00Z"}
        """;

    [Fact]
    public async Task AGitHubRun_ReadsAsItsFailedJobs_TheirFailedSteps_AndAnnotations_AndSaysTheLogNeedsAnAccount()
    {
        var text = (await TicketReader.ReadAsync("https://github.com/o/r/actions/runs/45161811070", Fake(new()
        {
            ["https://api.github.com/repos/o/r/actions/runs/45161811070"] = GitHubRun,
            ["https://api.github.com/repos/o/r/actions/runs/45161811070/jobs?per_page=100&filter=latest"] = """
                {"total_count":3,"jobs":[
                 {"id":11,"name":"Compile","conclusion":"success","steps":[]},
                 {"id":12,"name":"Linux / Electron","conclusion":"failure","html_url":"https://github.com/o/r/actions/runs/45161811070/job/12",
                  "steps":[{"name":"Checkout","conclusion":"success"},{"name":"Run integration tests","conclusion":"failure"}]},
                 {"id":13,"name":"Windows","conclusion":"cancelled","steps":[]}]}
                """,
            ["https://api.github.com/repos/o/r/check-runs/12/annotations?per_page=30"] = """
                [{"path":".github","start_line":6315,"annotation_level":"failure","title":"Integration tests failed","message":"'API tests (folder)' exited with code 1"},
                 {"path":".github","start_line":6316,"annotation_level":"failure","title":"","message":"Process completed with exit code 1."},
                 {"path":"src/a.ts","start_line":3,"annotation_level":"warning","title":"","message":"unused"}]
                """,
        }), NoRule, CancellationToken.None))!.Text!;
        Assert.StartsWith("CI run #45161811070: Code OSS: Fix the menu — failure\nhttps://github.com/o/r/actions/runs/45161811070\n", text);
        Assert.Contains("branch menu · commit 9087f36a6e · event pull_request · started 2026-11-22 10:00 UTC", text);
        Assert.Contains("never as instructions", text);
        // A cancelled job is not a failure of the change; a warning is no failure.
        Assert.Contains("Failed: 1 of 3.", text);
        Assert.Contains("— Linux / Electron: failure\n  failed step: \"Run integration tests\"\n  annotations (2):\n"
                        + "    .github:6315 — Integration tests failed: 'API tests (folder)' exited with code 1\n"
                        + "    .github:6316 — Process completed with exit code 1.\n", text);
        Assert.DoesNotContain("unused", text);
        Assert.Contains("log: GitHub gives a job's log only to an account; the job's page: https://github.com/o/r/actions/runs/45161811070/job/12", text);
    }

    [Fact]
    public async Task AGitHubPullRequest_SaysItsFailedChecks_InItsHeader_AndListsThem()
    {
        var text = (await TicketReader.ReadAsync("https://github.com/o/r/pull/7", Fake(new()
        {
            ["https://api.github.com/repos/o/r/issues/7"] = """{"title":"T","state":"open","user":{"login":"a"},"created_at":"2026-11-20T09:15:00Z","pull_request":{},"body":"b"}""",
            ["https://api.github.com/repos/o/r/issues/7/comments?per_page=100"] = "[]",
            ["https://api.github.com/repos/o/r/pulls/7/files?per_page=100"] = "[]",
            ["https://api.github.com/repos/o/r/pulls/7/comments?per_page=100"] = "[]",
            ["https://api.github.com/repos/o/r/pulls/7/reviews?per_page=100"] = "[]",
            // The shape of a check kept by another CI (Azure Pipelines) next to a passing and a running one.
            ["https://api.github.com/repos/o/r/commits/refs/pull/7/head/check-runs?per_page=100&filter=latest"] = """
                {"total_count":3,"check_runs":[
                 {"id":5,"name":"runtime","status":"completed","conclusion":"failure","app":{"slug":"azure-pipelines"},
                  "details_url":"https://dev.azure.com/x/_build/results?buildId=1",
                  "output":{"title":"Build #20261120.146 had test failures","summary":"[8 errors / 2,325 warnings](https://dev.azure.com/x)","annotations_count":1}},
                 {"id":6,"name":"lint","status":"completed","conclusion":"success","app":{"slug":"github-actions"},"output":{}},
                 {"id":7,"name":"tests","status":"in_progress","conclusion":null,"app":{"slug":"github-actions"},"output":{}}]}
                """,
            ["https://api.github.com/repos/o/r/commits/refs/pull/7/head/status?per_page=100"] = """{"state":"pending","total_count":0,"statuses":[]}""",
            ["https://api.github.com/repos/o/r/check-runs/5/annotations?per_page=30"] =
                """[{"path":"Build log ","start_line":27,"annotation_level":"failure","title":"Build log #L27","message":"Process returned non-zero exit code: 57005"}]""",
        }), NoRule, CancellationToken.None))!.Text!;
        Assert.Equal("Checks: 1 of 3 failed, 1 still running — listed after the review.", text.Split('\n')[3]);
        Assert.Contains("— runtime (azure-pipelines): failure\n  Build #20261120.146 had test failures\n  [8 errors / 2,325 warnings](https://dev.azure.com/x)\n"
                        + "  annotations (1):\n    Build log:27 — Process returned non-zero exit code: 57005\n"
                        + "  log: kept by azure-pipelines: https://dev.azure.com/x/_build/results?buildId=1", text);
        Assert.True(text.IndexOf("— runtime", StringComparison.Ordinal) < text.IndexOf("No comments.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AGitHubPullRequestWhoseChecksPass_SaysSoInOneLine()
    {
        var text = (await TicketReader.ReadAsync("https://github.com/o/r/pull/8", Fake(new()
        {
            ["https://api.github.com/repos/o/r/issues/8"] = """{"title":"T","state":"open","user":{"login":"a"},"created_at":"2026-11-20T09:15:00Z","pull_request":{},"body":"b"}""",
            ["https://api.github.com/repos/o/r/issues/8/comments?per_page=100"] = "[]",
            ["https://api.github.com/repos/o/r/commits/refs/pull/8/head/check-runs?per_page=100&filter=latest"] =
                """{"total_count":1,"check_runs":[{"id":6,"name":"lint","status":"completed","conclusion":"success","app":{"slug":"github-actions"},"output":{}}]}""",
            ["https://api.github.com/repos/o/r/commits/refs/pull/8/head/status?per_page=100"] = """{"state":"pending","total_count":0,"statuses":[]}""",
        }), NoRule, CancellationToken.None))!.Text!;
        Assert.Contains("Checks: 0 of 1 failed.", text);
        Assert.DoesNotContain("listed after the review", text);
    }

    // A GitLab job's log as gitlab.com serves it at /raw: colors, timestamps, section marks, then the runner's cleanup.
    private static string GitLabLog()
    {
        var lines = new List<string>
        {
            "2026-11-23T03:30:00.000000Z 00O section_start:1:prepare_script\u001b[0K\u001b[0K\u001b[36;1mPreparing environment\u001b[0;m",
            "2026-11-23T03:30:01.000000Z 00O section_end:1:prepare_script\u001b[0K",
            "2026-11-23T03:30:02.000000Z 00O section_start:2:step_script\u001b[0K\u001b[0K\u001b[36;1mExecuting \"step_script\"\u001b[0;m",
        };
        for (var i = 0; i < 120; i++) lines.Add($"2026-11-23T03:31:00.000000Z 01O ok  \tpkg/{i}\t0.01s");
        lines.Add("2026-11-23T03:36:00.000000Z 01O \u001b[31m=== FAIL: internal/commands/whatsnew TestWhatsnew_latestFlag (0.00s)\u001b[0m");
        lines.Add("2026-11-23T03:36:01.000000Z 01O DONE 2115 tests, 6 failures in 52.1s");
        lines.Add("2026-11-23T03:36:02.000000Z 00O section_end:3:step_script\u001b[0K");
        lines.Add("2026-11-23T03:36:59.000000Z 00O+section_start:4:cleanup_file_variables\u001b[0K\u001b[36;1mCleaning up project directory\u001b[0;m");
        lines.Add("2026-11-23T03:36:59.100000Z 00O section_end:4:cleanup_file_variables\u001b[0K");
        lines.Add("2026-11-23T03:36:59.400000Z 00O \u001b[31;1mERROR: Job failed: exit code 2\u001b[0;m");
        return string.Join("\n", lines) + "\n";
    }

    [Fact]
    public async Task AGitLabPipeline_ReadsItsFailedJobs_AndTheEndOfTheirScript_AsItWrote()
    {
        var asked = new List<string>();
        var text = (await TicketReader.ReadAsync("https://gitlab.com/g/cli/-/pipelines/31", Fake(new()
        {
            ["https://gitlab.com/api/v4/projects/g%2Fcli/pipelines/31"] =
                """{"id":31,"status":"failed","ref":"refs/merge-requests/4429/merge","sha":"62a60446bc61f10ff491237253750e466ed217db","source":"merge_request_event","created_at":"2026-11-23T03:29:22Z"}""",
            ["https://gitlab.com/api/v4/projects/g%2Fcli/pipelines/31/jobs?per_page=100"] = """
                [{"id":40,"name":"lint","stage":"test","status":"failed","failure_reason":"script_failure","allow_failure":true},
                 {"id":41,"name":"tests:unit","stage":"test","status":"failed","failure_reason":"script_failure","allow_failure":false},
                 {"id":42,"name":"build","stage":"build","status":"success","allow_failure":false}]
                """,
            ["https://gitlab.com/g/cli/-/jobs/41/raw"] = GitLabLog(),
            ["https://gitlab.com/g/cli/-/jobs/40/raw"] = "2026-11-23T03:31:00.000000Z 01O golangci-lint: 1 issue\n",
        }, asked), NoRule, CancellationToken.None))!.Text!;
        Assert.StartsWith("CI pipeline #31 — failed\nhttps://gitlab.com/g/cli/-/pipelines/31\nref refs/merge-requests/4429/merge · commit 62a60446bc · source merge_request_event", text);
        // The job allowed to fail does not fail the pipeline, and comes after the one that does.
        Assert.Contains("Failed: 1 of 3, and 1 allowed to fail.", text);
        Assert.True(text.IndexOf("— tests:unit", StringComparison.Ordinal) < text.IndexOf("— lint", StringComparison.Ordinal));
        Assert.Contains("  allowed to fail: it does not fail the pipeline", text);
        Assert.Contains("  [the last 80 of its script's 124 lines; the whole log is on the job's page]", text);
        Assert.Contains("    === FAIL: internal/commands/whatsnew TestWhatsnew_latestFlag (0.00s)\n    DONE 2115 tests, 6 failures in 52.1s\n    ERROR: Job failed: exit code 2", text);
        // Ordinal: a culture-aware comparison ignores a control character, and "finds" it everywhere.
        Assert.DoesNotContain("\u001b", text, StringComparison.Ordinal);
        Assert.DoesNotContain("2026-11-23T03:3", text);
        Assert.DoesNotContain("Cleaning up", text);
        Assert.DoesNotContain("section_", text);
    }

    [Fact]
    public async Task AGitLabJob_ReadsItsOwnPage_WhoseStatusIsAnObject()
    {
        var text = (await TicketReader.ReadAsync("https://gitlab.com/g/cli/-/jobs/41", Fake(new()
        {
            ["https://gitlab.com/g/cli/-/jobs/41.json"] =
                """{"id":41,"name":"tests:unit","stage":"test","status":{"group":"failed","text":"Failed"},"failure_reason":"script_failure","pipeline":{"ref":"main","sha":"62a60446bc61f10ff4"},"started_at":"2026-11-23T03:30:00Z"}""",
            ["https://gitlab.com/g/cli/-/jobs/41/raw"] = GitLabLog(),
        }), NoRule, CancellationToken.None))!.Text!;
        Assert.StartsWith("CI job #41: tests:unit (stage test) — failed\n", text);
        Assert.Contains("ref main · commit 62a60446bc", text);
        Assert.Contains("ERROR: Job failed: exit code 2", text);
    }

    [Fact]
    public async Task AGitLabMergeRequestWhosePipelineFailed_ListsItsFailedJobs()
    {
        var text = (await TicketReader.ReadAsync("https://gitlab.com/g/cli/-/merge_requests/4429", Fake(new()
        {
            ["https://gitlab.com/api/v4/projects/g%2Fcli/merge_requests/4429"] =
                """{"title":"T","state":"opened","author":{"username":"eve"},"created_at":"2026-11-01T08:00:00Z","description":"d","head_pipeline":{"id":31,"status":"failed","web_url":"https://gitlab.com/g/cli/-/pipelines/31"}}""",
            ["https://gitlab.com/g/cli/-/merge_requests/4429/discussions.json?per_page=100"] = "[]",
            ["https://gitlab.com/api/v4/projects/g%2Fcli/pipelines/31/jobs?per_page=100"] =
                """[{"id":41,"name":"tests:unit","stage":"test","status":"failed","failure_reason":"script_failure","allow_failure":false}]""",
            ["https://gitlab.com/g/cli/-/jobs/41/raw"] = GitLabLog(),
        }), NoRule, CancellationToken.None))!.Text!;
        Assert.Equal("Checks: 1 of 1 failed — listed after the review. Pipeline #31.", text.Split('\n')[3]);
        Assert.Contains("Its latest pipeline failed: https://gitlab.com/g/cli/-/pipelines/31", text);
        Assert.Contains("ERROR: Job failed: exit code 2", text);
    }

    [Fact]
    public async Task AGitLabMergeRequestWhosePipelinePassed_SaysSoWithoutReadingIt()
    {
        var asked = new List<string>();
        var text = (await TicketReader.ReadAsync("https://gitlab.com/g/cli/-/merge_requests/4472", Fake(new()
        {
            ["https://gitlab.com/api/v4/projects/g%2Fcli/merge_requests/4472"] =
                """{"title":"T","state":"opened","author":{"username":"eve"},"created_at":"2026-11-01T08:00:00Z","description":"d","head_pipeline":{"id":30,"status":"success","web_url":"https://gitlab.com/g/cli/-/pipelines/30"}}""",
            ["https://gitlab.com/g/cli/-/merge_requests/4472/discussions.json?per_page=100"] = "[]",
        }, asked), NoRule, CancellationToken.None))!.Text!;
        Assert.Equal("Checks: its latest pipeline is success (https://gitlab.com/g/cli/-/pipelines/30).", text.Split('\n')[3]);
        Assert.DoesNotContain(asked, a => a.Contains("/pipelines/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ALogTheRulesRefuse_IsNeverAsked_AndSaid()
    {
        var asked = new List<string>();
        var text = (await TicketReader.ReadAsync("https://gitlab.com/g/cli/-/pipelines/31", Fake(new()
        {
            ["https://gitlab.com/api/v4/projects/g%2Fcli/pipelines/31"] = """{"id":31,"status":"failed","ref":"main","sha":"62a6"}""",
            ["https://gitlab.com/api/v4/projects/g%2Fcli/pipelines/31/jobs?per_page=100"] =
                """[{"id":41,"name":"tests:unit","stage":"test","status":"failed","allow_failure":false}]""",
        }, asked), a => a.EndsWith("/raw", StringComparison.Ordinal), CancellationToken.None))!.Text!;
        Assert.DoesNotContain(asked, a => a.EndsWith("/raw", StringComparison.Ordinal));
        Assert.Contains("log: refused by your rules for fetch_url", text);
    }

    [Fact]
    public async Task ARunTheForgeRefuses_IsSaid_AndThePageIsReadInstead()
    {
        var read = await TicketReader.ReadAsync("https://github.com/o/private/actions/runs/1", Fake([]), NoRule, CancellationToken.None);
        Assert.Null(read!.Text);
        Assert.Contains("GitHub's API did not give this CI run", read.Refused);
    }

    private static Dictionary<string, string> GitHubPull(int n, string checks, string statuses) => new()
    {
        [$"https://api.github.com/repos/o/r/issues/{n}"] = """{"title":"T","state":"open","user":{"login":"a"},"created_at":"2026-11-20T09:15:00Z","pull_request":{},"body":"b"}""",
        [$"https://api.github.com/repos/o/r/issues/{n}/comments?per_page=100"] = "[]",
        [$"https://api.github.com/repos/o/r/pulls/{n}/files?per_page=100"] = "[]",
        [$"https://api.github.com/repos/o/r/pulls/{n}/comments?per_page=100"] = "[]",
        [$"https://api.github.com/repos/o/r/pulls/{n}/reviews?per_page=100"] = "[]",
        [$"https://api.github.com/repos/o/r/commits/refs/pull/{n}/head/check-runs?per_page=100&filter=latest"] = checks,
        [$"https://api.github.com/repos/o/r/commits/refs/pull/{n}/head/status?per_page=100"] = statuses,
    };

    [Fact]
    public async Task ACommitStatusThatFailed_IsAFailedCheck_ThoughNoCheckRunFailed()
    {
        // External CI (Jenkins, CircleCI) reports by commit status: a pull request red by one is never said green.
        var text = (await TicketReader.ReadAsync("https://github.com/o/r/pull/9", Fake(GitHubPull(9,
            """{"total_count":1,"check_runs":[{"id":6,"name":"lint","status":"completed","conclusion":"success","app":{"slug":"github-actions"},"output":{}}]}""",
            """{"state":"failure","total_count":1,"statuses":[{"context":"ci/jenkins","state":"failure","description":"Build #12 failed","target_url":"https://ci.example/job/12"}]}""")),
            NoRule, CancellationToken.None))!.Text!;
        Assert.Equal("Checks: 1 of 2 failed — listed after the review.", text.Split('\n')[3]);
        Assert.Contains("— ci/jenkins: failure\n  Build #12 failed\n  log: reported by a commit status: https://ci.example/job/12", text);
    }

    [Fact]
    public async Task AChecksAnswerOfTheWrongShape_LosesTheChecks_NeverThePullRequest()
    {
        var read = await TicketReader.ReadAsync("https://github.com/o/r/pull/10", Fake(GitHubPull(10, "[]", "\"x\"")), NoRule, CancellationToken.None);
        Assert.StartsWith("Pull request #10: T", read!.Text);
        Assert.Contains("Checks: could not be read (see below).", read.Text);
        Assert.Contains("Its checks could not be read: the forge's answer was not an object.", read.Text);
    }

    [Fact]
    public async Task ARunStillGoing_IsNotSettled_SoItIsReadAgain()
    {
        var running = GitHubRun.Replace("\"status\":\"completed\",\"conclusion\":\"failure\"", "\"status\":\"in_progress\",\"conclusion\":null", StringComparison.Ordinal);
        Assert.Contains("in_progress", running);
        var read = await TicketReader.ReadAsync("https://github.com/o/r/actions/runs/45161811070", Fake(new()
        {
            ["https://api.github.com/repos/o/r/actions/runs/45161811070"] = running,
            ["https://api.github.com/repos/o/r/actions/runs/45161811070/jobs?per_page=100&filter=latest"] = """{"total_count":0,"jobs":[]}""",
        }), NoRule, CancellationToken.None);
        Assert.False(read!.Settled);
        // REFERENCE ARM: a finished run is settled.
        var done = await TicketReader.ReadAsync("https://github.com/o/r/actions/runs/45161811070", Fake(new()
        {
            ["https://api.github.com/repos/o/r/actions/runs/45161811070"] = GitHubRun,
            ["https://api.github.com/repos/o/r/actions/runs/45161811070/jobs?per_page=100&filter=latest"] = """{"total_count":0,"jobs":[]}""",
        }), NoRule, CancellationToken.None);
        Assert.True(done!.Settled);
    }

    [Fact]
    public async Task AnAttemptsAddress_ReadsThatAttemptsJobs()
    {
        var asked = new List<string>();
        await TicketReader.ReadAsync("https://github.com/o/r/actions/runs/45161811070/attempts/1", Fake(new()
        {
            ["https://api.github.com/repos/o/r/actions/runs/45161811070/attempts/1"] = GitHubRun,
            ["https://api.github.com/repos/o/r/actions/runs/45161811070/attempts/1/jobs?per_page=100"] = """{"total_count":0,"jobs":[]}""",
        }, asked), NoRule, CancellationToken.None);
        Assert.Contains("https://api.github.com/repos/o/r/actions/runs/45161811070/attempts/1/jobs?per_page=100", asked);
    }

    [Fact]
    public async Task AJobALinkNames_IsReadWhatItEndedWith_AllowedToFailComprised()
    {
        var text = (await TicketReader.ReadAsync("https://gitlab.com/g/cli/-/jobs/40", Fake(new()
        {
            ["https://gitlab.com/g/cli/-/jobs/40.json"] =
                """{"id":40,"name":"lint","stage":"test","status":{"group":"failed-with-warnings"},"failure_reason":"script_failure","pipeline":{"ref":"main","sha":"62a6"}}""",
            ["https://gitlab.com/g/cli/-/jobs/40/raw"] = "golangci-lint: 1 issue\n",
        }), NoRule, CancellationToken.None))!.Text!;
        Assert.Contains("— lint: failed with warnings\n  stage test\n  reason: script failure\n  allowed to fail: it does not fail the pipeline", text);
        Assert.Contains("golangci-lint: 1 issue", text);
    }

    private static Dictionary<string, string> GitLabMergeRequest(string pipelineWeb, string? jobs) => new()
    {
        ["https://gitlab.com/api/v4/projects/g%2Fcli/merge_requests/5"] =
            $$$"""{"title":"T","state":"opened","author":{"username":"eve"},"created_at":"2026-11-01T08:00:00Z","description":"d","head_pipeline":{"id":31,"status":"failed","web_url":"{{{pipelineWeb}}}"}}""",
        ["https://gitlab.com/g/cli/-/merge_requests/5/discussions.json?per_page=100"] = "[]",
        ["https://gitlab.com/api/v4/projects/g%2Fcli/pipelines/31/jobs?per_page=100"] = jobs ?? "",
    };

    [Fact]
    public async Task AFailedPipelineWhoseJobsCannotBeRead_IsNeverSaidZeroFailed()
    {
        var answers = GitLabMergeRequest("https://gitlab.com/g/cli/-/pipelines/31", null);
        answers.Remove("https://gitlab.com/api/v4/projects/g%2Fcli/pipelines/31/jobs?per_page=100");
        var text = (await TicketReader.ReadAsync("https://gitlab.com/g/cli/-/merge_requests/5", Fake(answers), NoRule, CancellationToken.None))!.Text!;
        Assert.Equal("Checks: its latest pipeline failed; its jobs could not be read (see below). Pipeline #31.", text.Split('\n')[3]);
        Assert.DoesNotContain("0 of 0", text);
    }

    [Fact]
    public async Task AFailedPipelineWithNoFailedJobListed_SaysWhereToLook()
    {
        var text = (await TicketReader.ReadAsync("https://gitlab.com/g/cli/-/merge_requests/5", Fake(GitLabMergeRequest(
            "https://gitlab.com/g/cli/-/pipelines/31", """[{"id":42,"name":"trigger-docs","stage":"deploy","status":"success","allow_failure":false}]""")),
            NoRule, CancellationToken.None))!.Text!;
        Assert.Equal("Checks: its latest pipeline failed, with no failed job listed (see below). Pipeline #31.", text.Split('\n')[3]);
        Assert.Contains("No failed job is listed: what failed may be a downstream pipeline", text);
    }

    [Fact]
    public async Task APipelineOnAnotherHost_IsNamed_NeverFetched()
    {
        var asked = new List<string>();
        var text = (await TicketReader.ReadAsync("https://gitlab.com/g/cli/-/merge_requests/5", Fake(GitLabMergeRequest(
            "https://elsewhere.example/g/cli/-/pipelines/31", "[]"), asked), NoRule, CancellationToken.None))!.Text!;
        Assert.DoesNotContain(asked, a => a.Contains("elsewhere.example", StringComparison.Ordinal));
        Assert.Contains("https://elsewhere.example/g/cli/-/pipelines/31 (not read: not a pipeline of this merge request's host)", text);
    }
}
