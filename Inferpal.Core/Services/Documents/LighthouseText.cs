using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Inferpal.Services.Documents;

/// <summary>
/// A Lighthouse report — its JSON (<c>--output=json</c>, Lighthouse CI's <c>lhr-*.json</c>, a PageSpeed Insights answer that
/// wraps it in <c>lighthouseResult</c>) or its HTML report, which carries the same JSON — read as what it found: the category
/// scores and the metrics, then the audits that did not pass, the largest estimated gain first, each with the elements or
/// resources it names. The file stays a text file: asked for lines, each is the text it holds (as <see cref="TestReportText"/>).
/// </summary>
/// <remarks>⚠ Passed, not applicable and manual audits are counted, never listed: the report is about what to fix. A report
/// whose run failed (<c>runtimeError</c>) says so first: its scores are not a measure of the page. Never throws for a file of
/// an unexpected shape; any other JSON or HTML reads as its lines (<c>null</c>).</remarks>
internal static class LighthouseText
{
    private const int MaxChars = 32_000_000;
    private const int ItemsPerAudit = 5;
    private const int SnippetChars = 100;
    private const string HtmlMark = "window.__LIGHTHOUSE_JSON__ = ";

    /// <summary>The report's findings, <paramref name="text"/> read from <paramref name="name"/>, within
    /// <paramref name="budget"/> characters; <c>null</c> for any other file.</summary>
    internal static string? Summarize(string name, string text, int budget, CancellationToken ct = default)
    {
        if (text.Length > MaxChars) return null;
        var lower = name.ToLowerInvariant();
        string json;
        if (lower.EndsWith(".json", StringComparison.Ordinal))
        {
            if (!text.Contains("\"lighthouseVersion\"", StringComparison.Ordinal)) return null;
            json = text;
        }
        else if (lower.EndsWith(".html", StringComparison.Ordinal) || lower.EndsWith(".htm", StringComparison.Ordinal))
        {
            // The HTML report embeds its JSON in one script, "<" escaped: the script's end is the first "</script>" after it.
            var start = text.IndexOf(HtmlMark, StringComparison.Ordinal);
            if (start < 0) return null;
            start += HtmlMark.Length;
            var end = text.IndexOf("</script>", start, StringComparison.OrdinalIgnoreCase);
            if (end < 0) return null;
            json = text[start..end].TrimEnd().TrimEnd(';');
        }
        else return null;
        try
        {
            if (JsonNode.Parse(json) is not JsonObject root) return null;
            var lhr = root["lighthouseResult"] as JsonObject ?? root;
            if (Str(lhr["lighthouseVersion"]).Length == 0 || lhr["audits"] is not JsonObject audits) return null;
            return Render(lhr, audits, root["loadingExperience"] as JsonObject, budget, ct);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException or JsonException
                                       or KeyNotFoundException or InvalidCastException)
        {
            Diagnostics.Swallow("LighthouseText.Summarize", ex);
            return null;
        }
    }

    private sealed record Failed(string Category, int CategoryOrder, double Weight, double Score, double GainMs, double GainBytes,
                                 string Text);

