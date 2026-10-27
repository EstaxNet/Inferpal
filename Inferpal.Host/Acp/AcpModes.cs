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

    /// <summary>The id of a mode found for the workspace (<c>mode/list</c>): this prefix, then its name — never one of
    /// <see cref="Ids"/>, whatever the file calls it.</summary>
    public const string FoundPrefix = "mode:";

    public static bool IsFound(string id) => id.StartsWith(FoundPrefix, StringComparison.Ordinal);

    /// <summary>A mode found for the workspace, as the client lists it.</summary>
    /// <param name="Models">The models it asks for, in order: the first the server has answers.</param>
    internal sealed record Found(string Id, string Name, string Description, IReadOnlyList<string> Models)
    {
        public static Found Of(ModeDto m) =>
            new(FoundPrefix + m.Name, m.Name, m.Description.Length > 0 ? $"{m.Description} · {m.Source}" : m.Source, m.Models);

        /// <summary>The model this mode answers with among <paramref name="chatModels"/> — the host's own choice, made
        /// again at every question; <c>null</c> when it names none the server has.</summary>
        public string? ModelAmong(IReadOnlyList<string> chatModels)
        {
            foreach (var wanted in Models)
                if (chatModels.FirstOrDefault(m => ModelCatalog.SameModelName(m, wanted)) is { } found) return found;
            return null;
        }

        public bool Equals(Found? other) =>
            other is not null && Id == other.Id && Description == other.Description && Models.SequenceEqual(other.Models);

        public override int GetHashCode() => HashCode.Combine(Id, Description);
    }

    private sealed record ModeInfo(string Id, string Name, string Description);

    private static ModeInfo[] Modes(IReadOnlyList<Found> found) =>
    [
        new(Default, Strings.AcpModeDefault, Strings.AcpModeDefaultHint),
        new(Agent,   Strings.AcpModeAgent,   Strings.AcpModeAgentHint),
        new(Plan,    Strings.AcpModePlan,    Strings.AcpModePlanHint),
        .. found.Select(f => new ModeInfo(f.Id, f.Name, f.Description)),
    ];

    public static object State(string current, IReadOnlyList<Found> found) => new
    {
        currentModeId  = current,
        availableModes = Modes(found).Select(m => new { id = m.Id, name = m.Name, description = m.Description }).ToArray(),
    };

    /// <summary>The models a session can switch to: the installed ones, and the current one even when the server does not
    /// list it (unreachable, or a name it does not report) — a current value must be one of the choices.</summary>
    public static List<string> ModelChoices(string current, IReadOnlyList<string> installed)
    {
        var choices = installed.ToList();
        if (!string.IsNullOrWhiteSpace(current) && !choices.Contains(current, StringComparer.Ordinal)) choices.Insert(0, current);
        return choices;
    }

    public static List<object> ConfigOptions(string mode, string model, IReadOnlyList<string> installed, IReadOnlyList<Found> found)
    {
        var options = new List<object>
        {
            new
            {
                id = ModeOption, name = Strings.AcpOptionMode, category = "mode", type = "select", currentValue = mode,
                options = Modes(found).Select(m => new { value = m.Id, name = m.Name, description = m.Description }).ToArray(),
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
