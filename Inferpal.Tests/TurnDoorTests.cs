using Inferpal.Services.Hardware;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The turn door is held by a turn from start to end, model lease or not: a long command between two model calls is still
/// a turn, and what rewrites what it reads waits for it.
/// </summary>
[Collection(SignalCollection.Name)]
public sealed class TurnDoorTests
{
    /// <summary>Held, the door is busy and acts on nothing; what waited for it runs once it is given back.</summary>
    /// <remarks>⚠ Nothing here asserts that the door is FREE: other tests of the suite take model leases in parallel, and a
    /// lease closes the door too. Only what holding it does is asserted, and that what waited runs once it is given back.</remarks>
    [Fact]
    public async Task AHeldDoor_IsBusy_AndWhatWaitsForItRunsOnceItIsGivenBack()
    {
        var ran = 0;

        var hold = TurnDoor.Hold();
        try
        {
            Assert.True(TurnDoor.Busy);
            Assert.False(TurnDoor.TryNow(() => ran++));
            var waiting = TurnDoor.WhenIdleAsync(() => ran++);
            await Task.Delay(200);
            Assert.False(waiting.IsCompleted);
            Assert.Equal(0, ran);

            hold.Dispose();
            await waiting.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(1, ran);
        }
        finally
        {
            hold.Dispose();   // given back once, whatever happened
        }
    }
}
