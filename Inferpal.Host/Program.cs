using Inferpal.Host;
using Inferpal.Host.Acp;
using Inferpal.Services;

// ── Children die with the host ───────────────────────────────────────────────
// First, before anything starts a child: on Linux and macOS the host leads a process group of its own, and a watchdog
// kills that group when the host ends, crash included (Windows binds each child to a job instead).
ProcessLifetime.LeadOwnProcessGroup();

// ── The setup an ACP client runs in a terminal ───────────────────────────────
// `--acp --setup` (the client appends the method's args to its own launch): an interactive prompt on the real console,
// nothing else. It owns stdout, so it runs before the discipline below.
if (args.Contains("--setup", StringComparer.Ordinal))
{
    // ⚠ UTF-8 both ways: a Windows console writes in its OEM code page, and every accented word of a localized question
    // came out as a replacement character.
    Console.OutputEncoding = new System.Text.UTF8Encoding(false);
    Console.InputEncoding  = new System.Text.UTF8Encoding(false);
    return await AcpSetup.RunAsync(Console.In, Console.Out, CancellationToken.None);
}

// ── stdout discipline ─────────────────────────────────────────────────────────
// The real stdout belongs to JSON-RPC framing: a single stray Console.WriteLine
// would corrupt the stream, so grab the raw pipes first and reroute Console.Out
// to stderr for everything else (Diagnostics already logs to Debug/file).
var stdout = Console.OpenStandardOutput();
var stdin  = Console.OpenStandardInput();
Console.SetOut(Console.Error);

// ── An Agent Client Protocol agent ────────────────────────────────────────────
// `--acp`: the same host, for Zed, the JetBrains IDEs, Neovim and Emacs — one line per message, ACP's methods, every
// session driving a HostServer of its own. It ends when the client closes stdin.
if (args.Contains("--acp", StringComparer.Ordinal))
{
    await using var agent = new AcpAgent();
    using var acp = AcpRpc.Create(stdout, stdin, agent);
    agent.Attach(acp);
    acp.StartListening();
    try { await acp.Completion; }
    catch (Exception ex) { Console.Error.WriteLine($"[Inferpal.Host] ACP connection faulted: {ex.GetBaseException().Message}"); }
    return 0;
}

using var server = new HostServer();
using var rpc = HostRpc.Create(stdout, stdin, server);
server.Attach(rpc);
rpc.StartListening();

// Exit on graceful `shutdown` or when the adapter dies and the pipe closes —
// the host must never outlive its editor. A faulted connection is surfaced on
// stderr for the adapter's output channel before exiting.
var finished = await Task.WhenAny(rpc.Completion, server.ShutdownRequested);
if (finished.IsFaulted)
    Console.Error.WriteLine($"[Inferpal.Host] connection faulted: {finished.Exception?.GetBaseException()}");
return 0;
