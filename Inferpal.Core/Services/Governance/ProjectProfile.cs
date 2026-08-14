using System.IO;
using System.Text.Json;
using Inferpal.Config;

namespace Inferpal.Services.Governance;

/// <summary>A setting the profile proposes: what the repository suggests, and what the machine currently uses.</summary>
/// <param name="Key">Profile key, e.g. <c>agentModel</c>.</param>
/// <param name="Proposed">Value written by the repository.</param>
/// <param name="Current">Value currently in effect on this machine (empty when unset).</param>
internal sealed record ProfileRecommendation(string Key, string Proposed, string Current);

/// <summary>A key the profile carried and that was <b>not</b> honoured.</summary>
/// <param name="Key">The key as written in the file.</param>
/// <param name="Sensitive">
/// <c>true</c> when the key names something that executes, authorises or redirects (validators,
/// permissions, backend URL, API key…). Reported more loudly: a repository asking for one of these
/// is worth seeing, even though the outcome — ignored — is the same as for an unknown key.
/// </param>
internal sealed record ProfileIgnoredKey(string Key, bool Sensitive);

/// <summary>
/// The committable project profile — <c>.inferpal/project.json</c> (roadmap §19). It travels with
/// the repository, so a clone can describe how it likes to be worked on without any of it reaching
/// the machine's own settings.
/// </summary>
/// <remarks>
/// <para>
/// <b>The profile is non-privileged, by construction.</b> It ships with whatever you clone, exactly
/// like the <c>.inferpal/permissions.json</c> overlay (deny-only) and exactly like the commands in
/// <c>.inferpal/validators.json</c> (force-prompted). Three categories, and nothing else:
/// </para>
/// <list type="number">
///   <item><b>Applied</b> — <c>indexExclude</c> only, and <b>additively</b>: the profile can keep
///   files out of the semantic index, never put more in. The exact analogue of deny-only. Note what
///   this can and cannot do: it shrinks the semantic index, it does not hide anything from
///   <c>read_file</c>, <c>list_files</c> or <c>search_in_files</c> — and the patterns are printed
///   by <c>/onboard</c>, so a repository cannot quietly narrow what you can find.</item>
///   <item><b>Recommended</b> — model roles and <c>contextWindowSize</c> under <c>recommend</c>:
///   displayed by <c>/onboard</c>, applied only by an explicit <c>/onboard apply</c>. These are
///   <em>machine</em> choices (what is installed, how much VRAM you have), so a repository may
///   suggest them and may never decide them.</item>
///   <item><b>Never</b> — everything else. The classification is an <b>allow-list</b>: an unknown
///   key is ignored rather than interpreted. A deny-list of dangerous names would have to be
///   complete to be correct, and the one place this code base already relies on matching hostile
///   text (the shell denylist) is documented as a guard-rail, not a boundary.</item>
/// </list>
/// <para>
/// Nothing here prompts, because nothing here executes: a profile that cannot grant anything has
/// no approval to ask for. That is the whole point of restricting it to preferences.
/// </para>
/// </remarks>
internal sealed record ProjectProfile(
    IReadOnlyList<string>                 IndexExcludes,
    IReadOnlyList<ProfileRecommendation>  Recommendations,
    IReadOnlyList<ProfileIgnoredKey>      Ignored)
{
    /// <summary>An empty profile — what a repository without the file gets.</summary>
    public static readonly ProjectProfile Empty = new([], [], []);

    /// <summary><c>true</c> when the file carried nothing at all we could act on or report.</summary>
    public bool IsEmpty =>
        IndexExcludes.Count == 0 && Recommendations.Count == 0 && Ignored.Count == 0;

    /// <summary>Path of the profile inside a workspace.</summary>
    public static string PathIn(string root) => Path.Combine(root, ".inferpal", "project.json");

    // A cloned repository controls this file: bound what it can cost us. Neither limit is a
    // security boundary (the profile grants nothing) — they keep a silly file from being a
    // silly slowdown on every index pass.
    private const int MaxExcludes      = 100;
    private const int MaxPatternLength = 200;

    /// <summary>Keys accepted under <c>recommend</c>, mapped to the configuration they describe.</summary>
    private static readonly string[] RecommendableKeys =
    [
        "defaultModel", "agentModel", "utilityModel", "codeActionsModel",
        "inlineEditModel", "inlineCompletionModel", "ragEmbeddingModel", "contextWindowSize",
    ];

    /// <summary>
    /// Keys we recognise as belonging to category 3 — they execute, authorise or redirect. Naming
    /// them changes nothing about the outcome (an unknown key is ignored just as thoroughly); it
    /// only lets <c>/onboard</c> say "this repository asked for X and did not get it".
    /// </summary>
    private static readonly string[] SensitiveKeys =
    [
        "validators", "permissions", "permissionRules", "customTools", "mcpServers",
        "baseUrl", "apiKey", "provider", "securityAlertsDisabled", "smartFixEnabled",
    ];

    /// <summary>Reads and parses <c>.inferpal/project.json</c>; never throws, never returns null.</summary>
    public static ProjectProfile Load(string? root)
    {
        if (string.IsNullOrEmpty(root)) return Empty;
        try
        {
            var path = PathIn(root!);
            return File.Exists(path) ? Parse(File.ReadAllText(path), InferpalConfig.Load()) : Empty;
        }
        catch (Exception ex)
        {
            Diagnostics.Swallow("ProjectProfile.Load", ex);
            return Empty;
        }
    }

    /// <summary>
    /// Parses the profile JSON. <paramref name="config"/> is read only to fill in
    /// <see cref="ProfileRecommendation.Current"/>, so a recommendation can be shown next to what
    /// it would replace. Malformed JSON yields <see cref="Empty"/> (recorded in <c>/diagnostics</c>).
    /// </summary>
    public static ProjectProfile Parse(string? json, InferpalConfig? config = null)
    {
        if (string.IsNullOrWhiteSpace(json)) return Empty;

        var excludes = new List<string>();
        var recs     = new List<ProfileRecommendation>();
        var ignored  = new List<ProfileIgnoredKey>();

        try
        {
            // Comments and trailing commas are accepted: this file is written and re-read by
            // humans, and the scaffolded example explains each category in place.
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling     = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return Empty;

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                switch (prop.Name)
                {
                    case "indexExclude":
                        ReadExcludes(prop.Value, excludes);
                        break;

                    case "recommend":
                        ReadRecommendations(prop.Value, config, recs, ignored);
                        break;

                    default:
                        ignored.Add(Reject(prop.Name));
                        break;
                }
            }
        }
        catch (JsonException ex)
        {
            Diagnostics.Swallow("ProjectProfile.Parse", ex);
            return Empty;
        }

        return new ProjectProfile(excludes, recs, ignored);
    }

    private static void ReadExcludes(JsonElement value, List<string> into)
    {
        if (value.ValueKind != JsonValueKind.Array) return;
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) continue;
            var pattern = item.GetString()?.Trim();
            if (string.IsNullOrEmpty(pattern) || pattern!.Length > MaxPatternLength) continue;
            if (into.Count >= MaxExcludes) break;
            into.Add(pattern);
        }
    }

    private static void ReadRecommendations(
        JsonElement value, InferpalConfig? config,
        List<ProfileRecommendation> recs, List<ProfileIgnoredKey> ignored)
    {
        if (value.ValueKind != JsonValueKind.Object) return;

        foreach (var prop in value.EnumerateObject())
        {
            if (!RecommendableKeys.Contains(prop.Name, StringComparer.Ordinal))
            {
                // Nesting a forbidden key under `recommend` does not launder it.
                ignored.Add(Reject(prop.Name));
                continue;
            }

            var proposed = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString() ?? string.Empty,
                JsonValueKind.Number => prop.Value.GetRawText(),
                _                    => string.Empty,
            };
            if (string.IsNullOrWhiteSpace(proposed)) continue;

            recs.Add(new ProfileRecommendation(prop.Name, proposed.Trim(), CurrentValue(prop.Name, config)));
        }
    }

    private static ProfileIgnoredKey Reject(string key)
    {
        var sensitive = SensitiveKeys.Contains(key, StringComparer.OrdinalIgnoreCase);
        Diagnostics.Record("ProjectProfile",
            sensitive
                ? $"Refused key '{key}' from .inferpal/project.json — a repository never sets what executes, authorises or redirects."
                : $"Ignored unknown key '{key}' from .inferpal/project.json.");
        return new ProfileIgnoredKey(key, sensitive);
    }

    private static string CurrentValue(string key, InferpalConfig? c) => c is null ? string.Empty : key switch
    {
        "defaultModel"          => c.DefaultModel,
        "agentModel"            => c.AgentModel,
        "utilityModel"          => c.UtilityModel,
        "codeActionsModel"      => c.CodeActionsModel,
        "inlineEditModel"       => c.InlineEditModel,
        "inlineCompletionModel" => c.InlineCompletionModel,
        "ragEmbeddingModel"     => c.RagEmbeddingModel,
        "contextWindowSize"     => c.ContextWindowSize.ToString(),
        _                       => string.Empty,
    };

    /// <summary>
    /// Writes the recommendations into <paramref name="config"/> — the <c>/onboard apply</c> path,
    /// and the only one: nothing else in the code base calls this. Returns the keys actually
    /// changed, so the command can report what it did rather than claim it did something.
    /// </summary>
    public IReadOnlyList<string> Apply(InferpalConfig config)
    {
        var changed = new List<string>();
        foreach (var rec in Recommendations)
        {
            if (string.Equals(rec.Proposed, rec.Current, StringComparison.Ordinal)) continue;

            switch (rec.Key)
            {
                case "defaultModel":          config.DefaultModel          = rec.Proposed; break;
                case "agentModel":            config.AgentModel            = rec.Proposed; break;
                case "utilityModel":          config.UtilityModel          = rec.Proposed; break;
                case "codeActionsModel":      config.CodeActionsModel      = rec.Proposed; break;
                case "inlineEditModel":       config.InlineEditModel       = rec.Proposed; break;
                case "inlineCompletionModel": config.InlineCompletionModel = rec.Proposed; break;
                case "ragEmbeddingModel":     config.RagEmbeddingModel     = rec.Proposed; break;

                case "contextWindowSize":
                    // A context window is a VRAM commitment: an absurd value would not be a
                    // permission the repository gained, but it would still be the machine paying
                    // for the repository's opinion. Same bounds as the settings form.
                    if (!int.TryParse(rec.Proposed, out var ctx) || ctx < 512 || ctx > 1_000_000) continue;
                    config.ContextWindowSize = ctx;
                    break;

                default: continue;
            }
            changed.Add(rec.Key);
        }
        return changed;
    }
}
