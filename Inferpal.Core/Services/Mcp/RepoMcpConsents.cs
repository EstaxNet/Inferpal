using Inferpal.Services.Persistence;

namespace Inferpal.Services.Mcp;

/// <summary>
/// The agreements the user gave to start a repository's MCP servers: per repository, per server, the
/// <see cref="RepoMcpServer.Fingerprint"/> agreed to. A definition changed since is a new question.
/// </summary>
/// <remarks>
/// ⚠ The file is SHARED by the two editors (<c>%AppData%\Inferpal</c>): every decision reads it as it is now, never a
/// copy this process loaded earlier. A refusal is not kept: the server is asked about again at the next start, as
/// Visual Studio does.
/// ⚠ And the two editors can run two versions: a fingerprint that covers files (<see cref="RepoMcpServer.Files"/>) is
/// kept under a key of its own (<see cref="KeyOf"/>), because a version that hashes the definition alone writes the
/// server's name — one key for both, and each editor would overwrite the other's agreement and ask at every start. A
/// server that runs no file of the repository has the same fingerprint in both, under the name both read.
/// </remarks>
internal sealed class RepoMcpConsents
{
    private readonly AppDataJsonFile<Dictionary<string, Dictionary<string, string>>> _file =
        new("mcp-repository-consents.json", "McpRepositoryConsents");
    private readonly object _gate = new();

    /// <summary>Test seam: where the agreements are kept.</summary>
    internal string? PathOverride { get => _file.PathOverride; set => _file.PathOverride = value; }

    public bool IsAgreed(string root, RepoMcpServer server)
    {
        lock (_gate)
        {
            var (all, _) = _file.Read([]);
            return Entry(all, root) is { } servers
                && servers.TryGetValue(KeyOf(server), out var agreed) && agreed == server.Fingerprint;
        }
    }

    /// <summary>Where a server's agreement is kept: its name, or, for one that covers files, a key only this version writes.</summary>
    internal static string KeyOf(RepoMcpServer server) =>
        server.Files is { Count: > 0 } ? server.Name + "\u0001files" : server.Name;

    public void Agree(string root, RepoMcpServer server)
    {
        lock (_gate)
        {
            var (all, unreadable) = _file.Read([]);
            if (unreadable) all = [];
            var key = all.Keys.FirstOrDefault(k => PathComparer.SameDirectory(k, root)) ?? root;
            if (!all.TryGetValue(key, out var servers)) all[key] = servers = new(StringComparer.Ordinal);
            servers[KeyOf(server)] = server.Fingerprint;
            // Not written (traced by the file): the user is asked again at the next start — the safe side.
            _file.Save(all);
        }
    }

    private static Dictionary<string, string>? Entry(Dictionary<string, Dictionary<string, string>> all, string root) =>
        all.FirstOrDefault(kv => PathComparer.SameDirectory(kv.Key, root)).Value;
}
