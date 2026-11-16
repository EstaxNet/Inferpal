using System.Diagnostics;

namespace Inferpal.Services.Hardware;

/// <summary>
/// Best-effort detection of the total GPU VRAM of the <em>local</em> machine.
/// </summary>
/// <remarks>
/// Ollama does not expose total VRAM through its API, and the server is frequently remote.
/// So VRAM is normally set manually (<c>/hardware &lt;gb&gt;</c>). This probe only fires when
/// Ollama runs on loopback, on every system: NVIDIA cards through <c>nvidia-smi</c>; an Apple
/// Silicon Mac's unified memory (the share macOS lets the GPU wire); an AMD card on Linux through
/// the driver's own figure in <c>/sys/class/drm</c>. Other cards and all remote setups fall back to
/// the manual value. It must never throw.
/// </remarks>
internal static class HardwareProbe
{
    /// <summary>True when the Ollama base URL points at this machine (localhost / 127.0.0.0/8 / ::1).</summary>
    public static bool IsLoopback(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return false;
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)) return false;

        var host = uri.Host;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (System.Net.IPAddress.TryParse(host, out var ip))
            return System.Net.IPAddress.IsLoopback(ip);
        return false;
    }

    /// <summary>
    /// Parses the total memory (MiB) from <c>nvidia-smi --query-gpu=memory.total
    /// --format=csv,noheader,nounits</c> output (one integer per GPU line) and returns the
    /// largest GPU's capacity in bytes. Returns <c>null</c> on empty/garbage input.
    /// </summary>
    public static long? ParseNvidiaSmiMemory(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;

        long maxMib = 0;
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (long.TryParse(line, out var mib) && mib > maxMib) maxMib = mib;
        }
        return maxMib > 0 ? maxMib * 1024L * 1024L : null;
    }

    /// <summary>
    /// Detects local GPU VRAM (bytes) when Ollama is on loopback, else <c>null</c>.
    /// Swallows every failure (no nvidia-smi, AMD card, remote host, timeout).
    /// </summary>
    public static async Task<long?> TryDetectLocalVramBytesAsync(string? baseUrl, CancellationToken ct)
    {
        if (!IsLoopback(baseUrl)) return null;
        if (await NvidiaAsync(ct).ConfigureAwait(false) is { } nvidia) return nvidia;
        try
        {
            if (OperatingSystem.IsMacOS() && System.Runtime.InteropServices.RuntimeInformation.OSArchitecture
                    == System.Runtime.InteropServices.Architecture.Arm64)
            {
                // The limit the person set (iogpu.wired_limit_mb) first, else the share macOS wires by default.
                var limit = await ChildProcess.RunAsync(new ProcessStartInfo("/usr/sbin/sysctl", ["-n", "iogpu.wired_limit_mb"]),
                                                        TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                if (!limit.TimedOut && long.TryParse(limit.Stdout.Trim(), out var mb) && mb > 0) return mb * 1024 * 1024;
                var run = await ChildProcess.RunAsync(new ProcessStartInfo("/usr/sbin/sysctl", ["-n", "hw.memsize"]),
                                                      TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                return !run.TimedOut && long.TryParse(run.Stdout.Trim(), out var memory) ? AppleGpuShare(memory) : null;
            }
            if (OperatingSystem.IsLinux()) return AmdSysfsVram("/sys/class/drm");
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Diagnostics.Swallow("HardwareProbe", ex); }
        return null;
    }

    /// <summary>
    /// What an Apple Silicon Mac lets its GPU use of the unified memory: macOS wires about two thirds of it up to
    /// 36 GB, three quarters above — the working set Ollama's Metal back end is held to.
    /// </summary>
    internal static long? AppleGpuShare(long memoryBytes) =>
        memoryBytes <= 0 ? null : memoryBytes >= 36L * 1024 * 1024 * 1024 ? memoryBytes / 4 * 3 : memoryBytes / 3 * 2;

    /// <summary>Under this, an AMD figure is an integrated GPU's carve-out, not a card's memory.</summary>
    internal const long MinDedicatedVram = 4L * 1024 * 1024 * 1024;

    /// <summary>
    /// The largest AMD card's VRAM on Linux, as its driver (amdgpu) publishes it, in bytes: <c>mem_info_vram_total</c>
    /// under each <c>card*/device</c> of <paramref name="drm"/>; <c>null</c> when no card publishes one, or only an
    /// integrated GPU's carve-out (under <see cref="MinDedicatedVram"/>).
    /// </summary>
    internal static long? AmdSysfsVram(string drm)
    {
        if (!Directory.Exists(drm)) return null;
        long best = 0;
        foreach (var card in Directory.EnumerateDirectories(drm, "card*"))
        {
            var file = Path.Combine(card, "device", "mem_info_vram_total");
            if (File.Exists(file) && long.TryParse(File.ReadAllText(file).Trim(), out var bytes) && bytes > best) best = bytes;
        }
        // An integrated GPU (an APU) publishes only the memory the firmware set aside for it, a few hundred MB to 2 GB:
        // saved as the budget, it would size every advice for a card that does not exist. Unknown instead.
        return best >= MinDedicatedVram ? best : null;
    }

    private static async Task<long?> NvidiaAsync(CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName  = "nvidia-smi",
                Arguments = "--query-gpu=memory.total --format=csv,noheader,nounits",
            };

            // The shared runner drains both pipes: stderr redirected and never read wedges this on
            // a full buffer when the driver is chatty.
            var run = await ChildProcess.RunAsync(psi, TimeSpan.FromSeconds(5), ct)
                                        .ConfigureAwait(false);

            return run.TimedOut ? null : ParseNvidiaSmiMemory(run.Stdout);
        }
        catch { return null; }
    }
}
