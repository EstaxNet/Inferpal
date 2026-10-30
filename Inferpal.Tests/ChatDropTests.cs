using System.IO;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A file or folder dropped on Visual Studio's chat window reaches the out-of-process window through
/// <see cref="ChatDropSignal"/>: once, in the order dropped, and never later than the drop itself. The in-process half
/// that takes the drop is tested in <c>ChatDropTargetTests</c> (Windows).
/// </summary>
[Collection(SignalCollection.Name)]
public sealed class ChatDropTests : IDisposable
{
    private readonly SignalScratchDir _signals = new();

    public void Dispose() => _signals.Dispose();

    [Fact]
    public void ADrop_ReachesTheWindow_Once_InTheOrderDropped()
    {
        ChatDropSignal.Write([@"C:\src\Shop\Billing\InvoiceService.cs", @"C:\src\Shop\Billing\Rounding"], ["Shop.Billing"]);

        var drop = ChatDropSignal.Take();

        Assert.NotNull(drop);
        Assert.Equal([@"C:\src\Shop\Billing\InvoiceService.cs", @"C:\src\Shop\Billing\Rounding"], drop!.Paths);
        Assert.Equal(["Shop.Billing"], drop.Projects);
        Assert.Null(ChatDropSignal.Take());   // taken once: a second event of the same write attaches nothing more
    }

    [Fact]
    public void ADropNobodyTookInTime_IsNeverAttachedLater()
    {
        var now = DateTimeOffset.UtcNow;
        SignalFile._nowOverride = () => now;
        ChatDropSignal.Write([@"C:\src\a.cs"], []);

        SignalFile._nowOverride = () => now + ChatDropSignal.MaxAge + TimeSpan.FromSeconds(1);

        Assert.Null(ChatDropSignal.Take());
        Assert.False(File.Exists(ChatDropSignal.FilePath));   // and it does not wait for a later window either
    }

    [Fact]
    public async Task TheWindowWatching_IsCalledWithTheDrop()
    {
        var seen = new TaskCompletionSource<ChatDrop>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watch = ChatDropSignal.Watch(drop => seen.TrySetResult(drop));
        Assert.NotNull(watch);

        ChatDropSignal.Write([@"C:\src\a.cs"], []);

        var drop = await seen.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal([@"C:\src\a.cs"], drop.Paths);
    }
}
