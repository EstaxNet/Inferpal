using System.Text;
using System.Text.Json;
using Inferpal.Models;
using Inferpal.Services.Mcp;
using Inferpal.Services.Tools;

namespace Inferpal.Services.Execution;

/// <summary>
/// Which MCP tools are declared with every request and which wait to be loaded by <c>tool_search</c>, and the order
/// the loaded ones are declared in.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ An editor that hands the agent its own MCP server can fill the window before the first word: Rider's 93 tools weigh
/// ~49 000 tokens as a request counts them, more than a 32K window. While the MCP definitions fit in
/// <see cref="WindowShare"/> of the window in use they are all declared; beyond, servers go on demand one at a time, the
/// heaviest first (ties by name, ordinal), until the rest fits — a small server stays declared beside a large one.
/// </para>
/// <para>
/// ⚠ A loaded tool is declared after every other tool, in the order it was loaded, and never moves: the definitions
/// sit near the head of a request, and a server that reuses its cache reads again everything after the first change —
/// one re-read per load, never one per request. The set lasts for the conversation (<see cref="Forget"/>).
/// </para>
/// <para>
/// ⚠ Every cut and every miss of a search is said apart: a tool left out by the per-call cap, a name several servers
/// share, a match beyond the closest ones — answered "not found", the model concludes the tool does not exist.
/// </para>
/// </remarks>
/// <param name="mcpTools">The MCP tools served now, without the ones turned off — the same list object while they do not
/// change: the split is cached on that reference.</param>
/// <param name="turnedOff">What a request to load a name answers when the tool it names is turned off; <c>null</c> when no
/// tool turned off is named so. Said apart: "no tool is named" would read "it does not exist".</param>
internal sealed class OnDemandTools(Func<IReadOnlyList<ITool>> mcpTools, Func<int> windowInUse, Func<string, string?>? turnedOff = null)
{
    /// <summary>The share of the window in use the MCP definitions may take before servers go on demand.</summary>
    internal const double WindowShare = 0.10;

    /// <summary>The window assumed while none is known: the one the recommendations are measured at.</summary>
    internal const int UnknownWindow = 32_768;

    /// <summary>How many tools one search loads at most.</summary>
    internal const int MaxPerSearch = 5;

    /// <summary>How many of the other matches a keyword search names, so they can be selected.</summary>
    internal const int MoreNamed = 20;

    /// <summary>Shorter words of a query match too much to rank anything ("a", "of", "to").</summary>
    internal const int MinWordLength = 3;

    private readonly object _gate = new();
    private readonly List<string> _loaded = [];
    private (IReadOnlyList<ITool> Tools, int Window, Plan Plan)? _cache;

    /// <summary>The split for the tools served now: what is declared, what waits (by server), and what is said of it.</summary>
    internal sealed class Plan
    {
        public Plan(IReadOnlyList<ITool> declared, IReadOnlyList<(string Server, IReadOnlyList<McpTool> Tools)> deferred, int budgetChars)
        {
            Declared = declared;
            Deferred = deferred;
            Waiting = deferred.SelectMany(d => d.Tools).ToDictionary(t => t.Name, StringComparer.Ordinal);
            Description = Describe(deferred, budgetChars);
            _budgetChars = budgetChars;
        }

        private readonly int _budgetChars;

        /// <summary>This plan with only the tools <paramref name="allows"/> keeps.</summary>
        public Plan KeepingOnly(Func<string, bool> allows) =>
            new([.. Declared.Where(t => allows(t.Name))],
                [.. Deferred.Select(d => (d.Server, (IReadOnlyList<McpTool>)[.. d.Tools.Where(t => allows(t.Name))]))
                            .Where(d => d.Item2.Count > 0)],
                _budgetChars);

        public IReadOnlyList<ITool> Declared { get; }
        public IReadOnlyList<(string Server, IReadOnlyList<McpTool> Tools)> Deferred { get; }
        /// <summary>Every tool on demand, loaded or not, by the name a request calls it.</summary>
        public IReadOnlyDictionary<string, McpTool> Waiting { get; }
        /// <summary>What <c>tool_search</c> tells the model (<see cref="Describe"/>).</summary>
        public string Description { get; }
        public bool HasDeferred => Deferred.Count > 0;
    }

    /// <summary>The window the budget is a share of: the one in use, else <see cref="UnknownWindow"/>.</summary>
    private int Window => windowInUse() is > 0 and var w ? w : UnknownWindow;

