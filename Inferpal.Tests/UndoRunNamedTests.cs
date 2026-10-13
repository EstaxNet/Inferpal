using System.IO;
using Inferpal.Localization;
using Inferpal.Services.Commands;
using Inferpal.Services.Execution;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ A result bar's Undo undoes ITS run. It ran a bare <c>/undo-run</c> — the most recent run that changed files — and
/// <c>/fix-build</c>, <c>/tdd</c>, <c>/task apply</c> open runs without a bar of their own: the older bar's Undo then
/// reverted the later run's files and left its own as they were. Named, a run is undone only while no later run changed
/// files after it (its snapshots predate the later writes, which the undo would overwrite).
/// </summary>
[Collection(CultureSerialCollection.Name)]
public sealed class UndoRunNamedTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("inferpal-undo-named-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static async Task<HistoryRun> RunThatWritesAsync(FileHistoryService history, string path, string content)
    {
        using (history.BeginRunScope())
        {
            await history.BackUpBeforeChangeAsync(path, CancellationToken.None);
            File.WriteAllText(path, content);
        }
        return history.Runs[0];
    }

    private Task<string> UndoAsync(FileHistoryService history, params string[] args) =>
        UndoRunCommandHandler.HandleAsync(history, ["/undo-run", .. args], _root, CancellationToken.None);

    [Fact]
    public async Task ABarsUndo_UndoesItsRun_NeverALaterOne()
    {
        var a = Path.Combine(_root, "A.cs");
        var b = Path.Combine(_root, "B.cs");
        File.WriteAllText(a, "a original\n");
        File.WriteAllText(b, "b original\n");
        var history = new FileHistoryService { WorkspaceRoot = () => _root };
        var first  = await RunThatWritesAsync(history, a, "a by the turn\n");
        var second = await RunThatWritesAsync(history, b, "b by /fix-build\n");   // a run without a bar of its own

        // The turn's bar: refused, nothing touched — undoing it would have reverted B.cs in its name.
        Assert.Equal(Strings.UndoRunNotTheLatest, await UndoAsync(history, "#" + first.Id));
        Assert.Equal("a by the turn\n", File.ReadAllText(a));
        Assert.Equal("b by /fix-build\n", File.ReadAllText(b));

        // The later run undone, the turn's run is the latest again: its Undo undoes A.cs, and only A.cs.
        await UndoAsync(history, "#" + second.Id);
        Assert.Equal("b original\n", File.ReadAllText(b));
        await UndoAsync(history, "#" + first.Id);
        Assert.Equal("a original\n", File.ReadAllText(a));

        // A run that is not there (the editor restarted since) is said, nothing is touched.
        Assert.Equal(Strings.UndoRunNone, await UndoAsync(history, "#run-not-here"));
    }

    [Fact]
    public async Task ABareUndoRun_StillUndoesTheMostRecentRun()
    {
        // Reference arm: typed bare, /undo-run is what it was.
        var a = Path.Combine(_root, "A.cs");
        var b = Path.Combine(_root, "B.cs");
        File.WriteAllText(a, "a original\n");
        File.WriteAllText(b, "b original\n");
        var history = new FileHistoryService { WorkspaceRoot = () => _root };
        await RunThatWritesAsync(history, a, "a by the turn\n");
        await RunThatWritesAsync(history, b, "b by the next\n");

        await UndoAsync(history);
        Assert.Equal("b original\n", File.ReadAllText(b));
        Assert.Equal("a by the turn\n", File.ReadAllText(a));
    }

    /// <summary>Both editors' bars name their run.</summary>
    [Fact]
    public void BothEditorsBars_NameTheirRun()
    {
        var vm = ConventionCoverageTests.CodeOnly(Path.Combine(ConventionCoverageTests.RepoRoot(),
                                                               "Inferpal", "ToolWindow", "InferpalToolWindowData.Chrome.cs"));
        Assert.Contains("await HandleSlashCommandAsync(\"/undo-run #\" + runId, CancellationToken.None);", vm, StringComparison.Ordinal);
        Assert.Contains("undo = () => _ = UndoFromResultBarAsync(runId);", vm, StringComparison.Ordinal);
        Assert.Contains("post({ type: 'send', text: `/undo-run #${runId}` })", WebviewRebuildTests.TsCode("webview/main.ts"), StringComparison.Ordinal);
    }
}
