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
}
