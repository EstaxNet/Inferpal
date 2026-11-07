using System.IO;
using Inferpal.Services;
using Inferpal.Services.Signals;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The signal folder belongs to the account, and no other account can write to it (<c>SignalFile.DefaultDirFor</c>). On
/// Linux the temp folder is the machine's <c>/tmp</c>: a shared <c>Inferpal</c> folder is owned by the first account that
/// creates it, every other account's markers fail to write in silence, and that account can plant links in it.
/// </summary>
public sealed class SignalFolderPerUserTests
{
    private static readonly string Temp = Path.GetTempPath();

    [Fact]
    public void OutsideWindows_TwoAccounts_GetTwoFolders_EachInItsOwnHome()
    {
        var aliceData = Path.Combine(Temp, "home-alice", ".local", "share");
        var bobData   = Path.Combine(Temp, "home-bob", ".local", "share");

        Assert.Equal(Path.Combine(aliceData, "Inferpal", "signals"), SignalFile.DefaultDirFor(windows: false, Temp, aliceData));
        Assert.Equal(Path.Combine(bobData, "Inferpal", "signals"), SignalFile.DefaultDirFor(windows: false, Temp, bobData));
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative/data")]
    public void AHomeTheSystemCannotName_GetsAFolderNobodyCanGuess_NeverOneInTheCurrentDirectory(string localData)
    {
        var first  = SignalFile.DefaultDirFor(windows: false, Temp, localData);
        var second = SignalFile.DefaultDirFor(windows: false, Temp, localData);

        Assert.True(Path.IsPathRooted(first), first);
        Assert.Equal(Path.GetFullPath(Temp), Path.GetFullPath(Path.GetDirectoryName(first)! + Path.DirectorySeparatorChar));
        Assert.StartsWith("Inferpal-", Path.GetFileName(first), StringComparison.Ordinal);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void OnWindows_TheFolderStaysTheTempFolders()
    {
        var data = Path.Combine(Temp, "home-alice", "AppData", "Local");
        Assert.Equal(Path.Combine(Temp, "Inferpal"), SignalFile.DefaultDirFor(windows: true, Temp, data));
    }

    [Fact]
    public void ThisProcesssFolder_IsTheOneTheRuleGives()
    {
        var expected = Environment.OSVersion.Platform == PlatformID.Win32NT
            ? Path.Combine(Temp, "Inferpal")
            : Path.Combine(UserFolders.Of(Environment.SpecialFolder.UserProfile), ".local", "share", "Inferpal", "signals");
        Assert.Equal(expected, SignalFile.DefaultDir);
    }
}
