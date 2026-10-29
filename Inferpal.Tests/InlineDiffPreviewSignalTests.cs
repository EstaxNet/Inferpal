using Inferpal.Services.Signals;
using Xunit;

namespace Inferpal.Tests;

// File-IPC protocol behind the inline diff preview (host writes request → devenv acks + consumes).
// Serialized on the signal files: xUnit runs same-class facts sequentially, and the collection
// keeps other signal-file tests apart.
[Collection(SignalCollection.Name)]
public class InlineDiffPreviewSignalTests : IDisposable
{
    private const string FilePath = @"C:\proj\a.cs";

    private readonly SignalScratchDir _scratch = new();

    public InlineDiffPreviewSignalTests() => Cleanup();
    public void Dispose()
    {
        SignalFile._isProcessAliveOverride = null;
        SignalFile._nowOverride = null;
        Cleanup();
        _scratch.Dispose();
    }

    private static void Cleanup()
    {
        try { System.IO.File.Delete(InlineDiffPreviewSignal.RequestPath); } catch { }
        try { System.IO.File.Delete(InlineDiffPreviewSignal.AckPath); } catch { }
    }

    [Fact]
    public void Request_RoundTrips_ForItsTargetFile()
    {
        var id = InlineDiffPreviewSignal.WriteRequest(FilePath, "old", "new");

        var read = InlineDiffPreviewSignal.TryReadRequestFor(FilePath);

        Assert.NotNull(read);
        Assert.Equal(id,    read.Id);
        Assert.Equal("old", read.OldText);
        Assert.Equal("new", read.NewText);
    }

    // The sentences the renderer cannot compose.
    //
    // Inferpal.InProc targets net472 and does not reference the Core: it has neither Strings nor
    // satellites. Without this transport its three exits that apply nothing stay mute - a tick
    // clicked with no effect, a keystroke that throws the rewrite away - and their only trace falls
    // into the in-proc Diagnostics ring, which /diagnostics (out-of-process host) does not read.

    [Fact]
    public void Request_CarriesTheNoticesTheRendererCannotCompose()
    {
        var notices = new InlineDiffNotices("abandoned", "drifted", "failed");

        InlineDiffPreviewSignal.WriteRequest(FilePath, "old", "new", notices);
        var read = InlineDiffPreviewSignal.TryReadRequestFor(FilePath);

        Assert.NotNull(read);
        Assert.Equal(notices, read.Notices);
    }

    /// <summary>
    /// A request with no sentences (a host older than the renderer, or a caller that does not pass
    /// them) must read back, not throw: the preview works, it just stays as mute as before. The
    /// failure to avoid is a renderer tripping over a missing field - it would cost the WHOLE
    /// preview for three sentences.
    /// </summary>
    [Fact]
    public void Request_WithoutNotices_StillReadsBack()
    {
        InlineDiffPreviewSignal.WriteRequest(FilePath, "old", "new");
        var read = InlineDiffPreviewSignal.TryReadRequestFor(FilePath);

        Assert.NotNull(read);
        Assert.Null(read.Notices);
        Assert.Equal("new", read.NewText);
    }

    [Fact]
    public void Request_ForAnotherFile_IsInvisible()
    {
        InlineDiffPreviewSignal.WriteRequest(FilePath, "old", "new");

        Assert.Null(InlineDiffPreviewSignal.TryReadRequestFor(@"C:\proj\other.cs"));
    }

    [Fact]
    public void Request_FromDeadProcess_IsIgnored()
    {
        InlineDiffPreviewSignal.WriteRequest(FilePath, "old", "new");
        SignalFile._isProcessAliveOverride = _ => false;

        Assert.Null(InlineDiffPreviewSignal.TryReadRequestFor(FilePath));
    }

    [Fact]
    public void Request_OlderThanMaxAge_IsIgnored()
    {
        InlineDiffPreviewSignal.WriteRequest(FilePath, "old", "new");
        SignalFile._nowOverride = () => DateTimeOffset.UtcNow + InlineDiffPreviewSignal.MaxAge + TimeSpan.FromSeconds(1);

        Assert.Null(InlineDiffPreviewSignal.TryReadRequestFor(FilePath));
    }

    [Fact]
    public void Acknowledge_SatisfiesPickup_AndTakeConsumesTheRequest()
    {
        var id = InlineDiffPreviewSignal.WriteRequest(FilePath, "old", "new");

        Assert.False(InlineDiffPreviewSignal.IsPickedUp(id));
        Assert.True(InlineDiffPreviewSignal.Take(id));
        InlineDiffPreviewSignal.Acknowledge(id);

        Assert.True(InlineDiffPreviewSignal.IsPickedUp(id));
        Assert.Null(InlineDiffPreviewSignal.TryReadRequestFor(FilePath));   // consumed
        Assert.False(InlineDiffPreviewSignal.Take(id));                     // a second view of the document gets nothing
    }

