using System.IO;
using Inferpal.Services.Shell;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// npm and npx are batch scripts on Windows, which CreateProcess does not resolve: the MCP client (`"command": "npx"`,
/// the configuration of most server READMEs) and the npm test runner run them through the node.exe beside them.
/// </summary>
public class NodeShimTests
{
    // Paths built for the HOST: a literal "C:\…" has no directory part under Linux, where this test runs too.
    private static readonly string Dir = Path.Combine(Path.GetTempPath(), "nodejs");
    private static readonly string Node = Path.Combine(Dir, "node.exe");
    private static string Cli(string name) => Path.Combine(Dir, "node_modules", "npm", "bin", name + "-cli.js");

    private static (string FileName, string[] Prefix)? Resolve(string command, bool isWindows = true) =>
        NodeShim.Resolve(command, isWindows,
            onPath: name => name is "npm.cmd" or "npx.cmd" ? Path.Combine(Dir, name) : null,
            exists: path => path == Node || path == Cli("npm") || path == Cli("npx"));

    [Theory]
    [InlineData("npx")]
    [InlineData("npx.cmd")]
    [InlineData("NPX")]
    public void Npx_RunsItsOwnCliThroughNode(string command)
    {
        var shim = Resolve(command);

        Assert.NotNull(shim);
        Assert.Equal(Node, shim!.Value.FileName);
        Assert.Equal([Cli("npx")], shim.Value.Prefix);
    }

    [Fact]
    public void Npm_RunsItsOwnCli_NotNpxs()
    {
        Assert.Equal([Cli("npm")], Resolve("npm")!.Value.Prefix);
    }

    [Fact]
    public void AnAbsoluteNpxCmd_IsResolvedInItsOwnFolder()
    {
        Assert.Equal(Node, Resolve(Path.Combine(Dir, "npx.cmd"))!.Value.FileName);
    }

    [Theory]
    [InlineData("node")]      // an executable already: launched as given
    [InlineData("uvx")]
    [InlineData("npx.exe")]   // named explicitly as something else: not ours to reinterpret
    [InlineData("")]
    public void AnythingElse_IsLaunchedAsGiven(string command)
    {
        Assert.Null(Resolve(command));
    }

    [Fact]
    public void OutsideWindows_NpxIsLaunchedAsGiven()
    {
        // A script with a shebang: exec runs it, no shell in between.
        Assert.Null(Resolve("npx", isWindows: false));
    }

    [Fact]
    public void WithoutTheCliBesideIt_ThereIsNoShellFallback()
    {
        Assert.Null(NodeShim.Resolve("npx", isWindows: true,
            onPath: name => Path.Combine(Dir, name), exists: _ => false));
    }
}
