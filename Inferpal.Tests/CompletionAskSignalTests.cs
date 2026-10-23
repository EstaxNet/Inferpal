using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The key of inline completion pressed in Visual Studio reaches the ghost text of that devenv once, fresh, and with
/// the sentences it may have to say.
/// </summary>
[Collection(SignalCollection.Name)]
public sealed class CompletionAskSignalTests : IDisposable
{
    private readonly SignalScratchDir _signals = new();

    public void Dispose() => _signals.Dispose();

    [Fact]
    public void AKeyPress_ArrivesWithItsSentences()
    {
        CompletionAskSignal.Write("refused", "nothing", "failed", "list", "no editor");

        var ask = CompletionAskSignal.TryTake(null);

        Assert.NotNull(ask);
        Assert.Equal(("refused", "nothing", "failed", "list", "no editor"),
                     (ask!.Refusal, ask.Nothing, ask.Failed, ask.ListHint, ask.NoEditor));
    }

    [Fact]
    public void AKeyPressServed_IsNotServedAgain_ButTheNextOneIs()
    {
        // The file system reports one write several times: the id is what tells a new press from the same one.
        CompletionAskSignal.Write(null, "nothing", "failed", "list", "no editor");
        var first = CompletionAskSignal.TryTake(null)!;

        Assert.Null(CompletionAskSignal.TryTake(first.Id));
        CompletionAskSignal.Write(null, "nothing", "failed", "list", "no editor");
        Assert.NotNull(CompletionAskSignal.TryTake(first.Id));
    }

    [Fact]
    public void AnOldKeyPress_IsNotServed()
    {
        var now = DateTimeOffset.UtcNow;
        SignalFile._nowOverride = () => now;
        CompletionAskSignal.Write(null, "nothing", "failed", "list", "no editor");

        SignalFile._nowOverride = () => now + CompletionAskSignal.MaxAge + TimeSpan.FromSeconds(1);

        Assert.Null(CompletionAskSignal.TryTake(null));
        SignalFile._nowOverride = () => now + TimeSpan.FromSeconds(1);
        Assert.NotNull(CompletionAskSignal.TryTake(null));   // witness: the same press, while it is fresh
    }

    [Fact]
    public void AKeyPressFromAnotherVisualStudio_IsNotServedHere()
    {
        SignalScope.DeclareVsInstance(4242);
        CompletionAskSignal.Write(null, "nothing", "failed", "list", "no editor");
        Assert.NotNull(CompletionAskSignal.TryTake(null));   // witness: read in the scope it was written in

        SignalScope.ResetForTests();
        SignalScope.DeclareVsInstance(4343);

        Assert.Null(CompletionAskSignal.TryTake(null));
    }
}
