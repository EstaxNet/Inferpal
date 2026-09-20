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
/// The sidecar receives the PID of the hosting devenv so it lands in the same signal scope.
/// </para>
/// <para>
/// <b>Lifetime.</b> Started on demand, recycled when the configuration changes (backend or model),
/// killed by <c>GhostTextPackage.Dispose</c>. And if devenv dies without disposing anything, the
/// child dies too: its stdin closes, which its loop treats as a shutdown order.
/// </para>
/// </remarks>
internal static class FimSidecar
{
    private const string ExeName = "Inferpal.Fim.exe";

    private static readonly object _gate = new object();

    private static Process? _process;
    private static Stream?  _stdin;
    private static long     _configStamp = -1;
    private static int      _nextId;
    private static long     _seenStamp = -1;

    // Two natures of failure, not one: a missing executable is permanent, a failed start is not.
    // A single boolean conflated them and nothing ever cleared it, so an antivirus holding the exe
    // for a second killed ghost text for the whole life of that devenv - with its only trace in
    // the in-process diagnostics ring, which `/diagnostics` does not read.
    private static readonly RetryGate _startGate = new RetryGate(TimeSpan.FromSeconds(30));

    private static readonly ConcurrentDictionary<int, TaskCompletionSource<string?>> _pending =
        new ConcurrentDictionary<int, TaskCompletionSource<string?>>();

    // What a request receives when the pipe closes under it: the sidecar went away without answering.
    // A distinct instance compared by reference — never an answer, never a cancellation (null).
    private static readonly string DeadPipe = new string('\0', 1);

    // Same, for the sidecar that is ALIVE and simply never answers.
    private static readonly string NoAnswer = new string('\0', 2);

    /// <summary>
    /// How long a completion may take before the wait is abandoned.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠ Without it the wait is bounded by the CALLER's token alone, and that token is cancelled by
    /// the next keystroke — so a sidecar that is alive, whose pipe is open, and that never answers
    /// leaves the request hanging for as long as the user waits for the suggestion, which is exactly
    /// what someone does while waiting for a suggestion. Every other death branch here says why
    /// (<see cref="NoteDeathLocked"/>, <see cref="ReleasePending"/> — "nobody must stay hanging on a
    /// dead pipe"); this was the one branch that never returned at all, so the <c>fim</c> door stayed
    /// false with no reason, the state this component is built to never produce.
    /// </para>
    /// <para>
    /// Generous on purpose, and aligned with the MCP client's <c>CallTimeout</c>: the three other
    /// RPCs of this repository (MCP over stdio, MCP over HTTP, the LSP server) all carry a budget,
    /// and this is the fourth. ⚠ It does <b>not</b> recycle the sidecar: a cold model load can take
    /// a long time, and killing a process that is loading turns a slow first completion into a
    /// restart loop. A wedged sidecar therefore costs one budget per keystroke — and says so.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(120);

    // First stderr line of the current sidecar: a .NET start-up crash puts its cause there ("Could
    // not load file or assembly…"), and that is the reason a user needs to read.
    private static string? _firstStderr;

    // 1 once the heartbeat says the sidecar answers, back to 0 when it dies. The heartbeat is a file:
    // rewriting it on every completion cost a read and a write per pause in typing.
    private static int _answered;

    /// <summary>Test seam: the directory to look for the sidecar executable in.</summary>
    internal static string? DirectoryOverride;

    /// <summary>Test seam: how many sidecar processes this devenv has launched.</summary>
    internal static int LaunchCount;

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

            string? raw;
            using (var budget = new CancellationTokenSource(CallTimeout))
            using (budget.Token.Register(() => Expire(id)))
            using (ct.Register(() => Cancel(id)))
                raw = await tcs.Task.ConfigureAwait(false);

            // Cancelled (null), the pipe closed under the request (DeadPipe — ReadLoop has said
            // why), or the budget expired (NoAnswer — Expire has said why): none is an answer, and
            // none may be recorded as one.
            if (raw is null || ReferenceEquals(raw, DeadPipe) || ReferenceEquals(raw, NoAnswer))
                return null;

