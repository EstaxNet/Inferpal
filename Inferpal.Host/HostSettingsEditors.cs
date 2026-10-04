using Inferpal.Services.Commands;
using Inferpal.Services.Mcp;
using Inferpal.Services.Presentation;
using StreamJsonRpc;

namespace Inferpal.Host;

/// <summary>
/// The structured editors of the settings panel's Tools page: MCP server cards (with what each server is
/// doing and why it is not) and the approval rules table. Built by the same Core presenters as the Visual
/// Studio window, so both editors say the same thing about the same server.
/// </summary>
internal sealed partial class HostServer
{
    /// <summary>`mcp/cards` — one card per configured server, from the SAVED settings and the running service.</summary>
    [JsonRpcMethod("mcp/cards")]
    public McpCardsDto McpCards() => ToMcpCards(Session());

    /// <summary>`mcp/retry` — restarts the servers and answers with what they did.</summary>
    /// <remarks>Inside the turn slot: a restart tears down every server, so it waits for a running turn to end.</remarks>
    [JsonRpcMethod("mcp/retry")]
    public Task<McpCardsDto> McpRetry(CancellationToken ct)
    {
        var s = Session();
        return WithTurnSlotAsync("mcp/retry", ct, async _ =>
        {
            await s.Mcp.RefreshAsync();
            return ToMcpCards(s);
        });
    }

    /// <summary>`mcp/authorize` — the OAuth sign-in of an HTTP server, as the Visual Studio window's button.</summary>
    /// <remarks>
    /// A failed sign-in is an ANSWER, not an RPC error: the card names it ("the browser was closed", "no secret store
    /// on this platform"), and the panel would otherwise only say that the host failed.
    /// </remarks>
    [JsonRpcMethod("mcp/authorize", UseSingleObjectParameterDeserialization = true)]
    public async Task<McpCardsDto> McpAuthorize(McpServerNameParams p, CancellationToken ct)
    {
        var s = Session();
        try
        {
            await s.Mcp.AuthorizeAsync(p.Name, ct);
            return ToMcpCards(s);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return ToMcpCards(s) with { Error = ex.Message };
        }
    }

    /// <summary>
    /// `permissions/table` — every approval rule written, team file and the machine rules the panel holds (unsaved
    /// edits included), in evaluation order, each saying whether it takes part.
    /// </summary>
    [JsonRpcMethod("permissions/table", UseSingleObjectParameterDeserialization = true)]
    public ApprovalRuleTableDto PermissionsTable(PermissionsTableParams p)
    {
        var s = Session();
        var table = ApprovalRuleTable.Build(PermissionsCommandHandler.ReadOverlayFile(s.RootDir), p.Rules);
        return new ApprovalRuleTableDto(
            table.TeamUnusable,
            [.. table.Rows.Select(r => new ApprovalRuleRowDto(
                r.Source == RuleSource.Team ? "team" : "machine",
                r.Allow, r.Tool, r.Pattern,
                r.Status switch { RuleStatus.IgnoredAllow => "ignoredAllow", RuleStatus.Unreadable => "unreadable", _ => "inForce" },
                r.EffectText, r.FromText, r.NoteText, r.MachineLine))]);
    }

    /// <summary>`permissions/newRule` — the line for a new machine rule, or <c>null</c> when it would not be read.</summary>
    [JsonRpcMethod("permissions/newRule", UseSingleObjectParameterDeserialization = true)]
    public string? PermissionsNewRule(NewRuleParams p) => ApprovalRuleTable.NewRuleLine(p.Allow, p.Tool, p.Pattern);

    private static McpCardsDto ToMcpCards(HostSession s)
    {
        var cards = McpServerCards.Build(McpServerConfig.Parse(s.Config.McpServersJson), s.Mcp.Status);
        return new McpCardsDto(
            McpServerCards.Summary(cards),
            [.. cards.Select(c => new McpCardDto(
                c.Name, c.Transport, c.Target, c.Enabled,
                c.State switch
                {
                    McpCardState.Connected   => "connected",
                    McpCardState.NeedsSignIn => "signIn",
                    McpCardState.Failed      => "failed",
                    McpCardState.Off         => "off",
                    _                        => "notStarted",
                },
                c.StatusText, c.Cause, c.ToolCount))],
            Error: null);
    }
}
