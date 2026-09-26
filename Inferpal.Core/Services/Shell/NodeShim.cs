using System.IO;

namespace Inferpal.Services.Shell;

/// <summary>
/// How npm and npx are launched without a shell on Windows: the node.exe beside them, running their own CLI script.
/// </summary>
/// <remarks>
/// ⚠ On Windows both are batch scripts (npm.cmd, npx.cmd) and CreateProcess only resolves an executable: launched by
/// name, they never started — the test runner of every Node project, and the <c>"command": "npx"</c> of most MCP
/// server READMEs. Launched as .cmd they would go through cmd.exe, which interprets "&amp;", "|" or "%" in their
/// arguments. Every Node install keeps node.exe and <c>node_modules\npm\bin\npm-cli.js</c> / <c>npx-cli.js</c> beside
/// the .cmd files; a script run by node takes its arguments as a list.
/// </remarks>
internal static class NodeShim
{
    /// <summary>
    /// The program and leading arguments that run <paramref name="command"/> without a shell, or <c>null</c> when it
    /// is not npm or npx on Windows, or their CLI cannot be found — then the command is launched as given.
    /// </summary>
    internal static (string FileName, string[] Prefix)? Resolve(
        string command, bool isWindows, Func<string, string?> onPath, Func<string, bool> exists)
    {
        if (!isWindows || string.IsNullOrWhiteSpace(command)) return null;
        var name = Path.GetFileNameWithoutExtension(command).ToLowerInvariant();
        if (name is not ("npm" or "npx")) return null;
        if (Path.HasExtension(command) && !command.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)) return null;

        var cmd = Path.IsPathRooted(command) ? command : onPath(name + ".cmd");
        if (cmd is null || Path.GetDirectoryName(cmd) is not { Length: > 0 } dir) return null;
        var node = Path.Combine(dir, "node.exe");
        var cli  = Path.Combine(dir, "node_modules", "npm", "bin", name + "-cli.js");
        return exists(node) && exists(cli) ? (node, [cli]) : null;
    }
}
