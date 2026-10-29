using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Services;
using Inferpal.Services.Commands;
using Inferpal.Services.Docs;
using Inferpal.Services.Editor;
using Inferpal.Services.Execution;
using Inferpal.Services.Lsp;
using Inferpal.Services.Mcp;
using Inferpal.Services.Rag;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// What a command, a test run, a user's shell tool or an MCP tool changes enters the run like a write: <c>/undo-run</c>
/// gives it back byte for byte, and what cannot be watched is said instead of promised.
/// </summary>
/// <remarks>
/// ⚠ These tools change files WITHOUT a backup of their own (<see cref="FileHistoryService.BackUpBeforeChangeAsync"/>):
/// without the watch of <c>ToolRegistry.ExecuteAsync</c> (<see cref="WorkTreeBracket"/>), a <c>dotnet format</c> or a
/// <c>sed -i</c> run by the agent was out of <c>/undo-run</c>'s reach while "Undo run" promised the run back. Real git,
/// real shell: the commands used here mean the same in PowerShell and in a POSIX shell.
/// </remarks>
public class CommandChangesUndoTests : IDisposable
{
    private readonly string _root;
    private readonly TimeSpan _slowReading = WorkTreeWatch.SlowReading;

    public CommandChangesUndoTests()
    {
        // A loaded test machine must not turn the watch off for "slowness" in the middle of a test.
        WorkTreeWatch.SlowReading = TimeSpan.FromMinutes(5);
        _root = Directory.CreateTempSubdirectory("inferpal-cmdundo").FullName;
    }