    private static string Render(JsonObject lhr, JsonObject audits, JsonObject? field, int budget, CancellationToken ct)
    {
        // Each audit's category, weight and group, from the categories that reference it (the first one that does).
        var refs = new Dictionary<string, (string Category, int Order, double Weight, string Group)>(StringComparer.Ordinal);
        var scores = new List<string>();
        var metrics = new List<string>();
        var order = 0;
        foreach (var (_, node) in lhr["categories"] as JsonObject ?? [])
        {
            if (node is not JsonObject category) continue;
            var title = Str(category["title"]);
            scores.Add($"{title} {(Num(category["score"]) is { } s ? Math.Round(s * 100, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture) : "n/a")}");
            foreach (var r in (category["auditRefs"] as JsonArray)?.OfType<JsonObject>() ?? [])
            {
                var id = Str(r["id"]);
                if (id.Length == 0 || refs.ContainsKey(id)) continue;
                refs[id] = (title, order, Num(r["weight"]) ?? 0, Str(r["group"]));
                if (Str(r["group"]) == "metrics" && audits[id] is JsonObject m && Str(m["displayValue"]) is { Length: > 0 } shown)
                    metrics.Add($"{Str(m["title"])} {shown}{(Num(m["score"]) is { } ms ? " (" + Rating(ms) + ")" : "")}");
            }
            order++;
        }
        // A report with insights also carries the older diagnostics they replace, one set behind a toggle: listing both
        // would name every problem twice. The insights are kept.
        var withInsights = refs.Values.Any(r => r.Group == "insights");

        var failed = new List<Failed>();
        var errored = new List<string>();
        int passed = 0, notApplicable = 0, manual = 0, informative = 0;
        foreach (var (id, node) in audits)
        {
            ct.ThrowIfCancellationRequested();
            // An audit no category references, or one in the hidden group (screenshots, traces), is not part of the report;
            // the metrics are on their own line.
            if (node is not JsonObject audit || !refs.TryGetValue(id, out var at)
                || at.Group is "hidden" or "metrics" || withInsights && at.Group == "diagnostics") continue;
            var mode = Str(audit["scoreDisplayMode"]);
            if (mode == "notApplicable") { notApplicable++; continue; }
            if (mode == "manual") { manual++; continue; }
            if (mode == "informative") { informative++; continue; }
            if (mode == "error")
            {
                errored.Add(Clip($"{Str(audit["title"])}{(Str(audit["errorMessage"]) is { Length: > 0 } e ? " (" + e + ")" : "")}", 200));
                continue;
            }
            if (mode is not ("binary" or "numeric" or "metricSavings") || Num(audit["score"]) is not { } score) continue;
            if (score >= 0.9) { passed++; continue; }
            var (gainMs, gainBytes) = Gain(audit);
            failed.Add(new Failed(at.Category, at.Order, at.Weight, score, gainMs, gainBytes, Audit(audit, at.Category, gainMs, gainBytes)));
        }

        var sb = new StringBuilder();
        var url = Str(lhr["finalDisplayedUrl"]) is { Length: > 0 } u ? u : Str(lhr["finalUrl"]) is { Length: > 0 } f ? f : Str(lhr["requestedUrl"]);
        var device = Str(lhr["configSettings"]?["formFactor"]);
        // A snapshot or a timespan measures part of a page's life, not its load: said, since its scores read differently.
        var gather = Str(lhr["gatherMode"]) is { Length: > 0 } g && g != "navigation" ? ", " + g + " (not a page load)" : "";
        sb.Append(CultureInfo.InvariantCulture,
            $"Lighthouse report (Lighthouse {Str(lhr["lighthouseVersion"])}) for {(url.Length > 0 ? url : "an unnamed page")}"
            + $"{(device.Length > 0 ? ", " + device : "")}{gather}{(Str(lhr["fetchTime"]) is { Length: > 0 } t ? ", " + t : "")}.\n");
        if (lhr["runtimeError"] is JsonObject runError && Str(runError["code"]) is { Length: > 0 } code)
            sb.Append(CultureInfo.InvariantCulture,
                $"⚠ The run failed ({code}: {Clip(Str(runError["message"]), 200)}): its scores do not measure the page.\n");
        foreach (var warning in ((lhr["runWarnings"] as JsonArray)?.Select(Str).Where(w => w.Length > 0) ?? []).Take(5))
            sb.Append("⚠ ").Append(Clip(warning, 200)).Append('\n');
        if (scores.Count > 0) sb.Append("Scores (out of 100): ").Append(string.Join(", ", scores)).Append(".\n");
        if (metrics.Count > 0) sb.Append("Metrics: ").Append(string.Join(", ", metrics)).Append(".\n");
        if (FieldData(field) is { } real) sb.Append(real);
        sb.Append(CultureInfo.InvariantCulture,
            $"{failed.Count} audit{(failed.Count == 1 ? "" : "s")} not passed; {passed} passed, {notApplicable} not applicable, "
            + $"{manual} to check by hand, {informative} informative (not scored, not listed)"
            + $"{(errored.Count > 0 ? $", {errored.Count} errored" : "")}.\n");
        if (withInsights) sb.Append("The older diagnostics the insights replace are left out.\n");

        // The largest time saved first, then bytes saved, then the rest by category and the weight its score carries.
        var ranked = failed
            .OrderByDescending(a => a.GainMs).ThenByDescending(a => a.GainBytes)
            .ThenBy(a => a.CategoryOrder).ThenByDescending(a => a.Weight).ThenBy(a => a.Score)
            .ToList();
        var tail = errored.Count == 0 ? ""
            : "\nAudits that could not run: " + string.Join("; ", errored.Take(10)) + (errored.Count > 10 ? "; …" : "") + ".\n";
        // Room kept for what follows the list: the audits that errored and the note that counts the cut.
        var room = budget - tail.Length - 120;
        var cut = 0;
        if (ranked.Count > 0) sb.Append("\nNot passed, largest estimated gain first:\n");
        foreach (var a in ranked)
        {
            if (cut > 0 || sb.Length + a.Text.Length > room) { cut++; continue; }
            sb.Append(a.Text);
        }
        sb.Append(tail);
        if (cut > 0)
            sb.Append(CultureInfo.InvariantCulture,
                $"\n[{cut} more audit{(cut == 1 ? "" : "s")} not passed, not shown: read_file with start_line pages the raw report]\n");
        return sb.ToString().TrimEnd();
    }

