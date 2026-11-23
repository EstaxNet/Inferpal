using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Inferpal.Services.Documents;

/// <summary>
/// A static analysis report in SARIF (CodeQL, the Roslyn analyzers, ESLint, Semgrep and many others write it) read as what it
/// found: the tool, then its results by severity — errors first — each at its file, line and column with its rule and message,
/// then the rules met with their description and link. Version 2.x and 1.0 (what <c>dotnet build -p:ErrorLog</c> writes by
/// default). The file stays a text file: asked for lines, each is the text it holds (as <see cref="TestReportText"/>).
/// </summary>
/// <remarks>
/// ⚠ A result suppressed and in effect (in the code, a baseline accepted) is counted, never listed: a model fixing "every
/// warning" would otherwise undo what the team chose to keep; a suppression rejected or under review is no suppression. A
/// result "absent" from a baseline comparison was FIXED: counted as such, never as kept. ⚠ A result whose kind is not a
/// failure (a pass, not applicable) is no finding unless it says a level. ⚠ Every list is bounded by the budget, and what it
/// leaves out is counted. ⚠ Never throws for a file of an unexpected shape: it is read as its lines (<c>null</c>).
/// </remarks>
internal static class SarifText
{
    private const int MaxChars = DocumentText.ReportMaxChars;

    /// <summary>How long a message may be in one line.</summary>
    private const int MessageChars = 300;

