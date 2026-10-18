using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Inferpal.Services.Hardware;

/// <summary>
/// Keeps the machine awake while the model works — a chat answer, an agent run, a background task — and lets it sleep
/// again once nothing does. Held by every chat lease (<see cref="GpuScheduler.AcquireChatLease"/>), so the two editors
/// and every ACP thread have it without a line of their own.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ A laptop left alone during a ten-minute test run goes to sleep, and the request in flight to a server on another
/// machine is lost: the run ends with a connection error nobody caused.
/// </para>
/// <para>
/// A hold, not a count: the machine stays awake while one live hold is not paused. A turn waiting for a person (an
/// approval card, a step pause) pauses ITS holds (<see cref="PauseThisFlow"/>): nothing is in flight meanwhile, and a
/// card left for an hour must not drain the battery — a background task running beside it keeps the machine awake.
/// ⚠ The flow knows its holds by the tokens it took, never by a number: a number is copied into every task the turn
/// starts, and a task that outlives the turn's hold would pause a hold that is gone — and with it another work's.
/// </para>
/// <para>
/// Released <see cref="Grace"/> after the last hold, not at once: one turn chains several requests (a compaction
/// summary, then the answer), and on macOS and Linux each lock is a process. ⚠ A lock is let go OUTSIDE the guard's
/// lock: ending a command may wait for it, and the next turn's first request takes a hold.
/// </para>
/// </remarks>
internal sealed class SleepGuard
{
    /// <summary>What a platform does to keep the machine awake: a lock, or <c>null</c> with why it could not.</summary>
    internal interface IPlatform
    {
        /// <summary>Takes the lock; <paramref name="lost"/> is called if it is lost on its own (its process ended).</summary>
        IDisposable? Inhibit(string reason, Action<string> lost, out string? failure);
    }

    /// <summary>The guard the chat leases hold. The test suite replaces it once, before any test, with a silent one.</summary>
    internal static SleepGuard Shared { get; set; } = new(SystemPlatform.ForThisSystem(), TimeSpan.FromSeconds(10));

    /// <summary>The reason the system shows for the lock (Windows: <c>powercfg /requests</c>; macOS: <c>pmset -g assertions</c>).</summary>
    internal const string Reason = "Inferpal: the model is working (a chat answer, an agent run or a background task)";

    private readonly IPlatform _platform;
    private readonly object _lock = new();
    private readonly AsyncLocal<FlowLink?> _flow = new();
    private readonly HashSet<HoldToken> _live = [];
    private IDisposable? _held;
    private Timer? _releaseTimer;
    private int _generation;

    internal SleepGuard(IPlatform platform, TimeSpan grace)
    {
        _platform = platform;
        Grace = grace;
    }

    /// <summary>How long the lock outlives the last hold.</summary>
    internal TimeSpan Grace { get; }

    /// <summary>Whether the machine is being kept awake now.</summary>
    internal bool IsHeld
    {
        get { lock (_lock) return _held is not null; }
    }

    /// <summary>
    /// Keeps the machine awake until the returned token is disposed. Synchronous on purpose: the flow that calls it —
    /// the turn — is the one <see cref="PauseThisFlow"/> recognises later.
    /// </summary>
    public IDisposable Hold()
    {
        var hold = new HoldToken(this);
        _flow.Value = new FlowLink(hold, _flow.Value);
        Step step;
        lock (_lock)
        {
            _live.Add(hold);
            step = UpdateLocked();
        }
        Apply(step);
        return hold;
    }

    /// <summary>
    /// Lets the machine sleep while this flow waits for a person (an approval card, a step pause), as far as this flow's
    /// own live holds go; a no-op for a flow that holds none — a dialog shown outside a turn must not release a
    /// background task's lock.
    /// </summary>
    public IDisposable PauseThisFlow()
    {
        List<HoldToken> mine = [];
        Step step;
        lock (_lock)
        {
            for (var link = _flow.Value; link is not null; link = link.Next)
                if (_live.Contains(link.Hold))
                {
                    link.Hold.Pauses++;
                    mine.Add(link.Hold);
                }
            if (mine.Count == 0) return NoOp.Instance;
            step = UpdateLocked();
        }
        Apply(step);
        return new PauseToken(this, mine);
    }

