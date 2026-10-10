using Inferpal.Localization;

namespace Inferpal.Host.Acp;

/// <summary>
/// The session's modes and configuration options, as ACP clients show them: three ways of working, and the model.
/// </summary>
/// <remarks>
/// ⚠ Both the legacy <c>modes</c> and the <c>configOptions</c> that replace them are sent, kept in step: a client
/// reads one or the other, and a mode changed through either is the same mode. Only <c>select</c> options — a
/// <c>boolean</c> one may not be sent to a client that did not say it reads them.
/// </remarks>
internal static class AcpModes
{
    public const string Default = "default", Agent = "agent", Plan = "plan";
    public const string ModeOption = "mode", ModelOption = "model";

    public static readonly IReadOnlySet<string> Ids = new HashSet<string>(StringComparer.Ordinal) { Default, Agent, Plan };

    private sealed record ModeInfo(string Id, string Name, string Description);

    private static ModeInfo[] Modes() =>
    [
        new(Default, Strings.AcpModeDefault, Strings.AcpModeDefaultHint),
        new(Agent,   Strings.AcpModeAgent,   Strings.AcpModeAgentHint),
        new(Plan,    Strings.AcpModePlan,    Strings.AcpModePlanHint),
    ];

    public static object State(string current) => new
    {
        currentModeId  = current,
        availableModes = Modes().Select(m => new { id = m.Id, name = m.Name, description = m.Description }).ToArray(),
    };

    /// <summary>The models a session can switch to: the installed ones, and the current one even when the server does not
    /// list it (unreachable, or a name it does not report) — a current value must be one of the choices.</summary>
    public static List<string> ModelChoices(string current, IReadOnlyList<string> installed)
    {
        var choices = installed.ToList();
        if (!string.IsNullOrWhiteSpace(current) && !choices.Contains(current, StringComparer.Ordinal)) choices.Insert(0, current);
        return choices;
    }

    public static List<object> ConfigOptions(string mode, string model, IReadOnlyList<string> installed)
    {
        var options = new List<object>
        {
            new
            {
                id = ModeOption, name = Strings.AcpOptionMode, category = "mode", type = "select", currentValue = mode,
                options = Modes().Select(m => new { value = m.Id, name = m.Name, description = m.Description }).ToArray(),
            },
        };
        var models = ModelChoices(model, installed);
        if (models.Count > 0)
            options.Add(new
            {
                id = ModelOption, name = Strings.AcpOptionModel, category = "model", type = "select",
                currentValue = string.IsNullOrWhiteSpace(model) ? models[0] : model,
                options = models.Select(m => new { value = m, name = m }).ToArray(),
            });
        return options;
    }
}