    public void Dispose()
    {
        WorkTreeWatch.SlowReading = _slowReading;
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // ── fixtures ────────────────────────────────────────────────────────────────

    private static bool Git(string workDir, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = workDir, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit(30_000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>A committed repository: a.txt, b.txt, sub/c.txt and sub/d.txt — or the test is UNDECIDED, never green.</summary>
    private string Repository()
    {
        Assert.True(Git(_root, "init", "-q", "."), "`git init` failed: this test is UNDECIDED, not green.");
        Git(_root, "config", "user.email", "test@example.invalid");
        Git(_root, "config", "user.name", "test");
        Git(_root, "config", "core.autocrlf", "false");
        File.WriteAllText(Path.Combine(_root, "a.txt"), "alpha\n");
        File.WriteAllText(Path.Combine(_root, "b.txt"), "bravo\n");
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        File.WriteAllText(Path.Combine(_root, "sub", "c.txt"), "charlie\n");
        File.WriteAllText(Path.Combine(_root, "sub", "d.txt"), "delta\n");
        Assert.True(Git(_root, "add", "-A") && Git(_root, "commit", "-q", "-m", "seed"),
                    "the seed commit failed: this test is UNDECIDED, not green.");
        // Seeds an hour old: a file a call makes by renaming or copying keeps its dates, and only old dates tell it apart
        // from a file git was ignoring all along.
        foreach (var file in Directory.EnumerateFiles(_root, "*.txt", SearchOption.AllDirectories))
        {
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddHours(-1));
            File.SetCreationTimeUtc(file, DateTime.UtcNow.AddHours(-1));
        }
        return _root;
    }

    /// <summary>Every file of the work tree but git's and Inferpal's own, by relative path, with its bytes.</summary>
    private static Dictionary<string, byte[]> Tree(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                 .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
                 .Where(r => !r.StartsWith(".git/", StringComparison.Ordinal) && !r.StartsWith(".inferpal/", StringComparison.Ordinal)
                             && r != ".git")
                 .ToDictionary(r => r, r => File.ReadAllBytes(Path.Combine(root, r)), StringComparer.Ordinal);

    private static void AssertSameTree(Dictionary<string, byte[]> expected, Dictionary<string, byte[]> actual)
    {
        Assert.Equal(expected.Keys.OrderBy(k => k, StringComparer.Ordinal), actual.Keys.OrderBy(k => k, StringComparer.Ordinal));
        foreach (var (path, bytes) in expected)
            Assert.True(bytes.AsSpan().SequenceEqual(actual[path]), $"{path} is not given back byte for byte.");
    }

    private static ToolRegistry Registry(string root, IApprovalService? approval = null, string customTools = "",
                                         OpenDocumentOverlay? overlay = null)
    {
        var config = new InferpalConfig { CustomTools = customTools };
        var client = new FakeInferenceProvider();
        var editor = new NullEditorSurface();
        approval ??= new NoopApproval();
        var index  = new ProjectIndexService(client, config, new LspSemanticProvider());
        index.SetRoot(root);
        return new ToolRegistry(editor, approval, config, index, client, new ProjectMapService(editor),
                                new McpToolService(config, approval), new DocsIndexService(client, config),
                                overlay ?? new OpenDocumentOverlay(), new NullDebugSession());
    }

    private static JsonElement Args(object o) => JsonSerializer.SerializeToElement(o);

    private static Task<string> Run(ToolRegistry tools, string command, CancellationToken ct = default) =>
        tools.ExecuteAsync("run_command", Args(new { command }), ct);

    private static Task<string> Undo(ToolRegistry tools, string root) =>
        UndoRunCommandHandler.HandleAsync(tools.History, ["/undo-run"], root, CancellationToken.None);

    // ── the commands ────────────────────────────────────────────────────────────

    /// <summary>
    /// ⚠ A write whose Smart Fix may run the repository's own check (an <c>eslint --fix</c> rewriting other files) is
    /// watched like a command: unwatched, <c>/undo-run</c> gave back the written file and left the rest, unsaid.
    /// </summary>
    [Fact]
    public void AWriteThatMayRunTheRepositorysCheck_IsWatched()
    {
        var config   = new InferpalConfig();
        var smartFix = new SmartFixValidator(config, () => _root);
        var history  = new FileHistoryService();
        ITool write  = new WriteFileTool(new NoopApproval(), history, () => _root, smartFix);
        var ts = Args(new { path = "src/a.ts", content = "x" });
        File.WriteAllText(Path.Combine(_root, "package.json"), "{}");
        Assert.False(write.NeedsWorkTreeWatchFor(ts));   // the built-in checks only build

        Directory.CreateDirectory(Path.Combine(_root, ".inferpal"));
        File.WriteAllText(Path.Combine(_root, ".inferpal", "validators.json"),
                          """{ ".ts": { "command": "npx eslint --fix .", "marker": "package.json" } }""");

        Assert.True(write.NeedsWorkTreeWatchFor(ts));
        Assert.False(write.NeedsWorkTreeWatchFor(Args(new { path = "src/A.cs", content = "x" })));   // not a file it checks
        Assert.True(((ITool)new ApplyDiffTool(new NoopApproval(), history, () => _root, smartFix)).NeedsWorkTreeWatchFor(ts));
        Assert.True(((ITool)new ApplyEditsTool(new NoopApproval(), history, () => _root, smartFix))
            .NeedsWorkTreeWatchFor(Args(new { edits = new[] { new { path = "src/a.ts", old_content = "x", new_content = "y" } } })));
        config.SmartFixEnabled = false;
        Assert.False(write.NeedsWorkTreeWatchFor(ts));
    }

    /// <summary>
    /// ⚠ Where the file system folds case, <c>git mv Foo.txt foo.txt</c> renames a file that was there before the call:
    /// read as created, <c>/undo-run</c> would delete it.
    /// </summary>
    [Fact]
    public async Task ACaseOnlyRename_IsNoCreation()
    {
        if (OperatingSystem.IsLinux()) return;   // a file system that keeps both names: two files, and the rule is Ordinal
        var root   = Repository();
        var before = await WorkTreeWatch.ReadAsync(root, null, CancellationToken.None);
        Assert.True(Git(root, "mv", "a.txt", "A.txt"), "`git mv` failed: this test is UNDECIDED, not green.");

        var (changes, _) = await WorkTreeWatch.CompareAsync(before, CancellationToken.None);

        Assert.DoesNotContain(changes, c => c.Kind == WorkTreeChangeKind.Created);
        Assert.True(File.Exists(Path.Combine(root, "A.txt")));
    }

    [Fact]
    public async Task WhatACommandChanged_IsGivenBackByUndoRun_ByteForByte()
    {
        var root  = Repository();
        File.WriteAllText(Path.Combine(root, "b.txt"), "bravo, edited by hand before the run\n");   // dirty before
        File.WriteAllText(Path.Combine(root, "notes.txt"), "untracked notes\n");                    // untracked before
        var before = Tree(root);
        var tools  = Registry(root);

        using (tools.History.BeginRunScope())
            await Run(tools, "echo more >> a.txt; echo again >> b.txt; echo fresh > created.txt; echo gone > notes.txt; rm sub/d.txt");

        // WITNESS: the command really changed the tree — a command that failed to run would make the undo trivially "right".
        var after = Tree(root);
        Assert.False(after.ContainsKey("sub/d.txt"), "the command did not run: this test is UNDECIDED, not green.");
        Assert.True(after.ContainsKey("created.txt"));

        var run = tools.History.Runs[0];
        Assert.Equal(5, run.FileCount);   // a.txt, b.txt, created.txt, notes.txt, sub/d.txt

        var report = await Undo(tools, root);
        AssertSameTree(before, Tree(root));
        Assert.Contains(Strings.UndoRunIgnoredNote, report);
    }

    [Fact]
    public async Task AFolderACommandDeleted_ComesBackWithItsFiles()
    {
        var root   = Repository();
        var before = Tree(root);
        var tools  = Registry(root);

        using (tools.History.BeginRunScope())
            await Run(tools, "git rm -r -q sub");

        Assert.False(Directory.Exists(Path.Combine(root, "sub")), "the command did not run: UNDECIDED.");
        await Undo(tools, root);
        AssertSameTree(before, Tree(root));
    }

    [Fact]
    public async Task ADiscardedEdit_GitCheckout_IsGivenBack()
    {
        var root = Repository();
        File.WriteAllText(Path.Combine(root, "a.txt"), "alpha, work in progress\n");
        var before = Tree(root);
        var tools  = Registry(root);

        using (tools.History.BeginRunScope())
            await Run(tools, "git checkout -- a.txt");

        Assert.Equal("alpha\n", File.ReadAllText(Path.Combine(root, "a.txt")));   // the command threw the edit away
        await Undo(tools, root);
        AssertSameTree(before, Tree(root));
    }

    [Fact]
    public async Task ACommit_ThatLeavesTheTreeClean_ChangesNoFile()
    {
        var root = Repository();
        File.WriteAllText(Path.Combine(root, "a.txt"), "alpha, committed by the agent\n");
        var tools = Registry(root);

        using (tools.History.BeginRunScope())
            await Run(tools, "git commit -q -am agent");

        // Reference arm: the index moved, the files did not — nothing to give back, nothing counted.
        Assert.Equal(0, tools.History.Runs[0].FileCount);
    }

    [Fact]
    public async Task ACheckoutOfAnotherBranch_IsGivenBack_EvenWithATreeLeftClean()
    {
        var root = Repository();
        Assert.True(Git(root, "checkout", "-q", "-b", "other") , "UNDECIDED: no branch.");
        File.WriteAllText(Path.Combine(root, "a.txt"), "alpha on the other branch\n");
        File.WriteAllText(Path.Combine(root, "e.txt"), "only on the other branch\n");
        Assert.True(Git(root, "add", "-A") && Git(root, "commit", "-q", "-m", "other"), "UNDECIDED: no commit.");
        Assert.True(Git(root, "checkout", "-q", "-"), "UNDECIDED: no checkout back.");
        var before = Tree(root);
        var tools  = Registry(root);

        using (tools.History.BeginRunScope())
            await Run(tools, "git checkout -q other");

        Assert.True(File.Exists(Path.Combine(root, "e.txt")), "the checkout did not run: UNDECIDED.");
        var report = await Undo(tools, root);
        // The files come back; the branch does not — undo gives back content, never the repository's state.
        AssertSameTree(before, Tree(root));
        Assert.DoesNotContain("⚠", report);
    }

    [Fact]
    public async Task TheWatch_WritesNothingToTheRepository()
    {
        var root = Repository();
        File.WriteAllText(Path.Combine(root, "b.txt"), "bravo, dirty\n");
        File.WriteAllText(Path.Combine(root, "untracked.txt"), "loose\n");
        var index   = File.ReadAllBytes(Path.Combine(root, ".git", "index"));
        var objects = Directory.EnumerateFiles(Path.Combine(root, ".git", "objects"), "*", SearchOption.AllDirectories).Count();
        var tools   = Registry(root);

        using (tools.History.BeginRunScope())
            await Run(tools, "echo more >> a.txt");

        Assert.True(index.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(root, ".git", "index"))),
                    "the watch rewrote the user's index.");
        Assert.Equal(objects,
                     Directory.EnumerateFiles(Path.Combine(root, ".git", "objects"), "*", SearchOption.AllDirectories).Count());
        Assert.False(File.Exists(Path.Combine(root, ".git", "refs", "stash")), "the watch stashed something.");
    }

    // ── what is not the command's ──────────────────────────────────────────────

    /// <summary>The person edits and saves while the card waits: their change is not the command's.</summary>
    private sealed class EditingWhileTheCardWaits(string file) : ApprovalServiceBase(new InferpalConfig(), () => null)
    {
        protected override Task<ApprovalDecision> PromptUserAsync(string message, DiffInfo? diff, CancellationToken ct)
        {
            File.WriteAllText(file, "charlie, saved by the person while the card waited\n");
            return Task.FromResult(ApprovalDecision.Once);
        }
    }

    [Fact]
    public async Task WhatThePersonSavesWhileTheCardWaits_IsNotTheCommands()
    {
        var root  = Repository();
        var saved = Path.Combine(root, "sub", "c.txt");
        var tools = Registry(root, new EditingWhileTheCardWaits(saved));

        using (tools.History.BeginRunScope())
            await Run(tools, "rm a.txt");

        var changes = tools.History.Runs[0].Changes.Select(c => Path.GetFileName(c.OriginalPath)).ToList();
        Assert.Contains("a.txt", changes);          // the command's: given back by an undo
        Assert.DoesNotContain("c.txt", changes);    // the person's: an undo must not throw it away
        await Undo(tools, root);
        Assert.Equal("charlie, saved by the person while the card waited\n", File.ReadAllText(saved));
        Assert.True(File.Exists(Path.Combine(root, "a.txt")));
    }

    [Fact]
    public async Task AFileTheRunWroteItself_IsNotCountedTwice()
    {
        var root  = Repository();
        var tools = Registry(root);

        using (tools.History.BeginRunScope())
            await tools.ExecuteAsync("write_file", Args(new { path = Path.Combine(root, "new.txt"), content = "written\n" }),
                                     CancellationToken.None);

        var run = tools.History.Runs[0];
        Assert.Equal(1, run.FileCount);
        Assert.Equal(1, run.WriteCount);
        Assert.Empty(run.Uncovered);
    }

    [Fact]
    public async Task AWriteTheCallBackedUpItself_IsNotRecordedAgain()
    {
        var root    = Repository();
        var file    = Path.Combine(root, "a.txt");
        var history = new FileHistoryService();
        using (history.BeginRunScope())
        {
            var watch = await WorkTreeBracket.OpenAsync(root, null, CancellationToken.None);
            File.WriteAllText(file, "alpha, written by the call through its own backup\n");
            await history.RecordWatchedCallAsync(watch, [file], "test", CancellationToken.None);
        }
        // Its own backup is the one an undo goes back to: a second one, of the same change, would be counted twice.
        Assert.Equal(0, history.Runs[0].FileCount);
    }

    [Fact]
    public async Task AFileGitIgnoredBefore_ThatAGitignoreEditUncovers_IsNeverDeletedByUndo()
    {
        var root = Repository();
        File.WriteAllText(Path.Combine(root, ".gitignore"), "*.log\n");
        Assert.True(Git(root, "add", ".gitignore") && Git(root, "commit", "-q", "-m", "ignore"), "UNDECIDED.");
        var log = Path.Combine(root, "build.log");
        File.WriteAllText(log, "an old build log\n");
        File.SetLastWriteTimeUtc(log, DateTime.UtcNow.AddHours(-1));
        File.SetCreationTimeUtc(log, DateTime.UtcNow.AddHours(-1));
        var tools = Registry(root);

        using (tools.History.BeginRunScope())
            await Run(tools, "echo '' > .gitignore");

        await Undo(tools, root);
        Assert.True(File.Exists(log), "an ignored file the run never created was deleted by /undo-run.");
        Assert.Equal("*.log\n", File.ReadAllText(Path.Combine(root, ".gitignore")));
    }

    // ── what cannot be watched is said ─────────────────────────────────────────

    [Fact]
    public async Task OutsideAGitRepository_TheCommandIsSaidNotRecorded_AndNothingIsDeleted()
    {
        var root  = _root;   // no git init
        Assert.True(GitProcess.WorkTreeOf(root) is null,
                    $"the temporary folder {root} is inside a git work tree: this test is UNDECIDED, not red for the product.");
        var tools = Registry(root);

        using (tools.History.BeginRunScope())
            await Run(tools, "echo hello > out.txt");

        Assert.True(File.Exists(Path.Combine(root, "out.txt")), "the command did not run: UNDECIDED.");
        var report = await Undo(tools, root);
        Assert.Contains(Strings.UndoRunNotCovered, report);
        Assert.Contains(Strings.NotCoveredNoRepository("run_command `echo hello > out.txt`"), report);
        Assert.True(File.Exists(Path.Combine(root, "out.txt")), "an undo deleted what it never recorded.");
    }

    [Fact]
    public async Task ABackgroundJob_IsSaidNotWatchedAfterItStarts()
    {
        var root  = Repository();
        var tools = Registry(root);

        using (tools.History.BeginRunScope())
            await tools.ExecuteAsync("run_command", Args(new { command = "git status", background = true }), CancellationToken.None);

        var report = await Undo(tools, root);
        Assert.Contains(Strings.NotCoveredBackground("run_command `git status`"), report);
    }

    [Fact]
    public async Task ARefusedCommand_IsNotSaidNotRecorded()
    {
        var root  = _root;   // outside git: a command that RAN would be said
        Assert.True(GitProcess.WorkTreeOf(root) is null,
                    $"the temporary folder {root} is inside a git work tree: this test is UNDECIDED, not red for the product.");
        var tools = Registry(root, new RefusingApproval());

        using (tools.History.BeginRunScope())
            await Run(tools, "echo hello > out.txt");

        Assert.Empty(tools.History.Runs[0].Uncovered);
    }

    private sealed class RefusingApproval() : ApprovalServiceBase(new InferpalConfig(), () => null)
    {
        protected override Task<ApprovalDecision> PromptUserAsync(string message, DiffInfo? diff, CancellationToken ct) =>
            Task.FromResult(ApprovalDecision.Deny);
    }

    [Fact]
    public async Task AFileTooLargeToKeep_IsNamed_WhenTheCommandChangesIt()
    {
        var root = Repository();
        var big  = Path.Combine(root, "big.bin");
        File.WriteAllBytes(big, new byte[4096]);
        var previous = WorkTreeWatch.MaxFileBytes;
        WorkTreeWatch.MaxFileBytes = 1024;
        try
        {
            var tools = Registry(root);
            using (tools.History.BeginRunScope())
            {
                await Run(tools, "echo more >> a.txt");   // big.bin untouched: not named
                Assert.Empty(tools.History.Runs[0].Uncovered);
                await Run(tools, "echo more >> big.bin");
            }
            Assert.Contains(tools.History.Runs[0].Uncovered, u => u.Note.Reason == UncoveredReason.FileTooLarge);
        }
        finally
        {
            WorkTreeWatch.MaxFileBytes = previous;
        }
    }

    // ── every tool that is not read-only is watched, not a list ────────────────

    [Fact]
    public async Task AUsersShellTool_IsWatchedLikeACommand()
    {
        var root   = Repository();
        var before = Tree(root);
        var tools  = Registry(root, customTools: "drop_a=git rm -q a.txt");

        using (tools.History.BeginRunScope())
            await tools.ExecuteAsync("drop_a", Args(new { }), CancellationToken.None);

        Assert.False(File.Exists(Path.Combine(root, "a.txt")), "the user's tool did not run: UNDECIDED.");
        await Undo(tools, root);
        AssertSameTree(before, Tree(root));
    }

    [Fact]
    public async Task ACommandStopped_HalfwayThrough_IsGivenBackAllTheSame()
    {
        var root   = Repository();
        var before = Tree(root);
        var tools  = Registry(root);
        using var stop = new CancellationTokenSource();

        using (tools.History.BeginRunScope())
        {
            var running = Run(tools, "rm a.txt; sleep 20", stop.Token);
            var waited  = Stopwatch.StartNew();
            while (File.Exists(Path.Combine(root, "a.txt")) && waited.Elapsed < TimeSpan.FromSeconds(30))
                await Task.Delay(100);
            Assert.False(File.Exists(Path.Combine(root, "a.txt")), "the command never started: UNDECIDED.");
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        }

        await Undo(tools, root);
        AssertSameTree(before, Tree(root));
    }

    [Fact]
    public async Task ARename_IsUndoneWhole_TheNewNameRemovedAndTheOldGivenBack()
    {
        var root   = Repository();
        var before = Tree(root);
        var tools  = Registry(root);

        using (tools.History.BeginRunScope())
            await Run(tools, "git mv a.txt renamed.txt");

        // A renamed file keeps its dates: it must still read as created by the call, or the undo leaves two copies.
        Assert.True(File.Exists(Path.Combine(root, "renamed.txt")), "the rename did not run: UNDECIDED.");
        await Undo(tools, root);
        AssertSameTree(before, Tree(root));
    }

    [Fact]
    public async Task ACommandRefusedAtItsCard_RecordsNothing_EvenWhatThePersonSavedMeanwhile()
    {
        var root  = Repository();
        var saved = Path.Combine(root, "sub", "c.txt");
        var tools = Registry(root, new RefusingWhileEditing(saved));

        using (tools.History.BeginRunScope())
            await Run(tools, "rm a.txt");

        Assert.Equal("charlie, saved while the card waited\n", File.ReadAllText(saved));
        Assert.Equal(0, tools.History.Runs[0].FileCount);
    }

    private sealed class RefusingWhileEditing(string file) : ApprovalServiceBase(new InferpalConfig(), () => null)
    {
        protected override Task<ApprovalDecision> PromptUserAsync(string message, DiffInfo? diff, CancellationToken ct)
        {
            File.WriteAllText(file, "charlie, saved while the card waited\n");
            return Task.FromResult(ApprovalDecision.Deny);
        }
    }

    [Fact]
    public async Task AFileOfTheRepositoryOutsideTheWorkspace_IsSaidNotRecorded()
    {
        var root      = Repository();
        var workspace = Path.Combine(root, "sub");
        var tools     = Registry(workspace);

        using (tools.History.BeginRunScope())
            await Run(tools, "git rm -q ../a.txt");

        Assert.False(File.Exists(Path.Combine(root, "a.txt")), "the command did not run: UNDECIDED.");
        Assert.Equal(0, tools.History.Runs[0].FileCount);
        var report = await Undo(tools, workspace);
        Assert.Contains(Strings.NotCoveredFileOutside("run_command `git rm -q ../a.txt`", Path.Combine(root, "a.txt")), report);
        Assert.False(File.Exists(Path.Combine(root, "a.txt")), "an undo touched a file outside the workspace.");
    }

    [Fact]
    public async Task TwoSlowReadingsInARow_TurnTheWatchOff_AndItIsSaid()
    {
        var root = Repository();
        WorkTreeWatch.SlowReading = TimeSpan.Zero;   // every reading is "slow"
        try
        {
            var tools = Registry(root);
            using (tools.History.BeginRunScope())
            {
                await Run(tools, "echo one >> a.txt");   // read before and after: two strikes
                await Run(tools, "echo two >> b.txt");   // not read any more
            }
            var run = tools.History.Runs[0];
            Assert.Contains(run.Changes, c => Path.GetFileName(c.OriginalPath) == "a.txt");
            Assert.Contains(run.Uncovered, u => u.Note.Reason == UncoveredReason.TooSlow);
        }
        finally
        {
            WorkTreeWatch.SlowReading = TimeSpan.FromMinutes(5);   // the class's value (constructor)
        }
    }

    [Fact]
    public async Task AFileThatGrowsPastTheLimit_IsStillGivenBack_ItsOldContentWasSmall()
    {
        var root     = Repository();
        var before   = Tree(root);
        var previous = WorkTreeWatch.MaxFileBytes;
        WorkTreeWatch.MaxFileBytes = 1024;
        try
        {
            var history = new FileHistoryService();
            using (history.BeginRunScope())
            {
                var watch = await WorkTreeBracket.OpenAsync(root, null, CancellationToken.None);
                File.WriteAllText(Path.Combine(root, "a.txt"), new string('x', 4096));
                await history.RecordWatchedCallAsync(watch, [], "test", CancellationToken.None);
            }
            await history.UndoRunAsync(history.Runs[0], CancellationToken.None);
            AssertSameTree(before, Tree(root));
        }
        finally
        {
            WorkTreeWatch.MaxFileBytes = previous;
        }
    }

    [Fact]
    public async Task AClosedWatch_IsNoLongerCurrent_AndLetsItsReadingGo()
    {
        var root  = Repository();
        File.WriteAllText(Path.Combine(root, "a.txt"), "dirty\n");
        var watch = await WorkTreeBracket.OpenAsync(root, null, CancellationToken.None);
        WorkTreeBracket.Enter(watch);
        Assert.Same(watch, WorkTreeBracket.Current);
        Assert.NotEmpty(watch.Before.Copies);

        watch.Close();

        Assert.Null(WorkTreeBracket.Current);
        Assert.Empty(watch.Before.Copies);
    }

    /// <summary>
    /// A tool spared the watch (<c>NeedsWorkTreeWatch =&gt; false</c>) backs up what it writes, or writes the editor's
    /// buffer — and every tool that backs up says so. A property of the sources, not a list of names.
    /// </summary>
    [Fact]
    public void EveryToolThatBacksUpItsWrites_IsSparedTheWatch_AndOnlyThose()
    {
        var tools = ConventionCoverageTests.CoreSources(Path.Combine("Services", "Tools"))
                                           .Select(f => (File: f, Code: ConventionCoverageTests.CodeOnly(f)))
                                           .Where(t => t.Code.Contains(": ITool", StringComparison.Ordinal)
                                                       || t.Code.Contains(", ITool", StringComparison.Ordinal))
                                           .ToList();
        // WITNESS: the scan reads the tools — under this, it reads nothing and every rule holds.
        Assert.True(tools.Count >= 20, $"only {tools.Count} tool sources found: the scan is blind.");

        var backups = new System.Text.RegularExpressions.Regex(
            @"BackUpBeforeChangeAsync|RestoreWithBackupAsync|WriteAllOrRollBackAsync|BackedUpFileWriter");
        foreach (var (file, code) in tools)
        {
            // Spared but for what the repository's own check changes after the write (MayRunWorkspaceCommandFor).
            var sparedButForTheCheck = code.Contains("NeedsWorkTreeWatchFor(JsonElement args) => _smartFix?.MayRunWorkspaceCommandFor(Name, args) == true",
                                                     StringComparison.Ordinal);
            var spared   = code.Contains("NeedsWorkTreeWatch => false", StringComparison.Ordinal);
            var backsUp  = backups.IsMatch(code);
            var buffer   = code.Contains("EditorWriteGate", StringComparison.Ordinal);
            var name     = Path.GetFileName(file);
            Assert.False(backsUp && !spared, $"{name} backs up its writes but is watched too (NeedsWorkTreeWatch).");
            // ⚠ A write that runs Smart Fix may run the repository's command, which changes files no backup covers.
            Assert.False(code.Contains("_smartFix", StringComparison.Ordinal) && !sparedButForTheCheck,
                         $"{name} runs Smart Fix but is spared the watch even when the repository's check may run.");
            // Named exemption: tool_search loads definitions and writes nothing at all — no file, no buffer.
            if (name == "ToolSearchTool.cs") continue;
            Assert.False(spared && !backsUp && !buffer,
                         $"{name} is spared the watch but neither backs up its writes nor writes the editor's buffer.");
        }
    }

    // ── the reviews' cases ─────────────────────────────────────────────────────

    [Fact]
    public async Task ACleanFileCheckedOutWithLF_UnderAutocrlf_IsGivenBackWithItsOwnLineEndings()
    {
        var root = Repository();   // committed and checked out with LF
        // ⚠ Turned on afterwards, the files stay LF and clean — and a checkout now would write CRLF.
        Assert.True(Git(root, "config", "core.autocrlf", "true"), "UNDECIDED: no autocrlf.");
        var before = Tree(root);
        var tools  = Registry(root);

        using (tools.History.BeginRunScope())
            await Run(tools, "echo more >> a.txt");

        Assert.Equal(1, tools.History.Runs[0].FileCount);
        await Undo(tools, root);
        AssertSameTree(before, Tree(root));
    }

    [Fact]
    public async Task AStash_IsGivenBack()
    {
        var root = Repository();
        File.WriteAllText(Path.Combine(root, "a.txt"), "alpha, work in progress\n");
        File.WriteAllText(Path.Combine(root, "sub", "c.txt"), "charlie, work in progress\n");
        var before = Tree(root);
        var tools  = Registry(root);

        using (tools.History.BeginRunScope())
            await Run(tools, "git stash -q");

        Assert.Equal("alpha\n", File.ReadAllText(Path.Combine(root, "a.txt")));   // the stash ran
        await Undo(tools, root);
        AssertSameTree(before, Tree(root));
    }

    [Fact]
    public async Task AStagedChange_ThatACommandThrowsAway_IsGivenBack()
    {
        var root = Repository();
        File.WriteAllText(Path.Combine(root, "a.txt"), "alpha, staged\n");
        Assert.True(Git(root, "add", "a.txt"), "UNDECIDED: nothing staged.");
        var before = Tree(root);
        var tools  = Registry(root);

        using (tools.History.BeginRunScope())
            await Run(tools, "git checkout HEAD -- a.txt");

        Assert.Equal("alpha\n", File.ReadAllText(Path.Combine(root, "a.txt")));
        await Undo(tools, root);
        AssertSameTree(before, Tree(root));
    }

    [Fact]
    public async Task ARename_BesideADirtyGitignore_IsStillUndoneWhole()
    {
        var root = Repository();
        File.WriteAllText(Path.Combine(root, ".gitignore"), "*.log\n");
        Assert.True(Git(root, "add", ".gitignore") && Git(root, "commit", "-q", "-m", "ignore"), "UNDECIDED.");
        // Dirty before the call and left alone by it — the way Inferpal's own index leaves it (PatchGitIgnore).
        File.WriteAllText(Path.Combine(root, ".gitignore"), "*.log\n.inferpal/history/\n");
        var before = Tree(root);
        var tools  = Registry(root);

        using (tools.History.BeginRunScope())
            await Run(tools, "git mv a.txt renamed.txt");

        Assert.True(File.Exists(Path.Combine(root, "renamed.txt")), "the rename did not run: UNDECIDED.");
        await Undo(tools, root);
        AssertSameTree(before, Tree(root));
    }

    [Fact]
    public async Task ACommandStoppedAtItsCard_RecordsNothing_EvenWhatThePersonSavedMeanwhile()
    {
        var root  = Repository();
        var saved = Path.Combine(root, "sub", "c.txt");
        using var stop = new CancellationTokenSource();
        var tools = Registry(root, new StoppedWhileEditing(saved, stop));

        using (tools.History.BeginRunScope())
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(tools, "rm a.txt", stop.Token));

        Assert.True(File.Exists(Path.Combine(root, "a.txt")), "the command ran although its card was stopped.");
        Assert.Equal(0, tools.History.Runs[0].FileCount);
        Assert.Empty(tools.History.Runs[0].Uncovered);
    }

