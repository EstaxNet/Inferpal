using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;

namespace Inferpal.Services.Documents;

/// <summary>
/// A query plan read as where its time goes: the costliest operators first (by what the run measured when the plan holds it,
/// else by the planner's estimate), then what is worth a look — full scans of large tables, estimates far from what the run
/// read, the engine's own warnings and missing-index suggestions — then the plan as a tree. SQL Server's <c>.sqlplan</c>,
/// PostgreSQL's <c>EXPLAIN (FORMAT JSON)</c> (with <c>ANALYZE</c> or not), MySQL's and MariaDB's <c>EXPLAIN FORMAT=JSON</c>
/// (and MariaDB's <c>ANALYZE FORMAT=JSON</c>); the plan <c>query_database</c> reads through <see cref="FromQueryResult"/>.
/// The file stays a text file: asked for lines, each is the text it holds.
/// </summary>
/// <remarks>
/// ⚠ An operator's cost and time in a plan include its children's: ranked by its OWN share (its total less its children's),
/// else the root is always "the costliest". What "its children's" means differs by engine and mode, each handled where read:
/// PostgreSQL's inner side of a loop is costed per loop, a parallel worker's time is not wall time, a CTE's body is timed
/// inside its scan; SQL Server's batch-mode operators are timed on their own. Never throws for a file of an unexpected
/// shape: it is read as its lines.
/// </remarks>
internal static class SqlPlanText
{
    private const int MaxChars = 16_000_000;
    private const int Ranked = 8;
    private const int TreeLines = 80;
    private const int LookLines = 15;
    private const int NoteChars = 160;

    /// <summary>A full scan is named from this many rows read: below it, scanning beats an index.</summary>
    private const double LargeScan = 1000;

    /// <summary>In an estimated plan, which says no row read, a full scan is named when it is this share of the plan's cost.</summary>
    private const double CostlyShare = 0.1;

    /// <summary>An estimate is named when it is off by this factor, on at least <see cref="EstimateFloor"/> rows.</summary>
    private const double EstimateFactor = 10;
    private const double EstimateFloor = 100;

    private sealed record Op(string Label, int Depth, double Total, double Self, double? Est, double? Act, double? SelfMs, string Note);

    private sealed record Plan(string Engine, bool Measured, double? TotalCost, List<Op> Ops, List<string> Look, string Head);

    /// <summary>The plan, <paramref name="text"/> read from <paramref name="name"/>, within <paramref name="budget"/>
    /// characters; <c>null</c> for any other file.</summary>
    internal static string? Summarize(string name, string text, int budget, CancellationToken ct = default)
    {
        if (text.Length > MaxChars) return null;
        var lower = name.ToLowerInvariant();
        if (!lower.EndsWith(".sqlplan", StringComparison.Ordinal) && !lower.EndsWith(".json", StringComparison.Ordinal)) return null;
        var plan = Read(text, lower.EndsWith(".sqlplan", StringComparison.Ordinal));
        ct.ThrowIfCancellationRequested();
        const string Raw = "\n[The raw plan: read_file with start_line.]";
        return plan is null ? null : Render($"Query plan {name}", plan, budget - Raw.Length) + Raw;
    }

    /// <summary>A query's one cell holding a plan (PostgreSQL's <c>EXPLAIN (FORMAT JSON)</c>, MySQL's and MariaDB's
    /// <c>EXPLAIN FORMAT=JSON</c>), read as one; <c>null</c> for any other result.</summary>
    internal static string? FromQueryResult(string cell, int budget) =>
        Read(cell, xml: false) is { } plan ? Render("Query plan", plan, budget) : null;

    private static Plan? Read(string text, bool xml)
    {
        try
        {
            if (xml) return SqlServer(text);
            var trimmed = text.TrimStart();
            if (trimmed.StartsWith('['))
                return trimmed.Contains("\"Plan\"", StringComparison.Ordinal) && JsonNode.Parse(text) is JsonArray a ? Postgres(a) : null;
            return trimmed.StartsWith('{') && trimmed.Contains("\"query_block\"", StringComparison.Ordinal) && JsonNode.Parse(text) is JsonObject o
                ? MySql(o) : null;
        }
        catch (Exception ex) when (ex is JsonException or XmlException or InvalidOperationException or ArgumentException
                                       or FormatException or InvalidCastException or KeyNotFoundException)
        {
            Diagnostics.Swallow("SqlPlanText.Read", ex);
            return null;
        }
    }

    // ── PostgreSQL ──────────────────────────────────────────────────────────────────────────────────────────────────

