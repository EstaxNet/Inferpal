using System.Text;
using Inferpal.Config;
using Inferpal.Fim;
using Inferpal.Services;
using Inferpal.Services.CodeActions;
using Inferpal.Services.Signals;

// ── Discipline de stdout ──────────────────────────────────────────────────────
// stdout belongs to the JSON-RPC framing: a single stray Console.WriteLine would corrupt the
// stream. Grab the raw pipes first, then send Console.Out to stderr, which the caller (the
// ghost-text in-process half) copies into /diagnostics.
var stdout = Console.OpenStandardOutput();
var stdin  = Console.OpenStandardInput();
Console.SetOut(Console.Error);
Console.OutputEncoding = Encoding.UTF8;

// ── Signal scope ────────────────────────────────────────────────
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

// ── The Core, and nothing else ───────────────────────────────────────────────
// The configuration is read once: the in-process client recycles this process when the file
// changes, which is safer than a hot reload (switching backend in flight would leave an HttpClient
// and a loaded model behind).
var config = InferpalConfig.Load();
var client = InferenceProviderFactory.Create(config);

using var lifetime = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; lifetime.Cancel(); };

string? declaredFor = null;   // the solution whose workspace the ignore files were declared for
var loop = new FimRpcLoop(stdin, stdout, async (request, ct) =>
{
    // A backend without FIM is not an error: it is "nothing to suggest".
    if (!client.Capabilities.Fim) return string.Empty;
    // ⚠ A completion sends the document around the caret: a file the workspace's ignore files exclude is never completed.
    // The root is the open solution's extent, or the folder Visual Studio has open, as the chat window decides it.
    // Declared when what is open changes, not at each pause in typing: the extent reads the solution and the
    // repository, and a declaration reads the ignore files again.
    if (request.Path is { Length: > 0 } path)
    {
        var open = ActiveSolutionSignal.TryReadSolutionPath() ?? ActiveSolutionSignal.TryReadOpenFolder();
        if (!string.Equals(open, declaredFor, StringComparison.Ordinal))
        {
            declaredFor = open;
            if (SolutionExtent.ActiveRoot() is { } root)
                Inferpal.Services.Governance.ContentExclusions.Declare(root, () => config.IgnoreFiles);
        }
        if (Inferpal.Services.Governance.ContentExclusions.Of(path) is not null) return string.Empty;
    }

    var sb = new StringBuilder();
    await client.StreamFimAsync(
        prefix:      request.Prefix,
        suffix:      request.Suffix,
        maxTokens:   request.MaxTokens,
        temperature: request.Temperature,
        onToken:     token => sb.Append(token),
        ct:          ct,
        model:       request.Model);
    // Without what already follows the caret: models repeat it, and accepted it is doubled.
    return FimCompletion.Finish(sb.ToString(), request.Suffix);
});

await loop.RunAsync(lifetime.Token);
