using System.IO;
using Inferpal.GhostText;
using Xunit;
using static Inferpal.Tests.ConfigUnreadableRereadTests;

namespace Inferpal.Tests;

/// <summary>
/// The in-proc completion's read of the settings file while it cannot be (held during its replacement, cut off): the
/// answer given then is not cached under the file's date. Read while the other editor replaced it, completion kept the
/// old values for good — the person had just turned it off. Tried again after a delay, never at every keystroke; off
/// until the person's choice could be read; and the values kept carry the stamp they were read at, so no sidecar is
/// recycled on a read that failed.
/// </summary>
[Collection(GlobalConfigPathCollection.Name)]
public sealed class InProcConfigUnreadableTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("inferpal-inproc-reread").FullName;
    private string Path_ => Path.Combine(_dir, "config.json");
    private readonly string? _previous = InProcConfig.PathOverride;
    private readonly TimeSpan _retry = InProcConfig.RetryUnread;

    public InProcConfigUnreadableTests()
    {
        InProcConfig.PathOverride = Path_;
        InProcConfig.ResetForTests();
    }

    public void Dispose()
    {
        InProcConfig.PathOverride = _previous;
        InProcConfig.RetryUnread = _retry;
        InProcConfig.ResetForTests();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private void Write(string text, DateTime at)
    {
        File.WriteAllText(Path_, text);
        File.SetLastWriteTimeUtc(Path_, at);
    }

    private long Stamp() { var i = new FileInfo(Path_); return i.LastWriteTimeUtc.Ticks ^ i.Length; }

    [Fact]
    public void AnAbsentFile_IsTheFactoryValues_OneThatCannotBeRead_LeavesCompletionOff()
    {
        Assert.True(InProcConfig.Current().Enabled);       // first install: no file
        InProcConfig.ResetForTests();
        Write(CutOff, At);
        Assert.False(InProcConfig.Current().Enabled);      // the person's choice unknown: off, not the factory "on"
    }

    [Fact]
    public void AFileReadWhileBeingReplaced_IsReadAgain_AndKeepsTheStampItWasReadAt()
    {
        InProcConfig.RetryUnread = TimeSpan.Zero;
        Write(On, At);
        var before = InProcConfig.Current();
        Assert.True(before.Enabled);

        Write(CutOff, At.AddSeconds(5));                   // the person turns completion off; read mid-replacement
        var stampOfTheNewFile = Stamp();
        var during = InProcConfig.Current();
        Assert.True(during.Enabled);                       // the last values read...
        Assert.Equal(before.Stamp, during.Stamp);          // ...at their own stamp: no sidecar recycled on a read that failed

        Write(Off, At.AddSeconds(5));                      // the replacement done, the date where it was
        Assert.Equal(stampOfTheNewFile, Stamp());          // WITNESS: only a read again can see the change
        var after = InProcConfig.Current();
        Assert.False(after.Enabled);
        Assert.Equal(stampOfTheNewFile, after.Stamp);
    }

    [Fact]
    public void AFailedRead_IsNotTriedAgainAtEveryKeystroke()
    {
        InProcConfig.RetryUnread = TimeSpan.FromHours(1);
        Write(On, At);
        InProcConfig.Current();
        Write(CutOff, At.AddSeconds(5));
        InProcConfig.Current();
        Write(Off, At.AddSeconds(5));
        Assert.True(InProcConfig.Current().Enabled);       // within the delay: not read again
    }
}