            // ⚠ The door is recorded on an ANSWER, not on a start: a process that starts and then
            // dies on the first request is not a working sidecar. Recording it clears the reason,
            // so a failure that has been repaired stops being announced. Once per sidecar life:
            // the heartbeat is a file.
            if (Interlocked.Exchange(ref _answered, 1) == 0)
                Services.Signals.InProcAliveSignal.Record(Services.Signals.InProcAliveSignal.ComponentFim);
            return NullIfEmpty(raw);
        }
        catch (Exception ex)
        {
            Diagnostics.Swallow("FimSidecar.Complete", ex);
            lock (_gate)
            {
                // Writing to a sidecar that has already died is the other way its death shows. Only
                // while it is still the current one: a newer, healthy sidecar must not pay for it.
                if (ReferenceEquals(_stdin, stdin))
                {
                    NoteDeathLocked(_process);
                    RecycleLocked();
                }
            }
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
        // A new configuration is new evidence: a start that failed under the previous one says
        // nothing about this one. ⚠ The comparison is against the last SEEN stamp, not against
        // `_configStamp` - which is only written after a SUCCESSFUL start, and would therefore
        // differ on every call while we keep failing: the cooldown would never hold.
        if (configStamp != _seenStamp)
        {
            _seenStamp = configStamp;
            _startGate.ClearCooldown();
        }

        // The configuration moved (backend, model, key): the sidecar read the old one at startup.
        if (_process != null && configStamp != _configStamp) RecycleLocked();
        if (_process != null && !_process.HasExited) return true;
        if (_process != null)
        {
            // Died on its own since the last request: a failure to hold the door for, not a cue to
            // start again at once — a sidecar that dies at start-up was relaunched on every pause in
            // typing, and nothing said why.
            NoteDeathLocked(_process);
            RecycleLocked();
        }
        if (!_startGate.MayTry()) return false;

        var dir = DirectoryOverride
                  ?? Path.GetDirectoryName(typeof(FimSidecar).Assembly.Location)
                  ?? ".";
        var exe = Path.Combine(dir, ExeName);
        if (!File.Exists(exe))
        {
            // No exception: an incomplete VSIX must not flood the log on every keystroke. Say it
            // once, and ghost text simply stays quiet.
            Diagnostics.Record("FimSidecar.Start", "not found: " + exe);
            // ⚠ And in the channel the user can READ: this ring is the in-process one, not the one
            // /diagnostics renders. Without this line ghost text goes quiet and the cause exists
            // for nobody.
            Services.Signals.InProcAliveSignal.RecordFimUnavailable("sidecar executable not found");
            _startGate.LatchPermanently();   // an incomplete VSIX does not repair itself
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
            if (proc is null)
            {
                Services.Signals.InProcAliveSignal.RecordFimUnavailable("sidecar process did not start");
                _startGate.Backoff();
                return false;
            }

            LaunchCount++;
            _process     = proc;
            _stdin       = proc.StandardInput.BaseStream;
            _configStamp = configStamp;
            _firstStderr = null;
            Interlocked.Exchange(ref _answered, 0);

            var stdout = proc.StandardOutput.BaseStream;
            // Detached read loop: it lives as long as the pipe does (VSTHRD110: _ =).
            _ = Task.Run(() => ReadLoop(proc, stdout));

            // The sidecar's stderr: its diagnostic traces, never the protocol.
            proc.ErrorDataReceived += (_, e) =>
            {
                if (string.IsNullOrWhiteSpace(e.Data)) return;
                Diagnostics.Record("Fim.stderr", e.Data!);
                Interlocked.CompareExchange(ref _firstStderr, e.Data, null);
            };
            proc.BeginErrorReadLine();
            return true;
        }
        catch (Exception ex)
        {
            Diagnostics.Swallow("FimSidecar.Start", ex);
            Services.Signals.InProcAliveSignal.RecordFimUnavailable(ex.GetType().Name + " at start");
            _startGate.Backoff();            // antivirus, memory pressure... : this can pass
            return false;
        }
    }

    private static void Recycle() { lock (_gate) RecycleLocked(); }

    private static void RecycleLocked()
    {
        ReleasePending(null);

        var proc = _process;
        _process = null;
        _stdin   = null;
        if (proc is null) return;

        try { if (!proc.HasExited) proc.Kill(); } catch { /* nettoyage */ }
        try { proc.Dispose(); } catch { /* cleanup */ }
    }

    /// <summary>Releases pending waits: nobody must stay hanging on a dead pipe.</summary>
    /// <param name="outcome"><c>null</c> for a deliberate recycle (nothing to report),
    /// <see cref="DeadPipe"/> when the sidecar went away under the requests.</param>
    private static void ReleasePending(string? outcome)
    {
        foreach (var kv in _pending) kv.Value.TrySetResult(outcome);
        _pending.Clear();
    }

    /// <summary>
    /// The sidecar went away without answering: hold the door for one cooldown, and say why in the
    /// channel <c>/diagnostics</c> reads — this process's own ring is one nobody can.
    /// </summary>
    private static void NoteDeathLocked(Process? proc)
    {
        _startGate.Backoff();
        Interlocked.Exchange(ref _answered, 0);
        Services.Signals.InProcAliveSignal.RecordFimUnavailable(DescribeDeath(proc));
    }

    private static string DescribeDeath(Process? proc)
    {
        string? code = null;
        try { if (proc is { HasExited: true }) code = proc.ExitCode.ToString(CultureInfo.InvariantCulture); }
        catch (InvalidOperationException) { /* disposed by a concurrent recycle */ }
        catch (System.ComponentModel.Win32Exception) { /* exit state unreadable */ }

        var reason = code is null ? "sidecar stopped without answering" : "sidecar exited with code " + code;
        var first  = _firstStderr?.Trim();
        if (string.IsNullOrEmpty(first)) return reason;
        return reason + ": " + (first!.Length <= 200 ? first : first.Substring(0, 200) + "…");
    }

    /// <summary>
    /// The budget ran out: the sidecar is alive and has not answered. Say why — a door that is
    /// false without a reason is the one thing <see cref="InProcAliveSignal"/> exists to prevent —
    /// then tell the sidecar to drop the request, exactly as a keystroke would.
    /// </summary>
    private static void Expire(int id)
    {
        if (!_pending.TryGetValue(id, out var tcs)) return;   // answered between the two
        if (!tcs.TrySetResult(NoAnswer)) return;

        Interlocked.Exchange(ref _answered, 0);
        Services.Signals.InProcAliveSignal.RecordFimUnavailable(
            "sidecar did not answer within " + CallTimeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + " s");

        Cancel(id);
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

    private static void ReadLoop(Process proc, Stream stdout)
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
        finally
        {
            // A closed output usually means the process is exiting: give it a moment, so the reason
            // can carry its exit code. Outside the lock — completions must not wait on it.
            try { proc.WaitForExit(500); }
            catch (InvalidOperationException) { /* disposed by a recycle */ }
            catch (System.ComponentModel.Win32Exception) { /* exit state unreadable */ }

            lock (_gate)
            {
                // Only this sidecar's requests. A recycle has already released them and may have
                // started the next sidecar, whose requests this reader must not fail.
                if (ReferenceEquals(_process, proc))
                {
                    NoteDeathLocked(proc);
                    ReleasePending(DeadPipe);
                    RecycleLocked();
                }
            }
        }
    }

    /// <summary>Reads headers up to the blank line. Returns the body size, or -1 if closed.</summary>
    private static int ReadHeaders(Stream stdout)
    {
        var reader = new Services.FrameHeaderReader();
        while (true)
        {
            var b = stdout.ReadByte();
            if (b < 0) return -1;

            var outcome = reader.Feed((byte)b);
            if (outcome == Services.FrameHeaderReader.Outcome.Pending) continue;
            return outcome == Services.FrameHeaderReader.Outcome.Complete ? reader.Length : -1;
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
            // An error response is still an answer — the sidecar is alive, there is just nothing to
            // show. null is what a cancellation receives, and would not say so.
            tcs.TrySetResult(string.Empty);
        }
        catch (Exception ex) { Diagnostics.Swallow("FimSidecar.Dispatch", ex); }
    }
}