    /// <summary>The split for the tools served now, recomputed only when the tools or the window change — narrowed to
    /// the tools of the mode the call is made under (<see cref="Narrow"/>).</summary>
    internal Plan Current()
    {
        var tools = mcpTools();
        var window = Window;
        Plan plan;
        lock (_gate)
        {
            if (_cache is { } c && ReferenceEquals(c.Tools, tools) && c.Window == window) plan = c.Plan;
            else
            {
                plan = Split(tools, window);
                _cache = (tools, window, plan);
            }
        }
        return Narrowing.Value is { } allows ? plan.KeepingOnly(allows) : plan;
    }

    /// <summary>The plan as served, whatever mode the call is made under.</summary>
    private Plan Unnarrowed()
    {
        var previous = Narrowing.Value;
        Narrowing.Value = null;
        try { return Current(); }
        finally { Narrowing.Value = previous; }
    }

    private static readonly AsyncLocal<Func<string, bool>?> Narrowing = new();

    /// <summary>
    /// Narrows what <c>tool_search</c> describes, finds and loads to the tools <paramref name="allows"/> keeps, for what
    /// runs within the scope — its awaited calls included.
    /// </summary>
    /// <remarks>⚠ A mode that keeps one MCP server's tools still offers <c>tool_search</c>: unnarrowed, it named the
    /// tools of every server on demand and loaded them, and the model's next calls went to tools the mode refuses.</remarks>
    internal static IDisposable Narrow(Func<string, bool> allows)
    {
        var previous = Narrowing.Value;
        Narrowing.Value = allows;
        return new Restore(previous);
    }

    private sealed class Restore(Func<string, bool>? previous) : IDisposable
    {
        public void Dispose() => Narrowing.Value = previous;
    }

    /// <summary>Whether a server is on demand right now — <c>tool_search</c> is offered only then.</summary>
    internal bool HasDeferred => Current().HasDeferred;

    /// <summary>What <c>tool_search</c> tells the model for the tools served now.</summary>
    internal string Description() => Current().Description;

    private List<string> LoadedNow()
    {
        lock (_gate) return [.. _loaded];
    }

    /// <summary>
    /// The MCP tools a request declares: the declared ones as served, then the loaded ones in the order they were loaded.
    /// </summary>
    internal IEnumerable<ITool> Declared()
    {
        var plan = Current();
        return plan.Declared.Concat(LoadedNow().Where(plan.Waiting.ContainsKey).Select(n => (ITool)plan.Waiting[n]));
    }

    /// <summary>The names a call may carry beyond the declared ones: every tool on demand, loaded or not.</summary>
    internal IEnumerable<string> WaitingNames => Current().Waiting.Keys;

    /// <summary>The servers on demand and how many of their tools are not loaded yet — what X-Ray says the next turn leaves
    /// out.</summary>
    internal IReadOnlyList<(string Server, int Tools)> Waiting()
    {
        var plan = Current();
        var loaded = LoadedNow().ToHashSet(StringComparer.Ordinal);
        return [.. plan.Deferred.Select(d => (d.Server, d.Tools.Count(t => !loaded.Contains(t.Name)))).Where(w => w.Item2 > 0)];
    }

    /// <summary>Loads <paramref name="name"/> when it waits on demand: a call that ran by that name loads it.</summary>
    internal void LoadIfDeferred(string name)
    {
        if (Current().Waiting.ContainsKey(name)) Load([name]);
    }

    private void Load(IEnumerable<string> names)
    {
        lock (_gate)
            foreach (var n in names)
                if (!_loaded.Contains(n)) _loaded.Add(n);
    }

    /// <summary>Forgets every loaded tool: a new conversation starts with the plan alone.</summary>
    internal void Forget()
    {
        lock (_gate) _loaded.Clear();
    }