    private void EndHold(HoldToken hold)
    {
        Step step;
        lock (_lock)
        {
            _live.Remove(hold);
            step = UpdateLocked();
        }
        Apply(step);
    }

    private void EndPause(List<HoldToken> paused)
    {
        Step step;
        lock (_lock)
        {
            foreach (var hold in paused) hold.Pauses = Math.Max(0, hold.Pauses - 1);
            step = UpdateLocked();
        }
        Apply(step);
    }

    private bool WantedLocked => _live.Any(h => h.Pauses == 0);

    /// <summary>What a change of holds asks for, done once the guard's lock is let go: a lock to let go, one to take.</summary>
    private readonly record struct Step(IDisposable? Release, bool Take);

    /// <summary>
    /// Decides — under the guard's lock — whether to take the lock, schedule its release, or let it go now. ⚠ Taking and
    /// letting go happen OUTSIDE it (<see cref="Apply"/>): both may start or wait for a process, and the next request's
    /// hold, every other turn's and every task's wait on the guard meanwhile.
    /// </summary>
    private Step UpdateLocked()
    {
        if (WantedLocked)
        {
            _generation++;   // a release scheduled before is void
            _releaseTimer?.Dispose();
            _releaseTimer = null;
            if (_held is not null || _taking || _unavailable) return default;
            _taking = true;
            return new Step(null, Take: true);
        }
        if (_held is null || _releaseTimer is not null) return default;
        if (Grace <= TimeSpan.Zero) return new Step(DetachLocked(), Take: false);
        var generation = ++_generation;
        _releaseTimer = new Timer(_ =>
        {
            IDisposable? release = null;
            lock (_lock)
                if (generation == _generation && !WantedLocked) release = DetachLocked();
            Let(release);
        }, null, Grace, Timeout.InfiniteTimeSpan);
        return default;
    }

    private void Apply(Step step)
    {
        Let(step.Release);
        if (step.Take) Take();
    }

    // ⚠ A system that refuses the lock refuses it every time (no logind, no permission, WSL): asked again at every
    // request of every run, it would start a process each time for nothing. One refusal, or a lock lost at once, is
    // remembered for the life of the process.
    private bool _unavailable;
    private bool _taking;
    private DateTime _takenUtc;

    /// <summary>A lock that ends sooner than this after it was taken was refused, not lost.</summary>
    private static readonly TimeSpan RefusedWithin = TimeSpan.FromSeconds(5);

    /// <summary>Takes the platform lock, outside the guard's lock, and keeps it only if it is still wanted.</summary>
    private void Take()
    {
        IDisposable? held = null;
        string? failure = null;
        try { held = _platform.Inhibit(Reason, why => Lost(held, why), out failure); }
        catch (Exception ex)
        {
            Diagnostics.Swallow("SleepGuard.Take", ex);
            failure = ex.Message;
        }

        IDisposable? unwanted = null;
        lock (_lock)
        {
            _taking = false;
            if (held is null) _unavailable = true;
            else if (WantedLocked && _held is null)
            {
                _held = held;
                _takenUtc = DateTime.UtcNow;
            }
            else unwanted = held;   // let go while it was being taken: the next hold takes a new one
        }
        Let(unwanted);
        if (held is null)
            Diagnostics.RecordOnce("SleepGuard",
                $"The machine cannot be kept awake during a run: {failure}. A sleep during a long run may lose it.",
                failure ?? "unknown");
    }

    private void Lost(IDisposable? which, string why)
    {
        var retake = false;
        lock (_lock)
        {
            if (which is null || !ReferenceEquals(_held, which)) return;
            _held = null;
            if (DateTime.UtcNow - _takenUtc < RefusedWithin) _unavailable = true;   // refused outright
            // Lost in the middle of a long command: taken again now, not at the run's next request.
            else if (WantedLocked && !_taking)
            {
                _taking = true;
                retake = true;
            }
        }
        Let(which);         // its process is gone: what is left of it is let go too
        Diagnostics.RecordOnce("SleepGuard",
            $"The lock that kept the machine awake during a run ended on its own: {why}. A sleep during a long run may lose it.",
            "lost|" + why);
        if (retake) Take();
    }

