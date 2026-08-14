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
    public void Acknowledge_SatisfiesPickup_AndConsumesTheRequest()
    {
        var id = InlineDiffPreviewSignal.WriteRequest(FilePath, "old", "new");

        Assert.False(InlineDiffPreviewSignal.IsPickedUp(id));
        InlineDiffPreviewSignal.Acknowledge(id);

        Assert.True(InlineDiffPreviewSignal.IsPickedUp(id));
        Assert.Null(InlineDiffPreviewSignal.TryReadRequestFor(FilePath));   // consumed
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
        var wait = InlineDiffPreviewSignal.WaitForPickupAsync(id, TimeSpan.FromSeconds(5), CancellationToken.None);

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
