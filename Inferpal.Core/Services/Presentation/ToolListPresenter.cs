using System.Text;
using Inferpal.Localization;

namespace Inferpal.Services.Presentation;

/// <summary>One tool as <c>/tools</c> and the Tools page list it.</summary>
/// <param name="Key">The name it is turned off and on by, as a person types it (<see cref="ToolSwitches.Typed"/>).</param>
/// <param name="Weight">What its definition weighs in a request, as a person reads it ("312", "1.4k").</param>
/// <param name="State"><c>sent</c>, <c>onDemand</c>, <c>off</c>, <c>offByEntry</c> or <c>notOffered</c> — the colour of
/// its row, never read back from its label.</param>
/// <param name="Off">The user turned it off: its button turns it back on.</param>
/// <param name="CanToggle">A button is offered: never for <c>tool_search</c>, nor for a tool only its server's entry turns
/// off (that entry is where it is turned back on — <paramref name="StateLabel"/> says so).</param>
internal sealed record ToolListRow(string Key, string Name, string Weight, string State, string StateLabel, bool Off,
                                   bool CanToggle, string ActionLabel);

/// <summary>The tools of one source: the built-in ones, the user's own, or one MCP server's.</summary>
internal sealed record ToolListGroup(string Title, IReadOnlyList<ToolListRow> Rows);

/// <summary>Every tool, grouped, with what the next request sends of them.</summary>
/// <param name="Summary">How many tools the next request declares and what they weigh, and how many are turned off or wait
/// on demand.</param>
internal sealed record ToolListModel(string Summary, IReadOnlyList<ToolListGroup> Groups, string HowTo);

/// <summary>
/// The tools as <c>/tools</c> and the Tools page show them, from <see cref="ToolRegistry.Profile"/> — one presenter, so
/// the command, the page of Visual Studio and the page of VS Code say the same thing about the same registry.
/// </summary>
internal static class ToolListPresenter
{
    /// <summary>The list of <paramref name="tools"/>, its total weighed on the definitions its rows say are sent.</summary>
    /// <remarks>⚠ Not the next turn's count (<c>ContextManager.NextTurnToolTokens</c>): with tools off for the conversation
    /// or in plan mode, that one weighs another set than the rows — "32 tools sent, ~0 tokens". The list says what a
    /// request with tools declares; the conversation's switch is said on its own line.</remarks>
    public static ToolListModel Build(ToolRegistry tools) =>
        Build(tools.Profile(), RequestSize.ToolTokens(tools.Definitions), tools.TurnedOffElsewhere());

    /// <param name="sentTokens">What the definitions of the rows in the <c>sent</c> state weigh in a request.</param>
    /// <param name="elsewhere">The lines of the setting that name no tool served here (<c>ToolRegistry.TurnedOffElsewhere</c>):
    /// a group of their own, each turned back on by its text.</param>
    public static ToolListModel Build(IReadOnlyList<ToolProfileEntry> profile, int sentTokens,
                                      IReadOnlyList<string>? elsewhere = null)
    {
        var groups = profile
            .GroupBy(e => (e.Source, e.Server))
            .Select(g => new ToolListGroup(Title(g.Key.Source, g.Key.Server), [.. g.Select(Row)]))
            .ToList();
        if (elsewhere is { Count: > 0 })
            groups.Add(new ToolListGroup(Strings.ToolGroupNotHere, [.. elsewhere.Select(line =>
                new ToolListRow(line, line, "—", "off", Strings.ToolStateOff, Off: true, CanToggle: true, Strings.ToolTurnOn))]));

        var sent = profile.Count(e => e.State == ToolState.Sent);
        var parts = new List<string> { Strings.ToolListSent(sent, SettingsWidgets.Amount(sentTokens)) };
        if (profile.Count(e => e.State is ToolState.OffByUser or ToolState.OffByEntry) + (elsewhere?.Count ?? 0) is > 0 and var off)
            parts.Add(Strings.ToolListTurnedOff(off));
        if (profile.Count(e => e.State == ToolState.OnDemand) is > 0 and var onDemand)
            parts.Add(Strings.ToolListOnDemand(onDemand));
        return new(string.Join(" · ", parts), groups, Strings.ToolListHowTo);
    }

    private static string Title(ToolSource source, string? server) => source switch
    {
        ToolSource.BuiltIn => Strings.ToolGroupBuiltIn,
        ToolSource.Custom  => Strings.ToolGroupCustom,
        _                  => Strings.ToolGroupServer(server ?? string.Empty),
    };

    private static ToolListRow Row(ToolProfileEntry e)
    {
        var (state, label) = e.State switch
        {
            ToolState.Sent       => ("sent",       Strings.ToolStateSent),
            ToolState.OnDemand   => ("onDemand",   Strings.ToolStateOnDemand),
            ToolState.OffByUser  => ("off",        Strings.ToolStateOff),
            ToolState.OffByEntry => ("offByEntry", Strings.ToolStateOffByEntry),
            _                    => ("notOffered", Strings.ToolStateNotOffered),
        };
        var off = e.State == ToolState.OffByUser;
        var canToggle = e.Tool.Name != ToolSwitches.AlwaysOn && e.State != ToolState.OffByEntry;
        return new(ToolSwitches.Typed(e.Tool), e.Tool.Name, SettingsWidgets.Amount(e.Tokens), state, label, off, canToggle,
                   off ? Strings.ToolTurnOn : Strings.ToolTurnOff);
    }

    /// <summary>The list as <c>/tools</c> prints it, under the line that says the conversation's switch.</summary>
    public static string Markdown(ToolListModel model, string sessionLine)
    {
        var sb = new StringBuilder(sessionLine).Append("\n\n**").Append(model.Summary).Append("**\n");
        foreach (var group in model.Groups)
        {
            sb.Append("\n**").Append(group.Title).Append("**\n\n")
              .Append("| ").Append(Strings.ToolListColTool).Append(" | ").Append(Strings.ToolListColTokens)
              .Append(" | ").Append(Strings.ToolListColState).Append(" |\n|---|---:|---|\n");
            foreach (var row in group.Rows)
                sb.Append("| `").Append(row.Key).Append("` | ").Append(row.Weight).Append(" | ").Append(row.StateLabel).Append(" |\n");
        }
        return sb.Append('\n').Append(model.HowTo).ToString();
    }
}
