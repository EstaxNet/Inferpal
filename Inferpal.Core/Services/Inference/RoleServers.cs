using Inferpal.Config;

namespace Inferpal.Services.Inference;

/// <summary>What a model does, and so which server it runs on.</summary>
internal enum ServerRole
{
    /// <summary>The chat and the agent: the server of <see cref="InferpalConfig.BaseUrl"/>.</summary>
    Chat,
    /// <summary>Inline completions (ghost text).</summary>
    Completion,
    /// <summary>Code-search embeddings: the project index, <c>@Docs</c>, and the queries against them.</summary>
    Search,
}

/// <summary>
/// The server each role runs on: the chat's, unless the settings put completions or code search on another — ghost text
/// and the index on this machine while the chat runs on a team server.
/// </summary>
/// <remarks>
/// ⚠ A key belongs to its server: a role on another server is sent that server's key, never the chat's. And two servers
/// on one MACHINE share its graphics card: a role yields to the chat when its server runs on the chat's machine, not
/// only on its port — the same machine under two ports is still one GPU.
/// </remarks>
internal static class RoleServers
{
    /// <summary><c>…RunsOn</c>: the chat's server.</summary>
    public const string OnChat = "chat";
    /// <summary><c>…RunsOn</c>: the server of the role's own address.</summary>
    public const string OnOther = "other";
    /// <summary><c>ragEmbeddingRunsOn</c>: the completion's server.</summary>
    public const string OnAutocomplete = "autocomplete";

    /// <summary>The server <paramref name="role"/> runs on: its backend code, address and key.</summary>
    public static (string Provider, string BaseUrl, string ApiKey) Of(InferpalConfig c, ServerRole role) => role switch
    {
        ServerRole.Completion when Is(c.InlineCompletionRunsOn, OnOther) && !string.IsNullOrWhiteSpace(c.InlineCompletionBaseUrl)
            => (Or(c.InlineCompletionProvider, c.Provider), c.InlineCompletionBaseUrl.Trim(), c.InlineCompletionApiKey ?? ""),
        ServerRole.Search when Is(c.RagEmbeddingRunsOn, OnOther) && !string.IsNullOrWhiteSpace(c.RagEmbeddingBaseUrl)
            => (Or(c.RagEmbeddingProvider, c.Provider), c.RagEmbeddingBaseUrl.Trim(), c.RagEmbeddingApiKey ?? ""),
        ServerRole.Search when Is(c.RagEmbeddingRunsOn, OnAutocomplete)
            => Of(c, ServerRole.Completion),
        _ => (c.Provider, c.BaseUrl, c.ApiKey ?? ""),
    };

    /// <summary><paramref name="role"/> runs on the chat's server (the same address): the chat's client serves it.</summary>
    public static bool OnChatServer(InferpalConfig c, ServerRole role) =>
        role == ServerRole.Chat || ServerScan.SameAddress(Of(c, role).BaseUrl, c.BaseUrl);

    /// <summary><paramref name="role"/>'s server runs on the chat's machine, so on its graphics card: it yields to a chat
    /// answering there. Unknown hosts are taken as the same machine — yielding when not needed costs a pause, not
    /// yielding when needed steals the card from an answer.</summary>
    public static bool SharesMachineWithChat(InferpalConfig c, ServerRole role) =>
        OnChatServer(c, role) || SameMachine(Of(c, role).BaseUrl, c.BaseUrl);

    /// <summary>The model a role on another server completes with: its own, never the chat's, which that server may not
    /// have; null when none is chosen.</summary>
    /// <remarks>⚠ An OpenAI-compatible server — llama-server, vLLM — completes only with a model chosen for completions:
    /// there the chat model is often a large one on a server a team shares, and falling back to it, every pause in anyone's
    /// typing asked it — completions switched on by themselves for everyone already chatting on such a server.</remarks>
    public static string? CompletionModel(InferpalConfig c, string? asked) =>
        !string.IsNullOrWhiteSpace(asked) ? asked
        : !string.IsNullOrWhiteSpace(c.InlineCompletionModel) ? c.InlineCompletionModel
        : OnChatServer(c, ServerRole.Completion) && !NeedsAChosenCompletionModel(c) ? c.DefaultModel
        : null;

    /// <summary>The completion server is OpenAI-compatible: it completes with a model chosen for it, never the chat's.</summary>
    public static bool NeedsAChosenCompletionModel(InferpalConfig c) =>
        InferenceProviderFactory.Canonical(Of(c, ServerRole.Completion).Provider) == InferenceProviderFactory.OpenAiCompatible;

    /// <summary>Says once what <paramref name="role"/>'s settings leave to a fallback: "another server" with no address
    /// runs on the chat's, and a server of its own whose kind is not set is spoken to as the chat's kind.</summary>
    public static void NoteFallbacks(InferpalConfig c, ServerRole role)
    {
        var (runsOn, url, provider, what) = role switch
        {
            ServerRole.Completion => (c.InlineCompletionRunsOn, c.InlineCompletionBaseUrl, c.InlineCompletionProvider, "Inline completions"),
            ServerRole.Search     => (c.RagEmbeddingRunsOn, c.RagEmbeddingBaseUrl, c.RagEmbeddingProvider, "Code-search embeddings"),
            _                     => (null, null, null, null),
        };
        if (what is null || !Is(runsOn, OnOther)) return;
        if (string.IsNullOrWhiteSpace(url))
            Diagnostics.RecordOnce("Settings",
                $"{what} are set to run on another server, but no address is given: they run on the chat's server.",
                "role-no-address|" + role);
        else if (string.IsNullOrWhiteSpace(provider))
            Diagnostics.RecordOnce("Settings",
                $"{what} run on {Commands.DiagnosticsCommandHandler.RedactEndpoint(url)}, whose kind of server is not set: " +
                $"it is spoken to as the chat's ({c.Provider}). The Test button beside its address sets it.",
                "role-no-kind|" + role + "|" + url);
    }