    private IDisposable? DetachLocked()
    {
        _releaseTimer?.Dispose();
        _releaseTimer = null;
        var held = _held;
        _held = null;
        return held;
    }

    private static void Let(IDisposable? held)
    {
        try { held?.Dispose(); }
        catch (Exception ex) { Diagnostics.Swallow("SleepGuard.Release", ex); }
    }

    /// <summary>The holds this flow took, newest first — tokens, so that a task the turn started knows which are alive.</summary>
    private sealed record FlowLink(HoldToken Hold, FlowLink? Next);

    private sealed class HoldToken(SleepGuard owner) : IDisposable
    {
        private int _disposed;

        /// <summary>The pauses covering this hold now. Read and written under the guard's lock.</summary>
        internal int Pauses;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.EndHold(this);
        }
    }

    private sealed class PauseToken(SleepGuard owner, List<HoldToken> paused) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.EndPause(paused);
        }
    }

    private sealed class NoOp : IDisposable
    {
        internal static readonly NoOp Instance = new();
        public void Dispose() { }
    }

    /// <summary>A platform that keeps nothing awake and says nothing — the test suite's, whose leases are counted all
    /// the same.</summary>
    internal sealed class Silent : IPlatform
    {
        public IDisposable? Inhibit(string reason, Action<string> lost, out string? failure)
        {
            failure = null;
            return NoOp.Instance;
        }
    }
}

