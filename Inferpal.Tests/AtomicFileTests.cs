using System.IO;
using Inferpal.Services.Persistence;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <see cref="AtomicFile"/> is the write path of every small JSON store Inferpal keeps under
/// <c>%AppData%</c> — and the configuration file is <b>shared between the two front-ends by
/// design</b> (<see cref="ConfigLoadCacheTests.AnExternalRewrite_IsPickedUp"/> pins that contract).
/// So two writers at once is a normal situation, not an exotic one, and it must not throw.
/// </summary>
public class AtomicFileTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "inferpal-tests", $"atomic-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>
    /// Staging under a name derived only from the target (<c>config.json.tmp</c>) makes every
    /// concurrent writer collide on that one file: one truncates what another is writing, or the
    /// rename finds nothing left to rename. Both surface as an <see cref="IOException"/> out of a
    /// <c>/model</c> or a settings save — which is exactly the failure this class exists to prevent,
    /// arriving through the door it left open.
    /// </summary>
    [Fact]
    public void ConcurrentWriters_OfTheSameFile_NeitherThrowNorTear()
    {
        var path   = Path.Combine(_dir, "config.json");
        var errors = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        Parallel.For(0, 8, worker =>
        {
            for (var i = 0; i < 40; i++)
            {
                try { AtomicFile.WriteAllText(path, Payload(worker)); }
                catch (Exception ex) { errors.Add(ex); }
            }
        });

        Assert.Empty(errors);

        // Last writer wins, and what it left behind is one complete payload — never a splice of two.
        var final = File.ReadAllText(path);
        Assert.Contains(final, Enumerable.Range(0, 8).Select(Payload));
    }

    /// <summary>The async overload shares the staging scheme, so it shares the hazard.</summary>
    [Fact]
    public async Task ConcurrentAsyncWriters_OfTheSameFile_NeitherThrowNorTear()
    {
        var path   = Path.Combine(_dir, "sessions.json");
        var errors = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(async () =>
        {
            for (var i = 0; i < 40; i++)
            {
                try { await AtomicFile.WriteAllTextAsync(path, Payload(worker)); }
                catch (Exception ex) { errors.Add(ex); }
            }
        })));

        Assert.Empty(errors);
        Assert.Contains(File.ReadAllText(path), Enumerable.Range(0, 8).Select(Payload));
    }

    /// <summary>
    /// A failed write must not leave its staging file behind: the name is unique per writer, so
    /// nothing would ever overwrite it, and %AppData% would slowly fill with debris.
    /// </summary>
    [Fact]
    public void AFailedWrite_LeavesNoStagingFileBehind()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "target.json");

        // A directory where the target belongs: the rename cannot succeed, the staging can.
        Directory.CreateDirectory(path);

        Assert.ThrowsAny<Exception>(() => AtomicFile.WriteAllText(path, "{}"));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    // Padded so a torn write shows up as a length mismatch rather than a lucky prefix.
    private static string Payload(int worker) => new string((char)('a' + worker), 20_000);
}