    /// <summary>The person saves a file, then presses Stop instead of answering the card.</summary>
    private sealed class StoppedWhileEditing(string file, CancellationTokenSource stop)
        : ApprovalServiceBase(new InferpalConfig(), () => null)
    {
        protected override Task<ApprovalDecision> PromptUserAsync(string message, DiffInfo? diff, CancellationToken ct)
        {
            File.WriteAllText(file, "charlie, saved before Stop\n");
            stop.Cancel();
            throw new OperationCanceledException(stop.Token);
        }
    }

    [Fact]
    public async Task AFileWithUnsavedChangesInTheEditor_SavedDuringTheCall_IsSaidNotRecorded()
    {
        var root    = Repository();
        var file    = Path.Combine(root, "sub", "c.txt");
        var overlay = new OpenDocumentOverlay();
        overlay.Set(file, "charlie, typed and not saved\n");
        var tools   = Registry(root, overlay: overlay);

        using (tools.History.BeginRunScope())
            await Run(tools, "echo saved > sub/c.txt; echo more >> a.txt");   // stands for the editor's save, and the command's

        var run = tools.History.Runs[0];
        Assert.Contains(run.Changes, c => Path.GetFileName(c.OriginalPath) == "a.txt");
        Assert.DoesNotContain(run.Changes, c => Path.GetFileName(c.OriginalPath) == "c.txt");
        Assert.Contains(run.Uncovered, u => u.Note is { Reason: UncoveredReason.SavedByEditor } n && n.File == file);
    }

