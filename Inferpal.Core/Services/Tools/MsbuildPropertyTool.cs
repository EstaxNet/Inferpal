using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Inferpal.Localization;

namespace Inferpal.Services.Tools;

/// <summary>
/// <c>msbuild_property</c>: the value an MSBuild property has for a project, and WHERE it comes from — the file and line that
/// set it, then each file that changed it — read from MSBuild's own property tracking on an evaluation.
/// </summary>
/// <remarks>
/// ⚠ A build error names a symptom ("not available in C# 7.3", "no static Main"), never the file that decided it: a
/// Directory.Build.props, an import, or a NuGet package's build files, outside the repository where no read tool reaches. The
/// model then repairs at random. With <c>MSBUILDLOGPROPERTYTRACKING=15</c>, an evaluation logs every first value and every
/// reassignment with its file and line (diagnostic file log) — no dependency, a fraction of a second.
/// ⚠ An evaluation runs the repository's InitialTargets: asked like a build (<see cref="Execution.WorkspaceCodeConsent"/>).
/// </remarks>
internal sealed class MsbuildPropertyTool(Execution.WorkspaceCodeConsent? consent, Func<string?> getRoot) : ITool
{
    private readonly Execution.WorkspaceCodeConsent? _consent = consent;

    internal const int BudgetSeconds = 60, MaxLines = 40;

    public string Name => "msbuild_property";

    /// <inheritdoc/>
    public ToolEffect EffectOf(JsonElement args) => ToolEffect.Runs;

    public string Description =>
        "Tells the value an MSBuild property has for a .NET project, and WHERE it comes from: the file and line that set it " +
        "first, then each file that changed it, in order — a Directory.Build.props, an imported file, a NuGet package's " +
        "build files or the SDK. Use it when a build error is about a build setting (language version, target framework, " +
        "output type, warnings treated as errors, which files are compiled) that the project file does not set itself.";

    public object Parameters => new
    {
        type = "object",
        properties = new
        {
            name    = new { type = "string", description = "The property, e.g. LangVersion, TargetFramework, OutputType, TreatWarningsAsErrors." },
            project = new { type = "string", description = "The project file (optional when the workspace has a single one)." },
        },
        required = new[] { "name" },
    };

    private string? _offeredRoot;
    private bool _offered;

    /// <summary>Offered where the workspace holds a .NET project or solution: elsewhere it can only say there is none.</summary>
    public bool IsOffered
    {
        get
        {
            var root = getRoot();
            if (string.IsNullOrEmpty(root)) return false;
            if (!string.Equals(root, _offeredRoot, StringComparison.Ordinal))
            {
                _offered = GetDiagnosticsTool.FindProjectFile(root) is not null;
                _offeredRoot = root;
            }
            return _offered;
        }
    }

    private static readonly Regex PropertyName = new(@"^[A-Za-z_][A-Za-z0-9_.-]{0,127}$", RegexOptions.CultureInvariant, RegexBudget.Default);

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var name = args.Trimmed("name");
        if (name is null || !PropertyName.IsMatch(name)) return "Error: 'name' must be an MSBuild property name, e.g. LangVersion.";
        var root = getRoot();
        if (string.IsNullOrEmpty(root)) return Execution.ToolFailure.Said(PathSanitizer.NoWorkspaceRoot);

        string project;
        if (args.Trimmed("project") is { } given)
        {
            project = PathSanitizer.Sanitize(given, root);
            PathSanitizer.AssertUnderRoot(project, root);
            if (!File.Exists(project)) return Execution.ToolFailure.Said(Strings.ToolFileNotFound(given));
            if (Path.GetExtension(project).ToLowerInvariant() is not (".csproj" or ".fsproj" or ".vbproj"))
                return "Error: 'project' must be a project file (.csproj, .fsproj or .vbproj): a solution evaluates no project's properties.";
        }
        else
        {
            var projects = WorkspaceScan.ShallowestFirst(WorkspaceScan.EnumerateFiles(root, "*.*proj")
                .Where(p => Path.GetExtension(p).ToLowerInvariant() is ".csproj" or ".fsproj" or ".vbproj")).ToList();
            if (projects.Count == 0) return GetDiagnosticsTool.NoProject;
            if (projects.Count > 1)
                return "Error: the workspace has several projects; name one in 'project': " +
                       NamedFew.Of(projects.Select(p => Path.GetRelativePath(root, p).Replace('\\', '/')).ToList(), 10) + ".";
            project = projects[0];
        }

