using Inferpal.Localization;
using Inferpal.Services.Mcp;

namespace Inferpal.Services.Presentation;

/// <summary>The state a server card shows — it picks the dot's color and the action offered.</summary>
internal enum McpCardState
{
    Connected,
    /// <summary>Up, and waiting for the user to sign in: the card offers Sign in.</summary>
    NeedsSignIn,
    /// <summary>Did not start, or its entry could not be read: the card shows why, and offers Retry.</summary>
    Failed,
    /// <summary>Switched off by the user.</summary>
    Off,
    /// <summary>No status yet: not saved, MCP switched off, or still connecting.</summary>
    NotStarted,
}

/// <summary>One server card of the settings window.</summary>
/// <param name="Transport"><c>stdio</c> or <c>HTTP</c> — protocol names, never translated; empty for an
/// entry that never became a server.</param>
/// <param name="Target">The command line (stdio) or the address (HTTP).</param>
/// <param name="Cause">Why it is not working, or what went wrong while connected — the server's own words,
/// shown as is; <c>null</c> when there is nothing to say.</param>
internal sealed record McpServerCard(
    string Name, string Transport, string Target, bool Enabled,
    McpCardState State, string StatusText, string? Cause, int ToolCount);

/// <summary>
/// The MCP servers as the settings window shows them, in both editors: one card per configured server,
/// with its state and, when it does not work, WHY — the server's stderr or the refusal, already kept by
/// <see cref="McpToolService"/>. A server that does not start is the case this screen exists for: a line
/// that says "✗" without its cause sends the user to the logs.
/// </summary>
internal static class McpServerCards
{
    /// <param name="configs">The configured servers (the settings text, parsed).</param>
    /// <param name="status">What the running service reports — servers that tried to start, and entries
    /// rejected before they could (a misspelt key): those have no config, they get a card of their own.</param>
    public static IReadOnlyList<McpServerCard> Build(
        IReadOnlyList<McpServerConfig> configs, IReadOnlyList<McpServerStatus> status)
    {
        var byName = new Dictionary<string, McpServerStatus>(StringComparer.Ordinal);
        foreach (var s in status) byName[s.Name] = s;

        var cards = new List<McpServerCard>();
        foreach (var config in configs)
        {
            var (transport, target) = config.IsHttp
                ? ("HTTP", config.Url ?? string.Empty)
                : ("stdio", CommandLine(config));

            if (!config.Enabled)
                cards.Add(new(config.Name, transport, target, false, McpCardState.Off, Strings.McpCardOff, null, 0));
            else if (byName.TryGetValue(config.Name, out var st))
                cards.Add(FromStatus(config.Name, transport, target, st));
            else
                cards.Add(new(config.Name, transport, target, true, McpCardState.NotStarted, Strings.McpCardNotStarted, null, 0));
            byName.Remove(config.Name);
        }

        // Entries the parser rejected never became a config: without a card of their own they would
        // vanish from the very screen where the user looks for them.
        foreach (var orphan in status.Where(s => byName.ContainsKey(s.Name)))
            cards.Add(FromStatus(orphan.Name, string.Empty, string.Empty, orphan));

        return cards;
    }

    /// <summary>"3 configured · 1 needs your attention" — the attention count only when there is one.</summary>
    public static string Summary(IReadOnlyList<McpServerCard> cards)
    {
        var attention = cards.Count(c => c.State is McpCardState.Failed or McpCardState.NeedsSignIn);
        return attention == 0
            ? Strings.McpCardsSummary(cards.Count)
            : Strings.McpCardsSummaryAttention(cards.Count, attention);
    }

    private static McpServerCard FromStatus(string name, string transport, string target, McpServerStatus st) =>
        st.Connected     ? new(name, transport, target, true, McpCardState.Connected,
                               Strings.McpCardConnected(st.ToolCount), NullIfEmpty(st.Error), st.ToolCount)
      : st.AuthRequired  ? new(name, transport, target, true, McpCardState.NeedsSignIn, Strings.McpCardSignIn, null, 0)
      : new(name, transport, target, true, McpCardState.Failed, Strings.McpCardDidNotStart, NullIfEmpty(st.Error), 0);

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    /// <summary>The command and its arguments, an argument with a space quoted so the line reads as typed.</summary>
    private static string CommandLine(McpServerConfig config) =>
        string.Join(' ', new[] { config.Command ?? string.Empty }
            .Concat(config.Args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)))
            .Trim();
}
