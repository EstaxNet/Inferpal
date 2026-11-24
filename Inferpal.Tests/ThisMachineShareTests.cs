using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// "Is this host this machine?" has one answer: every loopback spelling of an administrative share names the drive it
/// shares, for the path judge as for the network-path test — never a device namespace (<c>\\.\</c>).
/// </summary>
public class ThisMachineShareTests
{
    [Theory]
    [InlineData(@"\\localhost\c$\inferpal-none\a.txt")]
    [InlineData(@"\\127.0.0.1\c$\inferpal-none\a.txt")]
    [InlineData(@"\\127.0.0.2\c$\inferpal-none\a.txt")]
    [InlineData(@"\\0--1.ipv6-literal.net\c$\inferpal-none\a.txt")]
    public void ALoopbackSpellingOfAnAdminShare_IsTheDrivePath(string share)
    {
        if (!OperatingSystem.IsWindows()) return;   // a share is a Windows path
        Assert.Equal(PathSanitizer.Canonical(@"C:\inferpal-none\a.txt"), PathSanitizer.Canonical(share), ignoreCase: true);
    }

    [Theory]
    [InlineData("127.evil.example")]
    [InlineData("127.0.0.1.nip.io")]
    [InlineData("127")]
    public void ANameThatOnlyBeginsLikeALoopbackAddress_IsAnotherMachine(string host)
    {
        // A DNS name is not an address: 127.evil.example resolves wherever its owner says.
        Assert.False(Inferpal.Services.Execution.NetworkPath.IsThisMachine(host));
        if (OperatingSystem.IsWindows())   // elsewhere \\host\… is no network path: NetworkPath.Is reads Windows spellings only
            Assert.True(Inferpal.Services.Execution.NetworkPath.Is($@"\\{host}\c$\work\a.txt"));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.1.2.3")]
    [InlineData("[::1]")]
    [InlineData("::1")]
    public void ALoopbackAddress_IsThisMachine(string host)
    {
        Assert.True(Inferpal.Services.Execution.NetworkPath.IsThisMachine(host));
    }
}
