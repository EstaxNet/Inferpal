using System.IO;
using Xunit;

namespace Inferpal.Tests;

// Serialized: the cache file path is a process-wide static.
[CollectionDefinition("LastKnownSolutionFile", DisableParallelization = true)]
public class LastKnownSolutionFileCollection { }

[Collection("LastKnownSolutionFile")]
public class LastKnownSolutionFileTests : IDisposable
{
    private readonly string _slnPath;

    public LastKnownSolutionFileTests()
    {
        DeleteCacheFile();
        // A real .sln on disk — Record and TryReadSolutionPath both validate File.Exists.
        _slnPath = Path.Combine(Path.GetTempPath(), $"Inferpal_test_{Guid.NewGuid():N}.sln");
        File.WriteAllText(_slnPath, "Microsoft Visual Studio Solution File");
    }

    public void Dispose()
    {
        DeleteCacheFile();
        try { File.Delete(_slnPath); } catch { }
    }

    private static void DeleteCacheFile()
    {
        try { File.Delete(LastKnownSolutionFile.FilePath); } catch { }
    }

    [Fact]
    public void TryRead_NoFile_ReturnsNull()
    {
        Assert.Null(LastKnownSolutionFile.TryReadSolutionPath());
    }

    [Fact]
    public void RecordThenRead_RoundTrips()
    {
        LastKnownSolutionFile.Record(_slnPath);

        Assert.Equal(_slnPath, LastKnownSolutionFile.TryReadSolutionPath());
    }

    [Fact]
    public void Record_NonExistentPath_IsIgnored()
    {
        LastKnownSolutionFile.Record(@"C:\does\not\exist\Nope.sln");

        Assert.Null(LastKnownSolutionFile.TryReadSolutionPath());
    }

    [Fact]
    public void Record_NullOrEmpty_IsIgnored()
    {
        LastKnownSolutionFile.Record(null);
        LastKnownSolutionFile.Record("");

        Assert.Null(LastKnownSolutionFile.TryReadSolutionPath());
    }

    [Fact]
    public void TryRead_RecordedFileDeleted_ReturnsNull()
    {
        LastKnownSolutionFile.Record(_slnPath);
        File.Delete(_slnPath);   // moved/deleted since it was cached

        Assert.Null(LastKnownSolutionFile.TryReadSolutionPath());
    }

    [Fact]
    public void TryRead_CorruptedFile_ReturnsNull()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LastKnownSolutionFile.FilePath)!);
        File.WriteAllText(LastKnownSolutionFile.FilePath, "{ not json");

        Assert.Null(LastKnownSolutionFile.TryReadSolutionPath());
    }
}
