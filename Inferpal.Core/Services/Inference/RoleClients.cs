using Inferpal.Config;

namespace Inferpal.Services.Inference;

/// <summary>
/// A process's clients, one per role (<see cref="ServerRole"/>): the chat's own client for a role on the chat's server, a
/// client of its own for a role on another — created when first asked for, and again when that server changes.
/// </summary>
/// <remarks>
/// ⚠ Asked for at each use, never kept: a role moved to another server in the settings is served there from the next
/// request on. And a new server — another kind, address or key — gets a new client: a client carries what it learned of
/// its server (a breaker opened on a wrong address, a refused completion), which says nothing of the next one.
/// </remarks>
internal sealed class RoleClients(
    IInferenceProvider chat, InferpalConfig config, Func<InferpalConfig, ServerRole, IInferenceProvider>? create = null)
{
    private readonly Func<InferpalConfig, ServerRole, IInferenceProvider> _create = create ?? InferenceProviderFactory.Create;
    private readonly Dictionary<ServerRole, ((string, string, string) Server, IInferenceProvider Client)> _own = [];
    private readonly object _gate = new();

    /// <summary>The chat's client.</summary>
    public IInferenceProvider Chat => chat;

    /// <summary>The client that serves <paramref name="role"/> now.</summary>
    public IInferenceProvider For(ServerRole role)
    {
        RoleServers.NoteFallbacks(config, role);
        if (RoleServers.OnChatServer(config, role)) return chat;
        var (provider, url, key) = RoleServers.Of(config, role);
        var server = (InferenceProviderFactory.Canonical(provider), url, key);
        lock (_gate)
        {
            if (_own.TryGetValue(role, out var own) && own.Server == server) return own.Client;
            var client = _create(config, role);
            _own[role] = (server, client);
            return client;
        }
    }
}
