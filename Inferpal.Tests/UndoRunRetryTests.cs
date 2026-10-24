using System.IO;
using Inferpal.Localization;
using Inferpal.Services.Commands;
using Inferpal.Services.Execution;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A run is undone only once every file it changed is given back: after a failure, <c>/undo-run</c> tries the failed
/// files again — and only them.
/// </summary>
/// <remarks>
/// The run was marked undone whatever happened. A file the undo could not write (held by another program, a folder in
/// its place) was listed with ⚠, and the next <c>/undo-run</c> answered "already undone": the file never came back.
/// </remarks>
public sealed class UndoRunRetryTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("inferpal-undo-retry-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private Task<string> UndoAsync(FileHistoryService history) =>
        UndoRunCommandHandler.HandleAsync(history, ["/undo-run"], _root, CancellationToken.None);

    private async Task<FileHistoryService> RunThatWritesBothAsync(string cart, string order)
    {
        File.WriteAllText(cart, "cart before\n");
        File.WriteAllText(order, "order before\n");
        var history = new FileHistoryService { WorkspaceRoot = () => _root };
        using (history.BeginRunScope())
        {
            await history.BackUpBeforeChangeAsync(cart, CancellationToken.None);
            File.WriteAllText(cart, "cart by the run\n");
            await history.BackUpBeforeChangeAsync(order, CancellationToken.None);
            File.WriteAllText(order, "order by the run\n");
        }
        return history;
    }

    [Fact]
    public async Task AFileThatFailed_IsTriedAgain_AndOnlyIt()
    {
        var cart  = Path.Combine(_root, "Cart.cs");
        var order = Path.Combine(_root, "Order.cs");
        var history = await RunThatWritesBothAsync(cart, order);
        // A failure that leaves the file as the run left it (a file changed since the run is not a failure: it is left
        // as it is, and said): its copy from before the run is out of reach.
        var copy  = history.Runs[0].Changes.Single(c => string.Equals(c.OriginalPath, order, StringComparison.OrdinalIgnoreCase)).SnapshotPath!;
        var bytes = File.ReadAllBytes(copy);
        File.Delete(copy);

        var first = await UndoAsync(history);
        Assert.Equal("cart before\n", File.ReadAllText(cart));              // witness: the other file came back
        Assert.Contains(Strings.UndoRunRetryFailed, first, StringComparison.Ordinal);

        File.WriteAllText(cart, "cart fixed by hand\n");                    // edited since the undo
        File.WriteAllBytes(copy, bytes);                                    // the cause is gone

        var second = await UndoAsync(history);
        Assert.NotEqual(Strings.UndoRunAlreadyUndone, second);
        Assert.Equal("order before\n", File.ReadAllText(order));
        Assert.Equal("cart fixed by hand\n", File.ReadAllText(cart));       // given back once, never touched again
        Assert.Equal(Strings.UndoRunAlreadyUndone, await UndoAsync(history));
    }

    [Fact]
    public async Task ARunGivenBackWhole_IsUndone_AtOnce()
    {
        // Reference arm: nothing failed, nothing to retry, nothing said about a retry.
        var cart  = Path.Combine(_root, "Cart.cs");
        var order = Path.Combine(_root, "Order.cs");
        var history = await RunThatWritesBothAsync(cart, order);

        var first = await UndoAsync(history);

        Assert.DoesNotContain(Strings.UndoRunRetryFailed, first, StringComparison.Ordinal);
        Assert.Equal(Strings.UndoRunAlreadyUndone, await UndoAsync(history));
    }
}