/// <summary>The lock of the system this process runs on: Windows power request, <c>caffeinate</c>, <c>systemd-inhibit</c>.</summary>
internal static class SystemPlatform
{
    /// <summary>The platform lock of this system.</summary>
    internal static SleepGuard.IPlatform ForThisSystem() =>
        OperatingSystem.IsWindows() ? new Windows()
        : OperatingSystem.IsMacOS() ? new Command("caffeinate", ["-i", "-w", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)], keepsStdinOpen: false)
        : IsWsl() ? new Refused("the host runs under WSL: the Windows machine decides when it sleeps, and a Linux lock does not reach it")
        : new Command("systemd-inhibit", ["--what=idle:sleep", "--who=Inferpal", "--why=" + SleepGuard.Reason, "--mode=block", "cat"], keepsStdinOpen: true);

    /// <summary>
    /// Whether this Linux runs under WSL. ⚠ There <c>systemd-inhibit</c> succeeds — it inhibits the Linux VM's own logind
    /// — and the Windows machine around it sleeps all the same: the lock would be "held", and nothing said.
    /// </summary>
    internal static bool IsWsl()
    {
        if (!OperatingSystem.IsLinux()) return false;
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WSL_DISTRO_NAME"))
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WSL_INTEROP")))
            return true;
        try { return File.ReadAllText("/proc/sys/kernel/osrelease").Contains("microsoft", StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>A system where no lock can do its job: refused, with why — said once in /diagnostics.</summary>
    internal sealed class Refused(string why) : SleepGuard.IPlatform
    {
        public IDisposable? Inhibit(string reason, Action<string> lost, out string? failure)
        {
            failure = why;
            return null;
        }
    }

    /// <summary>
    /// A power request (<c>PowerRequestSystemRequired</c>): held by a HANDLE, not by a thread — unlike
    /// <c>SetThreadExecutionState</c>, whose state belongs to the calling thread, and the code that holds it is
    /// asynchronous. The system closes the handle when the process ends, however it ends.
    /// </summary>
    internal sealed class Windows : SleepGuard.IPlatform
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ReasonContext
        {
            public uint Version;
            public uint Flags;
            public string SimpleReasonString;
        }

        private const uint PowerRequestContextVersion      = 0;
        private const uint PowerRequestContextSimpleString = 0x1;
        private const int  PowerRequestSystemRequired      = 1;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr PowerCreateRequest(ref ReasonContext context);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool PowerSetRequest(IntPtr request, int requestType);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool PowerClearRequest(IntPtr request, int requestType);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        public IDisposable? Inhibit(string reason, Action<string> lost, out string? failure)
        {
            var context = new ReasonContext
            {
                Version = PowerRequestContextVersion, Flags = PowerRequestContextSimpleString, SimpleReasonString = reason,
            };
            var request = PowerCreateRequest(ref context);
            if (request == IntPtr.Zero || request == new IntPtr(-1))
            {
                failure = $"PowerCreateRequest failed (error {Marshal.GetLastWin32Error()})";
                return null;
            }
            if (!PowerSetRequest(request, PowerRequestSystemRequired))
            {
                failure = $"PowerSetRequest failed (error {Marshal.GetLastWin32Error()})";
                CloseHandle(request);
                return null;
            }
            failure = null;
            return new Request(request);
        }

        private sealed class Request(IntPtr handle) : IDisposable
        {
            private int _disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                PowerClearRequest(handle, PowerRequestSystemRequired);
                CloseHandle(handle);
            }
        }
    }

    /// <summary>
    /// A command that holds the lock for as long as it runs: <c>caffeinate -w</c> ends with this process,
    /// <c>systemd-inhibit … cat</c> with the pipe this process holds open as its input — so a crash releases it too.
    /// </summary>
    internal sealed class Command(string file, IReadOnlyList<string> args, bool keepsStdinOpen) : SleepGuard.IPlatform
    {
        public IDisposable? Inhibit(string reason, Action<string> lost, out string? failure)
        {
            var psi = new ProcessStartInfo(file)
            {
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardInput  = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            };
            foreach (var arg in args) psi.ArgumentList.Add(arg);

            Process process;
            try { process = Process.Start(psi) ?? throw new InvalidOperationException("no process"); }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                failure = $"{file} did not start ({ex.Message})";
                return null;
            }
            ProcessLifetime.Bind(process);
            if (!keepsStdinOpen) process.StandardInput.Close();
            var held = new Held(process, endsWithItsInput: keepsStdinOpen);

            // A lock refused (no logind, no permission) ends the command at once: said, with its own words, once its
            // error output is read to its end — that comes after the exit, not before.
            var stderr = process.StandardError.ReadToEndAsync();
            _ = process.StandardOutput.ReadToEndAsync();
            // ⚠ Attached BEFORE it can be raised: a command refused at once exits in microseconds, and an exit with no
            // handler yet is never seen — the dead lock would pass for a held one.
            process.Exited += (_, _) => _ = SayLostAsync(process, stderr, held, lost);
            process.EnableRaisingEvents = true;
            failure = null;
            return held;
        }

        private async Task SayLostAsync(Process process, Task<string> stderr, Held held, Action<string> lost)
        {
            if (held.Released) return;
            var said = string.Empty;
            try { said = (await stderr.WaitAsync(TimeSpan.FromSeconds(2))).Trim(); }
            catch (Exception ex) when (ex is TimeoutException or IOException or InvalidOperationException) { }
            int code;
            try { code = process.ExitCode; } catch (InvalidOperationException) { code = -1; }
            if (held.Released) return;
            lost($"{file} ended with code {code}" + (said.Length > 0 ? $": {said.Split('\n')[0].Trim()}" : string.Empty));
        }

        /// <param name="endsWithItsInput"><c>systemd-inhibit … cat</c> ends when its input closes; <c>caffeinate -w</c>
        /// does not — it waits for a process — and is stopped at once rather than waited for in vain.</param>
        private sealed class Held(Process process, bool endsWithItsInput) : IDisposable
        {
            private int _released;

            internal bool Released => Volatile.Read(ref _released) != 0;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _released, 1) != 0) return;
                try
                {
                    if (endsWithItsInput)
                    {
                        try { process.StandardInput.Close(); } catch { /* already closed */ }
                        if (!process.WaitForExit(2000)) process.Kill(entireProcessTree: true);
                    }
                    else if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { /* already gone */ }
                finally { process.Dispose(); }
            }
        }
    }
}