    /// <summary>Do <paramref name="a"/> and <paramref name="b"/> run on one machine? This machine is one host whatever
    /// it is called — localhost, its name, or one of its own network addresses.</summary>
    internal static bool SameMachine(string? a, string? b)
    {
        var (ha, hb) = (MachineOf(a), MachineOf(b));
        return ha is null || hb is null || ha == hb;
    }

    /// <summary>The machine <paramref name="url"/> names: <c>localhost</c> for this one whatever it is called, else its
    /// host in lower case; <c>null</c> when the address cannot be read.</summary>
    private static string? MachineOf(string? url)
    {
        if (Hardware.HardwareProbe.IsLoopback(url)) return "localhost";
        // "gpu-box:11434" without a scheme parses as the scheme "gpu-box" with no host: unread, not a machine named "".
        if (!Uri.TryCreate(ProviderProbe.RootOf(url), UriKind.Absolute, out var u) || u.Host.Length == 0) return null;
        var host = u.Host.ToLowerInvariant();
        return ThisMachine().Contains(host) ? "localhost" : host;
    }

    /// <summary>The machines other than this one that the roles in use send code to — the chat always, completions when
    /// they are on, code search when the index is or <c>@Docs</c> has sources (its queries go to the search server
    /// whatever the index setting) — in role order, each once; empty when everything runs here.</summary>
    /// <remarks>⚠ Unlike <see cref="SameMachine"/>, an address that cannot be read counts as ANOTHER machine, named as
    /// written: this list decides whether "your code stays on this machine" is said, a promise never made on a guess.
    /// </remarks>
    internal static IReadOnlyList<string> OffMachineHosts(InferpalConfig c)
    {
        var roles = new List<ServerRole> { ServerRole.Chat };
        if (c.InlineCompletionEnabled) roles.Add(ServerRole.Completion);
        if (c.RagEnabled || HasDocSources(c)) roles.Add(ServerRole.Search);

        // @Docs sources that cannot be read count as sources: the promise is never made on a guess.
        static bool HasDocSources(InferpalConfig c) =>
            !string.IsNullOrWhiteSpace(c.DocSitesJson)
            && (!Docs.DocSite.TryParse(c.DocSitesJson, out var sites, out _) || sites.Count > 0);

        var hosts = new List<string>();
        foreach (var url in roles.Select(r => Of(c, r).BaseUrl).Where(u => !string.IsNullOrWhiteSpace(u)))
        {
            var host = MachineOf(url) ?? url.Trim();
            if (host != "localhost" && !hosts.Contains(host, StringComparer.OrdinalIgnoreCase)) hosts.Add(host);
        }
        return hosts;
    }

    /// <summary>This machine's names and addresses as last read; replaced whole, never changed in place, so a thread that
    /// reads it while another refreshes it sees one reading or the other.</summary>
    private sealed record MachineNames(DateTime At, IReadOnlySet<string> Names);

    private static MachineNames _thisMachine = new(DateTime.MinValue, new HashSet<string>());

    /// <summary>This machine's names (its host name, its domain name, its Windows name) and its own addresses, read from
    /// the machine itself — no lookup on the network — and kept a minute: a completion asks on every pause in typing, and
    /// these change only when the network does.</summary>
    /// <remarks>⚠ <see cref="Environment.MachineName"/> alone is the NetBIOS name, upper case and cut at 15 characters:
    /// a URL written with the real host name (<c>dams-workstation-01</c>, <c>pc.lan</c>) would not be recognised.</remarks>
    private static IReadOnlySet<string> ThisMachine()
    {
        var known = _thisMachine;
        if (DateTime.UtcNow - known.At < TimeSpan.FromMinutes(1)) return known.Names;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Best effort: a machine whose names cannot be read is judged by localhost alone, never by an exception on the
        // completion's path.
        void Read(string what, Action read)
        {
            try { read(); }
            catch (Exception ex) { Diagnostics.Swallow("RoleServers.ThisMachine." + what, ex); }
        }
        Read("MachineName", () => names.Add(Environment.MachineName.ToLowerInvariant()));
        Read("HostName", () =>
        {
            var host = System.Net.Dns.GetHostName().ToLowerInvariant();
            names.Add(host);
            var domain = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().DomainName;
            if (!string.IsNullOrWhiteSpace(domain)) names.Add($"{host}.{domain.Trim('.')}".ToLowerInvariant());
        });
        Read("Addresses", () =>
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                foreach (var address in nic.GetIPProperties().UnicastAddresses)
                {
                    // An IPv6 address is written in brackets in a URL, and Uri.Host keeps them (without the scope).
                    var ip = address.Address;
                    names.Add(ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                        ? $"[{new System.Net.IPAddress(ip.GetAddressBytes())}]".ToLowerInvariant()
                        : ip.ToString());
                }
        });
        _thisMachine = new MachineNames(DateTime.UtcNow, names);
        return names;
    }

    private static bool Is(string? value, string expected) => string.Equals(value?.Trim(), expected, StringComparison.OrdinalIgnoreCase);

    private static string Or(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