    /// <summary>⚠ A request the renderer took at the window's last instant is not withdrawn behind it: the host waits for
    /// its answer instead of applying directly under the preview it shows.</summary>
    [Fact]
    public async Task ARequestTakenAtTheLastInstant_IsNotWithdrawn_AndTheRenderersAnswerDecides()
    {
        var id = InlineDiffPreviewSignal.WriteRequest(FilePath, "old", "new");

        Assert.True(InlineDiffPreviewSignal.Take(id));
        Assert.False(InlineDiffPreviewSignal.Withdraw());
        InlineDiffPreviewSignal.Acknowledge(id);

        Assert.True(await InlineDiffPreviewSignal.WaitForPickupAsync(id, TimeSpan.FromSeconds(30), CancellationToken.None));
    }

    /// <summary>Reference arm: withdrawn while waiting, the request is never shown.</summary>
    [Fact]
    public void AWithdrawnRequest_CannotBeTaken()
    {
        var id = InlineDiffPreviewSignal.WriteRequest(FilePath, "old", "new");

        Assert.True(InlineDiffPreviewSignal.Withdraw());
        Assert.False(InlineDiffPreviewSignal.Take(id));
    }

    /// <summary>A request declined (the buffer drifted) ends the wait at once: the direct apply does not wait the window.</summary>
    [Fact]
    public async Task ADeclinedRequest_EndsTheWaitAtOnce()
    {
        var id = InlineDiffPreviewSignal.WriteRequest(FilePath, "old", "new");
        Assert.True(InlineDiffPreviewSignal.Take(id));
        InlineDiffPreviewSignal.Acknowledge(id, shown: false);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(await InlineDiffPreviewSignal.WaitForPickupAsync(id, TimeSpan.FromSeconds(30), CancellationToken.None));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"waited {watch.Elapsed}");
    }

    /// <summary>A renderer that read one request never takes a newer one written since: it is left for its own answer.</summary>
    [Fact]
    public void ARendererThatReadAnOlderRequest_LeavesTheNewerOne()
    {
        var older = InlineDiffPreviewSignal.WriteRequest(FilePath, "old", "new");
        Assert.True(InlineDiffPreviewSignal.Withdraw());
        var newer = InlineDiffPreviewSignal.WriteRequest(FilePath, "old2", "new2");

        Assert.False(InlineDiffPreviewSignal.Take(older));
        Assert.True(InlineDiffPreviewSignal.TryReadRequestFor(FilePath) is { } r && r.Id == newer);   // put back
    }

    /// <summary>The editor ends of the protocol, which no test runs: the renderer takes before it shows or declines, and
    /// the host withdraws before it applies directly.</summary>
    [Fact]
    public void BothEnds_TakeAndWithdraw_BeforeActing()
    {
        var root = ConventionCoverageTests.RepoRoot();
        var controller = ConventionCoverageTests.CodeOnly(System.IO.Path.Combine(root, "Inferpal.InProc", "GhostText", "InlineDiffController.cs"));
        var take = controller.IndexOf("if (!InlineDiffPreviewSignal.Take(request.Id)) return;", StringComparison.Ordinal);
        Assert.True(take > 0, "the renderer shows a request it did not take");
        Assert.True(controller.IndexOf("InlineDiffPreviewSignal.Acknowledge(request.Id", StringComparison.Ordinal) > take);
        Assert.DoesNotContain("InlineDiffPreviewSignal.DiscardRequest()", controller, StringComparison.Ordinal);

        var host = ConventionCoverageTests.CodeOnly(System.IO.Path.Combine(root, "Inferpal", "Commands", "InPlaceCodeEdit.cs"));
        Assert.Contains("return !Services.Signals.InlineDiffPreviewSignal.Withdraw()", host, StringComparison.Ordinal);
    }

    [Fact]
    public void NewRequest_InvalidatesThePreviousAck()
    {
        var first = InlineDiffPreviewSignal.WriteRequest(FilePath, "old", "new");
        InlineDiffPreviewSignal.Acknowledge(first);

        var second = InlineDiffPreviewSignal.WriteRequest(FilePath, "old2", "new2");

        Assert.False(InlineDiffPreviewSignal.IsPickedUp(second));   // stale ack must not satisfy it
        Assert.True(InlineDiffPreviewSignal.TryReadRequestFor(FilePath) is { } r && r.Id == second);
    }

    [Fact]
    public async Task WaitForPickup_TimesOut_WhenNothingAcks()
    {
        var id = InlineDiffPreviewSignal.WriteRequest(FilePath, "old", "new");

        var picked = await InlineDiffPreviewSignal.WaitForPickupAsync(
            id, TimeSpan.FromMilliseconds(250), CancellationToken.None);

        Assert.False(picked);
    }

    [Fact]
    public async Task WaitForPickup_ReturnsTrue_OnceAcked()
    {
        var id = InlineDiffPreviewSignal.WriteRequest(FilePath, "old", "new");
        // 30 s: waiting for a FILE acknowledgement, not measuring latency.
        var wait = InlineDiffPreviewSignal.WaitForPickupAsync(id, TimeSpan.FromSeconds(30), CancellationToken.None);

        InlineDiffPreviewSignal.Acknowledge(id);

        Assert.True(await wait);
    }

    [Fact]
    public void DiscardRequest_RemovesAnUnclaimedRequest()
    {
        InlineDiffPreviewSignal.WriteRequest(FilePath, "old", "new");

        InlineDiffPreviewSignal.DiscardRequest();

        Assert.Null(InlineDiffPreviewSignal.TryReadRequestFor(FilePath));
    }
}
