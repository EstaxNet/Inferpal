using System.IO;
using Inferpal.Localization;
using Inferpal.Services;
using Inferpal.Services.Commands;
using Inferpal.Services.Execution;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A run outlives the process that made it: <c>/undo-run</c> and a reopened conversation's Undo still give the files
/// back after Visual Studio, VS Code's host or an ACP thread restarted — and a run read back from disk is not taken on
/// trust, since undoing is the only write made without an approval card.
/// </summary>
/// <remarks>
/// ⚠ The runs lived in memory only: after a restart <c>/undo-run</c> answered "no agent run with file changes" while the
/// copies from before the run were still in <c>.inferpal/history/</c>. Each test makes a second
/// <see cref="FileHistoryService"/> on the same folders — what a restart, or the other editor, is.
/// </remarks>
[Collection(CultureSerialCollection.Name)]
public sealed class RunsAcrossRestartsTests : IDisposable
{
    private readonly string _dir  = Directory.CreateTempSubdirectory("inferpal-runs-restart").FullName;
    private readonly string _user = Directory.CreateTempSubdirectory("inferpal-runs-key").FullName;   // the key's folder

    public void Dispose()
    {
        Strings.ApplyLanguage(null);
        try { Directory.Delete(_dir, recursive: true); } catch { /* cleanup */ }
        try { Directory.Delete(_user, recursive: true); } catch { /* cleanup */ }
    }

    private FileHistoryService Service() => new() { WorkspaceRoot = () => _dir, UserHistoryRoot = _user };