    private sealed class PgWalk
    {
        public readonly List<Op> Ops = [];
        public readonly List<string> Look = [];
        /// <summary>Each materialized CTE's measured time: its scan's time includes it, charged once.</summary>
        public readonly Dictionary<string, double> CteMs = new(StringComparer.Ordinal);
        public double PlanCost;
    }

    private static Plan? Postgres(JsonArray root)
    {
        if (root.FirstOrDefault() is not JsonObject top || top["Plan"] is not JsonObject plan) return null;
        var walk = new PgWalk { PlanCost = Num(plan["Total Cost"]) ?? 0 };
        CollectCtes(plan, walk);
        PgNode(plan, 0, parallel: false, times: 1, walk);
        var head = new List<string>();
        if (Num(top["Planning Time"]) is double p) head.Add($"planning {Ms(p)}");
        if (Num(top["Execution Time"]) is double e) head.Add($"execution {Ms(e)}");
        return new Plan("PostgreSQL", plan["Actual Rows"] is not null, Num(plan["Total Cost"]), walk.Ops, walk.Look, string.Join(", ", head));
    }

    private static void CollectCtes(JsonObject node, PgWalk walk)
    {
        foreach (var child in (node["Plans"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            if (Str(child["Subplan Name"]) is { } sub && sub.StartsWith("CTE ", StringComparison.Ordinal) && Inclusive(child, false) is double ms)
                walk.CteMs[sub[4..]] = ms;
            CollectCtes(child, walk);
        }
    }

    /// <summary>A node's measured time, its children's included: per loop times loops — except inside a parallel part, where
    /// each process's time runs alongside the others' (wall time, not their sum).</summary>
    private static double? Inclusive(JsonObject node, bool parallel) =>
        Num(node["Actual Total Time"]) is double t ? t * (parallel ? 1 : Num(node["Actual Loops"]) ?? 1) : null;

    /// <param name="times">How many times the planner's per-execution cost of this node runs (a loop's inner side, a
    /// SubPlan), through every loop above it.</param>
    private static double? PgNode(JsonObject node, int depth, bool parallel, double times, PgWalk walk)
    {
        var type = Str(node["Node Type"]);
        var label = type;
        if (Str(node["Join Type"]) is { Length: > 0 } join && !label.Contains(join, StringComparison.Ordinal)) label += $" ({join})";
        if (Str(node["Relation Name"]) is { Length: > 0 } rel)
            label += " on " + rel + (Str(node["Alias"]) is { Length: > 0 } alias && alias != rel ? " " + alias : "");
        if (Str(node["CTE Name"]) is { Length: > 0 } cteName) label += " of " + cteName;
        if (Str(node["Index Name"]) is { Length: > 0 } idx) label += " using " + idx;
        if (Str(node["Subplan Name"]) is { Length: > 0 } subName) label = $"{subName}: {label}";
        var index = walk.Ops.Count;
        walk.Ops.Add(null!);   // its place in the tree: filled once its children are read

        var children = (node["Plans"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        var childParallel = parallel || type is "Gather" or "Gather Merge";
        // The planner costs a loop's inner side, and a SubPlan, PER EXECUTION: the parent's cost holds them that many times.
        var outerRows = children.FirstOrDefault(c => Str(c["Parent Relationship"]) == "Outer") is { } outer ? Num(outer["Plan Rows"]) ?? 1 : 1;
        double childCost = 0, childMs = 0;
        var measured = true;
        foreach (var child in children)
        {
            var relation = Str(child["Parent Relationship"]);
            var childTimes = times * (type == "Nested Loop" && relation == "Inner" ? Math.Max(1, outerRows)
                                    : relation == "SubPlan" ? Math.Max(1, Num(node["Plan Rows"]) ?? 1) : 1);
            childCost += (Num(child["Total Cost"]) ?? 0) * childTimes;
            var ms = PgNode(child, depth + 1, childParallel, childTimes, walk);
            // A CTE's body is timed inside the scan that reads it: never subtracted twice.
            if (Str(child["Subplan Name"]).StartsWith("CTE ", StringComparison.Ordinal)) continue;
            if (ms is double m) childMs += m; else measured = false;
        }
        var total = (Num(node["Total Cost"]) ?? 0) * times;
        var loops = Num(node["Actual Loops"]);
        var inclusive = Inclusive(node, parallel);
        double? self = null;
        if (inclusive is double i && measured)
        {
            var cte = type == "CTE Scan" && walk.CteMs.Remove(Str(node["CTE Name"]), out var body) ? body : 0;
            self = Math.Max(0, i - childMs - cte);
        }
        var est = Num(node["Plan Rows"]);
        var act = Num(node["Actual Rows"]);
        var ran = loops is not 0;

        var notes = new List<string>();
        if (!ran) notes.Add("never executed");
        if (Str(node["Filter"]) is { Length: > 0 } filter)
            notes.Add("filter " + filter + (Num(node["Rows Removed by Filter"]) is double gone && gone > 0 ? $", {Count(gone * (loops ?? 1))} rows removed by it" : ""));
        if (Str(node["Sort Space Type"]) == "Disk") notes.Add("the sort spilled to disk");
        if (Num(node["Hash Batches"]) is double batches && batches > 1) notes.Add($"the hash spilled to disk ({batches:0} batches)");
        if (type is "Seq Scan" or "Parallel Seq Scan")
        {
            if (act is double a)
            {
                // Rows READ: those returned and those its filter removed, every loop.
                var read = (a + (Num(node["Rows Removed by Filter"]) ?? 0)) * (loops ?? 1);
                if (read >= LargeScan) walk.Look.Add($"Full scan: {label}, {Count(read)} rows read.");
            }
            // An estimated plan says what a scan RETURNS, never what it reads: named by its share of the cost.
            else if (walk.PlanCost > 0 && total / walk.PlanCost >= CostlyShare)
                walk.Look.Add($"Full scan: {label}, {total / walk.PlanCost * 100:0}% of the plan's cost (an estimated plan does not say how many rows it reads; about {Count(est ?? 0)} returned).");
        }
        if (ran && EstimateOff(est, act) is { } off) walk.Look.Add($"Estimate off {off}: {label} — estimated {Count(est!.Value)} rows, got {Count(act!.Value)}.");
        walk.Ops[index] = new Op(label, depth, total, Math.Max(0, total - childCost), est, act, self, string.Join("; ", notes));
        return inclusive;
    }

    // ── MySQL and MariaDB ───────────────────────────────────────────────────────────────────────────────────────────

    private static Plan? MySql(JsonObject root)
    {
        if (root["query_block"] is not JsonObject block) return null;
        var ops = new List<Op>();
        var look = new List<string>();
        MyWalk(block, 0, ops, look);
        if (ops.Count == 0) return null;
        var total = Num(block["cost"]) ?? Num(block["cost_info"]?["query_cost"]);
        var head = Num(block["r_total_time_ms"]) is double ms ? $"execution {Ms(ms)}" : "";
        // MariaDB writes "cost" and "rows"; MySQL "cost_info" and "rows_examined_per_scan".
        var engine = block["cost_info"] is not null ? "MySQL" : "MariaDB";
        return new Plan(engine, ops.Any(o => o.Act is not null), total, ops, look, head);
    }

    private static void MyWalk(JsonNode? node, int depth, List<Op> ops, List<string> look)
    {
        switch (node)
        {
            case JsonArray array:
                foreach (var item in array) MyWalk(item, depth, ops, look);
                break;
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    if (key == "table" && value is JsonObject t) { MyTable(t, depth, ops, look); MyWalk(t, depth + 1, ops, look); continue; }
                    // The steps that wrap the tables: a sort or a temporary table costs on its own.
                    if (key is "filesort" or "ordering_operation" or "grouping_operation" or "temporary_table" or "duplicates_removal"
                        && value is JsonObject wrap)
                    {
                        var sort = key == "filesort" || wrap["using_filesort"] is JsonValue fs && fs.TryGetValue<bool>(out var f) && f;
                        var temp = key == "temporary_table" || wrap["using_temporary_table"] is JsonValue tt && tt.TryGetValue<bool>(out var u) && u;
                        var label = key.Replace('_', ' ') + (Str(wrap["sort_key"]) is { Length: > 0 } sk ? " by " + sk : "");
                        ops.Add(new Op(label, depth, 0, 0, null, Num(wrap["r_output_rows"]), Num(wrap["r_total_time_ms"]),
                                       string.Join("; ", new[] { sort ? "sorts (filesort)" : "", temp ? "uses a temporary table" : "" }.Where(s => s.Length > 0))));
                        MyWalk(wrap, depth + 1, ops, look);
                        continue;
                    }
                    if (value is JsonObject or JsonArray) MyWalk(value, depth, ops, look);
                }
                break;
        }
    }

    private static void MyTable(JsonObject t, int depth, List<Op> ops, List<string> look)
    {
        var name = Str(t["table_name"]);
        var access = Str(t["access_type"]);
        var label = $"{access} on {name}" + (Str(t["key"]) is { Length: > 0 } key ? " using " + key : "");
        var est = Num(t["rows_examined_per_scan"]) ?? Num(t["rows"]);
        var act = Num(t["r_rows"]);
        var cost = Num(t["cost"]) ?? (Num(t["cost_info"]?["read_cost"]) ?? 0) + (Num(t["cost_info"]?["eval_cost"]) ?? 0);
        var ms = Num(t["r_table_time_ms"]) is double tm ? tm + (Num(t["r_other_time_ms"]) ?? 0) : (double?)null;
        var loops = Num(t["r_loops"]) ?? Num(t["loops"]) ?? 1;
        var note = Str(t["attached_condition"]) is { Length: > 0 } cond ? "condition " + cond : "";
        ops.Add(new Op(label, depth, cost, cost, est, act, ms, note));
        // "ALL" reads the whole table, "index" the whole index: these engines give the rows read per scan.
        if (access is "ALL" or "index" && (act ?? est) >= LargeScan)
            look.Add($"Full {(access == "ALL" ? "table" : "index")} scan: {name}, {Count((act ?? est)!.Value)} rows read{(act is null ? " (estimated)" : "")}" +
                     (loops > 1 ? $", {Count(loops)} times" : "") + ".");
        if (Num(t["r_loops"]) is not 0 && EstimateOff(est, act) is { } off)
            look.Add($"Estimate off {off}: {label} — estimated {Count(est!.Value)} rows, got {Count(act!.Value)}.");
    }

    // ── SQL Server ──────────────────────────────────────────────────────────────────────────────────────────────────

    private static Plan? SqlServer(string text)
    {
        var doc = XDocument.Parse(text);
        if (doc.Root?.Name.LocalName != "ShowPlanXML") return null;
        var relOps = doc.Descendants().Where(e => e.Name.LocalName == "RelOp").ToList();
        if (relOps.Count == 0) return null;
        var ops = new List<Op>();
        var look = new List<string>();
        var measured = relOps.Any(r => Counters(r).Count > 0);
        foreach (var r in relOps)
        {
            var depth = r.Ancestors().Count(a => a.Name.LocalName == "RelOp");
            var children = ChildRelOps(r);
            var physical = Attr(r, "PhysicalOp");
            var logical = Attr(r, "LogicalOp");
            var label = physical + (logical.Length > 0 && logical != physical ? $" ({logical})" : "");
            var obj = r.Descendants().FirstOrDefault(d => d.Name.LocalName == "Object" && NearestRelOp(d) == r);
            if (obj is not null)
                label += " on " + Unbracket(Attr(obj, "Table")) + (Attr(obj, "Index") is { Length: > 0 } ix && physical.Contains("Index", StringComparison.Ordinal) ? " using " + Unbracket(ix) : "");
            var total = NumAttr(r, "EstimatedTotalSubtreeCost") ?? 0;
            var self = Math.Max(0, total - children.Sum(c => NumAttr(c, "EstimatedTotalSubtreeCost") ?? 0));
            var est = NumAttr(r, "EstimateRows");
            var counters = Counters(r);
            double? act = null, ms = null, readRows = null;
            var ran = true;
            if (counters.Count > 0)
            {
                var rows = counters.Sum(c => NumAttr(c, "ActualRows") ?? 0);
                // EstimateRows is per execution, all threads together: a parallel operator's executions are counted per
                // thread, so the most of one thread is its count.
                var executions = Attr(r, "Parallel") is "1" or "true" ? counters.Max(c => NumAttr(c, "ActualExecutions") ?? 0)
                                                                       : counters.Sum(c => NumAttr(c, "ActualExecutions") ?? 0);
                ran = executions > 0 || counters.All(c => c.Attribute("ActualExecutions") is null);
                act = rows / Math.Max(1, executions);
                if (counters.Any(c => c.Attribute("ActualRowsRead") is not null)) readRows = counters.Sum(c => NumAttr(c, "ActualRowsRead") ?? 0);
                if (counters.Any(c => c.Attribute("ActualElapsedms") is not null))
                    ms = IsBatch(r) ? OwnMs(r) : Math.Max(0, OwnMs(r) - children.Sum(InclusiveMs));
            }
            ops.Add(new Op(label, depth, total, self, est, act, ms, ran ? "" : "never executed"));
            var scanned = readRows ?? NumAttr(r, "EstimatedRowsRead") ?? NumAttr(r, "TableCardinality") ?? est;
            if (physical is "Table Scan" or "Clustered Index Scan" or "Index Scan" && scanned >= LargeScan)
                look.Add($"Full scan: {label}, {Count(scanned!.Value)} rows read{(readRows is null ? " (estimated)" : "")}.");
            if (ran && EstimateOff(est, act) is { } off) look.Add($"Estimate off {off}: {label} — estimated {Count(est!.Value)} rows, got {Count(act!.Value)}.");
        }
        // The engine's own words: its warnings (elements and flags), then the indexes it would have used.
        foreach (var warnings in doc.Descendants().Where(e => e.Name.LocalName == "Warnings"))
        {
            foreach (var flag in warnings.Attributes().Where(a => a.Value is "true" or "1"))
                look.Add($"Warning from SQL Server: {flag.Name.LocalName}.");
            foreach (var w in warnings.Elements())
                look.Add("Warning from SQL Server: " + w.Name.LocalName +
                         (w.Attributes().Where(a => a.Name.LocalName is "ConvertIssue" or "Expression" or "SpillLevel" or "GrantWarningKind"
                                                     or "RequestedMemory" or "GrantedMemory").ToList() is { Count: > 0 } attrs
                             ? " (" + string.Join(", ", attrs.Select(a => $"{a.Name.LocalName} {a.Value}")) + ")" : "") + ".");
        }
        foreach (var g in doc.Descendants().Where(e => e.Name.LocalName == "MissingIndexGroup"))
            foreach (var mi in g.Elements().Where(e => e.Name.LocalName == "MissingIndex"))
            {
                string Cols(string usage) => string.Join(", ", mi.Elements().Where(c => c.Name.LocalName == "ColumnGroup" && Attr(c, "Usage") == usage)
                    .SelectMany(c => c.Elements()).Select(c => Unbracket(Attr(c, "Name"))));
                var keys = string.Join(", ", new[] { Cols("EQUALITY"), Cols("INEQUALITY") }.Where(s => s.Length > 0));
                var include = Cols("INCLUDE");
                look.Add($"Missing index suggested by SQL Server (estimated impact {NumAttr(g, "Impact") ?? 0:0}%): {Unbracket(Attr(mi, "Table"))} ({keys})" +
                         (include.Length > 0 ? $" include ({include})" : "") + ".");
            }
        var statements = doc.Descendants().Where(e => e.Name.LocalName == "StmtSimple" && e.Attribute("StatementSubTreeCost") is not null).ToList();
        double? cost = statements.Count == 0 ? null : statements.Sum(s => NumAttr(s, "StatementSubTreeCost") ?? 0);
        return new Plan("SQL Server", measured, cost, ops, look, statements.Count > 1 ? $"{statements.Count} statements, all read" : "");
    }

    private static List<XElement> Counters(XElement relOp) =>
        Child(relOp, "RunTimeInformation")?.Elements().Where(e => e.Name.LocalName == "RunTimeCountersPerThread").ToList() ?? [];

    private static List<XElement> ChildRelOps(XElement relOp) =>
        relOp.Descendants().Where(d => d.Name.LocalName == "RelOp" && NearestRelOp(d) == relOp).ToList();

    /// <summary>A batch-mode operator's elapsed time is its own; a row-mode one's includes its children's.</summary>
    private static bool IsBatch(XElement relOp) => Counters(relOp).Any(c => Attr(c, "ActualExecutionMode") == "Batch");

    private static double OwnMs(XElement relOp) => Counters(relOp).Select(c => NumAttr(c, "ActualElapsedms") ?? 0).DefaultIfEmpty(0).Max();

    /// <summary>An operator's elapsed time, its children's included: row mode as measured, batch mode its own plus its
    /// children's; one without counters (a Compute Scalar) is its children's, else its parent would be charged the subtree.</summary>
    private static double InclusiveMs(XElement relOp) =>
        Counters(relOp).Count == 0 ? ChildRelOps(relOp).Sum(InclusiveMs)
        : IsBatch(relOp) ? OwnMs(relOp) + ChildRelOps(relOp).Sum(InclusiveMs)
        : OwnMs(relOp);

    private static XElement? NearestRelOp(XElement e) => e.Ancestors().FirstOrDefault(a => a.Name.LocalName == "RelOp");

    private static XElement? Child(XElement e, string name) => e.Elements().FirstOrDefault(c => c.Name.LocalName == name);

    private static string Attr(XElement e, string name) => e.Attribute(name)?.Value ?? "";

    private static double? NumAttr(XElement e, string name) =>
        double.TryParse(Attr(e, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static string Unbracket(string s) => s.Replace("[", "").Replace("]", "");

    // ── Rendering ───────────────────────────────────────────────────────────────────────────────────────────────────

    private static string Render(string title, Plan plan, int budget)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture,
            $"{title} ({plan.Engine}, {(plan.Measured ? "with what the run measured" : "the planner's estimates only: nothing was run")})");
        if (plan.TotalCost is double tc) sb.Append(CultureInfo.InvariantCulture, $": total cost {tc:0.###}");
        if (plan.Head.Length > 0) sb.Append("; ").Append(plan.Head);
        sb.Append(".\n");

        var byTime = plan.Ops.Any(o => o.SelfMs > 0);
        var totalShare = byTime ? plan.Ops.Sum(o => o.SelfMs ?? 0) : plan.Ops.Sum(o => o.Self);
        var ranked = plan.Ops.Where(o => (byTime ? o.SelfMs ?? 0 : o.Self) > 0)
                             .OrderByDescending(o => byTime ? o.SelfMs : o.Self).Take(Ranked).ToList();
        if (ranked.Count > 0)
        {
            sb.Append(byTime ? "\nWhere the time went (each operator's own share):\n" : "\nCostliest operators (each one's own share of the estimate):\n");
            var i = 0;
            foreach (var o in ranked)
            {
                var value = byTime ? o.SelfMs!.Value : o.Self;
                var share = totalShare > 0 ? $" ({value / totalShare * 100:0}%)" : "";
                sb.Append(CultureInfo.InvariantCulture, $"  {++i}. {o.Label} — {(byTime ? Ms(value) : "cost " + value.ToString("0.###", CultureInfo.InvariantCulture))}{share}{Rows(o)}")
                  .Append(o.Note.Length > 0 ? "; " + Clip(o.Note) : "").Append('\n');
            }
        }
        var look = plan.Look.Distinct().ToList();
        if (look.Count > 0)
        {
            sb.Append("\nWorth a look:\n");
            foreach (var l in look.Take(LookLines)) sb.Append("  - ").Append(Clip(l)).Append('\n');
            if (look.Count > LookLines) sb.Append(CultureInfo.InvariantCulture, $"  [{look.Count - LookLines} more]\n");
        }
        sb.Append("\nPlan:\n");
        var tree = 0;
        foreach (var o in plan.Ops)
        {
            var line = $"  {new string(' ', 2 * Math.Min(o.Depth, 20))}{Clip(o.Label)}{Rows(o)}\n";
            if (++tree > TreeLines || sb.Length + line.Length > budget - 60)
            {
                sb.Append(CultureInfo.InvariantCulture, $"  [{plan.Ops.Count - tree + 1} more operators]\n");
                break;
            }
            sb.Append(line);
        }
        var text = sb.ToString().TrimEnd();
        // Every part is bounded, the budget too: what passes it is cut at a line and said.
        if (text.Length <= budget) return text;
        const string Cut = "\n[Cut to fit: read_file with start_line pages the raw plan.]";
        var at = text.LastIndexOf('\n', Math.Max(0, budget - Cut.Length));
        return text[..Math.Max(0, at)] + Cut;
    }

    private static string Clip(string s) => s.Length <= NoteChars ? s : s[..NoteChars] + "…";

    private static string Rows(Op o) =>
        o.Act is double a ? $", {Count(a)} {Noun(a)}{(o.Est is double e ? $" (estimated {Count(e)})" : "")}"
        : o.Est is double est ? $", ~{Count(est)} {Noun(est)}" : "";

    private static string Noun(double n) => n == 1 ? "row" : "rows";

    private static string? EstimateOff(double? est, double? act)
    {
        if (est is not double e || act is not double a || Math.Max(e, a) < EstimateFloor) return null;
        var factor = Math.Max(e, a) / Math.Max(1, Math.Min(e, a));
        return factor >= EstimateFactor ? $"{factor:0}x" : null;
    }

    private static string Count(double n) => n >= 1000 ? n.ToString("#,0", CultureInfo.InvariantCulture) : n.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Ms(double ms) => ms.ToString("0.###", CultureInfo.InvariantCulture) + " ms";

    private static string Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    private static double? Num(JsonNode? node) => node is JsonValue v
        ? v.TryGetValue<double>(out var d) ? d
          : v.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p : null
        : null;
}
