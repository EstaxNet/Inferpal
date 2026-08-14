using System.IO;
using System.Text;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services.Execution;
using Inferpal.Services.Governance;
using Inferpal.Services.Inference;
using Inferpal.Services.Rag;

namespace Inferpal.Services.Commands;

/// <summary>
/// Pure logic of <c>/onboard</c> — what a freshly cloned repository can say for itself (roadmap
/// §19): read <c>.inferpal/project.json</c> and report it category by category, apply the part
/// the user explicitly asks for, and draft <c>.inferpal/context.md</c> from the repository itself.
/// </summary>
/// <remarks>
/// <para>
/// The command is the visible half of <see cref="ProjectProfile"/>: the profile is deliberately
/// unable to change anything beyond index exclusions, so something has to <em>show</em> what it
/// proposed and what was refused — otherwise "recommended, never applied silently" would just mean
/// "silently discarded". <c>/onboard</c> prints the three categories, and <c>/onboard apply</c> is
/// the only path from the second one to the machine's configuration.
/// </para>
/// <para>
/// Git is injected, as in <see cref="CheckCommandHandler"/>: recent commit subjects are the
/// cheapest description of what a repository is actually about, and injecting the runner keeps
/// this testable without a repository.
/// </para>
/// </remarks>
internal static class OnboardCommandHandler
{
    /// <summary>A file the front-end must write, then open. Overwrites — the handler already decided.</summary>
    internal sealed record GeneratedFile(string Path, string Content);

    /// <param name="Message">Markdown to display (null when a scaffold or write carries the answer).</param>
    /// <param name="Scaffold">Example <c>project.json</c> to create (<c>/onboard init</c>).</param>
    /// <param name="Write">Generated <c>context.md</c> (<c>/onboard context</c>).</param>
    /// <param name="SaveConfig">The configuration was mutated and must be persisted.</param>
    /// <param name="RefreshSystemPrompt">Rebuild the system prompt — <c>context.md</c> is part of it.</param>
    /// <param name="NewDefaultModel">
    /// Set when <c>apply</c> changed the default model: both front-ends display it (VS status
    /// label, VS Code <c>stateChange</c>) and would otherwise keep showing the previous one until
    /// the next reload — the same refresh <c>/model</c> performs.
    /// </param>
    internal readonly record struct OnboardCommandResult(
        string?                                           Message,
        RulesChecksPromptsCommandHandler.ScaffoldRequest? Scaffold            = null,
        GeneratedFile?                                    Write               = null,
        bool                                              SaveConfig          = false,
        bool                                              RefreshSystemPrompt = false,
        string?                                           NewDefaultModel     = null);

    internal const string ProfileExampleContent =
        "{\n" +
        "  // Committed with the repository, and non-privileged by design: it describes\n" +
        "  // preferences, never permissions.\n" +
        "\n" +
        "  // Applied automatically — additive only: these patterns keep files OUT of the\n" +
        "  // semantic index, and nothing here can put more in.\n" +
        "  \"indexExclude\": [\n" +
        "    \"vendor\",\n" +
        "    \"**/*.generated.cs\"\n" +
        "  ],\n" +
        "\n" +
        "  // Shown by `/onboard`, applied only by `/onboard apply`: which model to use and how\n" +
        "  // big a context window to allocate are machine choices, not repository choices.\n" +
        "  \"recommend\": {\n" +
        "    \"agentModel\": \"qwen2.5-coder:14b\",\n" +
        "    \"utilityModel\": \"qwen2.5:3b\",\n" +
        "    \"contextWindowSize\": 16384\n" +
        "  }\n" +
        "\n" +
        "  // Anything else is ignored, and validators / permissions / backend URL / API key are\n" +
        "  // refused by name: a repository never sets what executes, authorises or redirects.\n" +
        "}\n";

    /// <param name="client">Inference provider — only <c>/onboard context</c> uses it.</param>
    /// <param name="config">Read for the report; written by <c>/onboard apply</c>.</param>
    /// <param name="projectRoot">Workspace root.</param>
    /// <param name="parts">Tokenised command.</param>
    /// <param name="git">Git runner supplied by the front-end.</param>
    /// <param name="onProgress">Status line while the model drafts; null = silent.</param>
    public static async Task<OnboardCommandResult> HandleAsync(
        IInferenceProvider client, InferpalConfig config, string? projectRoot, string[] parts,
        GitRunner git, Action<string>? onProgress, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(projectRoot)) return new(Strings.SlashContextNoSln);
        var root = projectRoot!;

