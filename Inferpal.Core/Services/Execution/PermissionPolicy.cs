using System.Text.Json;
using System.Text.RegularExpressions;

namespace Inferpal.Services.Execution;

/// <summary>
/// Thrown by the approval service when a tool call is blocked by a <c>deny</c> rule or the built-in
/// hard denylist (as opposed to being declined by the user at the prompt). Tools let it propagate;
/// <see cref="ToolRegistry.ExecuteAsync"/> turns it into a distinct tool-result string so the model
/// sees "blocked by policy" rather than the generic "cancelled by user" and stops retrying.
/// </summary>
internal sealed class PermissionDeniedException(string message) : Exception(message);

/// <summary>The outcome of evaluating a tool call against the permission rules.</summary>
internal enum PermissionDecision
{
    /// <summary>No rule matched — fall back to the interactive approval prompt (and session/YOLO logic).</summary>
    Prompt,
    /// <summary>An allow rule matched — auto-approve without prompting.</summary>
    Allow,
    /// <summary>A deny rule (or the built-in hard denylist) matched — never run, even under YOLO.</summary>
    Deny,
}

/// <summary>
/// A single user-defined permission rule: an allow/deny decision scoped to a tool name (or
/// <c>*</c> for any) and matched against the action's <em>subject</em> (a shell command, a file
/// path, a URL…) by a regular expression.
/// </summary>
internal sealed class PermissionRule
{
    public PermissionDecision Decision { get; }
    /// <summary>Tool this rule applies to, or <c>"*"</c> for any tool.</summary>
    public string Tool { get; }
    public Regex Pattern { get; }

    public PermissionRule(PermissionDecision decision, string tool, Regex pattern)
    {
        Decision = decision;
        Tool     = tool;
        Pattern  = pattern;
    }

    /// <summary>
    /// True when this rule applies to <paramref name="toolName"/> and matches <paramref name="subject"/>.
    /// A pattern that blows past <see cref="PermissionPolicy.MatchTimeout"/> (catastrophic
    /// backtracking) counts as "no match": a rule the engine cannot evaluate must never decide,
    /// and it must never freeze the approval path either.
    /// </summary>
    public bool Matches(string toolName, string subject)
    {
        if (Tool != "*" && !string.Equals(Tool, toolName, StringComparison.OrdinalIgnoreCase))
            return false;

        try { return Pattern.IsMatch(subject); }
        catch (RegexMatchTimeoutException)
        {
            Diagnostics.Record("Permission", $"Rule regex timed out, ignored: {Pattern}");
            return false;
        }
    }
}

/// <summary>
/// Pure, testable permission engine for tool approval. Classifies a tool call into
/// <see cref="PermissionDecision.Allow"/> / <see cref="PermissionDecision.Deny"/> /
/// <see cref="PermissionDecision.Prompt"/> from a list of user rules plus a built-in
/// hard denylist of catastrophic shell commands (a floor that no configuration can
/// switch off — see the honest-scope note on <see cref="IsHardDenied"/>).
/// </summary>
/// <remarks>
/// <para>
/// Rule DSL (one per line, used both by the per-machine config field and the workspace
/// <c>.inferpal/permissions.json</c> overlay):
/// </para>
/// <code>
/// allow run_command ^\s*(dotnet|git|npm|pnpm|yarn|cargo|go)\b   # auto-approve common dev commands
/// deny  run_command (Remove-Item|rm\s+-rf)                       # but always prompt-block these
/// allow write_file  \.(cs|ts|js|py)$                             # auto-approve edits to source files
/// deny  *           \.env$                                       # never touch secrets, any tool
/// # lines starting with '#' are comments
/// </code>
/// <para>
/// Evaluation: the built-in hard denylist wins first (cannot be overridden, not even by
/// <c>SecurityAlertsDisabled</c>), then user rules in order — <em>first match wins</em> —, then
/// <see cref="PermissionDecision.Prompt"/> when nothing matched. Subjects are matched
/// case-insensitively.
/// </para>
/// </remarks>
internal sealed class PermissionPolicy
{
    /// <summary>
    /// Per-match budget for a user/workspace regex. Rules are matched on every tool call, and
    /// their patterns are not necessarily written by the person running them (the workspace
    /// overlay ships with the repository) — an unbounded match is a denial-of-service on the
    /// approval path.
    /// </summary>
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Budget des deux jeux <b>intégrés</b> — dix fois celui des règles utilisateur, et la
    /// différence est voulue.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Les deux budgets n'ont pas la même conséquence. Un motif <em>utilisateur</em> qui expire est
    /// <b>ignoré</b> : la règle ne décide pas, on retombe sur le prompt, et personne ne le voit.
    /// Un motif <em>intégré</em> qui expire rend le sujet <see cref="IsOpaqueExecution">opaque</see>,
    /// donc <b>force un prompt</b> — visible, sur une commande peut-être parfaitement ordinaire.
    /// </para>
    /// <para>
    /// ⚠ Payé le 2026-08-30, une heure après avoir introduit le budget : la suite a rougi sur
    /// <c>powershell -ExecutionPolicy Bypass -File build.ps1</c>, en parallèle des deux jambes de
    /// test, et sur cette jambe-là seulement — irreproductible en isolation. C'était la contention,
    /// pas le motif. Le coût réel mesuré du pire cas est de 15 ms sur 64 Ko : une seconde est
    /// soixante-dix fois cette marge, et borne toujours le cas pathologique. Un garde-fou dont le
    /// mode d'échec est du bruit apprend aux gens à cliquer sans lire — ce qui coûte plus cher que
    /// ce qu'il protège.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan BuiltInMatchTimeout = TimeSpan.FromSeconds(1);

