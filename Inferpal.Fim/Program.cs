using System.Text;
using Inferpal.Config;
using Inferpal.Fim;
using Inferpal.Services;
using Inferpal.Services.Signals;

// ── Discipline de stdout ──────────────────────────────────────────────────────
// stdout belongs to the JSON-RPC framing: a single stray Console.WriteLine would corrupt the
// stream. Grab the raw pipes first, then send Console.Out to stderr, which the caller (the
// ghost-text in-process) recopie dans /diagnostics.
var stdout = Console.OpenStandardOutput();
var stdin  = Console.OpenStandardInput();
Console.SetOut(Console.Error);
Console.OutputEncoding = Encoding.UTF8;

// ── Signal scope (§22 slice 2) ────────────────────────────────────────────────
// Family-A channels are scoped by devenv PID. This sidecar is started BY devenv and receives its
// PID: without this declaration it would read an unscoped ChatBusySignal and ghost text would stop
// yielding to the chat - exactly the GPU contention that signal exists to avoid.
for (var i = 0; i < args.Length - 1; i++)
{
    if (args[i] != "--vs-pid" || !int.TryParse(args[i + 1], out var vsPid)) continue;
    try { SignalScope.DeclareVsInstance(vsPid); }
    catch (Exception ex) { Diagnostics.Swallow("Fim.DeclareVsInstance", ex); }
    break;
}

// ── Le Core, et rien d'autre ──────────────────────────────────────────────────
// The configuration is read once: the in-process client recycles this process when the file
// changes, which is safer than a hot reload (switching backend in flight would leave an HttpClient
// and a loaded model behind).
var config = InferpalConfig.Load();
var client = InferenceProviderFactory.Create(config);

using var lifetime = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; lifetime.Cancel(); };

var loop = new FimRpcLoop(stdin, stdout, async (request, ct) =>
{
    // A backend without FIM is not an error: it is "nothing to suggest".
    if (!client.Capabilities.Fim) return string.Empty;

    var sb = new StringBuilder();
    await client.StreamFimAsync(
        prefix:      request.Prefix,
        suffix:      request.Suffix,
        maxTokens:   request.MaxTokens,
        temperature: request.Temperature,
        onToken:     token => sb.Append(token),
        ct:          ct,
        model:       request.Model);
    return sb.ToString();
});

await loop.RunAsync(lifetime.Token);
