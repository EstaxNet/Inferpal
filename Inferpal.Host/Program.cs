using Inferpal.Host;

// ── stdout discipline ─────────────────────────────────────────────────────────
// The real stdout belongs to JSON-RPC framing: a single stray Console.WriteLine
// would corrupt the stream, so grab the raw pipes first and reroute Console.Out
// to stderr for everything else (Diagnostics already logs to Debug/file).
var stdout = Console.OpenStandardOutput();
var stdin  = Console.OpenStandardInput();
Console.SetOut(Console.Error);

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
