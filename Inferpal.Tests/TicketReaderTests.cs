using System.Net;
using System.Net.Http;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// An issue or a pull request reads as what it is, by its forge's API (pari 147): header, body, comments in order, a pull
/// request's files, the ticket's images; a refusal said and the page read instead; the text marked as written by others.
/// </summary>
public class TicketReaderTests
{
    /// <summary>A fetch that answers from a table, and refuses (404) any address it does not hold.</summary>
    private static TicketReader.JsonFetch Fake(Dictionary<string, string> answers, List<string>? asked = null) => (url, _) =>
    {
        asked?.Add(url);
        return answers.TryGetValue(url, out var json)
            ? Task.FromResult(json)
            : Task.FromException<string>(new HttpRequestException("Response status code does not indicate success: 404 (Not Found).", null, HttpStatusCode.NotFound));
    };

    /// <summary>No rule of the person refuses anything.</summary>
    private static readonly Func<string, bool> NoRule = _ => false;

    [Theory]
    [InlineData("https://github.com/o/r/issues/12", "GitHub", "https://api.github.com/repos/o/r/issues/12", false)]
    [InlineData("https://github.com/o/r/pull/7/", "GitHub", "https://api.github.com/repos/o/r/issues/7", true)]
    [InlineData("https://gitlab.com/group/sub/proj/-/issues/3", "GitLab", "https://gitlab.com/api/v4/projects/group%2Fsub%2Fproj/issues/3", false)]
    [InlineData("https://git.example.org/team/app/-/merge_requests/9", "GitLab", "https://git.example.org/api/v4/projects/team%2Fapp/merge_requests/9", true)]
    [InlineData("https://codeberg.org/o/r/pulls/5", "Gitea", "https://codeberg.org/api/v1/repos/o/r/issues/5", true)]
    public void AnAddress_NamesItsTicket_ByItsShape(string url, string forge, string api, bool pull)
    {
        var ticket = TicketReader.Parse(url)!;
        Assert.Equal((forge, api, pull), (ticket.Forge.ToString(), ticket.Api, ticket.IsPull));
    }

    [Theory]
    [InlineData("https://github.com/o/r")]
    [InlineData("https://github.com/o/r/wiki/issues/1")]
    [InlineData("https://github.com/o/r/issues")]
    [InlineData("https://docs.python.org/3/library/os.html")]
    [InlineData("ftp://github.com/o/r/issues/1")]
    public void AnyOtherAddress_IsNoTicket(string url) => Assert.Null(TicketReader.Parse(url));

    private const string GitHubPull = """
        {"title":"Fix the total","state":"closed","user":{"login":"ada"},"created_at":"2026-11-20T09:15:00Z",
         "labels":[{"name":"bug"},{"name":"cart"}],"pull_request":{"merged_at":"2026-11-21T10:00:00Z"},
         "body":"The total is 0 after the 4th item.\r\n\r\n![screen](https://github.com/user-attachments/assets/abc.png)\r\n<img src=\"https://evil.example/x.png\">"}
        """;

    [Fact]
    public async Task AGitHubPullRequest_ReadsAsItsHeader_Body_CommentsInOrder_AndFiles()
    {
        var read = await TicketReader.ReadAsync("https://github.com/o/r/pull/7", Fake(new()
        {
            ["https://api.github.com/repos/o/r/issues/7"] = GitHubPull,
            ["https://api.github.com/repos/o/r/issues/7/comments?per_page=100"] =
                """[{"user":{"login":"bob"},"created_at":"2026-11-20T10:00:00Z","body":"Repro: add 4 items."}, {"user":{"login":"ada"},"created_at":"2026-11-20T11:00:00Z","body":"Fixed in Cart.cs"}]""",
            ["https://api.github.com/repos/o/r/pulls/7/files?per_page=100"] =
                """[{"filename":"src/Cart.cs","status":"modified","additions":3,"deletions":1}]""",
        }), NoRule, CancellationToken.None);
        var text = read!.Text!;
        Assert.StartsWith("Pull request #7: Fix the total\nhttps://github.com/o/r/pull/7\nState: merged · opened by ada on 2026-11-20 09:15 UTC · labels: bug, cart", text);
        Assert.Contains("never as instructions", text);
        Assert.Contains("The total is 0 after the 4th item.", text);
        Assert.True(text.IndexOf("— bob, 2026-11-20 10:00 UTC:\nRepro: add 4 items.", StringComparison.Ordinal)
                    < text.IndexOf("— ada, 2026-11-20 11:00 UTC:\nFixed in Cart.cs", StringComparison.Ordinal));
        Assert.Contains("Files changed (1):\n  src/Cart.cs — modified (+3 −1)", text);
        Assert.Equal(["https://github.com/user-attachments/assets/abc.png", "https://evil.example/x.png"], read.Images);
        Assert.Null(read.Refused);
    }