        var sub   = parts.Length >= 2 ? parts[1].ToLowerInvariant() : string.Empty;
        var force = parts.Length >= 3 && string.Equals(parts[2], "force", StringComparison.OrdinalIgnoreCase);

        return sub switch
        {
            ""        => new(Report(root, config)),
            "init"    => new(null, new RulesChecksPromptsCommandHandler.ScaffoldRequest(
                             Path.Combine(root, ".inferpal"), "project.json", ProfileExampleContent)),
            "apply"   => Apply(root, config),
            "context" => await GenerateContextAsync(client, config, root, force, git, onProgress, ct),
            _         => new(Strings.OnboardUsage),
        };
    }

    // ── Report ────────────────────────────────────────────────────────────────

    /// <summary>The three categories, printed. A refusal nobody sees is indistinguishable from a bug.</summary>
    private static string Report(string root, InferpalConfig config)
    {
        var profile = ProjectProfile.Parse(ReadProfile(root), config);
        var sb      = new StringBuilder();

        sb.Append(Strings.OnboardHeading).Append("\n\n");

        if (profile.IsEmpty)
        {
            sb.Append(Strings.OnboardNoProfile(ProjectProfile.PathIn(root)));
        }
        else
        {
            if (profile.IndexExcludes.Count > 0)
            {
                sb.Append(Strings.OnboardAppliedHeading).Append('\n');
                foreach (var pattern in profile.IndexExcludes)
                    sb.Append("- `").Append(pattern).Append("`\n");
                sb.Append('\n');
            }

            if (profile.Recommendations.Count > 0)
            {
                sb.Append(Strings.OnboardRecommendedHeading).Append('\n');
                foreach (var rec in profile.Recommendations)
                    sb.Append("- ").Append(Strings.OnboardRecommendLine(
                        rec.Key, rec.Proposed,
                        string.IsNullOrEmpty(rec.Current) ? "—" : rec.Current)).Append('\n');
                sb.Append('\n').Append(Strings.OnboardApplyHint).Append("\n\n");
            }

            if (profile.Ignored.Count > 0)
            {
                sb.Append(Strings.OnboardIgnoredHeading).Append('\n');
                foreach (var key in profile.Ignored)
                    sb.Append("- `").Append(key.Key).Append('`')
                      .Append(key.Sensitive ? " ⛔" : string.Empty).Append('\n');
                sb.Append('\n');
            }
        }

        var contextPath = Path.Combine(root, ".inferpal", "context.md");
        sb.Append(File.Exists(contextPath)
            ? Strings.OnboardContextPresent(contextPath)
            : Strings.OnboardContextMissing);

        return sb.ToString().TrimEnd();
    }

    // ── /onboard apply ────────────────────────────────────────────────────────

    private static OnboardCommandResult Apply(string root, InferpalConfig config)
    {
        var profile = ProjectProfile.Parse(ReadProfile(root), config);
        var changed = profile.Apply(config);

        if (changed.Count == 0) return new(Strings.OnboardNothingToApply);

        return new(
            Strings.OnboardApplied(string.Join(", ", changed)),
            SaveConfig:      true,
            NewDefaultModel: changed.Contains("defaultModel") ? config.DefaultModel : null);
    }

    // ── /onboard context ──────────────────────────────────────────────────────

    private static async Task<OnboardCommandResult> GenerateContextAsync(
        IInferenceProvider client, InferpalConfig config, string root, bool force,
        GitRunner git, Action<string>? onProgress, CancellationToken ct)
    {
        var path = Path.Combine(root, ".inferpal", "context.md");
        if (File.Exists(path) && !force)
            return new(Strings.OnboardContextExists(path));

        onProgress?.Invoke(Strings.OnboardContextReadingLabel);
        var brief = await BuildRepoBriefAsync(root, git, ct);

        onProgress?.Invoke(Strings.OnboardContextDraftingLabel);
        var history = new List<ChatMessageDto>
        {
            new("system", Strings.OnboardContextSystemPrompt),
            new("user",   Strings.OnboardContextUserPrompt(brief)),
        };

        string answer;
        try
        {
            var result = await client.SendChatAsync(
                ModelRouter.Resolve(config, ModelRole.Chat), history, EmptyToolRegistry.Instance, null, ct);
            answer = result.TextContent;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new(Strings.MsgError(ex.Message)); }

        var content = StripCodeFence(answer).Trim();
        if (content.Length == 0) return new(Strings.OnboardContextEmpty);

        // The draft is written, then opened: a generated description of your own project is worth
        // reading and editing, and it lands in the system prompt of every following turn.
        return new(
            Strings.OnboardContextGenerated(path, content.Length),
            Write:               new GeneratedFile(path, content),
            RefreshSystemPrompt: true);
    }

    /// <summary>
    /// A deterministic description of the repository — layout, manifests, README, recent commit
    /// subjects. Deliberately not the model's job: what a repository contains is a fact, and a
    /// fact the model has to guess is a fact it can get wrong.
    /// </summary>
    internal static async Task<string> BuildRepoBriefAsync(string root, GitRunner git, CancellationToken ct)
    {
        const int MaxEntries = 40, MaxReadmeChars = 1_500, MaxCommits = 20;
        const int MaxSampledDirs = 12;

        var sb = new StringBuilder();
        sb.Append("# Repository: ").Append(Path.GetFileName(root.TrimEnd('\\', '/'))).Append("\n\n");

        sb.Append("## Top-level layout\n");
        try
        {
            var dirs    = new List<string>();
            var entries = new List<string>();
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var name = Path.GetFileName(dir);
                if (IndexExclusions.BuiltInDirs.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                dirs.Add(name);
                entries.Add(name + "/");
            }
            foreach (var file in Directory.EnumerateFiles(root))
                entries.Add(Path.GetFileName(file));

            foreach (var entry in entries.Take(MaxEntries)) sb.Append("- ").Append(entry).Append('\n');
            if (entries.Count > MaxEntries) sb.Append("- … +").Append(entries.Count - MaxEntries).Append('\n');

            // One level deeper, sampled. Without it the model only sees folder *names*, and a name
            // is exactly the kind of thing it will happily invent a purpose for: the first probe
            // read "Inferpal.Host" and announced it was the Visual Studio front-end.
            if (dirs.Count > 0) sb.Append("\n## Inside each top-level folder (sample)\n");
            foreach (var dir in dirs.Take(MaxSampledDirs))
            {
                var children = SampleChildren(Path.Combine(root, dir));
                if (children.Count == 0) continue;
                sb.Append("- `").Append(dir).Append("/` → ").Append(string.Join(", ", children)).Append('\n');
            }
        }
        catch (Exception ex) { Diagnostics.Swallow("Onboard.Layout", ex); }

        var readme = FindReadme(root);
        if (readme is not null)
        {
            try
            {
                var text = await File.ReadAllTextAsync(readme, ct);
                sb.Append("\n## ").Append(Path.GetFileName(readme)).Append(" (excerpt)\n")
                  .Append(text.Length > MaxReadmeChars ? text[..MaxReadmeChars] + "…" : text)
                  .Append('\n');
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Diagnostics.Swallow("Onboard.Readme", ex); }
        }

        var log = (await git($"log --format=%s -n {MaxCommits}", ct)).Output;
        if (!string.IsNullOrWhiteSpace(log))
            sb.Append("\n## Recent commit subjects\n").Append(log.Trim()).Append('\n');

        return sb.ToString();
    }

    /// <summary>A few immediate children of a folder — enough to tell what it holds, not a listing.</summary>
    private static List<string> SampleChildren(string dir)
    {
        const int Max = 8;
        var result = new List<string>();
        try
        {
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                var name = Path.GetFileName(sub);
                if (IndexExclusions.BuiltInDirs.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                result.Add(name + "/");
                if (result.Count >= Max) return result;
            }
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                result.Add(Path.GetFileName(file));
                if (result.Count >= Max) return result;
            }
        }
        catch (Exception ex) { Diagnostics.Swallow("Onboard.SampleChildren", ex); }
        return result;
    }

    private static string? FindReadme(string root)
    {
        foreach (var name in new[] { "README.md", "readme.md", "README.txt", "README" })
        {
            var candidate = Path.Combine(root, name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>Unwraps a whole answer wrapped in one fenced block — models like to do that with files.</summary>
    internal static string StripCodeFence(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal)) return text;

        var firstBreak = trimmed.IndexOf('\n');
        if (firstBreak < 0) return text;

        var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        if (lastFence <= firstBreak) return text;

        return trimmed[(firstBreak + 1)..lastFence];
    }

    private static string? ReadProfile(string root)
    {
        try
        {
            var path = ProjectProfile.PathIn(root);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex)
        {
            Diagnostics.Swallow("Onboard.ReadProfile", ex);
            return null;
        }
    }
}
