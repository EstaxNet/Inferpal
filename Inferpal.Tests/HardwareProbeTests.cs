using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

// Covers the pure, testable parts of the local VRAM probe: loopback detection (decides whether
// auto-seed may fire) and nvidia-smi output parsing. The Process launch itself is not unit-tested.
public class HardwareProbeTests
{
    // ── IsLoopback ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("http://localhost:11434")]
    [InlineData("http://127.0.0.1:11434")]
    [InlineData("http://127.5.6.7:11434")]   // whole 127.0.0.0/8 block
    [InlineData("http://[::1]:11434")]
    public void IsLoopback_True_ForLocalHosts(string url) =>
        Assert.True(HardwareProbe.IsLoopback(url));

    [Theory]
    [InlineData("http://192.168.1.2:11434")] // the user's remote inference box
    [InlineData("http://10.0.0.5:11434")]
    [InlineData("http://ollama.lan:11434")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("not a url")]
    public void IsLoopback_False_ForRemoteOrInvalid(string? url) =>
        Assert.False(HardwareProbe.IsLoopback(url));

    // ── ParseNvidiaSmiMemory ───────────────────────────────────────────────────

    [Fact]
    public void ParseNvidiaSmiMemory_SingleGpu_ReturnsBytes() =>
        // 24576 MiB → 24 GiB in bytes.
        Assert.Equal(24576L * 1024 * 1024, HardwareProbe.ParseNvidiaSmiMemory("24576"));

    [Fact]
    public void ParseNvidiaSmiMemory_MultiGpu_TakesLargest() =>
        Assert.Equal(24576L * 1024 * 1024, HardwareProbe.ParseNvidiaSmiMemory("8192\n24576\n12288"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("N/A")]
    [InlineData("nvidia-smi: command not found")]
    public void ParseNvidiaSmiMemory_GarbageOrEmpty_ReturnsNull(string? output) =>
        Assert.Null(HardwareProbe.ParseNvidiaSmiMemory(output));

    private const long Gib = 1024L * 1024 * 1024;

    [Fact]
    public void AnAppleSiliconMac_GivesItsGpuTheShareMacOSWires()
    {
        Assert.Equal(16 * Gib / 3 * 2, HardwareProbe.AppleGpuShare(16 * Gib));   // two thirds up to 36 GB
        Assert.Equal(64 * Gib / 4 * 3, HardwareProbe.AppleGpuShare(64 * Gib));   // three quarters above
        Assert.Null(HardwareProbe.AppleGpuShare(0));
    }

    [Fact]
    public void AnAmdCardOnLinux_IsReadFromItsDriver_TheLargestCard()
    {
        var drm = System.IO.Directory.CreateTempSubdirectory("inferpal-drm-").FullName;
        try
        {
            void Card(string name, string? vram)
            {
                var device = System.IO.Path.Combine(drm, name, "device");
                System.IO.Directory.CreateDirectory(device);
                if (vram is not null) System.IO.File.WriteAllText(System.IO.Path.Combine(device, "mem_info_vram_total"), vram + "\n");
            }
            Assert.Null(HardwareProbe.AmdSysfsVram(drm));
            Card("card0", null);                          // an integrated Intel GPU: no such file
            Card("card3", (1 * Gib).ToString(System.Globalization.CultureInfo.InvariantCulture));
            Assert.Null(HardwareProbe.AmdSysfsVram(drm));  // an APU's carve-out alone: unknown, never a 1 GB card
            Card("card1", (8 * Gib).ToString(System.Globalization.CultureInfo.InvariantCulture));
            Card("card2", (16 * Gib).ToString(System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(16 * Gib, HardwareProbe.AmdSysfsVram(drm));
        }
        finally { try { System.IO.Directory.Delete(drm, recursive: true); } catch { /* best-effort cleanup */ } }
    }
}
