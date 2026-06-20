using Inferpal.Localization;
using Inferpal.Services;
using Inferpal.Services.Commands;
using Xunit;

namespace Inferpal.Tests;

[Collection("Diagnostics")]
public class DiagnosticsCommandHandlerTests : IDisposable
{
    public DiagnosticsCommandHandlerTests() => Reset();
    public void Dispose() => Reset();

    private static void Reset()
    {
        Diagnostics.Clear();
        Diagnostics.FileLoggingEnabled = false;
        Diagnostics.LogPathOverride = null;
    }

    private static string[] Cmd(params string[] args) => ["/diagnostics", .. args];

    [Fact]
    public void List_Empty_ReturnsEmptyNotice()
    {
        Assert.Equal(Strings.DiagnosticsEmpty, DiagnosticsCommandHandler.Handle(Cmd()));
    }

    [Fact]
    public void List_WithEntries_ShowsHeaderContextAndDetail_MostRecentFirst()
    {
        Diagnostics.Swallow("CtxOld", new InvalidOperationException("first"));
        Diagnostics.Swallow("CtxNew", new InvalidOperationException("second"));

        var msg = DiagnosticsCommandHandler.Handle(Cmd());

        Assert.Contains(Strings.DiagnosticsHeader, msg);
        Assert.Contains("CtxOld", msg);
        Assert.Contains("CtxNew", msg);
        Assert.Contains("second", msg);
        // Most recent first: CtxNew appears before CtxOld.
        Assert.True(msg.IndexOf("CtxNew", StringComparison.Ordinal) < msg.IndexOf("CtxOld", StringComparison.Ordinal));
    }

    [Fact]
    public void Clear_EmptiesRingAndConfirms()
    {
        Diagnostics.Record("a", "1");

        var msg = DiagnosticsCommandHandler.Handle(Cmd("clear"));

        Assert.Equal(Strings.DiagnosticsCleared, msg);
        Assert.Empty(Diagnostics.Snapshot());
    }

    [Fact]
    public void On_EnablesFileLogging()
    {
        var msg = DiagnosticsCommandHandler.Handle(Cmd("on"));

        Assert.Equal(Strings.DiagnosticsFileOn, msg);
        Assert.True(Diagnostics.FileLoggingEnabled);
    }

    [Fact]
    public void Off_DisablesFileLogging()
    {
        Diagnostics.FileLoggingEnabled = true;

        var msg = DiagnosticsCommandHandler.Handle(Cmd("off"));

        Assert.Equal(Strings.DiagnosticsFileOff, msg);
        Assert.False(Diagnostics.FileLoggingEnabled);
    }
}
