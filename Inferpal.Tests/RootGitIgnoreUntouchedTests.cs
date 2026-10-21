using System.IO;
using System.Linq;
using Inferpal.Config;
using Inferpal.Services;
using Inferpal.Services.Execution;
using Inferpal.Services.Lsp;
using Inferpal.Services.Rag;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Inferpal never writes the repository's <c>.gitignore</c>: it is the user's file. The file history keeps git away from
/// its snapshots from inside its own folder, and nothing else of the product names a <c>.gitignore</c>.
/// </summary>
/// <remarks>
/// A line appended to the root's <c>.gitignore</c> — or the whole-folder block of the oldest versions narrowed in place —
/// is an edit of the user's repository nobody asked for, for a rule the history folder already carries itself.
/// </remarks>
public sealed class RootGitIgnoreUntouchedTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"rootignore-{Guid.NewGuid():N}");

    public RootGitIgnoreUntouchedTests()
    {
        TestRagStore.Redirect();
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);   // git marks its objects read-only
            Directory.Delete(_root, recursive: true);
        }
        catch { /* best-effort cleanup */ }
    }

    private string GitIgnore => Path.Combine(_root, ".gitignore");

    /// <summary>Indexes <see cref="_root"/> as a repository and waits for the WHOLE pass, not only its start.</summary>
    private async Task IndexAsync()
    {
        if (!Directory.Exists(Path.Combine(_root, ".git"))) Directory.CreateDirectory(Path.Combine(_root, ".git"));
        await File.WriteAllTextAsync(Path.Combine(_root, "Widget.cs"), "public class Widget { public int Size; }\n");
        using var lsp   = new LspSemanticProvider();
        using var index = new ProjectIndexService(new FakeInferenceProvider(), new InferpalConfig { RagEnabled = true }, lsp);
        index.SetRoot(_root);
        index.StartIndexing(_root);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while ((index.IsIndexing || index.ChunkCount == 0) && DateTime.UtcNow < deadline) await Task.Delay(50);
        // WITNESS: the pass ran to its end — a pass that never started would leave any .gitignore alone too.
        Assert.True(!index.IsIndexing && index.ChunkCount > 0, "the indexing pass did not run: UNDECIDED, not green. " + index.Status);
    }

    [Fact]
    public async Task IndexingARepositoryWithoutAGitIgnore_CreatesNone()
    {
        await IndexAsync();

        Assert.False(File.Exists(GitIgnore), "indexing wrote a .gitignore into the repository");
    }

    [Theory]
    [InlineData("the block the oldest versions appended")]
    [InlineData("a rule of the user's")]
    [InlineData("no final line break")]
    [InlineData("a legacy code page")]
    [InlineData("UTF-16")]
    [InlineData("a byte order mark")]
    [InlineData("empty")]
    public async Task IndexingARepository_LeavesItsGitIgnore_ByteForByte_AndUntouched(string kind)
    {
        byte[] content = kind switch
        {
            "empty"                                  => [],
            "the block the oldest versions appended" => "bin/\n\n# Inferpal AI assistant\n.inferpal/\n"u8.ToArray(),
            "a rule of the user's"                   => "bin/\nobj/\n"u8.ToArray(),
            "no final line break"                    => "bin/"u8.ToArray(),
            "a legacy code page"                     => [.. "bin/\r\nDonn"u8, 0xE9, .. "es/\r\n"u8],
            "UTF-16"                                 => UnicodeEncodedFilesTests.Saved(UnicodeEncodedFilesTests.EncodingNamed("utf-16LE"), "bin/\r\n上传/\r\n"),
            "a byte order mark"                      => [0xEF, 0xBB, 0xBF, .. "bin/\n"u8],
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        await File.WriteAllBytesAsync(GitIgnore, content);
        // An hour old: a rewrite with the same bytes would still move the date, and the date says "untouched".
        var written = DateTime.UtcNow.AddHours(-1);
        File.SetLastWriteTimeUtc(GitIgnore, written);

        await IndexAsync();

        Assert.Equal(content, await File.ReadAllBytesAsync(GitIgnore));
        Assert.Equal(written, File.GetLastWriteTimeUtc(GitIgnore));
    }

    /// <summary>
    /// The other half, end to end: without the root's line, a snapshot is still no untracked file — the history folder's
    /// own rule is the one that keeps it out of <c>git status</c>.
    /// </summary>
    [Fact]
    public async Task AfterIndexingAndASnapshot_GitListsNoBackup_AndTheRootHasNoGitIgnore()
    {
        var (_, initExit) = await GitProcess.RunAsync("init -q", _root, CancellationToken.None);
        Assert.True(initExit == 0, "`git init` failed: this test is UNDECIDED, not green.");
        await IndexAsync();
        var source = Path.Combine(_root, "appsettings.json");
        await File.WriteAllTextAsync(source, "{ \"ApiKey\": \"x\" }");

        var snapshot = await new FileHistoryService().SnapshotAsync(source, CancellationToken.None);
        Assert.StartsWith(Path.Combine(_root, ".inferpal", "history"), snapshot);   // witness: the backup is in the tree

        var (status, exit) = await GitProcess.RunAsync("status --porcelain --untracked-files=all", _root, CancellationToken.None);
        Assert.True(exit == 0, $"git status failed ({exit}): {status} — UNDECIDED, not green.");
        Assert.Contains("appsettings.json", status);   // witness: untracked files ARE listed
        Assert.DoesNotContain(".inferpal", status);
        Assert.False(File.Exists(GitIgnore));
        Assert.True(File.Exists(Path.Combine(_root, ".inferpal", "history", ".gitignore")));
    }

    /// <summary>
    /// A history whose last snapshot is older than the folder's own rule has none, and only a line of the root's
    /// <c>.gitignore</c> kept it out of git: with that line removed, opening the workspace gives the folder its rule — the
    /// solution below the repository root, the history at the git root.
    /// </summary>
    [Fact]
    public async Task AnOldHistoryWithoutItsRule_GetsIt_WhenTheWorkspaceOpens()
    {
        var (_, initExit) = await GitProcess.RunAsync("init -q", _root, CancellationToken.None);
        Assert.True(initExit == 0, "`git init` failed: this test is UNDECIDED, not green.");
        var history = Path.Combine(_root, ".inferpal", "history");
        Directory.CreateDirectory(history);
        await File.WriteAllTextAsync(Path.Combine(history, "2026-08-28_22-59-02-620_99d7fa88_appsettings.json"), "{ \"ApiKey\": \"x\" }");
        var solution = Path.Combine(_root, "src");
        Directory.CreateDirectory(solution);
        var (before, _) = await GitProcess.RunAsync("status --porcelain --untracked-files=all", _root, CancellationToken.None);
        Assert.Contains(".inferpal/history/", before);   // witness: without a rule, git lists the old copy

        using (var lsp = new LspSemanticProvider())
        using (var index = new ProjectIndexService(new FakeInferenceProvider(), new InferpalConfig(), lsp))
            index.SetRoot(solution);

        Assert.True(File.Exists(Path.Combine(history, ".gitignore")));
        var (after, exit) = await GitProcess.RunAsync("status --porcelain --untracked-files=all", _root, CancellationToken.None);
        Assert.True(exit == 0, $"git status failed ({exit}): {after} — UNDECIDED, not green.");
        Assert.DoesNotContain(".inferpal", after);
        Assert.False(File.Exists(GitIgnore));
    }

    [Fact]
    public void OpeningAWorkspaceWithoutAHistory_CreatesNothing()
    {
        // Reference arm: the rule goes only where a history already is.
        Directory.CreateDirectory(Path.Combine(_root, ".git"));

        using (var lsp = new LspSemanticProvider())
        using (var index = new ProjectIndexService(new FakeInferenceProvider(), new InferpalConfig(), lsp))
            index.SetRoot(_root);

        Assert.False(Directory.Exists(Path.Combine(_root, ".inferpal")));
        Assert.False(File.Exists(GitIgnore));
    }

    /// <summary>
    /// The whole product, not only the indexer: the only code that names a <c>.gitignore</c> is the history's own rule, and
    /// the one reader that watches a command's changes. A new writer — <c>/onboard</c>, a command, the VS Code extension —
    /// turns this red by its name.
    /// </summary>
    [Fact]
    public void NoProductCode_NamesAGitIgnore_ButTheHistorysOwnRule()
    {
        string[] allowed = ["FileHistoryService.cs", "WorkTreeWatch.cs"];
        var sources = new[] { "Inferpal.Core", "Inferpal", "Inferpal.Host", "Inferpal.InProc", "Inferpal.Fim" }
            .SelectMany(ConventionCoverageTests.ProjectSources)
            .ToList();

        var naming = sources.Where(f => ConventionCoverageTests.CodeOnly(f).Contains(".gitignore", StringComparison.OrdinalIgnoreCase))
                            .Select(Path.GetFileName)
                            .Order(StringComparer.Ordinal)
                            .ToList();

        // WITNESS: the scan reads the real sources — the history's own rule is found.
        Assert.Contains("FileHistoryService.cs", naming);
        Assert.True(naming.All(n => allowed.Contains(n)),
            "Product code names a .gitignore — the repository's is the user's file, and Inferpal never writes it:\n  "
            + string.Join("\n  ", naming.Where(n => !allowed.Contains(n))));

        var extension = Directory.EnumerateFiles(Path.Combine(ConventionCoverageTests.RepoRoot(), "vscode", "src"), "*.ts",
                                                 SearchOption.AllDirectories).ToList();
        Assert.True(extension.Count > 10, "the VS Code sources were not found: the scan reads nothing there.");
        var tsNaming = extension.Where(f => SettingsSchemaDriftTests.NeutralizeTypeScriptComments(File.ReadAllText(f))
                                                .Contains(".gitignore", StringComparison.OrdinalIgnoreCase))
                                .Select(Path.GetFileName)
                                .ToList();
        Assert.True(tsNaming.Count == 0, "The VS Code extension names a .gitignore:\n  " + string.Join("\n  ", tsNaming));
    }
}
