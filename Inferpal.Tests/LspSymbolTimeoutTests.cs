using System.Diagnostics;
using System.IO;
using Inferpal.Services;
using Inferpal.Services.Lsp;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A language server that does not answer <c>documentSymbol</c> is said, once, and after a few unanswered requests in a
/// row it is turned off for the session — the rest of the pass does not wait out the budget file after file.
/// </summary>
/// <remarks>
/// An expired request came back as a cancellation and was returned as null, like an empty answer: the file went to the
/// heuristic chunker without a word, and every following file paid the whole wait — a stuck server cost the index ten
/// seconds a file, in silence.
/// </remarks>
[Collection("Diagnostics")]
public sealed class LspSymbolTimeoutTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("inferpal-lsp-timeout-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    /// <summary>A server that answers <c>initialize</c>, and <c>documentSymbol</c> only when told to.</summary>
    private const string Server = """
        import json, sys
        answers = sys.argv[1] == "1"
        inp, out = sys.stdin.buffer, sys.stdout.buffer
        def read():
            length = None
            while True:
                line = inp.readline()
                if not line:
                    sys.exit(0)
                line = line.strip()
                if not line:
                    break
                k, v = line.split(b":", 1)
                if k.strip().lower() == b"content-length":
                    length = int(v.strip())
            return json.loads(inp.read(length))
        def send(m):
            b = json.dumps(m).encode()
            out.write(b"Content-Length: %d\r\n\r\n" % len(b) + b)
            out.flush()
        while True:
            m = read()
            if m.get("method") == "initialize":
                send({"jsonrpc": "2.0", "id": m["id"], "result": {"capabilities": {"documentSymbolProvider": True}}})
            elif m.get("method") == "textDocument/documentSymbol" and answers:
                send({"jsonrpc": "2.0", "id": m["id"], "result": []})
        """;

    private async Task<LspSemanticProvider.LspServerSession?> SessionAsync(bool answers)
    {
        if (!await PythonForTests.IsInstalledAsync()) return null;   // UNDECIDED without Python — never read as a pass
        var script = Path.Combine(_dir, "server.py");
        File.WriteAllText(script, Server);
        var python = OperatingSystem.IsWindows() ? "python" : "python3";
        return new LspSemanticProvider.LspServerSession("python", _dir, () => (python, $"\"{script}\" {(answers ? "1" : "0")}"))
        {
            RequestTimeout = TimeSpan.FromMilliseconds(500),
        };
    }

    private static List<DiagnosticEntry> Said() =>
        Diagnostics.Snapshot().Where(e => e.Context == "Lsp").ToList();

    [Fact]
    public async Task AServerThatDoesNotAnswer_IsSaidOnce_ThenTurnedOff()
    {
        Diagnostics.Clear();
        var session = await SessionAsync(answers: false);
        Assert.True(session is not null, "UNDECIDED: no Python to play the language server");
        using (session)
        {
            for (var i = 0; i < LspSemanticProvider.LspServerSession.MaxExpiredInARow; i++)
                Assert.Null(await session!.GetSymbolsAsync(Path.Combine(_dir, $"f{i}.py"), "x = 1\n", CancellationToken.None));

            var said = Said();
            Assert.Single(said, e => e.Detail.Contains("did not answer 'documentSymbol' for f0.py within 0.5 s"));
            Assert.Single(said, e => e.Detail.Contains($"did not answer 'documentSymbol' {LspSemanticProvider.LspServerSession.MaxExpiredInARow} times in a row"));

            // Off for the session: the next file does not wait for the budget.
            var watch = Stopwatch.StartNew();
            Assert.Null(await session!.GetSymbolsAsync(Path.Combine(_dir, "next.py"), "x = 1\n", CancellationToken.None));
            Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(400), $"the next file waited {watch.Elapsed}");
        }
    }

    [Fact]
    public async Task AServerThatAnswers_SaysNothing()
    {
        // Reference arm: an answer — even an empty one — is no expiry.
        Diagnostics.Clear();
        var session = await SessionAsync(answers: true);
        Assert.True(session is not null, "UNDECIDED: no Python to play the language server");
        using (session)
        {
            for (var i = 0; i < 4; i++)
                Assert.NotNull(await session!.GetSymbolsAsync(Path.Combine(_dir, $"f{i}.py"), "x = 1\n", CancellationToken.None));
            Assert.DoesNotContain(Said(), e => e.Detail.Contains("did not answer"));
        }
    }
}
