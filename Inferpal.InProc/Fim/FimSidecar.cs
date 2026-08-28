using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Inferpal.GhostText;

/// <summary>
/// The bridge between ghost text (in-process, net472) and inference (the Core, net8): a child
/// <c>Inferpal.Fim</c> process started on the first completion, kept alive, and queried over
/// header-framed JSON-RPC 2.0 (<c>Content-Length</c>) on its standard pipes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a process.</b> <c>devenv</c> is a .NET Framework 4.7.2 process, so the in-process
/// assembly cannot be .NET 8, so it cannot load <c>Inferpal.Core</c> — neither the inference
/// providers, nor <c>InferpalConfig</c>, nor the <c>GpuScheduler</c>. Rewriting a FIM client in
/// net472 would have duplicated backend resolution, per-provider capabilities and authentication:
/// exactly the drift this repository has already paid for elsewhere. The sidecar, by contrast,
/// <em>is</em> the Core.
/// </para>
/// <para>
/// <b>Why not the out-of-process host.</b> It already exists and carries the Core — but its
/// lifetime is not ours: the Extensibility hub starts when VS decides to, and ghost text cannot
/// depend on the user having opened the chat at least once.
/// </para>
/// <para>
/// <b>GPU coordination.</b> None is added here: <c>StreamFimAsync</c> already yields to the chat
/// through <c>GpuScheduler.ShouldFimYield()</c> / <c>ChatBusySignal</c>, which is a <i>file</i>
/// signal and therefore cross-process by construction — precisely the case it was written for.
/// The sidecar receives the PID of the hosting devenv so it lands in the same signal scope (§22).
/// </para>
/// <para>
/// <b>Lifetime.</b> Started on demand, recycled when the configuration changes (backend or model),
/// killed by <c>GhostTextPackage.Dispose</c>. And if devenv dies without disposing anything, the
/// child dies too: its stdin closes, which its loop treats as a shutdown order.
/// </para>
/// </remarks>
/// </remarks>
internal static class FimSidecar
{
    private const string ExeName = "Inferpal.Fim.exe";

    private static readonly object _gate = new object();

    private static Process? _process;
    private static Stream?  _stdin;
    private static long     _configStamp = -1;
    private static int      _nextId;
    private static bool     _disabled;   // start failed: do not retry on every keystroke

    private static readonly ConcurrentDictionary<int, TaskCompletionSource<string?>> _pending =
        new ConcurrentDictionary<int, TaskCompletionSource<string?>>();

    /// <summary>Test seam: the directory to look for the sidecar executable in.</summary>
    internal static string? DirectoryOverride;

    /// <summary>
    /// Requests a completion. Returns <c>null</c> when there is nothing to show — sidecar
    /// unavailable, cancellation, backend without FIM, or an empty answer. Never throws.
    /// </summary>
    internal static async Task<string?> CompleteAsync(
        string prefix, string suffix, int maxTokens, double temperature, string? model,
        long configStamp, CancellationToken ct)
    {
        Stream? stdin;
        int id;
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_gate)
        {
            if (!EnsureStarted(configStamp)) return null;
            stdin = _stdin;
            id    = ++_nextId;
        }
        if (stdin is null) return null;

        _pending[id] = tcs;
        try
        {
            var payload = new StringBuilder()
                .Append("{\"jsonrpc\":\"2.0\",\"id\":").Append(id)
                .Append(",\"method\":\"fim/complete\",\"params\":{")
                .Append("\"prefix\":").Append(JsonSerializer.Serialize(prefix))
                .Append(",\"suffix\":").Append(JsonSerializer.Serialize(suffix))
                .Append(",\"maxTokens\":").Append(maxTokens)
                .Append(",\"temperature\":")
                .Append(temperature.ToString("R", CultureInfo.InvariantCulture))
                .Append(",\"model\":").Append(model is null ? "null" : JsonSerializer.Serialize(model))
                .Append("}}")
                .ToString();

            Send(stdin, payload);

            using (ct.Register(() => Cancel(id)))
                return NullIfEmpty(await tcs.Task.ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            Diagnostics.Swallow("FimSidecar.Complete", ex);
            Recycle();
            return null;
        }
        finally { _pending.TryRemove(id, out _); }
    }

    /// <summary>Stops the sidecar. Called from the package <c>Dispose</c>; safe to repeat.</summary>
    internal static void Shutdown() => Recycle();

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    // ── Lifetime ──────────────────────────────────────────────────────────────

