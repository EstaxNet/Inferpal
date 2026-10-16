using System.IO;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services;
using Inferpal.Services.Commands;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  /check reviews the WHOLE change, with the repository's checks as added criteria.
//
//  Told to review "ONLY against the checks", a model answers "all checks pass" on changes that break
//  something no check names: the more checks a repository writes, the narrower its review. The prompt
//  is one English text (what the MODEL reads is never translated); the interface language reaches the
//  model through the reply-language line, and the severity words stay English in both prompts — the
//  parser reads no other. `/check <name>` stays a review against that one check: the user narrowed it.
// ──────────────────────────────────────────────────────────────────────────────────────────────
[Collection(CultureSerialCollection.Name)]
public sealed class CheckReviewWideningTests : IDisposable
{
    private const string Diff =
        "diff --git a/src/Cart.cs b/src/Cart.cs\n--- a/src/Cart.cs\n+++ b/src/Cart.cs\n@@ -1,3 +1,3 @@\n"
        + " class Cart {\n-    int Total(int a, int b) => a + b;\n+    int Total(int a, int b) => a - b;\n }\n";

    private readonly string _root = Directory.CreateTempSubdirectory("inferpal-checkwide-").FullName;

    public CheckReviewWideningTests()
    {
        var checks = Path.Combine(_root, ".inferpal", "checks");
        Directory.CreateDirectory(checks);
        File.WriteAllText(Path.Combine(checks, "secrets.md"), "---\ndescription: secrets\n---\nNo secret may be committed.");
        File.WriteAllText(Path.Combine(checks, "logging.md"), "---\ndescription: logging\n---\nErrors are logged.");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static Task<(string Output, int ExitCode)> Git(string args, CancellationToken _) =>
        Task.FromResult(args == "diff --staged" ? (Diff, 0) : (string.Empty, 0));

    /// <summary>The review in an English interface — the language is the subject of its own test.</summary>
    private async Task<(string? System, string? Message)> CheckInEnglishAsync(string[] parts, string answer = "Nothing to report.")
    {
        var previous = Strings.OverrideCulture?.Name;
        try
        {
            Strings.ApplyLanguage("en");
            return await CheckAsync(parts, answer);
        }
        finally { Strings.ApplyLanguage(previous); }
    }

    private async Task<(string? System, string? Message)> CheckAsync(string[] parts, string answer = "Nothing to report.")
    {
        string? system = null;
        var client = new FakeInferenceProvider
        {
            OnChatRequest = (_, history, _, _) =>
            {
                system = history.First(m => m.Role == "system").Content;
                return Task.FromResult(new ChatTurnResult(answer, [], 0, 0));
            },
        };
        var result = await CheckCommandHandler.HandleAsync(client, new InferpalConfig(), _root, parts, Git,
            onProgress: null, CancellationToken.None);
        return (system, result.Message);
    }

    [Fact]
    public async Task EveryCheck_ReviewsTheWholeChange_WithTheChecksAsAddedCriteria()
    {
        var (system, _) = await CheckInEnglishAsync(["/check"]);

        Assert.NotNull(system);
        Assert.Contains("any bug, regression or behaviour change it introduces", system, StringComparison.Ordinal);
        Assert.Contains("additional criteria, never the limit of the review", system, StringComparison.Ordinal);
        Assert.DoesNotContain("ONLY", system, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANamedCheck_StillReviewsAgainstThatCheckOnly()
    {
        // Reference arm: the user narrowed the review to one check, and that stays a review of that check.
        var (system, _) = await CheckInEnglishAsync(["/check", "secrets"]);

        Assert.NotNull(system);
        Assert.Contains("ONLY against the review checks provided", system, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFrenchInterface_SendsTheEnglishPrompt_PlusTheReplyLanguageLine()
    {
        var previous = Strings.OverrideCulture?.Name;
        try
        {
            Strings.ApplyLanguage("fr");
            var (french, _) = await CheckAsync(["/check"]);
            Strings.ApplyLanguage("en");
            var (english, _) = await CheckAsync(["/check"]);

            Assert.NotNull(french);
            Assert.NotNull(english);
            Assert.StartsWith(english!, french!, StringComparison.Ordinal);
            Assert.Contains("reply in French unless", french!, StringComparison.Ordinal);
            Assert.DoesNotContain("interface language", english!, StringComparison.Ordinal);
        }
        finally { Strings.ApplyLanguage(previous); }
    }

    [Fact]
    public async Task ANamedCheck_InAFrenchInterface_KeepsTheSeverityWordsInEnglish()
    {
        var previous = Strings.OverrideCulture?.Name;
        try
        {
            Strings.ApplyLanguage("fr");
            var (system, _) = await CheckAsync(["/check", "secrets"]);

            Assert.NotNull(system);
            Assert.Contains("(these three words in English)", system, StringComparison.Ordinal);
            Assert.Contains("reply in French unless", system, StringComparison.Ordinal);
        }
        finally { Strings.ApplyLanguage(previous); }
    }

    [Fact]
    public async Task ANamedCheckWithNoFinding_SaysThatCheckFoundNothing_NotTheWholeReview()
    {
        var (_, message) = await CheckInEnglishAsync(["/check", "secrets"], answer: "The check passes.");

        Assert.NotNull(message);
        Assert.Contains("the check turned up nothing on this diff", message, StringComparison.Ordinal);
        Assert.DoesNotContain("the review turned up", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoFinding_SaysTheReviewFoundNothing_NotOnlyTheChecks()
    {
        var (_, message) = await CheckInEnglishAsync(["/check"], answer: "No problem found; the checks pass.");

        Assert.NotNull(message);
        Assert.Contains("the review turned up nothing on this diff", message, StringComparison.Ordinal);
    }
}