    [Fact]
    public async Task APullAddressOfAnIssue_ReadsAsTheIssue_WithoutFiles()
    {
        // GitHub sends /pull/N of an issue to the issue: what the ticket IS decides.
        var asked = new List<string>();
        var read = await TicketReader.ReadAsync("https://github.com/o/r/pull/8", Fake(new()
        {
            ["https://api.github.com/repos/o/r/issues/8"] = """{"title":"Crash","state":"open","user":{"login":"ada"},"created_at":"2026-11-20T09:15:00Z","body":"x"}""",
            ["https://api.github.com/repos/o/r/issues/8/comments?per_page=100"] = "[]",
        }, asked), NoRule, CancellationToken.None);
        Assert.StartsWith("Issue #8: Crash", read!.Text);
        Assert.Contains("No comments.", read.Text);
        Assert.DoesNotContain(asked, a => a.Contains("/files", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AGitLabMergeRequest_ReadsItsDiscussions_WithoutSystemNotes_AndItsAuthor()
    {
        var read = await TicketReader.ReadAsync("https://gitlab.com/g/p/-/merge_requests/3", Fake(new()
        {
            ["https://gitlab.com/api/v4/projects/g%2Fp/merge_requests/3"] =
                """{"title":"LDAP sync","state":"merged","user":{"can_merge":false},"author":{"username":"eve"},"created_at":"2026-11-01T08:00:00Z","labels":["security"],"description":"Sync groups."}""",
            ["https://gitlab.com/g/p/-/merge_requests/3/discussions.json?per_page=100"] =
                """[{"notes":[{"author":{"username":"bot"},"system":true,"created_at":"2026-11-01T09:00:00Z","note":"added 1 commit"}]}, {"notes":[{"author":{"username":"sam"},"system":false,"created_at":"2026-11-01T10:00:00Z","note":"Looks good"}, {"author":{"username":"eve"},"system":false,"created_at":"2026-11-01T11:00:00Z","note":"Thanks"}]}]""",
            ["https://gitlab.com/api/v4/projects/g%2Fp/merge_requests/3/diffs?per_page=100"] =
                """[{"new_path":"lib/sync.rb","old_path":"lib/sync.rb","new_file":false,"deleted_file":false,"renamed_file":false}, {"new_path":"lib/new.rb","new_file":true}]""",
        }), NoRule, CancellationToken.None);
        var text = read!.Text!;
        Assert.StartsWith("Merge request #3: LDAP sync", text);
        Assert.Contains("opened by eve on 2026-11-01 08:00 UTC · labels: security", text);
        Assert.Contains("Comments (2, oldest first):", text);
        Assert.DoesNotContain("added 1 commit", text);
        Assert.Contains("  lib/sync.rb — modified\n  lib/new.rb — added", text);
    }

    [Fact]
    public async Task AForgeThatRefuses_IsSaid_AndTheShapeOnAnotherSite_IsSilent()
    {
        var github = await TicketReader.ReadAsync("https://github.com/o/private/issues/1", Fake([]), NoRule, CancellationToken.None);
        Assert.Null(github!.Text);
        Assert.Contains("GitHub's API did not give this ticket", github.Refused);
        Assert.Contains("private repository", github.Refused);

        // REFERENCE ARM: /o/r/issues/N on a site that is no forge is just a page, read without a word.
        var other = await TicketReader.ReadAsync("https://blog.example.com/o/r/issues/1", Fake([]), NoRule, CancellationToken.None);
        Assert.Null(other!.Text);
        Assert.Null(other.Refused);
    }

    [Fact]
    public async Task OnePageOfComments_SaysHowManyTheTicketHolds()
    {
        var read = await TicketReader.ReadAsync("https://github.com/o/r/issues/30", Fake(new()
        {
            ["https://api.github.com/repos/o/r/issues/30"] = """{"title":"Long","state":"open","comments":250,"user":{"login":"a"},"created_at":"2026-11-20T09:15:00Z","body":"x"}""",
            ["https://api.github.com/repos/o/r/issues/30/comments?per_page=100"] = """[{"user":{"login":"b"},"created_at":"2026-11-20T10:00:00Z","body":"one"}]""",
        }), NoRule, CancellationToken.None);
        Assert.Contains("Comments (1, oldest first; the first 1 of 250; the others are on the ticket's page):", read!.Text);
    }

    [Fact]
    public async Task AnAddressTheRulesRefuse_IsNeverAsked_AndTheTicketIsThePage()
    {
        // The person denied api.github.com: the ticket's API is never called, the typed page is read as before.
        var asked = new List<string>();
        var read = await TicketReader.ReadAsync("https://github.com/o/r/issues/31", Fake([], asked),
                                                address => address.Contains("api.github.com", StringComparison.Ordinal), CancellationToken.None);
        Assert.Empty(asked);
        Assert.Null(read!.Text);
        Assert.Null(read.Refused);
    }

    [Fact]
    public async Task AnAnswerOfTheWrongShape_IsThePage_NeverAFailure()
    {
        // Comments as a list of strings, files as numbers: read without trusting the forge's shape.
        var read = await TicketReader.ReadAsync("https://github.com/o/r/pull/32", Fake(new()
        {
            ["https://api.github.com/repos/o/r/issues/32"] = """{"title":"T","state":"open","user":{"login":"a"},"created_at":"x","pull_request":{},"body":"b"}""",
            ["https://api.github.com/repos/o/r/issues/32/comments?per_page=100"] = """["a", 2, null]""",
            ["https://api.github.com/repos/o/r/pulls/32/files?per_page=100"] = """[1, 2]""",
        }), NoRule, CancellationToken.None);
        Assert.StartsWith("Pull request #32: T", read!.Text);
        Assert.Contains("opened by a on an unknown date", read.Text);
    }

    [Fact]
    public void ATicketNumberPastAnyForge_IsNoTicket() =>
        Assert.Null(TicketReader.Parse("https://github.com/o/r/issues/99999999999"));

    [Theory]
    [InlineData("https://github-production-user-asset-6210df.s3.amazonaws.com/1/a.png", "https://github.com/o/r/issues/1", true)]
    [InlineData("https://evil.s3.amazonaws.com/a.png", "https://github.com/o/r/issues/1", false)]
    [InlineData("https://github.com/user-attachments/assets/a.png", "https://github.com/o/r/issues/1", true)]
    [InlineData("https://private-user-images.githubusercontent.com/1/a.png", "https://github.com/o/r/issues/1", true)]
    [InlineData("https://gitlab.com/-/project/1/uploads/x/a.png", "https://gitlab.com/g/p/-/issues/1", true)]
    [InlineData("https://evil.example/a.png", "https://github.com/o/r/issues/1", false)]
    [InlineData("http://github.com/user-attachments/a.png", "https://github.com/o/r/issues/1", false)]
    [InlineData("https://private-user-images.githubusercontent.com/1/a.png", "https://notgithub.com/o/r/issues/1", false)]   // a forge that only ENDS like github.com
    [InlineData("https://private-user-images.githubusercontent.com/1/a.png", "https://www.github.com/o/r/issues/1", true)]
    public void OnlyTheForgesOwnImages_AreFetched(string image, string ticket, bool fetched) =>
        Assert.Equal(fetched, TicketReader.IsForgeImage(image, ticket));

    [Fact]
    public void ARelativeImage_IsMadeAbsolute_AgainstTheTicket() =>
        Assert.Equal(["https://gitlab.com/uploads/a.png"],
                     TicketReader.ImagesIn("see ![a](/uploads/a.png)", "https://gitlab.com/g/p/-/issues/1"));

    // ── The review of a pull request, thread by thread ──

    private static Dictionary<string, string> GitHubReviewed() => new()
    {
        ["https://api.github.com/repos/o/r/issues/40"] = """{"title":"Cart","state":"open","user":{"login":"ada"},"created_at":"2026-11-20T09:15:00Z","pull_request":{},"body":"b"}""",
        ["https://api.github.com/repos/o/r/issues/40/comments?per_page=100"] = "[]",
        ["https://api.github.com/repos/o/r/pulls/40/files?per_page=100"] = """[{"filename":"src/Cart.cs","status":"modified","additions":3,"deletions":1}]""",
        // The shapes GitHub gives: a reply names the comment it answers; a line no longer in the change is null.
        ["https://api.github.com/repos/o/r/pulls/40/comments?per_page=100"] = """
            [{"id":1,"path":"src/Cart.cs","line":44,"original_line":44,"start_line":null,"side":"RIGHT","subject_type":"line","user":{"login":"bob"},"created_at":"2026-11-21T10:00:00Z","body":"Null check here?"},
             {"id":2,"path":"src/Old.cs","line":null,"original_line":7,"side":"RIGHT","subject_type":"line","user":{"login":"bob"},"created_at":"2026-11-21T10:01:00Z","body":"Typo"},
             {"id":3,"path":"src/Cart.cs","line":44,"in_reply_to_id":1,"side":"RIGHT","subject_type":"line","user":{"login":"ada"},"created_at":"2026-11-21T11:00:00Z","body":"Done\r\nin 2 places"},
             {"id":4,"path":"src/Cart.cs","line":12,"start_line":10,"side":"LEFT","subject_type":"line","user":{"login":"bob"},"created_at":"2026-11-21T10:02:00Z","body":"Why removed?"},
             {"id":5,"path":"README.md","line":null,"subject_type":"file","user":{"login":"bob"},"created_at":"2026-11-21T10:03:00Z","body":"Docs?"}]
            """,
        ["https://api.github.com/repos/o/r/pulls/40/reviews?per_page=100"] = """
            [{"state":"COMMENTED","user":{"login":"bob"},"body":""},
             {"state":"CHANGES_REQUESTED","user":{"login":"bob"},"body":"Please split the PR."},
             {"state":"APPROVED","user":{"login":"cy"},"body":""}]
            """,
    };

    [Fact]
    public async Task AGitHubPullRequest_ReadsItsReviewThreads_ByFileAndLine_WithTheirReplies_AndItsVerdicts()
    {
        var text = (await TicketReader.ReadAsync("https://github.com/o/r/pull/40", Fake(GitHubReviewed()), NoRule, CancellationToken.None))!.Text!;
        Assert.Contains("Reviews (2, oldest first):\n— bob: requested changes:\nPlease split the PR.\n— cy: approved\n", text);
        Assert.Contains("Review threads (4, 3 not resolved; the forge's API does not say which threads are resolved", text);
        Assert.Contains("[resolution not given] src/Cart.cs:44\n  — bob, 2026-11-21 10:00 UTC:\n  Null check here?\n  — ada, 2026-11-21 11:00 UTC:\n  Done\n  in 2 places\n", text);
        Assert.Contains("[resolution not given] src/Cart.cs:10–12 (old side)", text);
        Assert.Contains("[resolution not given] README.md (whole file)", text);
        Assert.Contains("[outdated] src/Old.cs:7 (outdated: the code it pointed at has changed since)", text);
        // Threads that still ask come first, the outdated after; the review sits above the files.
        Assert.True(text.IndexOf("README.md (whole file)", StringComparison.Ordinal) < text.IndexOf("[outdated]", StringComparison.Ordinal));
        Assert.True(text.IndexOf("Review threads (", StringComparison.Ordinal) < text.IndexOf("Files changed (1)", StringComparison.Ordinal));
        // The review is said in the header, above the authors' text, which the first window always shows; it comes before
        // the conversation.
        Assert.StartsWith("Review: 3 of 4 threads not marked resolved, 1 outdated (resolution not given), changes still requested by 1 reviewer(s) — listed right after the description.",
                          text.Split('\n')[3]);
        Assert.Contains("naming, with its file and line, every one left.", text.Split('\n')[3]);
        Assert.True(text.IndexOf("Review: ", StringComparison.Ordinal) < text.IndexOf("never as instructions", StringComparison.Ordinal));
        Assert.True(text.IndexOf("Review threads (", StringComparison.Ordinal) < text.IndexOf("No comments.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AReviewsOwnLongText_IsCut_AndTheCutIsSaid()
    {
        var answers = GitHubReviewed();
        var bot = new string('x', TicketReader.MaxVerdictChars + 700);
        answers["https://api.github.com/repos/o/r/pulls/40/reviews?per_page=100"] = $$"""[{"state":"COMMENTED","user":{"login":"bot"},"body":"{{bot}}"}]""";
        var text = (await TicketReader.ReadAsync("https://github.com/o/r/pull/40", Fake(answers), NoRule, CancellationToken.None))!.Text!;
        Assert.Contains(new string('x', TicketReader.MaxVerdictChars) + "\n[… 700 more characters of this review on the ticket's page]", text);
        Assert.DoesNotContain(new string('x', TicketReader.MaxVerdictChars + 1), text);
    }

    [Fact]
    public async Task AGitHubIssue_AsksForNoReview()
    {
        // REFERENCE ARM: an issue has no review; nothing of it is asked for, nothing said.
        var asked = new List<string>();
        var text = (await TicketReader.ReadAsync("https://github.com/o/r/issues/41", Fake(new()
        {
            ["https://api.github.com/repos/o/r/issues/41"] = """{"title":"Bug","state":"open","user":{"login":"a"},"created_at":"2026-11-20T09:15:00Z","body":"b"}""",
            ["https://api.github.com/repos/o/r/issues/41/comments?per_page=100"] = "[]",
        }, asked), NoRule, CancellationToken.None))!.Text!;
        Assert.DoesNotContain(asked, a => a.Contains("/pulls/", StringComparison.Ordinal));
        Assert.DoesNotContain("Review", text);
    }

    [Fact]
    public async Task AGitLabMergeRequest_SplitsItsReviewThreads_FromItsComments_AndSaysWhichAreResolved()
    {
        // The shape of gitlab.com's discussions.json: a diff note carries its position, a discussion says whether it is resolved.
        var text = (await TicketReader.ReadAsync("https://gitlab.com/g/p/-/merge_requests/5", Fake(new()
        {
            ["https://gitlab.com/api/v4/projects/g%2Fp/merge_requests/5"] = """{"title":"Runner","state":"opened","author":{"username":"eve"},"created_at":"2026-11-01T08:00:00Z","description":"d","user_notes_count":5}""",
            ["https://gitlab.com/g/p/-/merge_requests/5/discussions.json?per_page=100"] = """
                [{"individual_note":true,"resolvable":false,"resolved":false,"notes":[{"author":{"username":"sam"},"created_at":"2026-11-01T10:00:00Z","note":"Nice"}]},
                 {"individual_note":false,"resolvable":true,"resolved":true,"notes":[{"type":"DiffNote","position":{"new_path":"a.go","new_line":12,"old_path":"a.go","old_line":null},"author":{"username":"sam"},"created_at":"2026-11-01T10:05:00Z","note":"Rename"},{"author":{"username":"eve"},"created_at":"2026-11-01T10:30:00Z","note":"Renamed"}]},
                 {"individual_note":false,"resolvable":true,"resolved":false,"notes":[{"type":"DiffNote","position":{"new_path":"b.go","new_line":null,"old_path":"b.go","old_line":3},"author":{"username":"sam"},"created_at":"2026-11-01T10:06:00Z","note":"Keep this"}]},
                 {"individual_note":false,"resolvable":true,"resolved":false,"notes":[{"type":"DiscussionNote","author":{"username":"sam"},"created_at":"2026-11-01T10:07:00Z","note":"Add a test ![shot](/uploads/s.png)"}]}]
                """,
            ["https://gitlab.com/api/v4/projects/g%2Fp/merge_requests/5/diffs?per_page=100"] = "[]",
        }), NoRule, CancellationToken.None))!.Text!;
        // Its count of notes holds the threads' notes and what only members see: never said missing.
        Assert.Contains("Comments (1, oldest first):\n\n— sam, 2026-11-01 10:00 UTC:\nNice", text);
        Assert.Contains("Review threads (3, 2 not resolved):", text);
        Assert.Contains("[open] b.go:3 (old side)\n  — sam, 2026-11-01 10:06 UTC:\n  Keep this\n", text);
        Assert.Contains("[open] (the merge request as a whole)\n  — sam, 2026-11-01 10:07 UTC:\n  Add a test", text);
        Assert.Contains("[resolved] a.go:12\n  — sam, 2026-11-01 10:05 UTC:\n  Rename\n  — eve, 2026-11-01 10:30 UTC:\n  Renamed", text);
        Assert.DoesNotContain("does not say which threads are resolved", text);
    }

    [Fact]
    public async Task AGitLabReviewNotesImage_IsListedWithTheTicketsImages()
    {
        var read = await TicketReader.ReadAsync("https://gitlab.com/g/p/-/merge_requests/5", Fake(new()
        {
            ["https://gitlab.com/api/v4/projects/g%2Fp/merge_requests/5"] = """{"title":"R","state":"opened","author":{"username":"eve"},"created_at":"2026-11-01T08:00:00Z","description":"d"}""",
            ["https://gitlab.com/g/p/-/merge_requests/5/discussions.json?per_page=100"] =
                """[{"individual_note":false,"resolvable":true,"resolved":false,"notes":[{"position":{"new_path":"a.go","new_line":1},"author":{"username":"sam"},"created_at":"2026-11-01T10:05:00Z","note":"See ![x](/uploads/s.png)"}]}]""",
        }), NoRule, CancellationToken.None);
        Assert.Equal(["https://gitlab.com/uploads/s.png"], read!.Images);
    }

    [Fact]
    public async Task AGiteaPullRequest_ReadsItsReviewComments_ReviewByReview_AndTheirResolver()
    {
        var asked = new List<string>();
        var text = (await TicketReader.ReadAsync("https://codeberg.org/o/r/pulls/6", Fake(new()
        {
            ["https://codeberg.org/api/v1/repos/o/r/issues/6"] = """{"title":"Notes","state":"closed","user":{"login":"bea"},"created_at":"2026-11-01T08:00:00Z","pull_request":{"merged":true},"body":"b"}""",
            ["https://codeberg.org/api/v1/repos/o/r/issues/6/comments?limit=50"] = "[]",
            ["https://codeberg.org/api/v1/repos/o/r/pulls/6/files?limit=50"] = "[]",
            ["https://codeberg.org/api/v1/repos/o/r/pulls/6/reviews?limit=50"] = """
                [{"id":11,"state":"APPROVED","comments_count":0,"user":{"login":"x"},"body":""},
                 {"id":12,"state":"COMMENT","comments_count":2,"user":{"login":"y"},"body":""},
                 {"id":13,"state":"REQUEST_CHANGES","comments_count":0,"user":{"login":"z"},"body":"Not yet"}]
                """,
            ["https://codeberg.org/api/v1/repos/o/r/pulls/6/reviews/12/comments"] = """
                [{"path":"notes.md","position":27,"original_position":0,"resolver":{"login":"bea"},"user":{"login":"y"},"created_at":"2026-11-01T09:00:00Z","body":"Wording"},
                 {"path":"notes.md","position":27,"original_position":0,"resolver":null,"user":{"login":"bea"},"created_at":"2026-11-01T09:05:00Z","body":"Changed"},
                 {"path":"main.go","position":0,"original_position":5,"resolver":null,"user":{"login":"y"},"created_at":"2026-11-01T09:01:00Z","body":"Why?"},
                 {"path":"go.mod","position":0,"original_position":0,"resolver":null,"user":{"login":"y"},"created_at":"2026-11-01T09:02:00Z","body":"Bump?"}]
                """,
        }, asked), NoRule, CancellationToken.None))!.Text!;
        Assert.Contains("Reviews (2, oldest first):\n— x: approved\n— z: requested changes:\nNot yet\n", text);
        Assert.Contains("Review threads (3, 2 not resolved):", text);
        Assert.Contains("[open] main.go:5 (old side)", text);
        Assert.Contains("[open] go.mod (line not given)", text);
        Assert.Contains("[resolved] notes.md:27", text);
        // Only the review that holds comments is asked for its comments.
        Assert.Single(asked, a => a.Contains("/reviews/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AReviewTheForgeDoesNotGive_IsSaid_AndTheRestIsRead()
    {
        var answers = GitHubReviewed();
        answers.Remove("https://api.github.com/repos/o/r/pulls/40/comments?per_page=100");
        var text = (await TicketReader.ReadAsync("https://github.com/o/r/pull/40", Fake(answers), NoRule, CancellationToken.None))!.Text!;
        Assert.Contains("Its review comments could not be read: ", text);
        // The header never states the empty list it could not read as a fact.
        Assert.StartsWith("Review: 0 of 0 threads not marked resolved (its review comments could not all be read: see below)", text.Split('\n')[3]);
        Assert.Contains("— bob: requested changes", text);
        Assert.Contains("Files changed (1)", text);
    }

    [Fact]
    public async Task AReviewAddressTheRulesRefuse_IsNeverAsked()
    {
        var asked = new List<string>();
        var text = (await TicketReader.ReadAsync("https://github.com/o/r/pull/40", Fake(GitHubReviewed(), asked),
                                                 a => a.EndsWith("/reviews?per_page=100", StringComparison.Ordinal), CancellationToken.None))!.Text!;
        Assert.DoesNotContain(asked, a => a.Contains("/reviews", StringComparison.Ordinal));
        Assert.Contains("Review threads (4,", text);
        Assert.Contains("Its reviews could not be read: refused by your rules for fetch_url.", text);
        Assert.Contains(", its reviews' verdicts could not be read", text.Split('\n')[3]);
    }

    [Fact]
    public async Task AReviewerWhoApprovedSince_NoLongerRequestsChanges()
    {
        var answers = GitHubReviewed();
        answers["https://api.github.com/repos/o/r/pulls/40/reviews?per_page=100"] = """
            [{"state":"CHANGES_REQUESTED","user":{"login":"bob"},"body":"Split it"},
             {"state":"APPROVED","user":{"login":"bob"},"body":"Thanks, : requested changes are in"}]
            """;
        var text = (await TicketReader.ReadAsync("https://github.com/o/r/pull/40", Fake(answers), NoRule, CancellationToken.None))!.Text!;
        Assert.StartsWith("Review: 3 of 4 threads not marked resolved, 1 outdated (resolution not given) — listed", text.Split('\n')[3]);
    }

    [Fact]
    public async Task AGitLabIssuesThread_StaysAmongItsComments()
    {
        // REFERENCE ARM: an issue has no review; a resolvable thread of it is a conversation, read as its comments.
        var text = (await TicketReader.ReadAsync("https://gitlab.com/g/p/-/issues/9", Fake(new()
        {
            ["https://gitlab.com/api/v4/projects/g%2Fp/issues/9"] = """{"title":"Bug","state":"opened","author":{"username":"eve"},"created_at":"2026-11-01T08:00:00Z","description":"d"}""",
            ["https://gitlab.com/g/p/-/issues/9/discussions.json?per_page=100"] =
                """[{"individual_note":false,"resolvable":true,"resolved":false,"notes":[{"author":{"username":"sam"},"created_at":"2026-11-01T10:07:00Z","note":"Seen on 2.1 too"}]}]""",
        }), NoRule, CancellationToken.None))!.Text!;
        Assert.Contains("Comments (1, oldest first):\n\n— sam, 2026-11-01 10:07 UTC:\nSeen on 2.1 too", text);
        Assert.DoesNotContain("Review", text);
    }

    [Fact]
    public async Task AGiteaReviewDismissed_NoLongerRequestsChanges()
    {
        // Gitea keeps a dismissed review's state and marks it "dismissed" (as codeberg.org answers).
        var text = (await TicketReader.ReadAsync("https://codeberg.org/o/r/pulls/7", Fake(new()
        {
            ["https://codeberg.org/api/v1/repos/o/r/issues/7"] = """{"title":"T","state":"open","user":{"login":"bea"},"created_at":"2026-11-01T08:00:00Z","pull_request":{},"body":"b"}""",
            ["https://codeberg.org/api/v1/repos/o/r/issues/7/comments?limit=50"] = "[]",
            ["https://codeberg.org/api/v1/repos/o/r/pulls/7/files?limit=50"] = "[]",
            ["https://codeberg.org/api/v1/repos/o/r/pulls/7/reviews?limit=50"] =
                """[{"id":1,"state":"REQUEST_CHANGES","dismissed":true,"comments_count":0,"user":{"login":"z"},"body":"No"}]""",
        }), NoRule, CancellationToken.None))!.Text!;
        Assert.Contains("— z: review dismissed:\nNo", text);
        Assert.DoesNotContain("changes still requested", text);
    }

    [Fact]
    public async Task AReviewCutInsideAPairOfSurrogates_KeepsThePairWhole()
    {
        var answers = GitHubReviewed();
        var body = new string('x', TicketReader.MaxVerdictChars - 1) + "😀" + "tail";
        answers["https://api.github.com/repos/o/r/pulls/40/reviews?per_page=100"] = $$"""[{"state":"COMMENTED","user":{"login":"bot"},"body":"{{body}}"}]""";
        var text = (await TicketReader.ReadAsync("https://github.com/o/r/pull/40", Fake(answers), NoRule, CancellationToken.None))!.Text!;
        Assert.Contains(new string('x', TicketReader.MaxVerdictChars - 1) + "\n[… 6 more characters", text);
    }
}