    private static bool EnsureStarted(long configStamp)
    {
        // The configuration moved (backend, model, key): the sidecar read the old one at startup.
        if (_process != null && configStamp != _configStamp) RecycleLocked();
        if (_process != null && !_process.HasExited) return true;
        if (_process != null) RecycleLocked();            // died on its own: start again
        if (_disabled) return false;

        var dir = DirectoryOverride
                  ?? Path.GetDirectoryName(typeof(FimSidecar).Assembly.Location)
                  ?? ".";
        var exe = Path.Combine(dir, ExeName);
        if (!File.Exists(exe))
        {
            // No exception: an incomplete VSIX must not flood the log on every keystroke. Say it
            // once, and ghost text simply stays quiet.
            Diagnostics.Record("FimSidecar.Start", "not found: " + exe);
            _disabled = true;
            return false;
        }

        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                Arguments              = "--vs-pid " + Process.GetCurrentProcess().Id,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardInput  = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                WorkingDirectory       = dir,
            };
            var proc = Process.Start(psi);
            if (proc is null) { _disabled = true; return false; }

            _process     = proc;
            _stdin       = proc.StandardInput.BaseStream;
            _configStamp = configStamp;

            var stdout = proc.StandardOutput.BaseStream;
            // Detached read loop: it lives as long as the pipe does (VSTHRD110: _ =).
            _ = Task.Run(() => ReadLoop(stdout));

            // The sidecar's stderr: its diagnostic traces, never the protocol.
            proc.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data)) Diagnostics.Record("Fim.stderr", e.Data!);
            };
            proc.BeginErrorReadLine();
            return true;
        }
        catch (Exception ex)
        {
            Diagnostics.Swallow("FimSidecar.Start", ex);
            _disabled = true;
            return false;
        }
    }

    private static void Recycle() { lock (_gate) RecycleLocked(); }

    private static void RecycleLocked()
    {
        ReleasePending();

        var proc = _process;
        _process = null;
        _stdin   = null;
        if (proc is null) return;

        try { if (!proc.HasExited) proc.Kill(); } catch { /* nettoyage */ }
        try { proc.Dispose(); } catch { /* cleanup */ }
    }

    /// <summary>Releases pending waits: nobody must stay hanging on a dead pipe.</summary>
    private static void ReleasePending()
    {
        foreach (var kv in _pending) kv.Value.TrySetResult(null);
        _pending.Clear();
    }

    private static void Cancel(int id)
    {
        if (_pending.TryGetValue(id, out var tcs)) tcs.TrySetResult(null);

        Stream? stdin;
        lock (_gate) stdin = _stdin;
        if (stdin is null) return;

        try { Send(stdin, "{\"jsonrpc\":\"2.0\",\"method\":\"fim/cancel\",\"params\":{\"id\":" + id + "}}"); }
        catch (Exception ex) { Diagnostics.Swallow("FimSidecar.Cancel", ex); }
    }

    // ── Cadrage ───────────────────────────────────────────────────────────────

    private static void Send(Stream stdin, string json)
    {
        var body   = Encoding.UTF8.GetBytes(json);
        var header = Encoding.ASCII.GetBytes("Content-Length: " + body.Length + "\r\n\r\n");
        lock (stdin)
        {
            stdin.Write(header, 0, header.Length);
            stdin.Write(body, 0, body.Length);
            stdin.Flush();
        }
    }

    private static void ReadLoop(Stream stdout)
    {
        try
        {
            while (true)
            {
                var length = ReadHeaders(stdout);
                if (length < 0) break;                       // pipe closed

                var body = new byte[length];
                var read = 0;
                while (read < length)
                {
                    var n = stdout.Read(body, read, length - read);
                    if (n <= 0) return;
                    read += n;
                }
                Dispatch(Encoding.UTF8.GetString(body));
            }
        }
        catch (Exception ex) { Diagnostics.Swallow("FimSidecar.Read", ex); }
        finally { ReleasePending(); }
    }

    /// <summary>Reads headers up to the blank line. Returns the body size, or -1 if closed.</summary>
    private static int ReadHeaders(Stream stdout)
    {
        var line   = new StringBuilder();
        var length = -1;
        var any    = false;

        while (true)
        {
            var b = stdout.ReadByte();
            if (b < 0) return -1;
            if (b != '\n') { if (b != '\r') line.Append((char)b); continue; }

            var text = line.ToString();
            line.Length = 0;
            if (text.Length == 0) return any ? length : -1;   // end of headers

            any = true;
            const string marker = "Content-Length:";
            if (text.StartsWith(marker, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(text.Substring(marker.Length).Trim(), out var parsed) &&
                parsed >= 0 && parsed <= 8 * 1024 * 1024)
                length = parsed;
        }
    }

    private static void Dispatch(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("id", out var idElement) ||
                idElement.ValueKind != JsonValueKind.Number ||
                !_pending.TryRemove(idElement.GetInt32(), out var tcs))
                return;

            if (root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.String)
            {
                tcs.TrySetResult(result.GetString());
                return;
            }

            if (root.TryGetProperty("error", out var error)) Diagnostics.Record("Fim.error", error.ToString());
            tcs.TrySetResult(null);
        }
        catch (Exception ex) { Diagnostics.Swallow("FimSidecar.Dispatch", ex); }
    }
}
