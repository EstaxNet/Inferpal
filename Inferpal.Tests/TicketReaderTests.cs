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
            ["https://gitlab.com/g/p/-/merge_requests/3/discussions.json"] =
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
    public void OnlyTheForgesOwnImages_AreFetched(string image, string ticket, bool fetched) =>
        Assert.Equal(fetched, TicketReader.IsForgeImage(image, ticket));

    [Fact]
    public void ARelativeImage_IsMadeAbsolute_AgainstTheTicket() =>
        Assert.Equal(["https://gitlab.com/uploads/a.png"],
                     TicketReader.ImagesIn("see ![a](/uploads/a.png)", "https://gitlab.com/g/p/-/issues/1"));
}
