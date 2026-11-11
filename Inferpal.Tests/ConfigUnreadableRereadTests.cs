using System.IO;
using Inferpal.Config;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The settings file read while it cannot be (held during its replacement, cut off): <c>InferpalConfig.Load</c> does not
/// cache the factory values under the file's date — it keeps what it read before, and reads again after a delay, never
/// at every call. The in-proc reader holds to the same rule (<c>InProcConfigUnreadableTests</c>, Windows only).
/// </summary>
[Collection(GlobalConfigPathCollection.Name)]
public sealed class ConfigUnreadableRereadTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("inferpal-config-reread").FullName;
    private string Path_ => Path.Combine(_dir, "config.json");
    private readonly string? _previousConfig = InferpalConfig.OverridePathForTests;
    private readonly TimeSpan _loadRetry = InferpalConfig.RetryUnreadable;

    internal const string CutOff = """{"inlineCompletionEnabled":fals }""";   // same length as Off: see the in-proc stamp
    internal const string Off    = """{"inlineCompletionEnabled":false}""";
    internal const string On     = """{"inlineCompletionEnabled":true }""";
    internal static readonly DateTime At = new(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc);

    public ConfigUnreadableRereadTests() => InferpalConfig.OverridePathForTests = Path_;

    public void Dispose()
    {
        InferpalConfig.OverridePathForTests = _previousConfig;
        InferpalConfig.RetryUnreadable = _loadRetry;
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private void Write(string text, DateTime at)
    {
        File.WriteAllText(Path_, text);
        File.SetLastWriteTimeUtc(Path_, at);
    }

    [Fact]
    public void Load_AFileThatCouldNotBeRead_IsReadAgain()
    {
        InferpalConfig.RetryUnreadable = TimeSpan.Zero;
        Write(CutOff, At);
        Assert.True(InferpalConfig.Load().InlineCompletionEnabled);    // unreadable: the factory values, to start
        Write(Off, At);                                                // repaired, its date where it was
        Assert.False(InferpalConfig.Load().InlineCompletionEnabled);
    }

    /// <summary>Unreadable after a read: the values read before, never the factory ones — and the read says it could
    /// not read the file now (what decides on its content, the MCP sign-ins, waits).</summary>
    [Fact]
    public void Load_AFileThatBecomesUnreadable_KeepsTheValuesReadBefore()
    {
        InferpalConfig.RetryUnreadable = TimeSpan.Zero;
        Write(Off, At);
        Assert.False(InferpalConfig.Load().InlineCompletionEnabled);
        Assert.False(InferpalConfig.LastLoadUnreadable);

        Write(CutOff, At.AddSeconds(5));
        Assert.False(InferpalConfig.Load().InlineCompletionEnabled);   // not the factory "on"
        Assert.True(InferpalConfig.LastLoadUnreadable);
    }

    [Fact]
    public void Load_AFileThatCouldNotBeRead_IsNotReadAgainAtEveryCall()
    {
        InferpalConfig.RetryUnreadable = TimeSpan.FromHours(1);
        Write(CutOff, At);
        InferpalConfig.Load();
        Write(Off, At);
        Assert.True(InferpalConfig.Load().InlineCompletionEnabled);    // within the delay: the cached answer
    }
}