    /// <summary>The split of <paramref name="tools"/> for <paramref name="window"/> (pure).</summary>
    internal static Plan Split(IReadOnlyList<ITool> tools, int window)
    {
        var budget = (int)(window * WindowShare) * 4;   // tokens to characters, the ~4 a token RequestSize counts
        var byServer = tools.OfType<McpTool>()
            .GroupBy(t => t.ServerName, StringComparer.Ordinal)
            .Select(g => (Server: g.Key, Tools: (IReadOnlyList<McpTool>)[.. g], Chars: g.Sum(Chars)))
            .ToList();
        var total = byServer.Sum(s => s.Chars);
        var deferred = new List<(string Server, IReadOnlyList<McpTool> Tools)>();
        foreach (var s in byServer.OrderByDescending(s => s.Chars).ThenBy(s => s.Server, StringComparer.Ordinal))
        {
            if (total <= budget) break;
            deferred.Add((s.Server, s.Tools));
            total -= s.Chars;
        }
        var waiting = deferred.SelectMany(d => d.Tools).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        return new([.. tools.Where(t => !waiting.Contains(t.Name))], deferred, budget);
    }

    /// <summary>The characters of a tool's definition as a request sends it.</summary>
    private static int Chars(ITool t)
    {
        try { return JsonSerializer.Serialize(new ToolDefinition("function", new ToolFunction(t.Name, t.Description, t.Parameters))).Length; }
        catch (Exception ex)
        {
            Diagnostics.Swallow("OnDemandTools.Chars", ex);
            return 400;   // the rough per-tool budget RequestSize uses when a schema does not serialize
        }
    }