    /// <summary>The time and bytes an audit estimates it saves: its own figures, or the largest metric saving it names.</summary>
    private static (double Ms, double Bytes) Gain(JsonObject audit)
    {
        var details = audit["details"] as JsonObject;
        var ms = Num(details?["overallSavingsMs"]) ?? 0;
        if (ms <= 0)
            foreach (var (metric, value) in audit["metricSavings"] as JsonObject ?? [])
                if (metric != "CLS" && Num(value) is { } saved) ms = Math.Max(ms, saved);
        return (ms, Num(details?["overallSavingsBytes"]) ?? 0);
    }

    private static string Audit(JsonObject audit, string category, double gainMs, double gainBytes)
    {
        var gains = new List<string>();
        if (gainMs > 0) gains.Add($"saves ~{Ms(gainMs)}");
        if (gainBytes > 0) gains.Add($"saves ~{KiB(gainBytes)}");
        if (Num(audit["metricSavings"]?["CLS"]) is > 0 and var cls) gains.Add($"layout shift −{cls.ToString("0.###", CultureInfo.InvariantCulture)}");
        var shown = Str(audit["displayValue"]);
        var sb = new StringBuilder($"  [{category}] {Str(audit["title"])}");
        if (shown.Length > 0) sb.Append(" — ").Append(shown);
        if (gains.Count > 0) sb.Append(" (").Append(string.Join(", ", gains)).Append(')');
        sb.Append('\n');
        if (Str(audit["explanation"]) is { Length: > 0 } why) sb.Append("    ").Append(Clip(why, 200)).Append('\n');
        var rows = Items(audit["details"]).ToList();
        var items = rows.OfType<string>().ToList();
        foreach (var item in items.Take(ItemsPerAudit)) sb.Append("    ").Append(item).Append('\n');
        if (items.Count > ItemsPerAudit)
            sb.Append(CultureInfo.InvariantCulture, $"    [{items.Count - ItemsPerAudit} more]\n");
        // A row with nothing readable is still a row the audit names: counted, so "no element" is never inferred.
        if (rows.Count > items.Count)
            sb.Append(CultureInfo.InvariantCulture,
                $"    [{rows.Count - items.Count} row{(rows.Count - items.Count == 1 ? "" : "s")} of a shape not read here: read_file with start_line shows the raw report]\n");
        return sb.ToString();
    }

    /// <summary>A metric's score as the report colours it.</summary>
    private static string Rating(double score) => score >= 0.9 ? "good" : score >= 0.5 ? "needs improvement" : "poor";

    /// <summary>Each element or resource an audit's table names, with what it weighs or costs; a list's tables flattened;
    /// <c>null</c> for a row nothing could be read from, so it is still counted.</summary>
    private static IEnumerable<string?> Items(JsonNode? details)
    {
        if (details is not JsonObject d) yield break;
        if (Str(d["type"]) == "list")
        {
            foreach (var inner in (d["items"] as JsonArray)?.OfType<JsonObject>() ?? [])
                foreach (var line in Items(inner)) yield return line;
            yield break;
        }
        if (Str(d["type"]) is not ("table" or "opportunity")) yield break;
        var headings = (d["headings"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        foreach (var item in (d["items"] as JsonArray)?.OfType<JsonObject>() ?? [])
            yield return (headings.Count > 0 ? ByHeadings(item, headings) : Item(item)) is { Length: > 0 } line ? line : null;
    }

    /// <summary>A row as the report's columns show it, in their order: each heading's key read by its value type.</summary>
    private static string ByHeadings(JsonObject item, List<JsonObject> headings)
    {
        var parts = new List<string>();
        foreach (var heading in headings)
        {
            var key = Str(heading["key"]);
            if (key.Length == 0 || item[key] is not { } value) continue;
            var type = Str(heading["valueType"]) is { Length: > 0 } vt ? vt : Str(heading["itemType"]);
            var label = Str(heading["label"]) is { Length: > 0 } l ? l : key;
            var shown = Value(value, type, label);
            if (shown.Length > 0) parts.Add(shown);
        }
        return Clip(string.Join(" · ", parts), 300);
    }

    /// <summary>One cell: an element, a location, a link or a text as itself; a figure with its column's name and unit.</summary>
    private static string Value(JsonNode value, string type, string label)
    {
        if (value is JsonObject o)
            return Str(o["type"]) switch
            {
                "node" => Node(o),
                "source-location" => Location(o),
                "link" => Str(o["text"]) is { Length: > 0 } text ? $"{text} ({Str(o["url"])})" : Str(o["url"]),
                "url" or "code" or "text" => Str(o["value"]),
                "numeric" when Num(o["value"]) is { } n => $"{label} {Figure(n, type)}",
                _ => "",
            };
        if (Num(value) is { } number)
            return type is "bytes" or "ms" or "timespanMs" or "numeric" ? $"{label} {Figure(number, type)}" : "";
        var s = Str(value);
        return type is "thumbnail" ? "" : s;
    }

    private static string Figure(double n, string type) => type switch
    {
        "bytes" => KiB(n),
        "ms" or "timespanMs" => Ms(n),
        _ => n.ToString("0.###", CultureInfo.InvariantCulture),
    };

    private static string Node(JsonObject node)
    {
        var label = Str(node["selector"]);
        if (Str(node["snippet"]) is { Length: > 0 } snippet) label += (label.Length > 0 ? "  " : "") + Clip(snippet, SnippetChars);
        return label.Length > 0 ? label : Str(node["nodeLabel"]);
    }

    private static string Item(JsonObject item)
    {
        string label;
        if (item["node"] is JsonObject node) label = Node(node);
        else if (item["source"] is JsonObject source) label = Location(source);
        else if (Str(item["url"]) is { Length: > 0 } url) label = url;
        else if (Str(item["label"]) is { Length: > 0 } text) label = text;
        else if (item["entity"] is JsonNode entity) label = entity is JsonObject e ? Str(e["text"]) : Str(entity);
        else return "";
        var figures = new List<string>();
        if (Num(item["wastedMs"]) is > 0 and var ms) figures.Add($"{Ms(ms)} wasted");
        if (Num(item["wastedBytes"]) is > 0 and var wasted) figures.Add($"{KiB(wasted)} wasted");
        else if (Num(item["totalBytes"]) is > 0 and var total) figures.Add($"{KiB(total)} total");
        return Clip(label, 300) + (figures.Count > 0 ? " — " + string.Join(", ", figures) : "");
    }

    private static string Location(JsonObject source)
    {
        var url = Str(source["url"]);
        // Lighthouse counts lines and columns from 0.
        return Num(source["line"]) is { } line
            ? $"{url}:{(line + 1).ToString(CultureInfo.InvariantCulture)}" + (Num(source["column"]) is { } col ? $":{(col + 1).ToString(CultureInfo.InvariantCulture)}" : "")
            : url;
    }

    /// <summary>The real users' figures PageSpeed Insights adds (Chrome UX Report), when it has them.</summary>
    private static string? FieldData(JsonObject? field)
    {
        if (field?["metrics"] is not JsonObject metrics || metrics.Count == 0) return null;
        // The layout shift's percentile is the shift times 100; every other metric is in milliseconds.
        var parts = metrics.Select(p => p.Value is JsonObject m && Num(m["percentile"]) is { } value
                ? $"{p.Key} {(p.Key.Contains("LAYOUT_SHIFT", StringComparison.Ordinal)
                        ? (value / 100).ToString("0.###", CultureInfo.InvariantCulture)
                        : Ms(value))}{(Str(m["category"]) is { Length: > 0 } c ? " (" + c + ")" : "")}"
                : null)
            .OfType<string>().ToList();
        if (parts.Count == 0) return null;
        var overall = Str(field["overall_category"]);
        var origin = field["origin_fallback"] is JsonValue fb && fb.TryGetValue<bool>(out var whole) && whole
            ? " — for the whole site: this page has too few visits of its own" : "";
        return $"Real users (Chrome UX Report, 75th percentile){(overall.Length > 0 ? ", overall " + overall : "")}{origin}: "
             + $"{string.Join(", ", parts)}.\n";
    }

    private static string Ms(double ms) => ms >= 1000
        ? (ms / 1000).ToString("0.0", CultureInfo.InvariantCulture) + " s"
        : Math.Round(ms).ToString(CultureInfo.InvariantCulture) + " ms";

    private static string KiB(double bytes) => Math.Round(bytes / 1024).ToString(CultureInfo.InvariantCulture) + " KiB";

    private static string Clip(string text, int max)
    {
        var folded = OneLine.Unbroken(string.Join(' ', text.Split((char[])['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim());
        return folded.Length <= max ? folded : folded[..max] + "…";
    }

    private static double? Num(JsonNode? node) => node is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;

    private static string Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
}