    [Fact]
    public async Task UndoingARun_SaysWhatTheRunsAfterItCouldNotRecord()
    {
        var root  = Repository();
        var tools = Registry(root);
        using (tools.History.BeginRunScope())
            await Run(tools, "echo more >> a.txt");
        using (tools.History.BeginRunScope())
            await tools.ExecuteAsync("run_command", Args(new { command = "git status", background = true }), CancellationToken.None);

        // The later run changed no file that could be recorded: the earlier one is undone — and the later one's gap said.
        var report = await Undo(tools, root);
        Assert.Equal("alpha\n", File.ReadAllText(Path.Combine(root, "a.txt")));
        Assert.Contains(Strings.NotCoveredBackground("run_command `git status`"), report);
    }

    [Fact]
    public async Task PastTheCapOfFilesToFetch_NothingOfTheCallIsRecorded_AndItIsSaid()
    {
        var root = Repository();
        Assert.True(Git(root, "checkout", "-q", "-b", "other"), "UNDECIDED: no branch.");
        foreach (var f in (string[])["a.txt", "b.txt", Path.Combine("sub", "c.txt")])
            File.WriteAllText(Path.Combine(root, f), "changed on the other branch\n");
        Assert.True(Git(root, "commit", "-q", "-am", "other") && Git(root, "checkout", "-q", "-"), "UNDECIDED: no branch.");
        var previous = WorkTreeWatch.MaxFetchedFiles;
        WorkTreeWatch.MaxFetchedFiles = 2;
        try
        {
            var tools = Registry(root);
            using (tools.History.BeginRunScope())
                await Run(tools, "git checkout -q other");

            // Half a checkout given back is worse than none: all or nothing, and said.
            var run = tools.History.Runs[0];
            Assert.Equal(0, run.FileCount);
            Assert.Contains(run.Uncovered, u => u.Note.Reason == UncoveredReason.TooManyFiles);
        }
        finally
        {
            WorkTreeWatch.MaxFetchedFiles = previous;
        }
    }

    [Fact]
    public void ACallLabel_KeepsNoBacktick_ThatWouldEndItsCodeSpan()
    {
        Assert.Equal("Write-Host 'a'b", ToolRegistry.CallLabel("Write-Host `a`b"));
    }

    [Fact]
    public async Task OutsideARun_NothingIsWatched()
    {
        var root  = Repository();
        var tools = Registry(root);

        await Run(tools, "rm a.txt");   // a /run typed by the person: no run open

        Assert.Empty(tools.History.Runs);
    }
}
