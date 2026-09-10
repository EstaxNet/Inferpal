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
/// ⚠ <b>Pourquoi cette commande existe</b> (mesure du 2026-09-10). Des quatre artefacts de
/// gouvernance commitables, <c>.inferpal/permissions.json</c> était le seul sans surface de
/// listing : <c>/rules</c>, <c>/checks</c> et <c>/prompts</c> en ont une, et c'est le seul des
/// quatre qui <b>restreint</b> au lieu de conseiller. Or il peut cesser d'appliquer TOUTES ses
/// règles en silence — JSON invalide, tableau <c>rules</c> absent, ligne malformée — et le seul
/// canal qui le disait était <c>/diagnostics</c>, dont l'arbitrage de Dams du 2026-09-06 a établi
/// qu'il ne suffit pas : « personne n'ouvre /diagnostics après avoir écrit une règle qu'il croit
/// avoir posée ».
/// </para>
/// <para>
/// L'ordre affiché est l'ordre d'ÉVALUATION (<see cref="PermissionPolicy.Compose"/>) : overlay
/// d'abord, config machine ensuite, premier match gagnant. Le montrer autrement serait un rapport
/// juste sur le contenu et faux sur l'effet.
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

                // ⚠ L'état qui a motivé la commande passe EN PREMIER et il est nommé : le fichier
                // est là, l'utilisateur l'a écrit, et rien de ce qu'il contient ne s'applique.
                if (report.Unusable)
                    sb.Append('\n').Append(Strings.PermissionsOverlayUnusable);

                foreach (var r in report.Rules)
                    sb.Append("\n- `deny ").Append(r.Tool).Append(' ').Append(r.Pattern).Append('`');

                if (report.Rules.Count == 0 && !report.Unusable)
                    sb.Append('\n').Append(Strings.PermissionsOverlayEmpty);

                // Deux issues distinctes, parce qu'elles ne se réparent pas au même endroit : une
                // ligne malformée est un défaut à corriger, une règle `allow` écartée est le
                // contrat de l'overlay (deny-only) et ne demande rien à personne.
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

        // Même raison que pour l'overlay : une ligne écartée est une règle que l'utilisateur croit
        // avoir posée, et le réglage n'a pas de place pour le dire ligne par ligne.
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
            // Lire a échoué alors que le fichier est là : ne pas rendre `null`, qui se lirait
            // « aucun overlay » — la panne exacte que cette commande existe pour montrer.
            Diagnostics.Swallow("PermissionsCommandHandler.ReadOverlay", ex);
            return "{}";
        }
    }
}
