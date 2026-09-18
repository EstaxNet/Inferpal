using System.IO;
using Inferpal.Localization;
using Inferpal.Services.Execution;

namespace Inferpal.Services.Commands;

/// <summary>
/// Pure execution logic for <c>/permissions</c> — the effective permission ruleset, in the order it
/// is evaluated, plus what the workspace overlay declared and did <b>not</b> get.
/// </summary>
/// <remarks>
/// <para>
/// Of the four committable governance files, <c>.inferpal/permissions.json</c> is the only one that
/// restricts rather than advises, and it was the only one with no listing surface. It can also stop
/// applying all of its rules in silence — invalid JSON, missing <c>rules</c> array, malformed line —
/// and the only channel that said so was <c>/diagnostics</c>, which nobody opens after writing a
/// rule they believe they have set.
/// </para>
/// <para>
/// The order displayed is the evaluation order (<see cref="PermissionPolicy.Compose"/>): overlay
/// first, machine config next, first match wins. Showing it any other way would be a report that is
/// right about the content and wrong about the effect.
/// </para>
/// </remarks>
internal static class PermissionsCommandHandler
{
    /// <summary>Path of the workspace overlay, for display and reading.</summary>
    internal static string OverlayPath(string projectRoot) =>
        Path.Combine(projectRoot, ".inferpal", "permissions.json");

    /// <param name="projectRoot">Workspace root; empty/null ⇒ overlay section reports "no workspace".</param>
    /// <param name="configRules">The per-machine <c>PermissionRules</c> setting, verbatim.</param>
    /// <param name="readOverlay">Reads the overlay file; injected so the handler stays pure in tests.</param>
    public static string Permissions(string? projectRoot, string? configRules, Func<string, string?>? readOverlay = null)
    {
        readOverlay ??= ReadFileOrNull;

        var sb = new System.Text.StringBuilder(Strings.PermissionsHeader);

        // ── Workspace overlay (deny-only, evaluated FIRST) ────────────────────
        sb.Append("\n\n").Append(Strings.PermissionsOverlaySection);
        if (string.IsNullOrEmpty(projectRoot))
        {
            sb.Append('\n').Append(Strings.PermissionsNoWorkspace);
        }
        else
        {
            var path = OverlayPath(projectRoot);
            var json = readOverlay(path);
            if (json is null)
            {
                sb.Append('\n').Append(Strings.PermissionsOverlayAbsent);
            }
            else
            {
                var report = PermissionPolicy.ReadOverlay(json);

                // ⚠ The state that motivated this command comes first, and it is named: the file is
                // there, the user wrote it, and nothing in it applies.
                if (report.Unusable)
                    sb.Append('\n').Append(Strings.PermissionsOverlayUnusable);

                foreach (var r in report.Rules)
                    sb.Append("\n- `deny ").Append(r.Tool).Append(' ').Append(r.Pattern).Append('`');

                if (report.Rules.Count == 0 && !report.Unusable)
                    sb.Append('\n').Append(Strings.PermissionsOverlayEmpty);

                // Two distinct outcomes, because they are not fixed in the same place: a malformed
                // line is a defect to correct, while an `allow` rule set aside is the overlay's
                // contract (deny-only) and asks nothing of anyone.
                if (report.Malformed > 0)
                    sb.Append('\n').Append(Strings.PermissionsOverlayMalformed(report.Malformed));
                if (report.AllowIgnored > 0)
                    sb.Append('\n').Append(Strings.PermissionsOverlayAllowIgnored(report.AllowIgnored));
            }
        }

        // ── Per-machine rules (evaluated after the overlay) ───────────────────
        sb.Append("\n\n").Append(Strings.PermissionsConfigSection);
        var machine = PermissionPolicy.ParseRules(configRules ?? string.Empty, out var dropped);
        if (machine.Count == 0)
            sb.Append('\n').Append(Strings.PermissionsConfigEmpty);
        else
            foreach (var r in machine)
                sb.Append("\n- `").Append(r.Decision == PermissionDecision.Allow ? "allow" : "deny")
                  .Append(' ').Append(r.Tool).Append(' ').Append(r.Pattern).Append('`');

        // Same reason as for the overlay: a discarded line is a rule the user believes they have
        // set, and the setting has no room to say so line by line.
        if (dropped.Count > 0)
            sb.Append('\n').Append(Strings.PermissionsConfigDropped(dropped.Count));

        // ── The built-in denylist, which no rule can switch off ───────────────
        sb.Append("\n\n").Append(Strings.PermissionsDenylistNote);

        return sb.ToString();
    }

    private static string? ReadFileOrNull(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch (Exception ex)
        {
            // Reading failed although the file is there: do not return `null`, which would read as
            // "no overlay" — the exact failure this command exists to show.
            Diagnostics.Swallow("PermissionsCommandHandler.ReadOverlay", ex);
            return "{}";
        }
    }
}
