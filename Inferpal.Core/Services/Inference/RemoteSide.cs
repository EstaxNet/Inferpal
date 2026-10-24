using Inferpal.Localization;

namespace Inferpal.Services.Inference;

/// <summary>Where the host runs when the editor is connected to another machine: VS Code Remote runs the extension beside
/// the files — in WSL, on an SSH server, in a container — and its <c>localhost</c> is that machine.</summary>
internal enum RemoteKind { None, Wsl, Ssh, Machine, Container }

/// <summary>
/// What a server address means from the side the host runs on. ⚠ A <c>localhost</c> that does not answer there may be a
/// stopped server, or one running on the computer the person sits at: both are said, each with the gesture that works from
/// that side — "start it" alone sends them to start a second server next to the running one.
/// </summary>
internal static class RemoteSide
{
    /// <summary>The name Docker Desktop gives the machine that runs a container.</summary>
    internal const string ContainerHost = "host.docker.internal";

    /// <summary>VS Code's <c>vscode.env.remoteName</c> as a kind. ⚠ Only <c>ssh-remote</c> is a machine one can open a
    /// reverse tunnel to: a VS Code tunnel or a Codespace is another machine without that gesture, like any remote VS Code
    /// adds later.</summary>
    public static RemoteKind Of(string? remoteName) => remoteName?.Trim().ToLowerInvariant() switch
    {
        null or "" => RemoteKind.None,
        "wsl" => RemoteKind.Wsl,
        "ssh-remote" => RemoteKind.Ssh,
        "dev-container" or "attached-container" => RemoteKind.Container,
        _ => RemoteKind.Machine,
    };

    /// <summary>Why <paramref name="url"/> does not reach <paramref name="backend"/> from the side the host runs on, and
    /// what does; <c>null</c> when the host runs where the person sits, or the address is not a loopback one.</summary>
    public static string? Unreachable(string? remoteName, string url, string backend)
    {
        if (!Hardware.HardwareProbe.IsLoopback(url)) return null;
        var uri = new Uri(url);
        return Of(remoteName) switch
        {
            RemoteKind.Wsl       => Strings.RemoteLocalhostWsl(url, backend),
            // A tunnel needs the server's own port: an address without one (80, 443) would bind a privileged port there.
            RemoteKind.Ssh when !uri.IsDefaultPort => Strings.RemoteLocalhostSsh(url, backend, uri.Port),
            RemoteKind.Ssh or RemoteKind.Machine => Strings.RemoteLocalhostMachine(url, backend),
            RemoteKind.Container => Strings.RemoteLocalhostContainer(url, backend, OnContainerHost(url)),
            _                    => null,
        };
    }

    /// <summary><paramref name="url"/> on the machine that runs the container.</summary>
    internal static string OnContainerHost(string url) =>
        new UriBuilder(url) { Host = ContainerHost }.Uri.ToString().TrimEnd('/');

    /// <summary>What the first launch says under "no model server found here": where "here" is.</summary>
    public static string? SetupNote(string? remoteName) => Of(remoteName) switch
    {
        RemoteKind.Wsl       => Strings.RemoteSetupWsl,
        RemoteKind.Ssh       => Strings.RemoteSetupSsh,
        RemoteKind.Machine   => Strings.RemoteSetupMachine,
        RemoteKind.Container => Strings.RemoteSetupContainer,
        _                    => null,
    };
}
