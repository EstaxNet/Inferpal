using System.Reflection;
using Inferpal.Config;

namespace Inferpal.Services.Presentation;

/// <summary>
/// The "Show advanced settings" box of the Server and models page, in both settings panels.
/// </summary>
/// <remarks>
/// The box only folds: checking or unchecking it never changes a value. What it must not do is hide a
/// setting in effect — a per-task model the router keeps using while nobody can see it reads, at the
/// next 404, as a backend that lost its model. So the page opens with the box checked whenever a field
/// the schema marks <see cref="SettingField.OpensFold"/> departs from its factory value. The VS Code
/// panel applies the same rule to the same flag, with the factory values the host serves.
/// </remarks>
internal static class ModelRoleSettings
{
    /// <summary>The schema gate the box reveals.</summary>
    internal const string Gate = SettingsSchema.AdvancedGate;

    private static readonly InferpalConfig Factory = new();

    /// <summary>Whether the page must open with its advanced sections shown.</summary>
    internal static bool OpensAdvanced(InferpalConfig config) =>
        SettingsSchema.AllFields.Where(f => f.OpensFold).Any(f => Differs(f.Key, config));

    private static bool Differs(string key, InferpalConfig config)
    {
        var property = typeof(InferpalConfig).GetProperty(
            key, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (property is null) return false;
        var now     = property.GetValue(config);
        var factory = property.GetValue(Factory);
        return now is string s
            ? !string.Equals(s.Trim(), ((string?)factory ?? string.Empty).Trim(), StringComparison.Ordinal)
            : !Equals(now, factory);
    }
}
