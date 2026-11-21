using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Inferpal.Services.Documents;

/// <summary>
/// A Terraform (or OpenTofu) plan as <c>terraform show -json</c> writes it, read as what it will do: what is destroyed or
/// replaced first, then forgotten, created, imported, changed, moved and read, each resource with the attributes that
/// change, then the outputs. The file stays a text file: asked for lines, each is the text it holds (as
/// <see cref="TestReportText"/>).
/// </summary>
/// <remarks>
/// ⚠ A plan's JSON holds sensitive values IN CLEAR: a value is written <c>(sensitive)</c> on BOTH sides when either side's
/// mark (<c>before_sensitive</c>/<c>after_sensitive</c>, or any mark under it) says so — as Terraform's own renderer does: a
/// value that becomes sensitive still has its old value in the state. Outputs alike; <c>prior_state</c>,
/// <c>planned_values</c> and <c>variables</c>, which repeat them, are never read. ⚠ A state file (<c>values</c>, no
/// <c>planned_values</c>) and any other JSON read as their lines (<c>null</c>). Never throws for a file of an unexpected shape.
/// </remarks>
internal static class TerraformPlanText
{
    private const int MaxChars = 16_000_000;
    private const int ValueChars = 120;
    private const int AttributesPerResource = 20;

    /// <summary>The plan's changes, <paramref name="text"/> read from <paramref name="name"/>, within <paramref name="budget"/>
    /// characters; <c>null</c> for any other file.</summary>
    internal static string? Summarize(string name, string text, int budget, CancellationToken ct = default)
    {
        if (text.Length > MaxChars || !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return null;
        // Terraform omits an empty resource_changes or output_changes: a plan with no change names only its planned values.
        string[] marks = ["\"planned_values\"", "\"resource_changes\"", "\"output_changes\"", "\"deferred_changes\""];
        if (!marks.Any(m => text.Contains(m, StringComparison.Ordinal))) return null;
        try
        {
            if (JsonNode.Parse(text) is not JsonObject root || Str(root["format_version"]).Length == 0
                || !(root["planned_values"] is JsonObject || root["resource_changes"] is JsonArray
                     || root["output_changes"] is JsonObject || root["deferred_changes"] is JsonArray))
                return null;
            return Render(root, budget, ct);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException or JsonException
                                       or KeyNotFoundException or InvalidCastException)
        {
            Diagnostics.Swallow("TerraformPlanText.Summarize", ex);
            return null;
        }
    }

    private enum Kind { Destroy, Replace, Forget, Create, Import, Change, Move, Read, Other }

    private static readonly (Kind Kind, string Title, string Count)[] Sections =
    [
        (Kind.Destroy, "Destroy", "to destroy"),
        (Kind.Replace, "Replace (destroyed, then created)", "to replace"),
        (Kind.Forget, "Forget (removed from the state, NOT destroyed)", "to forget"),
        (Kind.Create, "Create", "to create"),
        (Kind.Import, "Import", "to import"),
        (Kind.Change, "Change in place", "to change"),
        (Kind.Move, "Move (new address, no change)", "to move"),
        (Kind.Read, "Read (data sources)", "data sources to read"),
        (Kind.Other, "Other actions", "other"),
    ];

    private static string Render(JsonObject root, int budget, CancellationToken ct)
    {
        var groups = Enum.GetValues<Kind>().ToDictionary(k => k, _ => new List<string>());
        var unchanged = 0;
        foreach (var rc in (root["resource_changes"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            ct.ThrowIfCancellationRequested();
            var actions = Actions(rc["change"]?["actions"]);
            var importing = rc["change"]?["importing"] is JsonObject;
            var moved = Str(rc["previous_address"]).Length > 0;
            Kind? kind = actions switch
            {
                ["delete"] => Kind.Destroy,
                ["delete", "create"] or ["create", "delete"] => Kind.Replace,
                ["forget"] => Kind.Forget,
                ["create"] => Kind.Create,
                ["update"] => importing ? Kind.Import : Kind.Change,
                ["read"] => Kind.Read,
                ["no-op"] or [] => importing ? Kind.Import : moved ? Kind.Move : null,
                _ => Kind.Other,
            };
            if (kind is null) { unchanged++; continue; }
            groups[kind.Value].Add(Resource(rc, kind.Value, actions));
        }

        var outputs = new List<string>();
        foreach (var (key, node) in root["output_changes"] as JsonObject ?? [])
        {
            if (node is not JsonObject oc) continue;
            var actions = Actions(oc["actions"]);
            if (actions is ["no-op"] or []) continue;
            var sensitive = AnyTrue(oc["before_sensitive"]) || AnyTrue(oc["after_sensitive"]);
            outputs.Add($"  {key}: {(actions is ["delete"] ? "(removed)" : Shown(oc["after"], sensitive, oc["after_unknown"]))}\n");
        }

        var deferred = (root["deferred_changes"] as JsonArray)?.OfType<JsonObject>()
            .Select(d => $"  {Str(d["resource_change"]?["address"])}{(Str(d["reason"]) is { Length: > 0 } r ? "  (" + r + ")" : "")}\n")
            .ToList() ?? [];
        var drift = (root["resource_drift"] as JsonArray)?.OfType<JsonObject>().Select(d => Str(d["address"])).ToList() ?? [];
        var tool = Str(root["terraform_version"]);
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"Terraform plan{(tool.Length > 0 ? " (Terraform " + tool + ")" : "")}: ");
        sb.Append(string.Join(", ", Sections
            .Where(s => s.Kind is Kind.Destroy or Kind.Replace or Kind.Create or Kind.Change || groups[s.Kind].Count > 0)
            .Select(s => $"{groups[s.Kind].Count} {s.Count}")));
        if (unchanged > 0) sb.Append(CultureInfo.InvariantCulture, $"; {unchanged} unchanged");
        sb.Append(". Sensitive values are written (sensitive).\n");
        if (IsTrue(root["errored"]))
            sb.Append("⚠ The plan errored: it is incomplete, and applying it is not possible.\n");
        if (root["complete"] is JsonValue c && c.TryGetValue<bool>(out var complete) && !complete || deferred.Count > 0)
            sb.Append(CultureInfo.InvariantCulture,
                $"⚠ The plan is incomplete: {deferred.Count} change{(deferred.Count == 1 ? "" : "s")} deferred to a later plan, applying it leaves work to plan again.\n");
        if (drift.Count > 0)
            sb.Append(CultureInfo.InvariantCulture,
                $"{drift.Count} resource{(drift.Count == 1 ? "" : "s")} changed outside Terraform since the last apply: {string.Join(", ", drift.Take(10))}{(drift.Count > 10 ? ", …" : "")}.\n");

        var cut = 0;
        void Section(string title, List<string> items)
        {
            if (items.Count == 0) return;
            var heading = $"\n{title}:\n";
            var shownHeading = false;
            foreach (var item in items)
            {
                if (cut > 0 || sb.Length + heading.Length + item.Length > budget * 9 / 10) { cut++; continue; }
                if (!shownHeading) { sb.Append(heading); shownHeading = true; }
                sb.Append(item);
            }
        }
        foreach (var (kind, title, _) in Sections) Section(title, groups[kind]);
        Section("Deferred", deferred);
        Section("Outputs", outputs);
        if (cut > 0)
            sb.Append(CultureInfo.InvariantCulture,
                $"\n[{cut} more change{(cut == 1 ? "" : "s")} not shown: read_file with start_line pages the raw JSON, which holds sensitive values in clear]\n");
        if (groups.Values.All(g => g.Count == 0) && outputs.Count == 0 && deferred.Count == 0) sb.Append("No changes.");
        return sb.ToString().TrimEnd();
    }

    private static string Resource(JsonObject rc, Kind kind, List<string> actions)
    {
        var change = rc["change"] as JsonObject;
        var notes = new List<string>();
        if (Str(rc["action_reason"]) is { Length: > 0 } reason) notes.Add(reason);
        if (Str(rc["previous_address"]) is { Length: > 0 } from) notes.Add("moved from " + from);
        if (change?["importing"] is JsonObject imp) notes.Add("import" + (Str(imp["id"]) is { Length: > 0 } id ? " " + id : ""));
        if (kind == Kind.Other) notes.Add("actions: " + string.Join(", ", actions));
        var sb = new StringBuilder($"  {Str(rc["address"])}{(notes.Count > 0 ? "  (" + string.Join("; ", notes) + ")" : "")}\n");
        if (kind is Kind.Destroy or Kind.Read or Kind.Forget || change is null) return sb.ToString();
        var lines = new List<string>();
        if (kind == Kind.Create)
        {
            // Every attribute it will have: set ones, then those known only after apply.
            var after = change["after"] as JsonObject;
            foreach (var (key, value) in after ?? [])
                if (value is not null) lines.Add($"{key} = {Shown(value, AnyTrue(change["after_sensitive"]?[key]), change["after_unknown"]?[key])}");
            foreach (var (key, unknown) in change["after_unknown"] as JsonObject ?? [])
                if (IsTrue(unknown) && after?[key] is null) lines.Add($"{key} = {Shown(null, AnyTrue(change["after_sensitive"]?[key]), unknown)}");
        }
        else
        {
            var forcing = ReplacePaths(change["replace_paths"]);
            Diff(change["before"], change["after"], change["before_sensitive"], change["after_sensitive"], change["after_unknown"], "", forcing, lines);
        }
        foreach (var line in lines.Take(AttributesPerResource)) sb.Append("    ").Append(line).Append('\n');
        if (lines.Count > AttributesPerResource)
            sb.Append(CultureInfo.InvariantCulture, $"    [{lines.Count - AttributesPerResource} more attributes]\n");
        return sb.ToString();
    }

    /// <summary>Every leaf that differs between <paramref name="before"/> and <paramref name="after"/>, as
    /// <c>path: before → after</c>; the sensitive and unknown marks walked alongside.</summary>
    private static void Diff(JsonNode? before, JsonNode? after, JsonNode? bs, JsonNode? asens, JsonNode? au, string path,
                             List<string> forcing, List<string> lines)
    {
        if (lines.Count > AttributesPerResource * 4) return;
        // Either side's mark hides both: a value that becomes sensitive (or stops being) has the other in clear.
        var sensitive = AnyTrue(bs) || AnyTrue(asens);
        if (IsTrue(au))
        {
            lines.Add($"{Label(path)}: {Shown(before, sensitive, null)} → {Shown(null, sensitive, au)}{Forces(path, forcing)}");
            return;
        }
        // Recurse where both sides are containers of the same kind and nothing marks the whole node sensitive.
        if (!IsTrue(bs) && !IsTrue(asens))
        {
            if (before is JsonObject bo && after is JsonObject ao)
            {
                foreach (var key in bo.Select(p => p.Key).Union(ao.Select(p => p.Key)))
                    Diff(bo[key], ao[key], (bs as JsonObject)?[key], (asens as JsonObject)?[key], (au as JsonObject)?[key],
                         Join(path, key), forcing, lines);
                return;
            }
            if (before is JsonArray ba && after is JsonArray aa && ba.Count == aa.Count)
            {
                for (var i = 0; i < ba.Count; i++)
                    Diff(ba[i], aa[i], (bs as JsonArray)?.ElementAtOrDefault(i), (asens as JsonArray)?.ElementAtOrDefault(i),
                         (au as JsonArray)?.ElementAtOrDefault(i), Join(path, i.ToString(CultureInfo.InvariantCulture)), forcing, lines);
                return;
            }
        }
        if (JsonNode.DeepEquals(before, after)) return;
        lines.Add($"{Label(path)}: {Shown(before, sensitive, null)} → {Shown(after, sensitive, au)}{Forces(path, forcing)}");
    }

    /// <summary>A value as shown: <c>(sensitive)</c> when <paramref name="sensitive"/>; <c>(known after apply)</c> when
    /// unknown; else its JSON, clipped.</summary>
    private static string Shown(JsonNode? value, bool sensitive, JsonNode? unknown)
    {
        if (sensitive) return "(sensitive)";
        if (IsTrue(unknown)) return "(known after apply)";
        if (value is null) return "null";
        var json = value.ToJsonString();
        if (AnyTrue(unknown)) json += " (parts known after apply)";
        return json.Length <= ValueChars ? json : json[..ValueChars] + "…";
    }

    /// <summary>The mark of a line that a replace path forces: the path itself, one under it, or one above it (a list whose
    /// length changed is printed whole).</summary>
    private static string Forces(string path, List<string> forcing) =>
        forcing.Any(f => f == path || path.Length == 0 || path.StartsWith(f + ".", StringComparison.Ordinal)
                         || f.StartsWith(path + ".", StringComparison.Ordinal))
            ? "  (forces replacement)" : "";

    private static List<string> ReplacePaths(JsonNode? paths) =>
        (paths as JsonArray)?.OfType<JsonArray>()
            .Select(p => string.Join(".", p.Select(step => step is JsonValue v && v.TryGetValue<int>(out var i)
                ? i.ToString(CultureInfo.InvariantCulture) : Str(step))))
            .ToList() ?? [];

    private static List<string> Actions(JsonNode? actions) => (actions as JsonArray)?.Select(Str).ToList() ?? [];

    private static string Join(string path, string step) => path.Length == 0 ? step : path + "." + step;

    private static string Label(string path) => path.Length == 0 ? "(whole value)" : path;

    private static bool IsTrue(JsonNode? node) => node is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    private static bool AnyTrue(JsonNode? node) => node switch
    {
        JsonValue => IsTrue(node),
        JsonObject o => o.Any(p => AnyTrue(p.Value)),
        JsonArray a => a.Any(AnyTrue),
        _ => false,
    };

    private static string Str(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
}