        if (_consent is not null && await _consent.RefusalAsync(Name, ct) is { } refused) return refused;

        var shown = Path.GetRelativePath(root, project).Replace('\\', '/');
        var (run, lines) = await EvaluateAsync(project, framework: null, ct);
        if (run.TimedOut) return $"The evaluation of {shown} did not finish within {BudgetSeconds} s: it was stopped, nothing was read.";
        var note = string.Empty;
        // ⚠ A multi-targeted project's outer evaluation has no such target (MSB4057), and its values are not the ones an inner
        // build compiles with (a LangVersion set per framework): read again for its first framework, and said.
        if (run.ExitCode != 0 && run.Combined.Contains("MSB4057", StringComparison.Ordinal)
            && LastValue("TargetFrameworks", lines) is { Length: > 0 } frameworks)
        {
            var first = frameworks.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];
            (run, lines) = await EvaluateAsync(project, first, ct);
            note = $"\n(The project builds several target frameworks — {frameworks} — and this is the build for {first}; a value can differ per framework.)";
        }
        if (run.TimedOut) return $"The evaluation of {shown} did not finish within {BudgetSeconds} s: it was stopped, nothing was read.";
        // ⚠ A failed evaluation (a missing import, a malformed file) read as "no file sets it" is a confident wrong answer.
        if (run.ExitCode != 0)
            return $"The evaluation of {shown} failed (exit code {run.ExitCode}), so nothing it says about {name} can be trusted:\n{Tail(run.Combined)}";
        // Not restored: the packages' build files (obj/*.nuget.g.props) are not imported, and a value a package sets is missing.
        var restored = File.Exists(Path.Combine(Path.GetDirectoryName(project)!, "obj", Path.GetFileName(project) + ".nuget.g.props"));
        if (!restored)
            note += "\n(The project was never restored: the build files of its NuGet packages are not read, so a value a package sets does not appear — run dotnet restore, then ask again.)";
        return Report(name, shown, lines, root) + note;
    }

    private static string Tail(string output)
    {
        var text = output.Trim();
        return text.Length > 2000 ? "…" + text[^2000..] : text;
    }

    /// <summary>One evaluation, its diagnostic log read into memory (an evaluation's log is a few hundred kilobytes).</summary>
    private static async Task<(ChildProcessResult Run, List<string> Lines)> EvaluateAsync(string project, string? framework, CancellationToken ct)
    {
        var dir = Directory.CreateTempSubdirectory("inferpal-msbuild-").FullName;
        try
        {
            var log = Path.Combine(dir, "evaluation.log");
            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                // An SDK target that only checks the configuration: the evaluation runs, nothing is built or written.
                ArgumentList = { "msbuild", project, "-t:_CheckForInvalidConfigurationAndPlatform", $"-flp:v=diag;logfile={log}",
                                 "-nologo", "-v:q" },
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding  = System.Text.Encoding.UTF8,
            };
            if (framework is not null) psi.ArgumentList.Add($"-p:TargetFramework={framework}");
            ChildProcess.DotnetInEnglish(psi);
            // The exact name MSBuild reads (Traits.cs): Linux and macOS match an environment variable's name case-sensitively.
            psi.EnvironmentVariables["MsBuildLogPropertyTracking"] = "15";
            var run = await ChildProcess.RunAsync(psi, TimeSpan.FromSeconds(BudgetSeconds), ct);
            return (run, File.Exists(log) ? [.. File.ReadLines(log)] : []);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* temporary folder: best-effort cleanup */ }
        }
    }

    /// <summary>The last value the log gives <paramref name="name"/>; <c>null</c> when none.</summary>
    private static string? LastValue(string name, IEnumerable<string> log) =>
        Steps(name, log).LastOrDefault().Value;

    // `Property initial value: $(LangVersion)="7.3" Source: C:\…\legacy.props (3,5)` and
    // `Property reassignment: $(X)="b" (previous value: "a") at C:\…\file.targets (12,5)` — as the evaluation logs them.
    private static Regex Tracked(string name) => new(
        @"Property (?<kind>initial value|reassignment): \$\(" + Regex.Escape(name) + @"\)=""(?<value>.*?)""(?: \(previous value: "".*?""\))? (?:Source:|at) (?<file>.+?) \((?<line>\d+),\d+\)\s*$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline, RegexBudget.Default);

    /// <summary>
    /// Each value <paramref name="name"/> is given, in order, with its file and line. ⚠ A value written over several lines
    /// (a <c>NoWarn</c> list) is logged over several lines, each prefixed again: joined before it is read.
    /// </summary>
    private static IEnumerable<(bool First, string Value, string File, string Line)> Steps(string name, IEnumerable<string> log)
    {
        var tracked = Tracked(name);
        var opener = $"$({name})=";
        string? open = null;
        var continued = 0;
        foreach (var line in log)
        {
            if (open is not null)
            {
                // A continuation: the text after the logger's own "message :" prefix, when it has one.
                var at = line.IndexOf("message :", StringComparison.Ordinal);
                open += "\n" + (at >= 0 ? line[(at + "message :".Length)..].TrimStart() : line);
                if (++continued > 50) { open = null; continue; }
            }
            else if (line.Contains(opener, StringComparison.OrdinalIgnoreCase) && line.Contains("Property ", StringComparison.Ordinal))
            {
                open = line;
                continued = 0;
            }
            else continue;
            if (tracked.Match(open) is not { Success: true } m) continue;
            open = null;
            yield return (m.Groups["kind"].Value.StartsWith("initial", StringComparison.OrdinalIgnoreCase), m.Groups["value"].Value,
                          m.Groups["file"].Value, m.Groups["line"].Value);
        }
    }

    /// <summary>The value and its origins, as the model reads them; paths in the workspace relative, a package named, a value
    /// from a file the ignore files exclude not shown.</summary>
    internal static string Report(string name, string project, IEnumerable<string> log, string root)
    {
        var steps = new List<string>();
        string? value = null;
        foreach (var (first, raw, file, line) in Steps(name, log))
        {
            // ⚠ The ignore files keep a file from the model; its values are its content.
            var hidden = Governance.ContentExclusions.Of(file.Trim()) is not null;
            value = hidden ? null : raw;
            var shownValue = hidden ? "a value not shown (the ignore files exclude this file)" : $"\"{raw}\"";
            var step = $"{(first ? "set" : "changed")} to {shownValue} in {Where(file, root)} line {line}";
            if (!steps.Contains(step)) steps.Add(step);
        }
        if (steps.Count == 0)
            return $"{name}: no file of {project}'s evaluation sets it — its value is MSBuild's default (empty), or comes from the command line or an environment variable.";
        var shown = steps.Count > MaxLines ? steps.Take(MaxLines / 2).Concat([$"… {steps.Count - MaxLines} more …"]).Concat(steps.TakeLast(MaxLines / 2)) : steps;
        return $"{name} = {(value is null ? "(not shown)" : $"\"{value}\"")} for {project}. Where it is set, in the order MSBuild read it (the last one wins):\n- " +
               string.Join("\n- ", shown);
    }

    private static readonly Regex PackageVersion = new(@"^\d+(\.\d+)+", RegexOptions.CultureInvariant, RegexBudget.Default);

    /// <summary>A file as the model can act on it: relative in the workspace, by its package for a NuGet package's build
    /// files, by its place in the SDK, else as written.</summary>
    internal static string Where(string file, string root)
    {
        var full = file.Trim();
        var relative = Path.GetRelativePath(root, full);
        if (!PathSanitizer.LeavesRoot(relative)) return relative.Replace('\\', '/');
        var parts = full.Replace('\\', '/').Split('/');
        // …/<packages folder>/<id>/<version>/build/… — the global packages folder, wherever NUGET_PACKAGES puts it.
        var build = Array.FindLastIndex(parts, p => p is "build" or "buildTransitive" or "buildMultiTargeting");
        if (build >= 2 && PackageVersion.IsMatch(parts[build - 1]))
            return $"the NuGet package {parts[build - 2]} {parts[build - 1]} ({string.Join('/', parts[build..])})";
        var sdk = Array.FindIndex(parts, p => p.Equals("sdk", StringComparison.OrdinalIgnoreCase));
        if (sdk >= 0 && sdk + 2 < parts.Length) return $"the .NET SDK {parts[sdk + 1]} ({string.Join('/', parts[(sdk + 2)..])})";
        return full;
    }
}
