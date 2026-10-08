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
/// Pure logic of <c>/onboard</c> — what a freshly cloned repository can say for itself: read
/// <c>.inferpal/project.json</c> and report it category by category, apply the part the user
/// explicitly asks for, and draft <c>.inferpal/context.md</c> from the repository itself.
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
    /// <remarks>
    /// ⚠ <c>context.md</c> may have been written by hand: front-ends write it through
    /// <see cref="Execution.BackedUpFileWriter"/>, never directly.
    /// </remarks>
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

    /// <summary>
    /// <c>/onboard init</c>: the example profile, unless the repository already has one. The front-ends
    /// only write a missing file, and would otherwise announce a creation that did not happen.
    /// </summary>
    private static OnboardCommandResult Init(string root)
    {
        var dir  = Path.Combine(root, ".inferpal");
        var path = Path.Combine(dir, "project.json");
        return File.Exists(path)
            ? new(Strings.OnboardProfileExists(path))
            : new(null, new RulesChecksPromptsCommandHandler.ScaffoldRequest(dir, "project.json", ProfileExampleContent));
    }

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
        "    \"agentModel\": \"devstral-small-2:24b\",\n" +
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
            "init"    => Init(root),
            "apply"   => Apply(root, config),
            "context" => await GenerateContextAsync(client, config, root, force, git, onProgress, ct),
            _         => new(Strings.OnboardUsage),
        };
    }

    // ── Report ────────────────────────────────────────────────────────────────

    /// <summary>The three categories, printed. A refusal nobody sees is indistinguishable from a bug.</summary>
    private static string Report(string root, InferpalConfig config)
    {
        var profile = ProjectProfile.Read(root, config);
        var sb      = new StringBuilder();

        sb.Append(Strings.OnboardHeading).Append("\n\n");

        // Every branch ends on a blank line: the context line below is a paragraph of its own.
        if (profile.Problem is { } problem)
        {
            sb.Append(Strings.OnboardProfileUnusable(ProjectProfile.PathIn(root), problem)).Append("\n\n");
        }
        else if (profile.IsEmpty)
        {
            sb.Append(Strings.OnboardNoProfile(ProjectProfile.PathIn(root))).Append("\n\n");
        }
        else
        {
            // The entries left out qualify the list kept: said above it, even when nothing was kept.
            if (profile.NotApplied is { Total: > 0 } notApplied)
                sb.Append(Strings.OnboardExcludesNotApplied(notApplied.Total)).Append("\n\n");
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
        var profile = ProjectProfile.Read(root, config);
        if (profile.Problem is { } problem)
            return new(Strings.OnboardProfileUnusable(ProjectProfile.PathIn(root), problem));

        var changed = profile.Apply(config, out var refused);
        var notApplied = string.Concat(refused.Select(r => "\n\n" + Strings.OnboardContextWindowRefused(r.Value)));

        if (changed.Count == 0)
            return new(refused.Count == 0 ? Strings.OnboardNothingToApply : notApplied.TrimStart('\n'));

        return new(
            Strings.OnboardApplied(string.Join(", ", changed)) + notApplied,
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
            var model  = ModelRouter.Resolve(config, ModelRole.Chat);
            var result = await client.SendChatAsync(model, history, EmptyToolRegistry.Instance, null, ct);
            // A draft that stopped at the length limit ends mid-sentence, and it would become the system
            // prompt of every following session — in place of the user's own file under `force`.
            if (result.CutAtLimit) return new(Strings.OnboardContextCut);
            // Same destination for promoted reasoning: thinking prose about the brief, not a description of the project.
            if (result.AnswerIsReasoning) return new(Strings.MsgOnlyReasoningFrom(model));
            // Reasoning a server sends inline would become part of every following session's system prompt.
            answer = MarkdownParser.WithoutLeadingReasoning(result.TextContent);
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
            var dirs  = new List<string>();
            var files = new List<string>();
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                if (WorkspaceScan.IsExcludedDirName(dir)) continue;
                dirs.Add(Path.GetFileName(dir));
            }
            foreach (var file in Directory.EnumerateFiles(root))
                files.Add(Path.GetFileName(file));
            // ⚠ Sorted BEFORE the caps below: the file system's order is arbitrary under POSIX, so unsorted, which 40
            // entries and which 12 folders make it into the brief — a file committed with the repository, the system
            // prompt of every later session — depends on the machine that writes it.
            dirs.Sort(StringComparer.Ordinal);
            files.Sort(StringComparer.Ordinal);
            var entries = dirs.Select(d => d + "/").Concat(files).ToList();

            foreach (var entry in entries.Take(MaxEntries)) sb.Append("- ").Append(entry).Append('\n');
            if (entries.Count > MaxEntries) sb.Append("- … +").Append(entries.Count - MaxEntries).Append('\n');

            // One level deeper, sampled. Without it the model only sees folder *names*, and a name
            // is exactly the kind of thing it will happily invent a purpose for — "Inferpal.Host"
            // reads as the Visual Studio front-end to anything that has not looked inside.
            if (dirs.Count > 0) sb.Append("\n## Inside each top-level folder (sample)\n");
            var nothingToSample = new List<string>();
            var unreadable      = new List<string>();
            foreach (var dir in dirs.Take(MaxSampledDirs))
            {
                var (children, more, failure) = SampleChildren(Path.Combine(root, dir));
                // ⚠ A folder that could not be read is not an empty one: written "empty" here, the brief tells every later
                // session that a folder full of code holds nothing.
                if (failure is not null) unreadable.Add($"{dir} ({failure})");
                if (children.Count == 0) { if (failure is null) nothingToSample.Add(dir); continue; }
                sb.Append("- `").Append(dir).Append("/` → ").Append(string.Join(", ", children))
                  .Append(more > 0 ? $", … +{more}" : "")
                  .Append(failure is null ? "" : ", … (not read to the end)").Append('\n');
            }

            // ⚠ A folder listed above with no line here is back to being a NAME, which the remark
            // above says is exactly what the model invents a purpose for. Three states render
            // identically without this: cut by the cap, empty, and holding only build/vendor
            // folders — and this brief becomes the system prompt of every later session.
            if (dirs.Count > MaxSampledDirs)
                sb.Append("- ⚠ not looked inside (the sample stops at ").Append(MaxSampledDirs)
                  .Append(" folders): ").Append(string.Join(", ", dirs.Skip(MaxSampledDirs))).Append('\n');
            if (nothingToSample.Count > 0)
                sb.Append("- ⚠ nothing to sample (empty, or only build/vendor folders): ")
                  .Append(string.Join(", ", nothingToSample)).Append('\n');
            if (unreadable.Count > 0)
                sb.Append("- ⚠ could not be read, so their content is unknown: ")
                  .Append(string.Join(", ", unreadable)).Append('\n');
        }
        catch (Exception ex)
        {
            Diagnostics.Swallow("Onboard.Layout", ex);
            // A heading with nothing under it reads as "this repository has no top-level folders".
            sb.Append("- ⚠ the layout could not be read: ").Append(ReadFailure(ex)).Append('\n');
        }

        var readme = FindReadme(root);
        if (readme is not null)
        {
            try
            {
                var text = await Tools.TextFileEncoding.ReadTextAsync(readme, ct);
                sb.Append("\n## ").Append(Path.GetFileName(readme)).Append(" (excerpt)\n")
                  .Append(text.Length > MaxReadmeChars ? text[..MaxReadmeChars] + "…" : text)
                  .Append('\n');
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Diagnostics.Swallow("Onboard.Readme", ex); }
        }

        // ⚠ This brief is what `/onboard context` writes into `.inferpal/context.md`, i.e. into the
        // system prompt of every session after it — and the summary above says "a fact the model has
        // to guess is a fact it can get wrong". A refused git arrives here as a non-empty output
        // (RunAsync appends stderr), so `fatal: …` was pasted under this heading and read as this
        // project's most recent commit. English on purpose, like the rest of the brief: its only
        // reader is the model.
        var log = await git($"log --format=%s -n {MaxCommits}", ct);
        sb.Append("\n## Recent commit subjects\n");
        if (log.ExitCode != 0)
            sb.Append("(unavailable — git said: ").Append(GitProcess.FirstLine(log.Output)).Append(")\n");
        else
            sb.Append(string.IsNullOrWhiteSpace(log.Output) ? "(none)" : log.Output.Trim()).Append('\n');

        return sb.ToString();
    }

    /// <summary>
    /// A few immediate children of a folder — enough to tell what it holds, not a listing —, how many more it holds, and,
    /// when the folder could not be read to the end, why (<c>null</c> otherwise).
    /// </summary>
    /// <remarks>
    /// ⚠ The first ones in ORDINAL order, folders first: the first ones the file system hands out are an arbitrary pick
    /// under POSIX. And the rest is counted — eight names read as the whole folder otherwise.
    /// </remarks>
    private static (List<string> Children, int More, string? Failure) SampleChildren(string dir)
    {
        const int Max = 8;
        var subs    = new List<string>();
        var files   = new List<string>();
        string? failure = null;
        try
        {
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                if (WorkspaceScan.IsExcludedDirName(sub)) continue;
                subs.Add(Path.GetFileName(sub) + "/");
            }
            foreach (var file in Directory.EnumerateFiles(dir))
                files.Add(Path.GetFileName(file));
        }
        catch (Exception ex)
        {
            Diagnostics.Swallow("Onboard.SampleChildren", ex);
            failure = ReadFailure(ex);
        }
        subs.Sort(StringComparer.Ordinal);
        files.Sort(StringComparer.Ordinal);
        var all = subs.Concat(files).ToList();
        return (all.Take(Max).ToList(), Math.Max(0, all.Count - Max), failure);
    }

    /// <summary>
    /// Why a folder could not be read, as the brief says it — the cause, never the exception's message: that message
    /// carries the absolute path (the home directory), and <c>.inferpal/context.md</c> is committed with the repository.
    /// </summary>
    private static string ReadFailure(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "access denied",
        DirectoryNotFoundException  => "removed while being read",
        IOException                 => "read error",
        _                           => ex.GetType().Name,
    };

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
}
