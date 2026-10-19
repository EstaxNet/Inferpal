using System.IO;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services;
using Inferpal.Services.Commands;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  /check reviews a branch, a range or a commit, not only the working tree.
//
//  A range is read as git log reads it — the commits B has and A has not, what a pull request shows — so its diff
//  starts at the common base: `git diff A..B` compares the tips and shows, reversed, what A received since the branches
//  parted. A check's name keeps its meaning over a revision of the same name. And the argument is the person's text on
//  a git command line: `--output=file` would make git write a file. Real git, real repository.
// ──────────────────────────────────────────────────────────────────────────────────────────────
[Collection(CultureSerialCollection.Name)]
public sealed class CheckTargetTests : IDisposable
{
    private readonly string _repo = Directory.CreateTempSubdirectory("inferpal-checktarget-").FullName;

    public void Dispose()
    {
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

    private void Write(string rel, string text)
    {
        var full = Path.Combine(_repo, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    private async Task Commit(string message)
    {
        await Git("add -A");
        await Git($"-c commit.gpgsign=false commit -q -m {message}");
    }

    /// <summary>main: base, then MAIN_LATER after feature left; feature: FEATURE_CHANGE.</summary>
    private async Task<(string Root, string Feature)> Repository()
    {
        await Git("init -q -b main");
        await Git("config user.email t@example.test");
        await Git("config user.name t");
        Write(".inferpal/checks/secrets.md", "---\ndescription: no secrets\n---\nNo secret may be committed.");
        Write("App.cs", "class App { int Base = 1; }\n");
        await Commit("base");
        var root = await Git("rev-parse HEAD");
        await Git("checkout -q -b feature");
        Write("Feature.cs", "class Feature { string FEATURE_CHANGE = \"x\"; }\n");
        await Commit("feature-work");
        var feature = await Git("rev-parse HEAD");
        await Git("checkout -q main");
        Write("Later.cs", "class Later { string MAIN_LATER = \"y\"; }\n");
        await Commit("main-later");
        return (root, feature);
    }

    /// <summary>The checks a refusal names: the repository's one check, named after its description.</summary>
    private const string Checks = "`no secrets`";

    private sealed class Counting
    {
        public int Calls;
    }

    private Task<(string Answer, string? Prompt, int GitCalls)> Check(params string[] args) => CheckThrough(null, args);

    /// <summary><paramref name="refuse"/>: the git commands that answer as a git that refused, everything else is real.</summary>
    private async Task<(string Answer, string? Prompt, int GitCalls)> CheckThrough(
        Func<string, (string, int)?>? refuse, params string[] args)
    {
        string? prompt = null;
        var client = new FakeInferenceProvider
        {
            OnChatRequest = (_, history, _, _) =>
            {
                prompt = history.Last().Content;
                return Task.FromResult(new ChatTurnResult("Nothing to report.", [], 0, 0));
            },
        };
        var counting = new Counting();
        var inner = GitProcess.For(_repo);
        GitRunner git = (a, ct) =>
        {
            counting.Calls++;
            return refuse?.Invoke(a) is { } refused ? Task.FromResult(refused) : inner(a, ct);
        };
        var result = await CheckCommandHandler.HandleAsync(client, new InferpalConfig(), _repo, ["/check", .. args],
                                                            git, onProgress: null, CancellationToken.None);
        return (result.Message ?? "", prompt, counting.Calls);
    }

    [Fact]
    public async Task ARange_ReviewsTheCommitsOfItsRightSide_NotWhatTheLeftReceivedSince()
    {
        await Repository();
        var (answer, prompt, _) = await Check("main..feature");
        Assert.NotNull(prompt);
        Assert.Contains("FEATURE_CHANGE", prompt);
        Assert.DoesNotContain("MAIN_LATER", prompt);
        Assert.StartsWith(Strings.CheckReviewedRange("main...feature", 1), answer);

        // Three dots mean the same; an empty side is HEAD (main here).
        (answer, prompt, _) = await Check("main...feature");
        Assert.DoesNotContain("MAIN_LATER", prompt);
        Assert.StartsWith(Strings.CheckReviewedRange("main...feature", 1), answer);
        (answer, prompt, _) = await Check("..feature");
        Assert.DoesNotContain("MAIN_LATER", prompt);
        Assert.StartsWith(Strings.CheckReviewedRange("HEAD...feature", 1), answer);
    }

    [Fact]
    public async Task ACommit_IsReviewedAlone_AndARootCommitAgainstTheEmptyTree()
    {
        var (root, feature) = await Repository();
        var (answer, prompt, _) = await Check(feature[..10]);
        Assert.Contains("FEATURE_CHANGE", prompt);
        Assert.DoesNotContain("MAIN_LATER", prompt);
        Assert.DoesNotContain("int Base", prompt);
        Assert.StartsWith(Strings.CheckReviewedCommit(feature[..8], "feature-work"), answer);

        (answer, prompt, _) = await Check(root);
        Assert.Contains("int Base = 1", prompt);
        Assert.StartsWith(Strings.CheckReviewedCommit(root[..8], "base"), answer);

        // HEAD is the commit it names, never the branch it points at (which would bring nothing to itself).
        (answer, prompt, _) = await Check("HEAD");
        Assert.Contains("MAIN_LATER", prompt);
        Assert.Contains("main-later", answer);
    }

    [Fact]
    public async Task ABranch_IsWhatItBringsToHead_AndNothingWhenItIsHead()
    {
        await Repository();
        var (answer, prompt, _) = await Check("feature");
        Assert.Contains("FEATURE_CHANGE", prompt);
        Assert.DoesNotContain("MAIN_LATER", prompt);
        Assert.StartsWith(Strings.CheckReviewedBranch("feature", 1), answer);

        await Git("checkout -q feature");
        (answer, prompt, _) = await Check("feature");
        Assert.Null(prompt);
        // The remedy names a branch this repository has, never a `main` it may not.
        Assert.Equal(Strings.CheckBranchAddsNothing("feature", "main"), answer);
        (_, prompt, _) = await Check("main..feature");
        Assert.Contains("FEATURE_CHANGE", prompt);
    }

    [Fact]
    public async Task ABranchSpelledInFull_OrARemoteByItsName_IsStillABranch()
    {
        var (_, feature) = await Repository();
        var (answer, prompt, _) = await Check("refs/heads/feature");
        Assert.Contains("FEATURE_CHANGE", prompt);
        Assert.StartsWith(Strings.CheckReviewedBranch("refs/heads/feature", 1), answer);

        // A remote's name resolves through its HEAD: what its default branch brings, never its tip commit alone.
        await Git($"update-ref refs/remotes/origin/feature {feature}");
        await Git("symbolic-ref refs/remotes/origin/HEAD refs/remotes/origin/feature");
        (answer, prompt, _) = await Check("origin");
        Assert.Contains("FEATURE_CHANGE", prompt);
        Assert.StartsWith(Strings.CheckReviewedBranch("origin", 1), answer);

        // And the remote's default branch is the base a branch that brings nothing is pointed to.
        await Git("branch merged main");
        (answer, _, _) = await Check("merged");
        Assert.Equal(Strings.CheckBranchAddsNothing("merged", "origin/feature"), answer);
    }

    [Fact]
    public async Task ACommitAtTheEdgeOfAShallowClone_IsRefused_NeverComparedWithTheEmptyTree()
    {
        await Repository();
        var clone = Directory.CreateTempSubdirectory("inferpal-checktarget-shallow-").FullName;
        try
        {
            var url = new Uri(_repo).AbsoluteUri;
            var (output, exit) = await GitProcess.RunAsync($"clone -q --depth 1 --branch main {url} \"{clone}\"", null,
                                                           CancellationToken.None);
            Assert.True(exit == 0, output);
            var head = (await GitProcess.RunAsync("rev-parse HEAD", clone, CancellationToken.None)).Output.Trim();
            var result = await CheckCommandHandler.HandleAsync(new FakeInferenceProvider(), new InferpalConfig(), clone,
                ["/check", "HEAD"], GitProcess.For(clone), onProgress: null, CancellationToken.None);
            // The clone's one commit carries the checks folder: the refusal is the shallow edge, not "no checks".
            Assert.Equal(Strings.CheckShallowBoundary(head[..8]), result.Message);
        }
        finally
        {
            foreach (var f in Directory.EnumerateFiles(clone, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            try { Directory.Delete(clone, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Theory]
    [InlineData("rev-parse --verify", "feature")]
    [InlineData("rev-parse --symbolic-full-name", "feature")]
    [InlineData("rev-list --count", "feature")]
    [InlineData("rev-list --count", "main..feature")]
    [InlineData("cat-file commit", "HEAD")]
    [InlineData("merge-base --all", "main..feature")]
    [InlineData("for-each-ref", "feature")]
    public async Task AGitThatRefuses_IsNamed_NeverReadAsNoRevisionACountOrASubject(string refused, string arg)
    {
        await Repository();
        var (answer, prompt, _) = await CheckThrough(
            a => a.StartsWith(refused, StringComparison.Ordinal) ? ("fatal: unable to read 0123abcd", 128) : null, arg);
        Assert.Null(prompt);
        Assert.Contains("fatal: unable to read 0123abcd", answer);
        Assert.DoesNotContain("-1 commit", answer);
        Assert.NotEqual(Strings.CheckUnknownName(arg, Checks), answer);
    }

    [Fact]
    public async Task ANameABranchAndATagShare_IsRefusedNamingBoth_AloneOrInARange()
    {
        await Repository();
        await Git("tag feature main");   // git would read `feature` as the tag, silently
        var (answer, prompt, _) = await Check("feature");
        Assert.Null(prompt);
        Assert.Equal(Strings.CheckAmbiguousRef("feature", "`refs/heads/feature`, `refs/tags/feature`"), answer);
        (answer, prompt, _) = await Check("main..feature");
        Assert.Null(prompt);
        Assert.Equal(Strings.CheckAmbiguousRef("feature", "`refs/heads/feature`, `refs/tags/feature`"), answer);

        // The spelling the refusal gives reviews the branch.
        (answer, prompt, _) = await Check("refs/heads/feature");
        Assert.Contains("FEATURE_CHANGE", prompt);
        Assert.StartsWith(Strings.CheckReviewedBranch("refs/heads/feature", 1), answer);
    }

    [Fact]
    public async Task AHashPrefixSeveralObjectsShare_IsRefusedSayingSo_NotNamedUnknown()
    {
        await Repository();
        // Two blobs whose names begin alike: git resolves neither, and "no revision of that name" would be false.
        // Six hex digits, so that no object of the repository itself shares the prefix.
        var seen = new Dictionary<string, string>();
        string first, second;
        for (var i = 0; ; i++)
        {
            var body = $"blob content {i}\n";
            var sha = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
                System.Text.Encoding.ASCII.GetBytes($"blob {body.Length}\0{body}"))).ToLowerInvariant();
            if (seen.TryGetValue(sha[..6], out var earlier)) { (first, second) = (earlier, body); break; }
            seen[sha[..6]] = body;
        }
        File.WriteAllBytes(Path.Combine(_repo, "a.blob"), System.Text.Encoding.ASCII.GetBytes(first));
        File.WriteAllBytes(Path.Combine(_repo, "b.blob"), System.Text.Encoding.ASCII.GetBytes(second));
        var written = (await Git("hash-object -w a.blob b.blob")).Split('\n').Select(l => l.Trim()).ToArray();
        var prefix = written[0][..6];
        Assert.StartsWith(prefix, written[1]);   // WITNESS: the two names do share the prefix

        var (answer, prompt, _) = await Check(prefix);
        Assert.Null(prompt);
        Assert.Equal(Strings.CheckAmbiguousHash(prefix, 2), answer);
    }

    [Fact]
    public async Task AMerge_IsReviewedAgainstItsFirstParent()
    {
        await Repository();
        await Git("-c commit.gpgsign=false merge -q --no-ff feature -m merge-feature");
        var (answer, prompt, _) = await Check("HEAD");
        // What the merge brought to main: the feature's change, never main's own history seen from the feature.
        Assert.Contains("FEATURE_CHANGE", prompt);
        Assert.DoesNotContain("MAIN_LATER", prompt);
        Assert.Contains("merge-feature", answer);
    }

    [Fact]
    public async Task SeveralCommonBases_AreSaid_AndGitsWarningNeverEntersTheReviewedDiff()
    {
        await Repository();
        // A criss-cross merge: x and y each merge the other's first commit, so they have two best common bases.
        await Git("checkout -q -b x main");
        Write("X.cs", "class X {}\n");
        await Commit("x1");
        await Git("checkout -q -b y main");
        Write("Y.cs", "class Y {}\n");
        await Commit("y1");
        await Git("checkout -q x");
        await Git("-c commit.gpgsign=false merge -q --no-ff y~0 -m x2");
        await Git("checkout -q y");
        await Git("-c commit.gpgsign=false merge -q --no-ff x~1 -m y2");
        Write("Y2.cs", "class Y2 { int CRISS_CROSS = 1; }\n");
        await Commit("y3");
        await Git("checkout -q main");
        Assert.Equal(2, (await Git("merge-base --all x y")).Split('\n').Length);   // WITNESS: two bases

        var (answer, prompt, _) = await Check("x..y");
        Assert.Contains("CRISS_CROSS", prompt);
        Assert.DoesNotContain("warning:", prompt);
        Assert.Contains(Strings.CheckSeveralBases(2, "").Split('`')[0], answer);
    }

    [Fact]
    public async Task TwoHistoriesWithNoCommitInCommon_SayThereIsNoBase()
    {
        await Repository();
        await Git("checkout -q --orphan lone");
        await Git("rm -rq --cached .");
        Write("Lone.cs", "class Lone {}\n");
        await Git("add Lone.cs");
        await Git("-c commit.gpgsign=false commit -q -m lone");
        await Git("checkout -q -f main");
        var (answer, prompt, _) = await Check("main..lone");
        Assert.Null(prompt);
        Assert.Equal(Strings.CheckNoCommonBase("main...lone"), answer);
    }

    [Fact]
    public async Task ATrueRootInAShallowClone_IsReviewed_AgainstTheEmptyTree()
    {
        await Repository();
        await Git("checkout -q --orphan lone");
        await Git("rm -rq --cached .");
        Write("Lone.cs", "class Lone { int LONE_ROOT = 1; }\n");
        await Git("add Lone.cs .inferpal");
        await Git("-c commit.gpgsign=false commit -q -m lone");
        var lone = await Git("rev-parse HEAD");
        await Git("checkout -q -f main");
        var clone = Directory.CreateTempSubdirectory("inferpal-checktarget-shallow-").FullName;
        try
        {
            var url = new Uri(_repo).AbsoluteUri;
            var (output, exit) = await GitProcess.RunAsync($"clone -q --depth 1 --no-single-branch --branch lone {url} \"{clone}\"",
                                                           null, CancellationToken.None);
            Assert.True(exit == 0, output);
            Assert.Equal("true", (await GitProcess.RunAsync("rev-parse --is-shallow-repository", clone,
                                                            CancellationToken.None)).Output.Trim());   // WITNESS
            string? prompt = null;
            var client = new FakeInferenceProvider
            {
                OnChatRequest = (_, history, _, _) =>
                {
                    prompt = history.Last().Content;
                    return Task.FromResult(new ChatTurnResult("Nothing to report.", [], 0, 0));
                },
            };
            var result = await CheckCommandHandler.HandleAsync(client, new InferpalConfig(), clone, ["/check", lone],
                                                               GitProcess.For(clone), onProgress: null, CancellationToken.None);
            Assert.True(prompt is not null, result.Message);
            Assert.Contains("LONE_ROOT", prompt);
        }
        finally
        {
            foreach (var f in Directory.EnumerateFiles(clone, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            try { Directory.Delete(clone, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task HeadInLowerCase_NamesTheCommit_WhereGitResolvesIt()
    {
        await Repository();
        // Only where the refs' file system folds case does git resolve `head` at all.
        var (_, exit) = await GitProcess.RunAsync("rev-parse --verify --quiet head", _repo, CancellationToken.None);
        if (exit != 0) return;
        var (answer, prompt, _) = await Check("head");
        Assert.True(prompt is not null, answer);
        Assert.Contains("MAIN_LATER", prompt);
        Assert.Contains("main-later", answer);
    }

    [Fact]
    public async Task ACutRevisionDiff_NamesARemedyACommittedChangeAllows()
    {
        var (_, _) = await Repository();
        await Git("checkout -q feature");
        Write("Big.cs", string.Concat(Enumerable.Range(0, 900).Select(i => $"// line {i} of a long generated file\n")));
        await Commit("big");
        var big = await Git("rev-parse HEAD");
        await Git("checkout -q main");
        Assert.True(File.Exists(Path.Combine(_repo, "App.cs")));   // WITNESS: back on main

        // "Stage it in smaller pieces" cannot apply to what is already committed.
        var (answer, _, _) = await Check("feature");
        Assert.Contains(Strings.CheckRangeDiffTruncated(0, 0).Split('`')[1], answer);
        Assert.DoesNotContain(Strings.CheckDiffTruncated(0, 0).Split('—')[^1].Trim(), answer);
        (answer, _, _) = await Check(big);
        Assert.Contains(Strings.CheckCommitDiffTruncated(0, 0).Split(':')[^1].Trim(), answer);
    }

    /// <summary>
    /// VS Code's "Review a Commit…": Enter runs the entry shown highlighted — a listed commit, or the typed text as its
    /// own entry — and the commits are the ones of the repository that holds the workspace. The two decisions are plain
    /// functions (<c>reviewCommitPick.ts</c>), run under Node by the probe of the journal; the suite holds the wiring.
    /// </summary>
    [Fact]
    public void TheVsCodeCommitPicker_RunsTheEntryItShows_FromTheWorkspacesRepository()
    {
        var picker = WebviewRebuildTests.TsCode("reviewCommit.ts");
        Assert.Contains("resolve(pick.selectedItems[0]?.revision)", picker, StringComparison.Ordinal);
        Assert.Contains("typedRevision(value)", picker, StringComparison.Ordinal);
        Assert.Contains("repositories[repositoryHolding(", picker, StringComparison.Ordinal);
        // Enter never reads the typed text behind the highlighted entry.
        Assert.DoesNotContain("pick.value", picker, StringComparison.Ordinal);

        // The graph's commit menu is a proposed API a published extension cannot contribute to.
        var manifest = File.ReadAllText(Path.Combine(WebviewRebuildTests.RepoRoot(), "vscode", "package.json"));
        Assert.Contains("\"inferpal.reviewCommit\"", manifest, StringComparison.Ordinal);   // WITNESS: the right manifest
        Assert.DoesNotContain("scm/historyItem", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AGitStoppedAtItsBudget_IsNamed_NeverReadAsNoRevision()
    {
        await Repository();
        var (answer, prompt, _) = await CheckThrough(
            a => a.StartsWith("rev-parse --verify", StringComparison.Ordinal)
                ? ("git gave no answer within 15s and was stopped.", -1) : null, "feature");
        Assert.Null(prompt);
        Assert.Contains("git gave no answer within 15s", answer);
    }

    [Fact]
    public async Task ACheckNamedLikeABranch_StaysTheCheck_AndSaysHowToReviewTheBranch()
    {
        await Repository();
        // A check without a description is named after its file.
        Write(".inferpal/checks/release.md", "Every public API change is noted.");
        await Commit("release-check");
        await Git("branch release feature");
        Write("App.cs", "class App { int Base = 2; }\n");   // a working-tree change for the check to review
        var (answer, prompt, _) = await Check("release");
        Assert.True(prompt is not null, answer);
        Assert.Contains("int Base = 2", prompt);
        Assert.DoesNotContain("FEATURE_CHANGE", prompt);
        Assert.EndsWith(Strings.CheckNameAlsoRevision("release", "refs/heads/release"), answer);

        // The spelling the answer gives reviews the branch: a remedy that works in the state it describes.
        (answer, prompt, _) = await Check("refs/heads/release");
        Assert.Contains("FEATURE_CHANGE", prompt);
        Assert.StartsWith(Strings.CheckReviewedBranch("refs/heads/release", 1), answer);

        // A tag of that name is spelled refs/tags/…: a commit, reviewed alone.
        await Git("branch -D release");
        await Git("tag release HEAD");
        (answer, _, _) = await Check("release");
        Assert.EndsWith(Strings.CheckNameAlsoRevision("release", "refs/tags/release"), answer);
        (answer, prompt, _) = await Check("refs/tags/release");
        Assert.Contains("+Every public API change", prompt);   // the diff line, not the check's own text
        Assert.Contains("release-check", answer);
    }

    [Theory]
    [InlineData("--output=x.txt")]
    [InlineData("-p")]
    [InlineData("a b")]
    [InlineData("\"x\"")]
    [InlineData("main..feature..x")]
    public async Task AnArgumentThatIsNoRevisionText_IsRefused_WithoutAnyGitCall(string arg)
    {
        await Repository();
        var (answer, prompt, calls) = await Check(arg.Split(' '));
        Assert.Null(prompt);
        Assert.Equal(0, calls);
        Assert.Equal(Strings.CheckUnknownName(arg, Checks), answer);
        Assert.False(File.Exists(Path.Combine(_repo, "x.txt")));
    }

    [Fact]
    public async Task NeitherACheckNorARevision_IsRefusedNamingBoth()
    {
        await Repository();
        var (answer, prompt, _) = await Check("no-such-thing");
        Assert.Null(prompt);
        Assert.Equal(Strings.CheckUnknownName("no-such-thing", Checks), answer);
    }

    [Fact]
    public async Task WithoutAnArgument_TheWorkingTreeIsStillReviewed()
    {
        // Reference arm: the target is opt-in.
        await Repository();
        Write("App.cs", "class App { int Base = 3; }\n");
        var (answer, prompt, _) = await Check();
        Assert.Contains("int Base = 3", prompt);
        Assert.DoesNotContain("FEATURE_CHANGE", prompt);
        Assert.DoesNotContain("`main", answer);
    }
}
