using System.IO;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services;
using Inferpal.Services.Commands;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ With nothing staged, <c>/check</c> reviews the working tree — and a NEW file is part of that change, but
/// <c>git diff</c> never shows one. The model was handed the status line "?? src/NewService.cs" alone, and its
/// silence came back "the checks turned up nothing on this diff": a clean verdict on code nobody read. New files now
/// go to the review as the diffs they will be once added (so a finding anchors to them), and the ones it cannot read
/// are named above the findings.
/// </summary>
public class CheckNewFilesTests
{
    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"checknew-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, ".inferpal", "checks"));
        Directory.CreateDirectory(Path.Combine(root, "src"));
        File.WriteAllText(Path.Combine(root, ".inferpal", "checks", "secrets.md"),
            "---\ndescription: no secrets\n---\nNo secret may be committed.");
        return root;
    }

    private static GitRunner Git(string status, string listing, List<string>? asked = null) => (args, _) =>
    {
        asked?.Add(args);
        return Task.FromResult(args switch
        {
            "status --short"                      => (status, 0),
            CheckCommandHandler.NewFilesListing   => (listing, 0),
            _                                     => ("", 0),
        });
    };

    [Fact]
    public async Task ANewFile_IsReviewed_AndAFindingInItIsAnchored()
    {
        var root = NewRoot();
        try
        {
            File.WriteAllText(Path.Combine(root, "src", "NewService.cs"),
                "class NewService\n{\n    const string Key = \"sk-live-123\";\n}\n");
            string? prompt = null;
            var client = new FakeInferenceProvider
            {
                OnChatRequest = (_, history, _, _) =>
                {
                    prompt = history.Last().Content;
                    return Task.FromResult(new ChatTurnResult("- [blocker] src/NewService.cs:3 — API key in clear text", [], 0, 0));
                },
            };

            var result = await CheckCommandHandler.HandleAsync(client, new InferpalConfig(), root, ["/check"],
                Git("?? src/NewService.cs\n", "src/NewService.cs\n"), onProgress: null, CancellationToken.None);

            Assert.NotNull(prompt);
            Assert.Contains("+++ b/src/NewService.cs", prompt);
            Assert.Contains("+    const string Key = \"sk-live-123\";", prompt);
            Assert.Contains("src/NewService.cs:3", result.Message);
            Assert.DoesNotContain(Strings.CheckAnchorUnanchored, result.Message);   // anchored to the new file's lines
            Assert.DoesNotContain(Strings.CheckNewFilesNotReviewed(1, "src/NewService.cs"), result.Message);   // all read
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ANewFileItCannotRead_IsNamed_AboveTheFindings()
    {
        var root = NewRoot();
        try
        {
            File.WriteAllBytes(Path.Combine(root, "src", "logo.bin"), [0x89, 0x50, 0x00, 0x01, 0x00]);
            var client = new FakeInferenceProvider
            {
                OnChat = (_, _) => Task.FromResult(new ChatTurnResult("No finding.", [], 0, 0)),
            };

            var result = await CheckCommandHandler.HandleAsync(client, new InferpalConfig(), root, ["/check"],
                Git("?? src/logo.bin\n", "src/logo.bin\n"), onProgress: null, CancellationToken.None);

            var note = Strings.CheckNewFilesNotReviewed(1, "src/logo.bin");
            Assert.StartsWith(note, result.Message);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task AStagedChange_IsReviewedAlone_AsBefore()   // reference arm: staged wins, like /commit
    {
        var root = NewRoot();
        try
        {
            var asked = new List<string>();
            GitRunner git = (args, ct) =>
            {
                asked.Add(args);
                return Task.FromResult(args == "diff --staged"
                    ? ("diff --git a/x.cs b/x.cs\n--- a/x.cs\n+++ b/x.cs\n@@ -1 +1 @@\n-a\n+b\n", 0)
                    : ("", 0));
            };
            var client = new FakeInferenceProvider { OnChat = (_, _) => Task.FromResult(new ChatTurnResult("No finding.", [], 0, 0)) };

            var result = await CheckCommandHandler.HandleAsync(client, new InferpalConfig(), root, ["/check"],
                git, onProgress: null, CancellationToken.None);

            Assert.Contains("diff --staged", asked);                                // WITNESS: the review ran
            Assert.DoesNotContain("ls-files --others --exclude-standard", asked);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ANewFile_ReadsAsTheDiffGitWillShow()
    {
        var diff = GitCommitPolicy.NewFileDiff(@"src\A.cs", "line one\r\nline two\r\n");

        Assert.Contains("+++ b/src/A.cs\n@@ -0,0 +1,2 @@\n+line one\n+line two\n", diff);
        var anchors = Services.Governance.DiffAnchors.Parse(diff);
        Assert.True(anchors.Covers("src/A.cs", 1) && anchors.Covers("src/A.cs", 2));
        Assert.False(anchors.Covers("src/A.cs", 3));
    }
}
