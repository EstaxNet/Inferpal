using System.IO;
using Inferpal.Services.Persistence;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// An unreadable file set aside before it is overwritten is kept by its BYTES, not by its time: two different contents set
/// aside within one second (or an hour the clock went back over) are both kept, the same content once.
/// </summary>
public sealed class PreserveAsideTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("inferpal-aside-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* teardown never throws (TeardownDoesNotThrowTests) */ }
    }

    [Fact]
    public void TwoContentsSetAsideInOneSecond_AreBothKept_TheSameOneOnce()
    {
        var path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, "{ broken one");
        var first = AtomicFile.PreserveAside(path);
        File.WriteAllText(path, "{ broken two");
        var second = AtomicFile.PreserveAside(path);
        var again = AtomicFile.PreserveAside(path);

        var kept = Directory.GetFiles(_dir, "config.json.unreadable-*").Select(File.ReadAllText).Order().ToList();
        Assert.Equal(["{ broken one", "{ broken two"], kept);
        Assert.NotEqual(first, second);
        Assert.Equal(second, again);
    }
}
