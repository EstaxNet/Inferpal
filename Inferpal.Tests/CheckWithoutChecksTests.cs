using System.IO;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services;
using Inferpal.Services.Commands;
using Inferpal.Services.Governance;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  /check in a repository that has no review check: the general review, never a refusal.
//
//  Refused, "Review a Commit…" was useless in every new repository, while the shipped prompt already reviews for any
//  bug, regression or behaviour change — the checks are added criteria. The system prompt stays the measured one; the
//  checks section says there is none, and the answer says so to the person with the gesture that adds some.
// ──────────────────────────────────────────────────────────────────────────────────────────────
[Collection(CultureSerialCollection.Name)]
public sealed class CheckWithoutChecksTests : IDisposable
{
    private readonly string _repo = Directory.CreateTempSubdirectory("inferpal-nochecks-").FullName;

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

    private async Task Git(string args)
    {
        var (output, exit) = await GitProcess.RunAsync(args, _repo, CancellationToken.None);
        Assert.True(exit == 0, $"git {args}: {output}");
    }

    private async Task Repository()
    {
        await Git("init -q -b main");
        await Git("config user.email t@example.test");
        await Git("config user.name t");
        File.WriteAllText(Path.Combine(_repo, "App.cs"), "class App { int Base = 1; }\n");
        await Git("add -A");
        await Git("-c commit.gpgsign=false commit -q -m base");
        File.WriteAllText(Path.Combine(_repo, "App.cs"), "class App { int Base = 2; }\n");
    }

    private async Task<(string Answer, string? System, string? User)> Check(params string[] args)
    {
        string? system = null, user = null;
        var client = new FakeInferenceProvider
        {
            OnChatRequest = (_, history, _, _) =>
            {
                system = history.First().Content;
                user   = history.Last().Content;
                return Task.FromResult(new ChatTurnResult("- [warning] App.cs:1 - Base changed without a reason.", [], 0, 0));
            },
        };
        var result = await CheckCommandHandler.HandleAsync(client, new InferpalConfig(), _repo, ["/check", .. args],
                                                            GitProcess.For(_repo), onProgress: null, CancellationToken.None);
        return (result.Message ?? "", system, user);
    }

    [Fact]
    public async Task WithoutAnyCheck_TheChangeIsReviewed_WithTheMeasuredPrompt_AndTheAnswerSaysThereIsNone()
    {
        await Repository();
        var (answer, system, user) = await Check();
        Assert.True(user is not null, answer);
        Assert.Contains("int Base = 2", user);
        Assert.Contains(ModelPrompts.CheckReviewNoChecks, user);
        Assert.Equal(CheckCommandHandler.ReviewSystemPrompt(narrowed: false), system);
        Assert.Contains("App.cs", answer);   // the finding is rendered
        // Above the findings: it qualifies them.
        Assert.StartsWith(Strings.CheckWithoutChecks, answer);
    }

    [Fact]
    public async Task WithoutAnyCheck_AnUnknownName_IsRefusedSayingThereIsNoCheck()
    {
        await Repository();
        var (answer, _, user) = await Check("security");
        Assert.Null(user);
        Assert.Equal(Strings.CheckUnknownRevision("security"), answer);
    }

    [Fact]
    public async Task WithoutAnyCheck_ARevision_IsStillReviewed()
    {
        await Repository();
        var (answer, _, user) = await Check("HEAD");
        Assert.True(user is not null, answer);
        Assert.Contains("int Base = 1", user);
        Assert.True(answer.IndexOf(Strings.CheckWithoutChecks, StringComparison.Ordinal) is > 0 and var at
                    && at < answer.IndexOf("App.cs:1", StringComparison.Ordinal), answer);   // under the label, above the findings
    }

    [Fact]
    public async Task AnUnreadableOnlyCheck_IsNamed_AndTheReviewStillRuns()
    {
        if (!OperatingSystem.IsWindows()) return;   // FileShare.None is advisory elsewhere
        await Repository();
        var dir = Path.Combine(_repo, ".inferpal", "checks");
        Directory.CreateDirectory(dir);
        var locked = Path.Combine(dir, "secrets.md");
        File.WriteAllText(locked, "No secret may be committed.");
        using var held = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);
        var (answer, _, user) = await Check();
        Assert.True(user is not null, answer);
        Assert.Contains(Strings.GovernanceFilesUnreadable(1, "secrets.md"), answer);
        // The check exists: what is said is that none applied, never that the repository has none.
        Assert.Contains(Strings.CheckWithoutChecks, answer);
        // Named after that very check: what is true is that it could not be read — no other remedy is.
        (answer, _, user) = await Check("secrets");
        Assert.Null(user);
        Assert.Equal(Strings.GovernanceFilesUnreadable(1, "secrets.md"), answer);
    }

    [Fact]
    public async Task WithACheck_NothingSaysThereIsNone()
    {
        // Reference arm: the line about checks belongs to a repository that has none.
        await Repository();
        var dir = Path.Combine(_repo, ".inferpal", "checks");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "secrets.md"), "No secret may be committed.");
        var (answer, _, user) = await Check();
        Assert.True(user is not null, answer);
        Assert.DoesNotContain(ModelPrompts.CheckReviewNoChecks, user);
        Assert.DoesNotContain(Strings.CheckWithoutChecks, answer);
        Assert.Contains("No secret may be committed.", user);
    }
}