    /// <summary>A SARIF report's results, <paramref name="text"/> read from <paramref name="name"/>, within
    /// <paramref name="budget"/> characters; <c>null</c> for any other file.</summary>
    internal static string? Summarize(string name, string text, int budget, CancellationToken ct = default)
    {
        if (text.Length > MaxChars) return null;
        var lower = name.ToLowerInvariant();
        var declared = lower.EndsWith(".sarif", StringComparison.Ordinal) || lower.EndsWith(".sarif.json", StringComparison.Ordinal);
        if (!declared && !lower.EndsWith(".json", StringComparison.Ordinal)) return null;
        // A .json is looked at only when its head names runs: most JSON files of a workspace are not reports.
        if (!declared && !(text.Length > 4096 ? text[..4096] : text).Contains("\"runs\"", StringComparison.Ordinal)) return null;
        try
        {
            if (JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip })
                is not JsonObject root || root["runs"] is not JsonArray runs)
                return null;
            // A .json is SARIF by its own shape: a version 1.x or 2.x, and runs that name their tool.
            var version = Str(root["version"]);
            if (!declared && !(version.StartsWith("2.", StringComparison.Ordinal) || version.StartsWith("1.", StringComparison.Ordinal)))
                return null;
            if (!declared && !runs.OfType<JsonObject>().Any(r => r["tool"] is JsonObject)) return null;
            return Render(name, runs, version.StartsWith("1.", StringComparison.Ordinal), budget, ct);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException or JsonException
                                       or KeyNotFoundException or InvalidCastException or IndexOutOfRangeException)
        {
            Diagnostics.Swallow("SarifText.Summarize", ex);
            return null;
        }
    }

    private sealed record Result(int Rank, string Level, string Rule, string File, int Line, string Where, string Message);

    private sealed record Rule(string Id, string Description, string Link, string Level, JsonObject? Messages);

    private static string Render(string name, JsonArray runs, bool v1, int budget, CancellationToken ct)
    {
        var tools = new List<string>();
        var results = new List<Result>();
        var described = new Dictionary<string, Rule>(StringComparer.Ordinal);
        int suppressed = 0, fixedSince = 0, notFindings = 0;
        foreach (var run in runs.OfType<JsonObject>())
        {
            ct.ThrowIfCancellationRequested();
            tools.Add(ToolName(run, v1));
            // Every component's rules — the driver's, and each extension's (a CodeQL query pack holds its own) — by position
            // and by id: a result names its rule either way.
            var components = new List<List<Rule>>();
            var byId = new Dictionary<string, Rule>(StringComparer.Ordinal);
            if (v1)
            {
                var list = new List<Rule>();
                if (run["rules"] is JsonObject dict)
                    foreach (var (id, node) in dict)
                        if (node is JsonObject r) list.Add(new(id, Str(r["shortDescription"]) is { Length: > 0 } d ? d : Str(r["fullDescription"]),
                                                               Str(r["helpUri"]), Str(r["defaultLevel"]), null));
                components.Add(list);
            }
            else
            {
                components.Add(RulesOf(run["tool"]?["driver"]));
                foreach (var extension in (run["tool"]?["extensions"] as JsonArray)?.OfType<JsonObject>() ?? [])
                    components.Add(RulesOf(extension));
            }
            foreach (var rule in components.SelectMany(c => c)) { byId.TryAdd(rule.Id, rule); described.TryAdd(rule.Id, rule); }
            var bases = BasesOf(run["originalUriBaseIds"] as JsonObject);

            foreach (var r in (run["results"] as JsonArray)?.OfType<JsonObject>() ?? [])
            {
                var rule = RuleOf(r, components, byId);
                var ruleId = Str(r["ruleId"]) is { Length: > 0 } rid ? rid : Str(r["rule"]?["id"]) is { Length: > 0 } nested ? nested : rule?.Id ?? "";
                if (rule is null && ruleId.Length > 0) byId.TryGetValue(ruleId, out rule);
                // A result that is no failure (a pass, not applicable, informational) is level "none" unless it says one.
                var kind = Str(r["kind"]);
                var level = Str(r["level"]) is { Length: > 0 } l ? l
                          : kind.Length > 0 && kind != "fail" ? "none"
                          : rule?.Level is { Length: > 0 } dl ? dl : "warning";
                if (Str(r["baselineState"]) == "absent") { fixedSince++; continue; }
                if (InEffectSuppression(r, v1)) { suppressed++; continue; }
                if (kind.Length > 0 && kind != "fail" && Str(r["level"]).Length == 0) { notFindings++; continue; }
                var (file, line, where) = Location(r, v1, bases);
                results.Add(new(Rank(level), level, ruleId, file, line, where, Clip(Message(r, rule, v1))));
            }
        }

        // Errors first; then by file, and in a file by line NUMBER (10 after 8, never before it).
        var live = results.OrderBy(r => r.Rank).ThenBy(r => r.File, StringComparer.Ordinal).ThenBy(r => r.Line).ToList();
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"SARIF report {name}: {string.Join(", ", tools.Distinct())}. ");
        sb.Append(CultureInfo.InvariantCulture, $"{live.Count} result{(live.Count == 1 ? "" : "s")}");
        var counts = live.GroupBy(r => r.Rank).OrderBy(g => g.Key).Select(g => $"{g.Count()} {RankName(g.Key, g.Count())}").ToList();
        if (counts.Count > 0) sb.Append(" (").Append(string.Join(", ", counts)).Append(')');
        if (suppressed > 0) sb.Append(CultureInfo.InvariantCulture, $"; {suppressed} suppressed in the code or a baseline, not listed: the team kept them");
        if (fixedSince > 0) sb.Append(CultureInfo.InvariantCulture, $"; {fixedSince} fixed since the baseline");
        if (notFindings > 0) sb.Append(CultureInfo.InvariantCulture, $"; {notFindings} checks that passed or did not apply, not listed");
        sb.Append(".\n");
        if (live.Count == 0) return sb.Append("Nothing to fix.").ToString();

        var shown = 0;
        int? rankShown = null;
        foreach (var r in live)
        {
            var line = $"  {r.Where}  {r.Rule}  {r.Message}\n";
            var heading = r.Rank != rankShown ? $"\n{char.ToUpperInvariant(RankName(r.Rank, 2)[0])}{RankName(r.Rank, 2)[1..]}:\n" : "";
            if (sb.Length + heading.Length + line.Length > budget * 3 / 4) break;
            sb.Append(heading).Append(line);
            rankShown = r.Rank;
            shown++;
        }
        if (shown < live.Count)
            sb.Append(CultureInfo.InvariantCulture,
                $"\n[{live.Count - shown} more result{(live.Count - shown == 1 ? "" : "s")} not shown: read_file with start_line pages the raw report]\n");

        var met = live.Take(shown).Select(r => r.Rule).Where(id => id.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (met.Count > 0)
        {
            sb.Append("\nRules:\n");
            var left = 0;
            foreach (var id in met)
            {
                var line = described.TryGetValue(id, out var rule) && rule.Description.Length > 0
                    ? $"  {id}: {Clip(rule.Description)}{(rule.Link.Length > 0 ? " (" + rule.Link + ")" : "")}\n"
                    : $"  {id}\n";
                if (sb.Length + line.Length > budget) { left = met.Count - met.IndexOf(id); break; }
                sb.Append(line);
            }
            if (left > 0) sb.Append(CultureInfo.InvariantCulture, $"  [{left} more rule{(left == 1 ? "" : "s")} not described]\n");
        }
        return sb.ToString().TrimEnd();
    }

    private static string ToolName(JsonObject run, bool v1)
    {
        var tool = v1 ? run["tool"] : run["tool"]?["driver"];
        var name = Str(tool?["name"]) is { Length: > 0 } n ? n : "an unnamed tool";
        var version = Str(tool?["semanticVersion"]) is { Length: > 0 } semantic ? semantic : Str(tool?["version"]);
        return version.Length > 0 ? name + " " + version : name;
    }

    private static List<Rule> RulesOf(JsonNode? component) =>
        (component?["rules"] as JsonArray)?.OfType<JsonObject>()
            .Select(r => new Rule(Str(r["id"]),
                                  Str(r["shortDescription"]?["text"]) is { Length: > 0 } d ? d : Str(r["fullDescription"]?["text"]),
                                  Str(r["helpUri"]), Str(r["defaultConfiguration"]?["level"]), r["messageStrings"] as JsonObject))
            .ToList() ?? [];

    /// <summary>A result's rule: by its index in the component it names (the driver when none), else by its id.</summary>
    private static Rule? RuleOf(JsonObject r, List<List<Rule>> components, Dictionary<string, Rule> byId)
    {
        var component = Index(r["rule"]?["toolComponent"]?["index"]) is int c && c >= 0 && c + 1 < components.Count ? components[c + 1] : components[0];
        var index = Index(r["ruleIndex"]) ?? Index(r["rule"]?["index"]);
        if (index is int i && i >= 0 && i < component.Count) return component[i];
        var id = Str(r["ruleId"]) is { Length: > 0 } rid ? rid : Str(r["rule"]?["id"]);
        return id.Length > 0 && byId.TryGetValue(id, out var rule) ? rule : null;
    }

    /// <summary>Whether a suppression is in effect: one accepted, or without a status (the default); never one rejected or
    /// under review. Version 1.0: <c>suppressionStates</c>.</summary>
    private static bool InEffectSuppression(JsonObject r, bool v1) =>
        v1
            ? r["suppressionStates"] is JsonArray states && states.Count > 0
            : r["suppressions"] is JsonArray s && s.OfType<JsonObject>().Any(x => Str(x["status"]) is "" or "accepted");

    /// <summary>The message, its <c>{0}</c> filled from its arguments, its links to related locations given their place.</summary>
    private static string Message(JsonObject r, Rule? rule, bool v1)
    {
        if (v1) return Str(r["message"]);
        var text = Str(r["message"]?["text"]);
        if (text.Length == 0 && Str(r["message"]?["id"]) is { Length: > 0 } id) text = Str(rule?.Messages?[id]?["text"]);
        if (r["message"]?["arguments"] is JsonArray args)
            for (var i = 0; i < args.Count; i++) text = text.Replace("{" + i.ToString(CultureInfo.InvariantCulture) + "}", Str(args[i]), StringComparison.Ordinal);
        var related = (r["relatedLocations"] as JsonArray)?.OfType<JsonObject>()
            .Where(l => Index(l["id"]) is not null)
            .ToDictionary(l => Index(l["id"])!.Value, l => Located(l["physicalLocation"])) ?? [];
        return related.Count == 0 ? text : EmbeddedLink.Replace(text, m =>
            int.TryParse(m.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && related.TryGetValue(n, out var at) && at.Length > 0
                ? $"{m.Groups[1].Value} ({at})" : m.Groups[1].Value);
    }

    private static readonly Regex EmbeddedLink = new(@"\[([^\]]*)\]\((\d+)\)", RegexOptions.CultureInvariant, RegexBudget.Default);

    /// <summary>Where a result is: its file, line and the text shown; a symbol's name when it names no file.</summary>
    private static (string File, int Line, string Where) Location(JsonObject r, bool v1, Dictionary<string, string> bases)
    {
        var first = (r["locations"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault();
        var physical = v1 ? first?["resultFile"] : first?["physicalLocation"];
        var uri = Path(physical?["artifactLocation"] ?? physical, bases);
        var region = v1 ? physical?["region"] : physical?["region"];
        var line = Index(region?["startLine"]) ?? 0;
        if (uri.Length == 0)
        {
            var symbol = Str((first?["logicalLocations"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault()?["fullyQualifiedName"])
                         is { Length: > 0 } fq ? fq : Str(first?["fullyQualifiedLogicalName"]);
            return ("~", 0, symbol.Length > 0 ? symbol : "(no location)");
        }
        var where = line > 0
            ? Index(region?["startColumn"]) is int col ? $"{uri}:{line}:{col}" : $"{uri}:{line}"
            : uri;
        return (uri, line, where);
    }

    private static string Located(JsonNode? physical)
    {
        var uri = Path(physical?["artifactLocation"], []);
        return uri.Length == 0 ? "" : Index(physical?["region"]?["startLine"]) is int l ? $"{uri}:{l}" : uri;
    }

    /// <summary>An artifact's path: a <c>file:</c> address as its local path (unescaped once), a relative one resolved against
    /// its base when the report says where that base is.</summary>
    private static string Path(JsonNode? artifact, Dictionary<string, string> bases)
    {
        var uri = Str(artifact?["uri"]);
        if (uri.Length == 0) return "";
        if (Str(artifact?["uriBaseId"]) is { Length: > 0 } baseId && bases.TryGetValue(baseId, out var root)
            && !Uri.TryCreate(uri, UriKind.Absolute, out _))
            uri = root.TrimEnd('/') + "/" + uri.TrimStart('/');
        if (uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(uri, UriKind.Absolute, out var file))
            return file.LocalPath;
        return Uri.UnescapeDataString(uri);
    }

    private static Dictionary<string, string> BasesOf(JsonObject? bases)
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (id, node) in bases ?? [])
            if (Str(node?["uri"]) is { Length: > 0 } uri) found[id] = uri;
        return found;
    }

    /// <summary>Errors first, then warnings, notes, and everything else together.</summary>
    private static int Rank(string level) => level switch { "error" => 0, "warning" => 1, "note" => 2, _ => 3 };

    private static string RankName(int rank, int count) => rank switch
    {
        0 => count == 1 ? "error" : "errors",
        1 => count == 1 ? "warning" : "warnings",
        2 => count == 1 ? "note" : "notes",
        _ => "other",
    };

    private static string Clip(string text)
    {
        var one = string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return one.Length <= MessageChars ? one : one[..MessageChars] + "…";
    }

    private static string Str(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    private static int? Index(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;
}
