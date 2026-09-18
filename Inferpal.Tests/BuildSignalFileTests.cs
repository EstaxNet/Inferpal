using System.IO;
using Inferpal.Services;
using Inferpal.Services.Signals;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The "build failed" channel between the in-proc package (which sees the COM build events and VS's
/// error list) and the out-of-process monitor (which raises the banner).
/// </summary>
/// <remarks>
/// <para>
/// This channel has a particularity the others do not, and it is what decides the shape of its
/// write: its reader — <c>VsBuildMonitor.OnSignalFileEvent</c> — calls <c>Clear()</c>
/// <b>unconditionally</b> right after <c>TryRead()</c>. A read landing on a truncated file therefore
/// does not return "no signal, I will read again": it returns <c>default</c> and then <b>deletes</b>
/// the real payload. The build failed, and the product says nothing — no banner, no "Fix with AI"
/// entry towards <c>/fix-build</c>.
/// </para>
/// <para>
/// Hence <see cref="SignalFile"/>'s staging + rename: the target only appears complete.
/// </para>
/// </remarks>
[Collection(SignalCollection.Name)]
public class BuildSignalFileTests : IDisposable
{
    private readonly SignalScratchDir _scratch = new();

    public void Dispose() => _scratch.Dispose();

    // ── Writing through staging + rename ──────────────────────────────────────

    [Fact]
    public void Write_LandsACompletePayload_AndLeavesNoStagingBehind()
    {
        BuildSignalFile.Write(@"C:\work\Sln.sln", ["A.cs(1,1): error CS0103: nope", "B.cs(2,2): error CS1002: ;"]);

        Assert.True(File.Exists(BuildSignalFile.FilePath));
        Assert.Empty(Directory.EnumerateFiles(SignalFile.Dir, "*.staging"));

        var payload = BuildSignalFile.TryRead();
        Assert.Equal(@"C:\work\Sln.sln", payload.SolutionPath);
        Assert.Equal(2, payload.ErrorLines.Length);
        Assert.Equal("A.cs(1,1): error CS0103: nope", payload.ErrorLines[0]);
    }

    /// <summary>
    /// A signal that cannot be written <b>is traced</b> instead of being swallowed in silence.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the measurable half of going through the funnel, and the only channel through which
    /// one will ever learn that the banner is missing because the write failed. The old code had a
    /// mute <c>catch { /* non-critical */ }</c>: a lost signal looked exactly like a successful
    /// build.
    /// </para>
    /// <para>
    /// ⚠ The failure is forced where it discriminates: the target is occupied by a
    /// <b>directory</b> — staging succeeds, the final rename cannot (the same setup as
    /// <c>DebugCommandSignalTests.WriteRequest_ReturnsNull_WhenTheWriteCannotLand</c>). And the
    /// assertion is POSITIVE: we require the entry, not the absence of a symptom.
    /// </para>
    /// <para>
    /// ⚠ What this test does NOT prove: that the tearing window is gone. A truncated-then-filled
    /// write is not deterministically observable from the same process (a first version of this test
    /// read in a loop through sixty writes and stayed green with the defect installed — a
    /// decoration). What holds that half is rule 31 of <c>ConventionCoverageTests</c>, verified red,
    /// plus the rename's own tests in <c>DebugCommandSignalTests</c>.
    /// </para>
    /// </remarks>
    [Fact]
    public void AWriteThatCannotLand_IsTracedInsteadOfSwallowed()
    {
        Directory.CreateDirectory(SignalFile.Dir);
        Directory.CreateDirectory(BuildSignalFile.FilePath);   // the target is not a file
        Diagnostics.Clear();

        try
        {
            BuildSignalFile.Write(@"C:\work\Sln.sln", ["A.cs(1,1): error CS0103: nope"]);

            Assert.Contains(Diagnostics.Snapshot(), e => e.Context == "BuildSignal.Write");
            Assert.Empty(Directory.EnumerateFiles(SignalFile.Dir, "*.staging"));
        }
        finally
        {
            Directory.Delete(BuildSignalFile.FilePath, recursive: true);
            Diagnostics.Clear();
        }
    }

    // ── The thirty-second rule, on the shared clock ───────────────────────────

    /// <summary>
    /// A signal from a previous Visual Studio session must not raise a banner today.
    /// </summary>
    /// <remarks>
    /// This rule existed without a test because the channel read <c>DateTimeOffset.UtcNow</c>
    /// directly on both sides — the only pair on the bus to ignore the shared clock, hence the only
    /// one whose expiry could not be driven.
    /// </remarks>
    [Fact]
    public void ASignalOlderThanThirtySeconds_IsIgnored()
    {
        var t0 = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);
        SignalFile._nowOverride = () => t0;

        BuildSignalFile.Write(@"C:\work\Sln.sln", ["A.cs(1,1): error CS0103: nope"]);
        Assert.Equal(@"C:\work\Sln.sln", BuildSignalFile.TryRead().SolutionPath);

        SignalFile._nowOverride = () => t0.AddSeconds(29);
        Assert.Equal(@"C:\work\Sln.sln", BuildSignalFile.TryRead().SolutionPath);

        SignalFile._nowOverride = () => t0.AddSeconds(31);
        Assert.Null(BuildSignalFile.TryRead().SolutionPath);
    }

    // ── What the monitor does with a payload carrying no errors ───────────────

    /// <summary>
    /// Writing with no error list is still a signal: the banner must appear, and it is
    /// <c>VsBuildMonitor.FireOrFallback</c> that then triggers a fresh build.
    /// </summary>
    [Fact]
    public void Write_WithoutErrorLines_StillCarriesTheSolutionPath()
    {
        BuildSignalFile.Write(@"C:\work\Sln.sln");

        var payload = BuildSignalFile.TryRead();
        Assert.Equal(@"C:\work\Sln.sln", payload.SolutionPath);
        Assert.Empty(payload.ErrorLines);
    }
}
