namespace Inferpal.Services.Tools;

/// <summary>
/// What a tool call does to the world, declared by the tool for the arguments it is given — a property of the call, not a
/// list of tool names. A client draws a call by it (an ACP client's kind), and the declaration is held true to the tool's
/// code: what asks, starts a process or reaches the network declares it (<c>ToolEffectTests</c>). It does not decide a
/// card: each tool still asks for what it does.
/// </summary>
/// <remarks>⚠ A tool that does not say has every effect (<see cref="Unknown"/>): an MCP server's tool, whose own
/// annotations are not to be trusted, goes to the card like a command.</remarks>
[Flags]
internal enum ToolEffect
{
    /// <summary>Nothing outside the conversation.</summary>
    None = 0,

    /// <summary>Reads the workspace, the editor or the debugger's state.</summary>
    Reads = 1,

    /// <summary>Changes a file, the editor's buffer, the agent's memory or the debugger's breakpoints.</summary>
    Writes = 2,

    /// <summary>Runs a program — a command, a build, a test run, the code a debugger evaluates.</summary>
    Runs = 4,

    /// <summary>Reaches the network.</summary>
    Network = 8,

    /// <summary>Not said: every effect.</summary>
    Unknown = Reads | Writes | Runs | Network,
}

/// <summary>How an effect is named outside the Core (the host's notices, an ACP client's kind).</summary>
internal static class ToolEffects
{
    /// <summary>The weightiest effect's name — <c>network</c>, <c>runs</c>, <c>writes</c>, <c>reads</c> — or <c>unknown</c>.</summary>
    public static string Name(ToolEffect effect) =>
        effect == ToolEffect.Unknown ? "unknown"
        : effect.HasFlag(ToolEffect.Network) ? "network"
        : effect.HasFlag(ToolEffect.Runs) ? "runs"
        : effect.HasFlag(ToolEffect.Writes) ? "writes"
        : effect.HasFlag(ToolEffect.Reads) ? "reads"
        : "none";
}
