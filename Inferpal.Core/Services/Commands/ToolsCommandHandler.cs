using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Services.Mcp;

namespace Inferpal.Services.Commands;

/// <summary>What <c>/tools</c> answers, and what the front-end applies.</summary>
/// <param name="SessionTools">The conversation's switch to apply (<c>/tools on|off</c> without a name); <c>null</c>: unchanged.</param>
/// <param name="SaveConfig">The tools turned off changed in the configuration: the front-end saves and applies it.</param>
internal sealed record ToolsCommandResult(string Message, bool? SessionTools = null, bool SaveConfig = false);

/// <summary>
/// Execution logic for <c>/tools</c>: the list of the tools with what each weighs and what the next request does with it;
/// <c>/tools on|off</c> for the conversation's switch; <c>/tools on|off &lt;tool&gt;…</c> for the tools turned off in every
/// conversation (<see cref="InferpalConfig.DisabledTools"/>). Shared by both front-ends and the agent protocol, and by the
/// Tools page's buttons (<see cref="Switch"/>).
/// </summary>
internal static class ToolsCommandHandler
{
    /// <summary>The syntax a mistyped sub-command is answered with.</summary>
    internal const string Syntax = "/tools [on|off] [<tool>…]";

    /// <param name="parts">Tokenised command: <c>/tools</c>, then <c>on</c> or <c>off</c>, then tool names.</param>
    /// <param name="sessionTools">The conversation's switch as it is now.</param>
    public static ToolsCommandResult Handle(string[] parts, ToolRegistry tools, InferpalConfig config, bool sessionTools)
    {
        if (parts.Length < 2)
            return new(ToolListPresenter.Markdown(ToolListPresenter.Build(tools),
                                                  Strings.SlashToolsCurrent(sessionTools ? "on" : "off")));

        var verb = parts[1].ToLowerInvariant();
        // ⚠ A sub-command it does not know is named, never taken for the list: "/tools disable x" listing the tools
        // reads as done.
        if (verb is not ("on" or "off")) return new(Strings.SlashUsage(Syntax));
        if (parts.Length == 2) return new(Strings.SlashToolsChanged(verb), SessionTools: verb == "on");

        var names = parts[2..].SelectMany(p => p.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                              .Distinct(StringComparer.Ordinal).ToList();
        // `/tools off ,` names nothing: the syntax, never an empty answer.
        return names.Count == 0 ? new(Strings.SlashUsage(Syntax)) : Switch(names, off: verb == "off", tools, config);
    }

    /// <summary>
    /// Turns <paramref name="names"/> off or back on in the configuration — each name said with what became of it.
    /// </summary>
    /// <remarks>Nothing is written when nothing changes: a name already in the state asked, unknown, or refused.</remarks>
    public static ToolsCommandResult Switch(IReadOnlyList<string> names, bool off, ToolRegistry tools, InferpalConfig config)
    {
        // ⚠ Decided on the FILE's list: a tool the other editor turned off since this copy was read would answer "already
        // on" here, and nothing would be written.
        config.FollowFile(nameof(InferpalConfig.DisabledTools));
        var profile = tools.Profile();
        var elsewhere = tools.TurnedOffElsewhere();
        var changed = new List<ToolProfileEntry>();
        var lifted = new List<string>();   // lines that name no tool served here, turned back on by their text
        var already = new List<string>();
        var said = new List<string>();
        foreach (var name in names)
        {
            var found = Resolve(name, profile);
            if (found.Count == 0)
            {
                // A server stopped, the other editor's debugger: its line is listed, so it can be turned back on.
                if (!off && elsewhere.Contains(name)) lifted.Add(name);
                else said.Add(Strings.SlashToolsUnknown(name));
                continue;
            }
            if (found.Count > 1)
            {
                said.Add(Strings.SlashToolsAmbiguous(name, string.Join(", ", found.Select(e => $"`{ToolSwitches.Typed(e.Tool)}`"))));
                continue;
            }
            var entry = found[0];
            var typed = ToolSwitches.Typed(entry.Tool);
            if (entry.Tool.Name == ToolSwitches.AlwaysOn)
            {
                if (off) said.Add(Strings.SlashToolsSearchStaysOn);
                else already.Add(typed);
                continue;
            }
            var byUser  = entry.State == ToolState.OffByUser;
            var byEntry = entry.Tool is McpTool { TurnedOffByEntry: true };
            // Its server's entry turns it off: that entry is where it is turned back on — said whatever was asked.
            var entrySays = byEntry ? Strings.SlashToolsOffByEntry(typed, entry.Server ?? string.Empty) : null;
            if (off ? byUser : !byUser) { if (entrySays is not null) said.Add(entrySays); else already.Add(typed); continue; }
            if (off && entrySays is not null) { said.Add(entrySays); continue; }
            changed.Add(entry);
            if (entrySays is not null) said.Add(entrySays);
        }

        var lines = new List<string>();
        if (changed.Count > 0 || lifted.Count > 0)
        {
            config.DisabledTools = ToolSwitches.RemoveLines(
                ToolSwitches.Write(config.DisabledTools, [.. changed.Select(e => e.Tool)], off), lifted);
            var done = changed.Select(e => ToolSwitches.Typed(e.Tool)).Concat(lifted).ToList();
            var list = string.Join(", ", done.Select(k => $"`{k}`"));
            lines.Add(off ? Strings.SlashToolsTurnedOff(done.Count, list) : Strings.SlashToolsTurnedOn(done.Count, list));
        }
        if (already.Count > 0)
        {
            var list = string.Join(", ", already.Select(k => $"`{k}`"));
            lines.Add(off ? Strings.SlashToolsAlreadyOff(already.Count, list) : Strings.SlashToolsAlreadyOn(already.Count, list));
        }
        lines.AddRange(said);
        return new(string.Join("\n", lines), SaveConfig: changed.Count > 0 || lifted.Count > 0);
    }

    /// <summary>The tools <paramref name="name"/> designates: by the name <c>/tools</c> shows, by the one the model calls
    /// (<c>mcp__server__tool</c>), or by an MCP tool's own name when one server only has it.</summary>
    private static List<ToolProfileEntry> Resolve(string name, IReadOnlyList<ToolProfileEntry> profile)
    {
        var exact = profile.Where(e => e.Key == name || ToolSwitches.Typed(e.Tool) == name || e.Tool.Name == name).ToList();
        if (exact.Count > 0) return exact;
        return [.. profile.Where(e => e.Tool is McpTool m && m.LocalName == name)];
    }
}
