using System.IO;
using Inferpal.Localization;
using Inferpal.Services;
using Inferpal.Services.Governance;
using Inferpal.Services.Presentation;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>@url</c> and <c>@commit</c>: a chip that holds what it names, or a notice that says why there is none. <c>@url</c>
/// keeps <c>fetch_url</c>'s refusal of private addresses (no network here: every case is refused before a request);
/// <c>@commit</c> attaches one commit — never a range — read by <c>CheckTarget</c>, its excluded files left out. Real git,
/// real repository.
/// </summary>
[Collection(CultureSerialCollection.Name)]
public sealed class MentionUrlCommitTests : IDisposable
{
    private readonly string _repo = Directory.CreateTempSubdirectory("inferpal-mention-commit-").FullName;

    public void Dispose()
    {
        ContentExclusions.ForgetForTests(_repo);
        try
        {
            foreach (var f in Directory.EnumerateFiles(_repo, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_repo, recursive: true);
        }
        catch { /* best-effort cleanup */ }
    }

    private async Task<string> Git(string args)
    {
        var (output, exit) = await GitProcess.RunAsync(args, _repo, CancellationToken.None);
        Assert.True(exit == 0, $"git {args}: {output}");
        return output.Trim();
    }

    private void Write(string rel, string text) => File.WriteAllText(Path.Combine(_repo, rel), text);

    private async Task<string> Commit(string message)
    {
        await Git("add -A");
        await Git($"-c commit.gpgsign=false commit -q -m {message}");
        return await Git("rev-parse HEAD");
    }

    private async Task Init()
    {
        await Git("init -q -b main");
        await Git("config user.email t@example.test");
        await Git("config user.name t");
    }

    private static Task<MentionOutcome> Commit(string root, string typed) =>
        MentionController.CommitMentionAsync(root, typed, CancellationToken.None);

    [Theory]
    [InlineData("http://127.0.0.1:9/page")]
    [InlineData("localhost:8080/admin")]          // no scheme: read as https, and still refused
    [InlineData("http://169.254.169.254/latest")]
    public async Task AUrlToAPrivateAddress_IsANotice_NeverAFetch(string typed)
    {
        var url = await MentionController.UrlMentionAsync(typed, _ => null, CancellationToken.None);

        Assert.Null(url.Label);
        Assert.Null(url.Content);
        Assert.Contains("private/loopback", url.Notice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEmptyUrl_IsANotice() =>
        Assert.NotNull((await MentionController.UrlMentionAsync("  ", _ => null, CancellationToken.None)).Notice);

    [Fact]
    public async Task AFetchTheSettingsRefuse_IsTheirNotice_BeforeAnyRequest()
    {
        string? asked = null;
        var url = await MentionController.UrlMentionAsync("example.com/page", u => { asked = u; return "refused here"; },
                                                          CancellationToken.None);

        Assert.Equal("https://example.com/page", asked);   // judged on the address that would be fetched
        Assert.Equal("refused here", url.Notice);
        Assert.Null(url.Label);
    }

    [Fact]
    public async Task AnAddressThatDoesNotResolve_IsANoticeWithItsCause()
    {
        var url = await MentionController.UrlMentionAsync("https://nothing.invalid/page", _ => null, CancellationToken.None);

        Assert.Null(url.Label);
        Assert.Contains("could not be resolved", url.Notice, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ask about @url docs.rs/serde explain the derive", false, null, null)]     // the question goes on
    [InlineData("see @url npmjs.com/package/@types/node", true, "url", "npmjs.com/package/@types/node")]
    [InlineData("since @commit HEAD@{1}", true, "commit", "HEAD@{1}")]
    [InlineData("find @code auth logic", true, "code", "auth logic")]                   // a query keeps its spaces
    public void AnAddressOrARevision_EndsAtTheFirstSpace_AndMayHoldAnAt(string prompt, bool committed, string? category,
                                                                          string? query)
    {
        var state = MentionController.Parse(prompt);

        if (!committed)
        {
            Assert.IsNotType<MentionCommittedQuery>(state);
            return;
        }
        var c = Assert.IsType<MentionCommittedQuery>(state);
        Assert.Equal(category, c.Category);
        Assert.Equal(query, c.Query);
    }

    [Fact]
    public async Task TheLastCommit_IsItsMessageAndItsDiff_AgainstItsParent()
    {
        await Init();
        Write("App.cs", "class App { int FIRST_LINE = 1; }\n");
        await Commit("first");
        Write("App.cs", "class App { int SECOND_LINE = 2; }\n");
        var second = await Commit("second-commit-message");

        var chip = await Commit(_repo, "");

        Assert.Null(chip.Notice);
        Assert.Equal($"📜 @commit {second[..8]}", chip.Label);
        Assert.Contains("second-commit-message", chip.Content, StringComparison.Ordinal);
        Assert.Contains("+class App { int SECOND_LINE = 2; }", chip.Content, StringComparison.Ordinal);
        Assert.Contains("-class App { int FIRST_LINE = 1; }", chip.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARootCommit_IsCompared_WithNothing()
    {
        await Init();
        Write("App.cs", "class App { int ROOT_LINE = 1; }\n");
        await Commit("root");

        var chip = await Commit(_repo, "HEAD");

        Assert.Contains("+class App { int ROOT_LINE = 1; }", chip.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABranchName_AttachesItsLastCommit_NotWhatItAddsToHead()
    {
        await Init();
        Write("App.cs", "class App {}\n");
        await Commit("base");
        await Git("checkout -q -b feature");
        Write("Feature.cs", "class Feature { int ONE = 1; }\n");
        await Commit("feature-one");
        Write("Feature.cs", "class Feature { int TWO = 2; }\n");
        var tip = await Commit("feature-two");
        await Git("checkout -q main");

        var chip = await Commit(_repo, "feature");

        Assert.Equal($"📜 @commit {tip[..8]}", chip.Label);
        Assert.Contains("feature-two", chip.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("feature-one", chip.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFileTheIgnoreFilesExclude_IsLeftOut_AndCounted()
    {
        await Init();
        Write(".aiignore", "secrets.txt\n");
        Write("App.cs", "class App {}\n");
        await Commit("base");
        ContentExclusions.Declare(_repo, () => ContentExclusions.DefaultFiles);   // once the ignore file exists
        Write("secrets.txt", "SECRET_VALUE_5f2\n");
        Write("App.cs", "class App { int VISIBLE = 1; }\n");
        await Commit("both");

        var chip = await Commit(_repo, "");

        Assert.DoesNotContain("SECRET_VALUE_5f2", chip.Content, StringComparison.Ordinal);
        Assert.Contains("VISIBLE", chip.Content, StringComparison.Ordinal);
        Assert.Contains("1 file(s) excluded from AI tools", chip.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARange_AnUnknownName_AndAnOption_AreNotices()
    {
        await Init();
        Write("App.cs", "class App {}\n");
        await Commit("base");
        Write("App.cs", "class App { int X; }\n");
        await Commit("next");

        var range = await Commit(_repo, "HEAD~1..HEAD");
        Assert.Equal(Strings.MentionCommitRange("HEAD~1..HEAD"), range.Notice);

        var unknown = await Commit(_repo, "no-such-branch");
        Assert.Equal(Strings.MentionCommitUnknown("no-such-branch"), unknown.Notice);

        // The person's text on a git command line: refused as no revision before any git call, never git's own refusal.
        var option = await Commit(_repo, "--output=written.txt");
        Assert.Equal(Strings.MentionCommitUnknown("--output=written.txt"), option.Notice);
        Assert.False(File.Exists(Path.Combine(_repo, "written.txt")));
    }

    [Fact]
    public async Task ARepositoryWithoutACommit_SaysSo()
    {
        await Init();

        var chip = await Commit(_repo, "");

        Assert.Equal(Strings.MentionCommitNoneYet, chip.Notice);
    }

    [Fact]
    public async Task OutsideARepository_IsANotice()
    {
        var chip = await Commit(_repo, "");

        Assert.Null(chip.Label);
        Assert.Equal(Strings.MentionCommitUnavailable(Strings.GitNotRepo), chip.Notice);
    }

    [Fact]
    public async Task TheCommitList_MatchesTheHashOrTheSubject_ValuedByTheFullName()
    {
        await Init();
        Write("A.cs", "class A {}\n");
        var first = await Commit("add-parser");
        Write("A.cs", "class A { int B; }\n");
        var second = await Commit("fix-renderer");

        var all = await MentionController.FindCommitsAsync(_repo, "", CancellationToken.None);
        Assert.Equal([second, first], all.Select(c => c.Sha));

        var bySubject = await MentionController.FindCommitsAsync(_repo, "PARSER", CancellationToken.None);
        Assert.Equal(first, Assert.Single(bySubject).Sha);

        var byHash = await MentionController.FindCommitsAsync(_repo, second[..6], CancellationToken.None);
        Assert.Contains(byHash, c => c.Sha == second && c.Subject == "fix-renderer");
    }
}
