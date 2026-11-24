using Inferpal.Services.Mcp.OAuth;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The keychain's tools read what Inferpal writes them on their standard input (macOS <c>security -i</c>, Linux
/// <c>secret-tool store</c>): that input stays open until it is written — a child started with its input closed could not
/// be given the key, and the sign-in to MCP servers was never stored.
/// </summary>
public class SystemKeychainInputTests
{
    [Fact]
    public void WhatIsWrittenToTheTool_ReachesIt()
    {
        var (file, args) = OperatingSystem.IsWindows() ? ("findstr", new[] { "." }) : ("cat", Array.Empty<string>());
        var ran = SystemKeychain.Run(file, args, "the-key\n");
        Assert.Equal(0, ran.Exit);
        Assert.Contains("the-key", ran.Stdout);
    }
}