    /// <summary>
    /// What <c>tool_search</c> tells the model: what it does, then the servers on demand and the names of their tools.
    /// </summary>
    /// <remarks>⚠ The names share the budget the split enforces (<paramref name="budgetChars"/>): a fixed allowance would
    /// make the on-demand form outweigh, in a small window, the share it exists to keep. A server whose names do not fit is
    /// named with its count, the cut said.</remarks>
    private static string Describe(IReadOnlyList<(string Server, IReadOnlyList<McpTool> Tools)> deferred, int budgetChars)
    {
        var sb = new StringBuilder(
            "Loads tools that are available on demand: their definitions are not sent until you load them. "
          + "Search by keywords (matched against tool names and descriptions), or load exact names with "
          + "\"select:name1,name2\". At most " + MaxPerSearch + " tools per call; a loaded tool stays available for the "
          + "rest of the conversation. Load a tool before calling it.");
        var used = 0;
        foreach (var (server, tools) in deferred)
        {
            var names = string.Join(", ", tools.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal));
            if (used + names.Length <= budgetChars)
            {
                sb.Append($"\nOn demand from server '{server}' ({tools.Count} tools): {names}.");
                used += names.Length;
            }
            else
                sb.Append($"\nOn demand from server '{server}': {tools.Count} tools, not listed here — search them by keyword.");
        }
        return sb.ToString();
    }

    /// <summary>What a search answers, and the tools it loads.</summary>
    internal string Search(string query)
    {
        var plan = Current();
        if (!plan.HasDeferred) return "No tool is on demand now: every tool is already declared.";
        var q = query.Trim();
        return q.StartsWith("select:", StringComparison.OrdinalIgnoreCase)
            ? Select(plan, q["select:".Length..])
            : Keywords(plan, q);
    }

    private string Select(Plan plan, string list)
    {
        var asked = list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();
        if (asked.Count == 0) return "Error: \"select:\" needs one or more tool names.";
        var found = new List<McpTool>();
        var missing = new List<string>();
        var apart = new List<string>();   // ambiguous names, tools turned off: each said in its own words
        foreach (var a in asked)
        {
            if (plan.Waiting.TryGetValue(a, out var exact)) { found.Add(exact); continue; }
            var byLocal = plan.Waiting.Values.Where(t => t.LocalName == a).OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
            if (byLocal.Count == 1) found.Add(byLocal[0]);
            else if (byLocal.Count > 1)
                apart.Add($"'{a}' names several tools on demand: {string.Join(", ", byLocal.Select(t => t.Name))} — select one by its full name.");
            else if (turnedOff?.Invoke(a) is { } off) apart.Add(off);
            // ⚠ Left out by the mode the call is made under: said so — "no tool is named" would read as no such tool.
            else if (Narrowing.Value is not null && Unnarrowed().Waiting.ContainsKey(a))
                apart.Add($"'{a}' is not available in the mode in use.");
            else missing.Add(a);
        }

        var loaded = LoadedNow().ToHashSet(StringComparer.Ordinal);
        var fresh = found.Distinct().Where(t => !loaded.Contains(t.Name)).ToList();
        var already = found.Distinct().Where(t => loaded.Contains(t.Name)).Select(t => t.Name).ToList();
        var parts = new List<string>();
        if (fresh.Count > 0) parts.Add(LoadAndShow([.. fresh.Take(MaxPerSearch)]));
        if (fresh.Count > MaxPerSearch)
            parts.Add($"Not loaded yet — at most {MaxPerSearch} per call, select them again: "
                    + string.Join(", ", fresh.Skip(MaxPerSearch).Select(t => t.Name)) + ".");
        if (already.Count > 0) parts.Add("Already loaded: " + string.Join(", ", already) + ". Call them by these names.");
        parts.AddRange(apart);
        if (missing.Count > 0)
            parts.Add($"No tool on demand is named {string.Join(", ", missing.Select(m => $"'{m}'"))}. " + Servers(plan));
        return string.Join("\n\n", parts);
    }

    private string Keywords(Plan plan, string q)
    {
        var terms = Words(q).Where(w => w.Length >= MinWordLength).Distinct().ToList();
        if (terms.Count == 0) return "Error: 'query' needs keywords of three letters or more, or \"select:name1,name2\".";
        var loaded = LoadedNow().ToHashSet(StringComparer.Ordinal);
        var matches = plan.Waiting.Values
            .Where(t => !loaded.Contains(t.Name))
            .Select(t => (Tool: t, Score: Score(t, terms)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score).ThenBy(x => x.Tool.Name, StringComparer.Ordinal)
            .ToList();
        if (matches.Count == 0)
            return $"No tool on demand that is not loaded yet matches '{q}'. " + Servers(plan);

        // The closest ones only: a word every tool shares would otherwise load five tools nobody asked for.
        var best = matches[0].Score;
        var load = matches.TakeWhile(x => x.Score == best).Take(MaxPerSearch).Select(x => x.Tool).ToList();
        var more = matches.Skip(load.Count).Select(x => x.Tool.Name).ToList();
        var answer = LoadAndShow(load);
        if (more.Count > 0)
            answer += $"\n\n{more.Count} more match less closely or beyond the {MaxPerSearch} per call; load one with "
                    + "\"select:\": " + string.Join(", ", more.Take(MoreNamed))
                    + (more.Count > MoreNamed ? $", and {more.Count - MoreNamed} more." : ".");
        return answer;
    }

    /// <summary>Two points for a word of the tool's own name, one for a word of its description.</summary>
    private static int Score(McpTool t, IReadOnlyList<string> terms)
    {
        var name = Words(t.LocalName).ToHashSet(StringComparer.Ordinal);
        var description = Words(t.Description).ToHashSet(StringComparer.Ordinal);
        return terms.Sum(w => name.Contains(w) ? 2 : description.Contains(w) ? 1 : 0);
    }

    /// <summary>The lower-case words of a text: snake_case, camelCase and punctuation split.</summary>
    internal static IEnumerable<string> Words(string text)
    {
        var word = new StringBuilder();
        char prev = '\0';
        foreach (var ch in text)
        {
            if (!char.IsLetterOrDigit(ch)) { if (word.Length > 0) yield return word.ToString(); word.Clear(); }
            else
            {
                if (char.IsUpper(ch) && char.IsLower(prev) && word.Length > 0) { yield return word.ToString(); word.Clear(); }
                word.Append(char.ToLowerInvariant(ch));
            }
            prev = ch;
        }
        if (word.Length > 0) yield return word.ToString();
    }

    private static string Servers(Plan plan) =>
        "On demand: " + string.Join("; ", plan.Deferred.Select(d => $"server '{d.Server}' ({d.Tools.Count} tools)")) + ".";

    /// <summary>Loads <paramref name="tools"/> and says how to call them, each with its schema.</summary>
    private string LoadAndShow(IReadOnlyList<McpTool> tools)
    {
        Load(tools.Select(t => t.Name));
        var sb = new StringBuilder($"Loaded {tools.Count} tool{(tools.Count == 1 ? "" : "s")}; call {(tools.Count == 1 ? "it" : "them")} by "
                                   + "these names from now on:");
        foreach (var t in tools)
        {
            string schema;
            try { schema = JsonSerializer.Serialize(t.Parameters); }
            catch (Exception ex) { Diagnostics.Swallow("OnDemandTools.LoadAndShow", ex); schema = "{}"; }
            sb.Append($"\n\n{t.Name}\n{t.Description.Trim()}\nParameters: {schema}");
        }
        return sb.ToString();
    }
}
