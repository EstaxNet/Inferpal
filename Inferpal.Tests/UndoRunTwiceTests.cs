using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Inferpal.Localization;
using Inferpal.Services.Commands;
using Inferpal.Services.Execution;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  A run that has been undone is not undone again.
//
//  An undone run stays the most recent run with changes, and the Undo button of its result bar stays
//  on screen: a second /undo-run, or a second click, picked it again, restored its pre-run state
//  over whatever the user had fixed since, and answered "last run undone". It now touches nothing
//  and says the run was already undone; a newer run is undone as before.
// ──────────────────────────────────────────────────────────────────────────────────────────────
public sealed class UndoRunTwiceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("inferpal-undo-twice-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private async Task RunThatWritesAsync(FileHistoryService history, string path, string content)
    {
        using (history.BeginRunScope())
        {
            await history.BackUpBeforeChangeAsync(path, CancellationToken.None);
            File.WriteAllText(path, content);
        }
    }

    private Task<string> UndoAsync(FileHistoryService history) =>
        UndoRunCommandHandler.HandleAsync(history, ["/undo-run"], _root, CancellationToken.None);

    [Fact]
    public async Task ASecondUndo_TouchesNothing_AndSaysTheRunWasAlreadyUndone()
    {
        var file    = Path.Combine(_root, "Cart.cs");
        File.WriteAllText(file, "original\n");
        var history = new FileHistoryService { WorkspaceRoot = () => _root };
        await RunThatWritesAsync(history, file, "written by the run\n");

        await UndoAsync(history);
        Assert.Equal("original\n", File.ReadAllText(file));   // witness: the first undo did undo

        File.WriteAllText(file, "fixed by hand\n");
        var second = await UndoAsync(history);

        Assert.Equal(Strings.UndoRunAlreadyUndone, second);
        Assert.Equal("fixed by hand\n", File.ReadAllText(file));
    }

    [Fact]
    public async Task ANewerRun_IsUndoneAsBefore()
    {
        // Reference arm: the guard is about THAT run, not about undoing twice in a session.
        var file    = Path.Combine(_root, "Cart.cs");
        File.WriteAllText(file, "original\n");
        var history = new FileHistoryService { WorkspaceRoot = () => _root };
        await RunThatWritesAsync(history, file, "first run\n");
        await UndoAsync(history);

        await RunThatWritesAsync(history, file, "second run\n");
        var result = await UndoAsync(history);

        Assert.NotEqual(Strings.UndoRunAlreadyUndone, result);
        Assert.Equal("original\n", File.ReadAllText(file));
    }
}
