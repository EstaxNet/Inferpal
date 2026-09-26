using System.IO;
using Inferpal.Services;
using Inferpal.Services.Lsp;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// When the language server cannot start, indexing falls back to the heuristic chunker and /diagnostics says why. The
/// "why" was the pipe's — "TaskCanceledException: A task was canceled." — while the server's own reason, written on
/// its stderr, was drained and discarded.
/// </summary>
[Collection("Diagnostics")]
public sealed class LspStartFailureTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("inferpal-lsp-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    // A server that dies the way a broken pylsp does: its reason on stderr, then a non-zero exit. On Windows the session
    // launches through `cmd /c <name> <args>`; elsewhere it runs <name> with <args>.
    private static (string name, string args) DiesWithAReason() => OperatingSystem.IsWindows()
        ? ("echo", "ModuleNotFoundError: No module named pylsp_marker 1>&2 & exit 3")
        : ("/bin/sh", "-c \"echo 'ModuleNotFoundError: No module named pylsp_marker' >&2; exit 3\"");

    [Fact]
    public async Task AServerThatDiesAtStartup_IsReportedWithWhatItWroteAndItsExitCode()
    {
        Diagnostics.Clear();
        using var session = new LspSemanticProvider.LspServerSession("python", _dir, () => DiesWithAReason());
        var file = Path.Combine(_dir, "a.py");

        for (var i = 0; i < 3; i++)   // three failed starts turn the LSP off for the session
            Assert.Null(await session.GetSymbolsAsync(file, "x = 1\n", CancellationToken.None));

        var said = Diagnostics.Snapshot().Where(e => e.Context == "Lsp" && e.Detail.Contains("pylsp_marker")).ToList();
        Assert.Single(said);
        Assert.Contains("No module named pylsp_marker", said[0].Detail);
        Assert.Contains("exited with code 3", said[0].Detail);
    }
}