    private string File(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    private static async Task Edit(FileHistoryService history, string path, string content)
    {
        var (saved, _) = await history.BackUpBeforeChangeAsync(path, CancellationToken.None);
        Assert.True(saved);
        await System.IO.File.WriteAllTextAsync(path, content);
    }

    private string RunsDir => Path.Combine(_dir, ".inferpal", "history", "runs");

    private string[] RunFiles() => Directory.Exists(RunsDir) ? Directory.GetFiles(RunsDir, "*.json") : [];

    private Task<string> Undo(FileHistoryService history, params string[] parts) =>
        UndoRunCommandHandler.HandleAsync(history, ["/undo-run", .. parts], _dir, CancellationToken.None);

    /// <summary>One closed run: Program.cs edited, New.cs created.</summary>
    private async Task<(string Edited, string Created)> RunInAnEarlierProcess()
    {
        var edited  = File("Program.cs", "original\r\n  bytes\n");
        var created = Path.Combine(_dir, "New.cs");
        var before = Service();
        before.BeginRun();
        await Edit(before, edited, "edited by the agent");
        await Edit(before, created, "created by the agent");
        before.EndRun();
        return (edited, created);
    }

    [Fact]
    public async Task ARunOfAnEarlierProcess_IsUndoneByteForByte()
    {
        var (edited, created) = await RunInAnEarlierProcess();

        var answer = await Undo(Service());

        Assert.Equal("original\r\n  bytes\n", await System.IO.File.ReadAllTextAsync(edited));
        Assert.False(System.IO.File.Exists(created));
        Assert.StartsWith(Strings.UndoRunResult(1, 1), answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AResultBarsUndo_FindsItsRunAfterARestart()
    {
        var (edited, _) = await RunInAnEarlierProcess();
        var id = Assert.Single(Service().Runs).Id;   // the id a reopened conversation's bar carries

        await Undo(Service(), "#" + id);

        Assert.Equal("original\r\n  bytes\n", await System.IO.File.ReadAllTextAsync(edited));
    }

    [Fact]
    public async Task AnUndoneRun_StaysUndone_AfterAnotherRestart()
    {
        var (edited, _) = await RunInAnEarlierProcess();
        await Undo(Service());
        await System.IO.File.WriteAllTextAsync(edited, "edited by hand since");

        var again = await Undo(Service());

        Assert.Equal(Strings.UndoRunAlreadyUndone, again);
        Assert.Equal("edited by hand since", await System.IO.File.ReadAllTextAsync(edited));
    }

    /// <summary>The editor that made a run still holds it in memory: undone in the other editor, it is undone here too —
    /// never a second time over what was edited since.</summary>
    [Fact]
    public async Task ARunUndoneInTheOtherEditor_IsUndoneHereToo()
    {
        var file = File("A.cs", "a0");
        var here = Service();
        var id = here.BeginRun();
        await Edit(here, file, "a1");
        here.EndRun();
        await Undo(Service(), "#" + id);   // the other editor
        await System.IO.File.WriteAllTextAsync(file, "edited by hand since");

        var again = await Undo(here, "#" + id);

        Assert.Equal(Strings.UndoRunAlreadyUndone, again);
        Assert.Equal("edited by hand since", await System.IO.File.ReadAllTextAsync(file));
    }

    [Fact]
    public async Task ALaterRunOfTheOtherEditor_HoldsTheEarlierRunBack()
    {
        var file  = File("A.cs", "a0");
        var other = File("B.cs", "b0");
        var here  = Service();
        var runId = here.BeginRun();
        await Edit(here, file, "a1");
        here.EndRun();
        var there = Service();
        there.BeginRun();
        await Edit(there, other, "b1");
        there.EndRun();

        var answer = await Undo(here, "#" + runId);

        Assert.Equal(Strings.UndoRunNotTheLatest, answer);
        Assert.Equal("a1", await System.IO.File.ReadAllTextAsync(file));
    }

    /// <summary>A run the other editor's agent is still writing is not undone under it.</summary>
    [Fact]
    public async Task ARunStillOpenInTheOtherEditor_IsNotUndone()
    {
        var file = File("A.cs", "a0");
        var there = Service();
        there.BeginRun();
        await Edit(there, file, "a1");   // written to disk as it grows, still open

        var answer = await Undo(Service());

        Assert.Equal(Strings.UndoRunWhileBusy, answer);
        Assert.Equal("a1", await System.IO.File.ReadAllTextAsync(file));
    }

    /// <summary>An editor killed mid-run: its run is undone, and said to have been cut short.</summary>
    [Fact]
    public async Task ARunCutShortByItsEditor_IsUndone_AndSaysSo()
    {
        var file = File("A.cs", "a0");
        var dead = Service();
        dead.BeginRun();
        await Edit(dead, file, "a1");
        var run = Assert.Single(dead.Runs);
        // Its process gone: the same run, still open, written by a process id that no longer exists.
        await System.IO.File.WriteAllTextAsync(run.ManifestPath!,
            RunManifest.Serialize(run, open: true, RunManifest.KeyIn(_user)!, pid: int.MaxValue));

        var answer = await Undo(Service());

        Assert.Equal("a0", await System.IO.File.ReadAllTextAsync(file));
        Assert.Contains(Strings.UndoRunCutShort, answer, StringComparison.Ordinal);
    }

    /// <summary>A run cut short stays cut short once this process writes it again (a Keep in its review): rewritten as
    /// closed, the warning that its last changes may be missing would go.</summary>
    [Fact]
    public async Task ARunCutShort_StaysCutShort_AfterAKeepInItsReview()
    {
        var file  = File("A.cs", "a0");
        var other = File("B.cs", "b0");
        var dead  = Service();
        dead.BeginRun();
        await Edit(dead, file, "a1");
        await Edit(dead, other, "b1");
        var run = Assert.Single(dead.Runs);
        await System.IO.File.WriteAllTextAsync(run.ManifestPath!,
            RunManifest.Serialize(run, open: true, RunManifest.KeyIn(_user)!, pid: int.MaxValue));

        var here = Service();
        var read = Assert.Single(here.Runs);
        Assert.True(read.CutShort);                                       // witness: read as cut short
        here.KeepFile(read, file);

        Assert.True(Assert.Single(Service().Runs).CutShort);
    }

    /// <summary>The key the other editor is making right now — held open — is read once it lets go: taken as no key, the
    /// process would keep no run on disk for as long as it lives.</summary>
    [Fact]
    public async Task TheRunKey_HeldOpenByTheOtherEditor_IsReadOnceReleased()
    {
        var key = RunManifest.KeyIn(_user)!;
        var path = Path.Combine(_user, ".run-key");
        var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        var release = Task.Run(async () => { await Task.Delay(100); held.Dispose(); });

        var read = RunManifest.KeyIn(_user);

        await release;
        Assert.Equal(key, read);
    }

    /// <summary>A write that changed nothing leaves the run: kept on disk, it was a run an undo would "give back" over the
    /// file's later edits.</summary>
    [Fact]
    public async Task AWriteThatDidNotLand_LeavesNoRunOnDisk()
    {
        var file = File("A.cs", "same");
        var history = Service();
        history.BeginRun();
        using (history.TrackCall())
        {
            var (saved, _) = await history.BackUpBeforeChangeAsync(file, CancellationToken.None);
            Assert.True(saved);
            Assert.Single(RunFiles());   // WITNESS: the run was on disk while the write was pending
            await System.IO.File.WriteAllTextAsync(file, "same");
        }
        Assert.Empty(RunFiles());   // at once: an editor killed before the run ends leaves no phantom either
        history.EndRun();

        Assert.Empty(RunFiles());
        Assert.Equal(Strings.UndoRunNone, await Undo(Service()));
    }

    [Fact]
    public async Task AMissingCopy_FailsItsFileOnly()
    {
        var (edited, created) = await RunInAnEarlierProcess();
        var after = Service();
        var copy = Assert.Single(after.Runs).Changes.Single(c => c.SnapshotPath is not null).SnapshotPath!;
        System.IO.File.Delete(copy);   // deleted by hand

        var answer = await Undo(after);

        Assert.Equal("edited by the agent", await System.IO.File.ReadAllTextAsync(edited));
        Assert.False(System.IO.File.Exists(created));   // the other file still comes back
        Assert.Contains(Strings.UndoRunRetryFailed, answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnreadableRun_IsLeftOutAndSaid_TheOthersStay()
    {
        await RunInAnEarlierProcess();
        var broken = Path.Combine(RunsDir, "2026-01-01_00-00-00-000_broken.json");
        await System.IO.File.WriteAllTextAsync(broken, "{ not json");

        var runs = Service().Runs;

        Assert.Single(runs);
        Assert.Contains(Diagnostics.Snapshot(), e => e.Detail.Contains(broken, StringComparison.Ordinal));
    }

    /// <summary>A run is undone without an approval card. One edited in place — a copy swapped for a file the repository
    /// supplies — is not signed by this account's key any more, and is never read.</summary>
    [Fact]
    public async Task ARunEditedInPlace_IsNeverRead()
    {
        var (edited, _) = await RunInAnEarlierProcess();
        var manifest = Assert.Single(RunFiles());
        var text = await System.IO.File.ReadAllTextAsync(manifest);
        Assert.Contains("\"undoneGeneration\": 0", text, StringComparison.Ordinal);   // WITNESS: the edit has something to change
        await System.IO.File.WriteAllTextAsync(manifest,
            text.Replace("\"undoneGeneration\": 0", "\"undoneGeneration\": 1", StringComparison.Ordinal));

        var answer = await Undo(Service());

        Assert.Equal(Strings.UndoRunNone, answer);
        Assert.Equal("edited by the agent", await System.IO.File.ReadAllTextAsync(edited));
    }

    /// <summary>Signed or not, a run naming a file outside the workspace is never read: the signature says who wrote
    /// the run, the paths say what an undo may touch.</summary>
    [Fact]
    public async Task ASignedRunNamingAFileOutsideTheWorkspace_IsNeverRead()
    {
        var elsewhere = Directory.CreateTempSubdirectory("inferpal-runs-outside").FullName;
        try
        {
            var outside = Path.Combine(elsewhere, "victim.txt");
            var forged = new HistoryRun("2026-10-24_10-00-00-000_forged-0001");
            forged.RecordFirst(outside, snapshot: null);   // "created by the run": an undo would delete it
            await System.IO.File.WriteAllTextAsync(outside, "not the workspace's");
            Directory.CreateDirectory(RunsDir);
            await System.IO.File.WriteAllTextAsync(Path.Combine(RunsDir, RunManifest.FileName(forged)),
                RunManifest.Serialize(forged, open: false, RunManifest.KeyIn(_user)!));

            var answer = await Undo(Service());

            Assert.Equal(Strings.UndoRunNone, answer);
            Assert.True(System.IO.File.Exists(outside));
        }
        finally { try { Directory.Delete(elsewhere, true); } catch { } }
    }

    [Fact]
    public async Task FifteenRunsAreKept_AndAFileThatIsNoRunIsNeverDeleted()
    {
        Directory.CreateDirectory(RunsDir);
        var stranger = Path.Combine(RunsDir, "zzzz-not-a-run.json");
        await System.IO.File.WriteAllTextAsync(stranger, "someone else's file");
        var history = Service();
        for (var i = 0; i < 17; i++)
        {
            history.BeginRun();
            await Edit(history, File($"f{i}.txt", "v0"), "v1");
            history.EndRun();
        }

        Assert.Equal(16, RunFiles().Length);   // fifteen runs, and the stranger
        Assert.True(System.IO.File.Exists(stranger));
        // What a run file parsed to is not kept past its file: a long session would hold every run it ever wrote.
        var cache = (System.Collections.ICollection)typeof(FileHistoryService)
            .GetField("_runFileCache", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(history)!;
        Assert.InRange(cache.Count, 1, 16);

        // Nor past a file another process trimmed, or someone deleted.
        foreach (var gone in RunFiles().Where(f => f != stranger).Take(5)) System.IO.File.Delete(gone);
        history.BeginRun();
        await Edit(history, File("after.txt", "v0"), "v1");
        history.EndRun();
        Assert.InRange(cache.Count, 1, RunFiles().Length);
    }

    [Fact]
    public async Task TheCopyARunKeptOnDiskHolds_SurvivesTwentyLaterSnapshots()
    {
        var file = File("Hot.cs", "pre-run");
        var t = new DateTime(2026, 10, 24, 10, 0, 0, DateTimeKind.Utc);
        var before = new FileHistoryService { WorkspaceRoot = () => _dir, UserHistoryRoot = _user, UtcNow = () => t = t.AddSeconds(1) };
        before.BeginRun();
        await Edit(before, file, "run");
        before.EndRun();
        // The earlier process is gone: the list of copies it published for the live editors goes with it.
        var held = Directory.GetFiles(Path.Combine(_dir, ".inferpal", "history"), ".held-*");
        Assert.NotEmpty(held);   // WITNESS: what a live process would still protect is removed
        foreach (var list in held) System.IO.File.Delete(list);
        var after = new FileHistoryService { WorkspaceRoot = () => _dir, UserHistoryRoot = _user, UtcNow = () => t = t.AddSeconds(1) };
        for (var i = 0; i < 25; i++) await Edit(after, file, $"later {i}");   // outside any run
        await Edit(after, file, "run");   // back as the run left it: not changed since, so the undo gives it back

        await Undo(after);

        Assert.Equal("pre-run", await System.IO.File.ReadAllTextAsync(file));
    }

    [Fact]
    public async Task OutsideTheProject_TheRunGoesToTheUsersFolder_NeverIntoTheProject()
    {
        var file = File("Program.cs", "original");
        FileHistoryService Outside() => new() { WorkspaceRoot = () => _dir, OutsideProject = () => true, UserHistoryRoot = _user };
        var before = Outside();
        before.BeginRun();
        await Edit(before, file, "edited");
        before.EndRun();

        await Undo(Outside());

        Assert.Equal("original", await System.IO.File.ReadAllTextAsync(file));
        Assert.False(Directory.Exists(Path.Combine(_dir, ".inferpal")));
        Assert.NotEmpty(Directory.GetFiles(_user, "*.json", SearchOption.AllDirectories));   // WITNESS: kept somewhere
    }

    [Fact]
    public async Task AWorkspaceFromBeforeRunsWereKept_HasNothingToUndo_AndNoError()
    {
        File("Program.cs", "original");
        Directory.CreateDirectory(Path.Combine(_dir, ".inferpal", "history"));   // a history, no runs folder

        Assert.Equal(Strings.UndoRunNone, await Undo(Service()));
    }
}
