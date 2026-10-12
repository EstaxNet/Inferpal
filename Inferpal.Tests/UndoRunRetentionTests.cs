using System.IO;
using Inferpal.Localization;
using Inferpal.Services;
using Inferpal.Services.Commands;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// What <c>/undo-run</c> needs to put an agent's edit back stays as long as it can: the run that changed files is not
/// pushed out by the questions that changed nothing, and its backup is not pruned by the other editor.
/// </summary>
/// <remarks>
/// <para>⚠ A run begins with every question asked with tools on, and the session kept the last fifteen, oldest dropped
/// first: fifteen questions after an edit, <c>/undo-run</c> answered "no run with file changes" about it.</para>
/// <para>⚠ The history folder is the workspace's, shared by Visual Studio and VS Code, and the runs live in each process's
/// memory: one editor's twentieth snapshot of a file deleted the pre-run backup the other's run still held.</para>
/// </remarks>
[Collection(CultureSerialCollection.Name)]
public sealed class UndoRunRetentionTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("inferpal-undo-retention").FullName;

    public void Dispose()
    {
        Strings.ApplyLanguage(null);
        try { Directory.Delete(_dir, recursive: true); } catch { /* cleanup */ }
    }

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

    [Fact]
    public async Task AnEditRun_OutlivesTheQuestionsThatChangedNothing()
    {
        var file = File("Program.cs", "original");
        var history = new FileHistoryService();
        history.BeginRun();
        await Edit(history, file, "edited by the agent");
        for (var i = 0; i < 20; i++) history.BeginRun();   // twenty questions that changed nothing

        var answer = await UndoRunCommandHandler.HandleAsync(history, ["/undo-run"], _dir, CancellationToken.None);

        Assert.Equal("original", await System.IO.File.ReadAllTextAsync(file));
        Assert.StartsWith(Strings.UndoRunResult(1, 0), answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FifteenEditRuns_StillLetTheOldestGo()
    {
        // Reference arm: the cap still holds — sixteen runs that each changed a file keep fifteen.
        var history = new FileHistoryService();
        for (var i = 0; i < 16; i++)
        {
            history.BeginRun();
            await Edit(history, File($"f{i}.txt", "v0"), "v1");
        }

        Assert.Equal(15, history.Runs.Count);
        Assert.DoesNotContain(history.Runs, r => r.Changes.Any(c => c.OriginalPath.EndsWith("f0.txt", StringComparison.Ordinal)));
    }

    [Fact]
    public void ReplayPastTheRunsKept_SaysOlderOnesAreNoLongerKept()
    {
        var history = new FileHistoryService();
        for (var i = 0; i < 20; i++)
        {
            history.BeginRun();
            history.RecordToolCall("read_file", "a.cs", 1, error: false);
        }

        Assert.Equal(5, history.ForgottenRuns);
        Assert.Equal(Strings.ReplayRunForgotten(16, 15),
                     ReplayCommandHandler.Handle(history.Runs, ["/replay", "16"], _dir, history.ForgottenRuns));
        // Reference arm: nothing let go, a number past the last run is just out of range.
        Assert.Equal(Strings.ReplayRunOutOfRange(16, 15), ReplayCommandHandler.Handle(history.Runs, ["/replay", "16"], _dir, 0));
    }

    [Fact]
    public async Task ABackupTheOtherEditorsRunHolds_IsNotPruned()
    {
        var file = File("Shared.cs", "before the run");
        var vsCode = new FileHistoryService();
        var visualStudio = new FileHistoryService();

        vsCode.BeginRun();
        await Edit(vsCode, file, "edited in VS Code");
        for (var i = 0; i < 25; i++) await Edit(visualStudio, file, $"edited in Visual Studio {i}");   // a /tdd loop

        await UndoRunCommandHandler.HandleAsync(vsCode, ["/undo-run"], _dir, CancellationToken.None);

        Assert.Equal("before the run", await System.IO.File.ReadAllTextAsync(file));
    }

    [Fact]
    public async Task ABackupNobodyHolds_IsStillPruned()
    {
        // Reference arm: the per-file cap still applies to what no run holds.
        var file = File("Alone.cs", "v0");
        var history = new FileHistoryService();
        for (var i = 0; i < 25; i++) await Edit(history, file, $"v{i + 1}");

        var historyDir = Path.Combine(_dir, ".inferpal", "history");
        Assert.Equal(20, Directory.EnumerateFiles(historyDir).Count(f => f.Contains("Alone.cs", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task TheUndoList_CountsFilesInTheUsersLanguage()
    {
        var history = new FileHistoryService();
        history.BeginRun();
        await Edit(history, File("One.cs", "v0"), "v1");

        Strings.ApplyLanguage("fr");
        var list = await UndoRunCommandHandler.HandleAsync(history, ["/undo-run", "list"], _dir, CancellationToken.None);

        Assert.EndsWith("— 1 fichier", list.TrimEnd(), StringComparison.Ordinal);
    }
}
