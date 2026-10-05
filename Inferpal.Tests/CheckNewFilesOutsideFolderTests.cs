using System.IO;
using Inferpal.Config;
using Inferpal.Models;
using Inferpal.Services;
using Inferpal.Services.Commands;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  /check reads every new file of the repository, not only those under the workspace folder.
//
//  The workspace is often a folder of the repository (a solution in src/, a monorepo opened on one
//  package). `git status` lists the repository's new files, `git ls-files --others` only the folder it
//  runs in: a new file beside the workspace was neither reviewed nor named, and the answer was a
//  clean verdict on a file nobody read. Real git, real repository.
// ──────────────────────────────────────────────────────────────────────────────────────────────
public sealed class CheckNewFilesOutsideFolderTests : IDisposable
{
    private readonly string _repo = Directory.CreateTempSubdirectory("inferpal-checkoutside-").FullName;

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

    private async Task Git(string args) =>
        Assert.Equal(0, (await GitProcess.RunAsync(args, _repo, CancellationToken.None)).ExitCode);

    private async Task<string?> PromptOfACheck(string projectRoot)
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
        await CheckCommandHandler.HandleAsync(client, new InferpalConfig(), projectRoot, ["/check"],
            GitProcess.For(projectRoot), onProgress: null, CancellationToken.None);
        return prompt;
    }

    private async Task<string> RepoWithAWorkspaceFolder()
    {
        await Git("init -q");
        await Git("config user.email t@example.test");
        await Git("config user.name t");
        var src = Path.Combine(_repo, "src");
        Directory.CreateDirectory(Path.Combine(src, ".inferpal", "checks"));
        File.WriteAllText(Path.Combine(src, ".inferpal", "checks", "secrets.md"),
            "---\ndescription: no secrets\n---\nNo secret may be committed.");
        File.WriteAllText(Path.Combine(src, "App.cs"), "class App { }\n");
        await Git("add -A");
        await Git("-c commit.gpgsign=false commit -q -m first");
        return src;
    }

    [Fact]
    public async Task ANewFileBesideTheWorkspaceFolder_IsReviewed()
    {
        var src = await RepoWithAWorkspaceFolder();
        File.WriteAllText(Path.Combine(_repo, "deploy.yml"), "token: sk-live-123\n");

        var prompt = await PromptOfACheck(src);

        Assert.NotNull(prompt);
        Assert.Contains("sk-live-123", prompt);
    }

    [Fact]
    public async Task ANewFileInsideTheWorkspaceFolder_IsStillReviewed()
    {
        // Reference arm: the ordinary case keeps working.
        var src = await RepoWithAWorkspaceFolder();
        File.WriteAllText(Path.Combine(src, "Secret.cs"), "class Secret { const string K = \"sk-live-456\"; }\n");

        var prompt = await PromptOfACheck(src);

        Assert.NotNull(prompt);
        Assert.Contains("sk-live-456", prompt);
    }
}