    private readonly IReadOnlyList<PermissionRule> _rules;

    public PermissionPolicy(IReadOnlyList<PermissionRule> rules) => _rules = rules ?? [];

    /// <summary>An empty policy — every call falls through to <see cref="PermissionDecision.Prompt"/>.</summary>
    public static PermissionPolicy Empty { get; } = new([]);

    // Built-in hard denylist of catastrophic / unrecoverable shell commands. Kept
    // deliberately narrow so it never blocks ordinary dev work (a recursive delete of bin/ or
    // node_modules does NOT match — only deletes targeting a drive root or the home directory do).
    // Users layer their own, looser deny rules on top via the DSL; these are the floor.
    //
    // Honest scope: this is an accident guard, NOT a security boundary. Regexes match the
    // submitted text, and text matching cannot see through indirection ($c='…'; iex $c,
    // -EncodedCommand, FromBase64String…). Those constructs are handled one tier up:
    // IsOpaqueExecution forces the human prompt so no auto-approval path applies. The
    // approval prompt — where a human reads the raw command — is the actual boundary.
    //
    // ⚠ Every pattern here carries BuiltInMatchTimeout, for the reason written on MatchTimeout itself:
    // these run on the approval path, over text nobody in this process wrote. The user-rule leg
    // was bounded and this one was not (revue post-1.6.1) — measured on the first pattern below
    // in its previous form: 49 s on a 64 KB subject, ~3 h extrapolated at 1 MB, with no prompt,
    // no error and no way for the user to know why the turn had stopped.
    private static readonly Regex[] HardDeny =
    [
        // rm -rf targeting filesystem root / home / wildcard root, or with --no-preserve-root.
        // The two flags are found by same-position lookaheads rather than by three consecutive
        // [a-zA-Z]* runs: the old form was quadratic on a long flag cluster (the 49 s above) AND
        // order-sensitive, so `-fr` — the same command, flags swapped — was never denied at all.
        new(@"\brm\s+-(?=[a-zA-Z]*[rR])(?=[a-zA-Z]*[fF])[a-zA-Z]+\s+(/|~|\$HOME|/\*|\.\s*$)", RegexOptions.IgnoreCase | RegexOptions.Compiled, BuiltInMatchTimeout),
        new(@"\brm\b[^|&;]*--no-preserve-root", RegexOptions.IgnoreCase | RegexOptions.Compiled, BuiltInMatchTimeout),
        // Remove-Item -Recurse -Force (any order) targeting a bare drive root (C:\, D:/ …)
        new(@"Remove-Item\b(?=[^|&;]*-Rec)(?=[^|&;]*-Force)[^|&;]*\s['""]?[A-Za-z]:[\\/]?['""]?(\s|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled, BuiltInMatchTimeout),
        // Disk / volume destruction
        new(@"\b(mkfs(\.\w+)?|Format-Volume|Clear-Disk|diskpart)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, BuiltInMatchTimeout),
        new(@"\bformat\s+[A-Za-z]:", RegexOptions.IgnoreCase | RegexOptions.Compiled, BuiltInMatchTimeout),
        new(@"\bdd\b[^|&;]*\bif=", RegexOptions.IgnoreCase | RegexOptions.Compiled, BuiltInMatchTimeout),
        // Classic fork bomb
        new(@":\(\)\s*\{\s*:\s*\|\s*:", RegexOptions.Compiled, BuiltInMatchTimeout),
    ];

    /// <summary>True when <paramref name="subject"/> hits the built-in hard denylist — the
    /// catastrophic-command floor that no rule, config switch or session grant can disable.
    /// It matches literal text only; obfuscated equivalents are caught by the force-prompt
    /// tier (<see cref="IsOpaqueExecution"/>), not blocked.</summary>
    /// <remarks>
    /// A pattern that cannot be evaluated within budget does <b>not</b> deny — a guard that could
    /// not read its input has established nothing, and blocking on it would turn a pathological
    /// string into a denial-of-service of the other direction. The subject is instead treated as
    /// <see cref="IsOpaqueExecution">opaque</see>, which is the tier that exists for exactly this
    /// state: the effect of the text cannot be determined from the text, so a human reads it.
    /// </remarks>
    public static bool IsHardDenied(string? subject) =>
        !string.IsNullOrEmpty(subject) && MatchesAny(HardDeny, subject!, "hard denylist", out _);

    /// <summary>
    /// Runs a built-in pattern set over <paramref name="subject"/>. A pattern that exceeds
    /// <see cref="MatchTimeout"/> counts as "no match" and raises <paramref name="unreadable"/>:
    /// the two are different facts, and every caller here needs to tell them apart.
    /// </summary>
    /// <remarks>Internal rather than private so a test can drive it with its own pathological
    /// pattern: after the rewrite above, no built-in pattern can time out any more, so the
    /// timeout branch would otherwise be unreachable code that nothing exercises.</remarks>
    internal static bool MatchesAny(Regex[] patterns, string subject, string what, out bool unreadable)
    {
        unreadable = false;
        foreach (var pattern in patterns)
        {
            try
            {
                if (pattern.IsMatch(subject)) return true;
            }
            catch (RegexMatchTimeoutException)
            {
                unreadable = true;
                Diagnostics.Record("Permission",
                    $"A {what} pattern timed out on a {subject.Length}-char subject; " +
                    "the call is treated as opaque (human prompt), never as approved.");
            }
        }
        return false;
    }

    // Indirect-execution constructs that defeat text-based matching: what actually runs is
    // not the text the rules engine read. Matching one of these never *blocks* — it only
    // removes every auto-approval path (allow rules, SecurityAlertsDisabled, session grants)
    // so a human always reads the raw text before it runs. Kept short and specific: a false
    // positive costs exactly one approval prompt.
    private static readonly Regex[] OpaqueExecution =
    [
        // Invoke-Expression and its alias run a string the rules engine never saw
        new(@"\b(iex|Invoke-Expression)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, BuiltInMatchTimeout),
        // Nested PowerShell with an -Encoded* switch (-e, -ec, -enc, -EncodedCommand…);
        // (?![px]) keeps the legitimate -ExecutionPolicy / -ep flags out of the match
        new(@"\b(powershell|pwsh)(\.exe)?\b[^|&;]*[\s'""]-e(?![px])\w*", RegexOptions.IgnoreCase | RegexOptions.Compiled, BuiltInMatchTimeout),
        // Base64 payload decoded at run time, feeding whatever comes next
        new(@"FromBase64String", RegexOptions.IgnoreCase | RegexOptions.Compiled, BuiltInMatchTimeout),
        // Script block built from a string at run time
        new(@"\[\s*scriptblock\s*\]\s*::\s*Create", RegexOptions.IgnoreCase | RegexOptions.Compiled, BuiltInMatchTimeout),
        // Call operator on a variable/subexpression (& $cmd, & $(…)) or dot-sourcing one (. $script)
        new(@"(&\s*|(?<=^|[;|&(\s])\.\s+)\$", RegexOptions.Compiled, BuiltInMatchTimeout),

        // ── POSIX equivalents (§23) — same tier, same contract: force the prompt, never block ──
        // eval runs a string the rules engine never saw (the iex of POSIX shells)
        new(@"\beval\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, BuiltInMatchTimeout),
        // Piping anything into an interpreter executes downloaded/generated text — the script
        // interpreters are the same family as the shells (curl … | python3 ≡ curl … | sh),
        // not obfuscation (pre-1.6.0 architecture review).
        new(@"\|\s*(sudo\s+)?(sh|bash|zsh|dash|ksh|python[0-9.]*|perl|ruby|node)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, BuiltInMatchTimeout),
        // Invoke-Command / icm runs a script block, possibly remotely — same tier as iex
        new(@"\b(icm|Invoke-Command)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, BuiltInMatchTimeout),
        // Base64 payload decoded at run time (mirror of FromBase64String)
        new(@"\bbase64\b[^|&;]*(\s-[dD]\b|--decode)", RegexOptions.Compiled, BuiltInMatchTimeout),
        // A shell -c whose payload is a variable — a literal -c 'text' stays readable and does not match
        new(@"\b(sh|bash|zsh|dash|ksh)\b[^|&;]*-c\s*[""']?\s*\$", RegexOptions.IgnoreCase | RegexOptions.Compiled, BuiltInMatchTimeout),
        // Sourcing or exec-ing a path held in a variable
        new(@"\b(source|exec)\s+[""']?\$", RegexOptions.Compiled, BuiltInMatchTimeout),
    ];

    /// <summary>
    /// True when <paramref name="subject"/> contains an indirect-execution construct
    /// (<c>iex</c>, encoded nested shell, Base64 decoding, runtime script blocks, call
    /// operator on a variable) whose effect the rules cannot read from the text. Callers
    /// use it to force the interactive prompt instead of any auto-approval — never to block.
    /// </summary>
    /// <remarks>
    /// A subject that <em>either</em> built-in set failed to evaluate within budget is opaque too,
    /// and by the same definition: this tier's question is "can the text tell us what will run?",
    /// and a guard that timed out answered no. Folding the unreadable state in here — rather than
    /// inventing a third outcome — keeps one rule for callers: opaque means a human reads it.
    /// </remarks>
    public static bool IsOpaqueExecution(string? subject)
    {
        if (string.IsNullOrEmpty(subject)) return false;

        if (MatchesAny(OpaqueExecution, subject!, "opaque-execution", out var opaqueUnreadable)) return true;

        // Cheap in the normal case (the denylist is bounded and matches nothing), and the only
        // way the hard-deny timeout reaches a decision: Evaluate() has already turned a real
        // hard-deny match into Deny, so reaching here means it did not match — or could not say.
        MatchesAny(HardDeny, subject!, "hard denylist", out var denyUnreadable);
        return opaqueUnreadable || denyUnreadable;
    }

    /// <summary>Classifies a tool call. See the type remarks for the evaluation order.</summary>
    /// <remarks>
    /// A subject can carry <b>several paths</b> — <c>apply_edits</c> and <c>rename_symbol</c> join
    /// every affected file with <c>'\n'</c>. A rule that holds for one line does not hold for the
    /// aggregate: without <see cref="RegexOptions.Multiline"/>, a <c>$</c>-anchored deny only sees
    /// the last line, so a two-file edit used to slip past <c>deny * \.env$</c> (pre-1.6.0 architecture review,
    /// §1.1). Multi-line subjects are therefore evaluated line by line: one denied path denies the
    /// whole call, and the call is only auto-approved when <em>every</em> path is allowed.
    /// </remarks>
    public PermissionDecision Evaluate(string toolName, string? subject)
    {
        subject ??= string.Empty;

        if (subject.Contains('\n'))
        {
            var sawPrompt = false;
            foreach (var raw in subject.Split('\n'))
            {
                var line = raw.Trim('\r', ' ', '\t');
                if (line.Length == 0) continue;
                switch (EvaluateSingle(toolName, line))
                {
                    case PermissionDecision.Deny:   return PermissionDecision.Deny;
                    case PermissionDecision.Prompt: sawPrompt = true; break;
                }
            }
            return sawPrompt ? PermissionDecision.Prompt : PermissionDecision.Allow;
        }

        return EvaluateSingle(toolName, subject);
    }

    private PermissionDecision EvaluateSingle(string toolName, string subject)
    {
        if (IsHardDenied(subject)) return PermissionDecision.Deny;

        foreach (var rule in _rules)
            if (rule.Matches(toolName, subject))
                return rule.Decision;   // first match wins

        return PermissionDecision.Prompt;
    }

    /// <summary>
    /// Composes the machine config rules with the workspace overlay into one ordered ruleset.
    /// <b>Overlay rules come first.</b> The overlay is deny-only by construction
    /// (<see cref="ParseJsonOverlay"/>), so putting it ahead can only restrict — whereas the old
    /// config-first order let an ordinary machine <c>allow</c> (say, <c>allow write_file \.cs$</c>)
    /// shadow a repository's <c>deny</c> under first-match-wins, silently breaking the documented
    /// promise that a project tightening its own restrictions is always safe (pre-1.6.0 architecture review, §1.2).
    /// </summary>
    public static IReadOnlyList<PermissionRule> Compose(
        IReadOnlyList<PermissionRule> configRules,
        IReadOnlyList<PermissionRule> overlayRules)
    {
        if (overlayRules.Count == 0) return configRules;
        var rules = new List<PermissionRule>(overlayRules.Count + configRules.Count);
        rules.AddRange(overlayRules);
        rules.AddRange(configRules);
        return rules;
    }

    // ── Parsing ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Parses a single DSL line (<c>allow|deny &lt;tool|*&gt; &lt;regex&gt;</c>). Returns
    /// <c>null</c> for blank lines, <c>#</c> comments, malformed lines, and invalid regexes
    /// (skipped rather than throwing, so one bad line never disables the whole ruleset).
    /// </summary>
    public static PermissionRule? ParseLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        line = line.Trim();
        if (line.StartsWith('#')) return null;

        // decision <space> tool <space> regex(rest of line)
        var first = line.IndexOf(' ');
        if (first <= 0) return null;
        var decisionTok = line[..first].ToLowerInvariant();
        PermissionDecision? decision = decisionTok switch
        {
            "allow" => PermissionDecision.Allow,
            "deny"  => PermissionDecision.Deny,
            _       => null,
        };
        if (decision is null) return null;

        var rest = line[(first + 1)..].TrimStart();
        var second = rest.IndexOf(' ');
        if (second <= 0) return null;
        var tool    = rest[..second].Trim();
        var pattern = rest[(second + 1)..].Trim();
        if (string.IsNullOrEmpty(tool) || string.IsNullOrEmpty(pattern)) return null;

        try
        {
            // Bounded matching: rules can come from a cloned repository's permissions.json, so a
            // catastrophic-backtracking pattern must not be able to freeze the approval path.
            var regex = new Regex(pattern, RegexOptions.IgnoreCase, MatchTimeout);
            return new PermissionRule(decision.Value, tool, regex);
        }
        catch (ArgumentException)
        {
            return null;   // invalid regex — skip this rule
        }
    }

    /// <summary>Parses newline-separated DSL text into rules, preserving order and skipping bad lines.</summary>
    public static IReadOnlyList<PermissionRule> ParseRules(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var rules = new List<PermissionRule>();
        foreach (var line in text.Split('\n'))
        {
            var rule = ParseLine(line);
            if (rule is not null) rules.Add(rule);
        }
        return rules;
    }

    /// <summary>
    /// Parses the workspace <c>.inferpal/permissions.json</c> overlay — a JSON object with a
    /// <c>"rules"</c> array of DSL strings: <c>{ "rules": ["deny * \\.env$"] }</c>.
    /// Returns an empty list on missing/invalid JSON (never throws).
    /// </summary>
    /// <remarks>
    /// <b>The overlay can only restrict, never grant.</b> This file ships with the repository, so
    /// it is attacker-controlled the moment you clone something: an <c>allow</c> rule here would
    /// let a hostile project switch off the approval prompt for its own commands. Allow rules are
    /// therefore dropped (and recorded, visible in <c>/diagnostics</c>) — auto-approval is a
    /// decision only the machine's own configuration can make. Deny rules are kept: a project
    /// tightening its own restrictions is always safe.
    /// </remarks>
    public static IReadOnlyList<PermissionRule> ParseJsonOverlay(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("rules", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return [];
            var rules = new List<PermissionRule>();
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) continue;
                var rule = ParseLine(item.GetString());
                if (rule is null) continue;
                if (rule.Decision == PermissionDecision.Allow)
                {
                    Diagnostics.Record("Permission",
                        $"Ignored an 'allow' rule from the workspace overlay (deny-only): {item.GetString()}");
                    continue;
                }
                rules.Add(rule);
            }
            return rules;
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
