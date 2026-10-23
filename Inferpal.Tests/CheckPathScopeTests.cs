using System.IO;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services;
using Inferpal.Services.Commands;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>/check &lt;path&gt;</c> — what Solution Explorer's and VS Code's "Review Changes" send — against a real repository:
/// the uncommitted changes under a file or a folder only, new files included, the scope named above the findings; a
/// path git would match nothing for (a trailing separator, another case than the disk's) never answered "no change".
/// </summary>
public sealed class CheckPathScopeTests : IDisposable
{
    private readonly string _repo = Directory.CreateTempSubdirectory("inferpal-checkpath-").FullName;

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

    private void Write(string rel, string text)
    {
        var full = Path.Combine(_repo, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    /// <summary>Committed: src/Billing/Invoice.cs, lib/Util.cs, docs/Readme.md. Then Invoice.cs changed, and two new files —
    /// one under src/Billing, one under docs.</summary>
    private async Task Repository()
    {
        await Git("init -q -b main");
        await Git("config user.email t@example.test");
        await Git("config user.name t");
        Write("src/Billing/Invoice.cs", "class Invoice { int Total = 0; }\n");
        Write("lib/Util.cs", "class Util { }\n");
        Write("docs/Readme.md", "# Readme\n");
        await Git("add -A");
        await Git("-c commit.gpgsign=false commit -q -m base");
        Write("src/Billing/Invoice.cs", "class Invoice { int Total = 1; }\n");
        Write("src/Billing/New.cs", "class NewInBilling { }\n");
        Write("docs/draft.md", "OUTSIDE_THE_SCOPE\n");
    }

    private async Task<(string? Message, string Sent)> Check(string arg)
    {
        var sent = string.Empty;
        var reviewer = new FakeInferenceProvider
        {
            OnChatRequest = (_, history, _, _) =>
            {
                sent = history.Last().Content ?? string.Empty;
                return Task.FromResult(new ChatTurnResult("- [minor] src/Billing/Invoice.cs:1 — total starts at 1", [], 0, 0));
            },
        };
        var result = await CheckCommandHandler.HandleAsync(reviewer, new InferpalConfig(), _repo, ["/check", arg],
                                                           GitProcess.For(_repo), onProgress: null, CancellationToken.None);
        return (result.Message, sent);
    }

    [Fact]
    public async Task AFolder_IsReviewed_WithItsNewFiles_AndNothingOutsideIt()
    {
        await Repository();

        var (message, sent) = await Check($"\"{Path.Combine(_repo, "src", "Billing")}\"");

        Assert.Contains("+class Invoice { int Total = 1; }", sent);
        Assert.Contains("class NewInBilling", sent);
        Assert.DoesNotContain("OUTSIDE_THE_SCOPE", sent);   // a new file elsewhere is not this folder's change
        Assert.StartsWith(Strings.CheckPathLabel("src/Billing"), message);
    }

    [Fact]
    public async Task AFolderWithATrailingSeparator_IsTheSameFolder_NeverNoChange()
    {
        // Typed with a trailing slash, or as Solution Explorer names a folder: a quoted "…\" once escaped its own quote.
        await Repository();

        var (message, sent) = await Check($"\"{Path.Combine(_repo, "src", "Billing")}{Path.DirectorySeparatorChar}\"");

        Assert.Contains("+class Invoice { int Total = 1; }", sent);
        Assert.StartsWith(Strings.CheckPathLabel("src/Billing"), message);
    }

    [Fact]
    public async Task AFolderInAnotherCase_IsTheSameFolder_WhereTheFileSystemSaysSo()
    {
        await Repository();
        if (PathComparer.Comparison != StringComparison.OrdinalIgnoreCase) return;   // Linux: another case is another folder

        var (_, sent) = await Check(Path.Combine(_repo, "SRC", "billing"));

        Assert.Contains("+class Invoice { int Total = 1; }", sent);
    }

    [Fact]
    public async Task NothingChangedUnderThePath_IsSaid()
    {
        await Repository();

        var (message, _) = await Check("lib");

        Assert.Equal(Strings.CheckPathNoDiff("lib"), message);
    }

    [Fact]
    public async Task StagedChangesUnderThePath_AreNamedAsWhatWasReviewed()
    {
        await Repository();
        await Git("add src/Billing/Invoice.cs");

        var (message, sent) = await Check(Path.Combine("src", "Billing"));

        Assert.Contains("+class Invoice { int Total = 1; }", sent);
        Assert.StartsWith(Strings.CheckPathStagedLabel("src/Billing"), message);
    }

    [Fact]
    public async Task ARevisionOfTheSameName_KeepsItsMeaning_AndTheAnswerNamesTheSpellingForThePath()
    {
        await Repository();
        await Git("branch lib");

        var (asRevision, _) = await Check("lib");
        var (asPath, _)     = await Check("./lib");

        Assert.Contains(Strings.CheckRevisionAlsoPath("lib"), asRevision);
        Assert.Equal(Strings.CheckPathNoDiff("lib"), asPath);
    }

    [Fact]
    public async Task ACheckOfTheSameName_KeepsItsMeaning()
    {
        await Repository();
        Write(".inferpal/checks/lib.md", "Round half-up.");

        var (message, sent) = await Check("lib");

        Assert.Contains("Round half-up.", sent);                        // reviewed against that check
        Assert.DoesNotContain(Strings.CheckPathNoDiff("lib"), message); // not as the folder
    }

    [Fact]
    public async Task APathOutsideTheRepository_IsRefused()
    {
        await Repository();
        var outside = Directory.CreateTempSubdirectory("inferpal-elsewhere-").FullName;
        try
        {
            var (message, sent) = await Check(outside);

            Assert.Equal(Strings.CheckPathOutside(outside), message);
            Assert.Empty(sent);
        }
        finally { Directory.Delete(outside, true); }
    }

    [Fact]
    public void APath_IsReadFromTheRoot_OrAbsolute_WithoutItsTrailingSeparator_AndOnlyWhenItExists()
    {
        Directory.CreateDirectory(Path.Combine(_repo, "src", "Billing"));
        var folder = Path.Combine(_repo, "src", "Billing");
        Assert.Equal(folder, CheckCommandHandler.PathScope($"\"{folder}{Path.DirectorySeparatorChar}\"", _repo));
        Assert.Equal(folder, CheckCommandHandler.PathScope(Path.Combine("src", "Billing"), _repo));
        Assert.Null(CheckCommandHandler.PathScope(Path.Combine("src", "Missing"), _repo));
        Assert.EndsWith("src/Billing\"", CheckCommandHandler.Pathspec(folder, _repo));
    }
}
