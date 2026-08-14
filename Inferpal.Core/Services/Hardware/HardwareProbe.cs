using System.Diagnostics;

namespace Inferpal.Services.Hardware;

/// <summary>
/// Best-effort detection of the total GPU VRAM of the <em>local</em> machine.
/// </summary>
/// <remarks>
/// Ollama does not expose total VRAM through its API, and the server is frequently remote.
/// So VRAM is normally set manually (<c>/hardware &lt;gb&gt;</c>). This probe only fires when
/// Ollama runs on loopback, and currently covers NVIDIA cards via <c>nvidia-smi</c> only —
/// AMD/Intel and all remote setups fall back to the manual value. It must never throw.
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

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName  = "nvidia-smi",
                Arguments = "--query-gpu=memory.total --format=csv,noheader,nounits",
            };

            // stderr was redirected and never read here, so a chatty driver could have wedged the
            // probe on a full buffer; the shared runner drains both.
            var run = await ChildProcess.RunAsync(psi, TimeSpan.FromSeconds(5), ct)
                                        .ConfigureAwait(false);

            return run.TimedOut ? null : ParseNvidiaSmiMemory(run.Stdout);
        }
        catch { return null; }
    }
}
