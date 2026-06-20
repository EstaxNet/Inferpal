using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Inferpal.Localization;

namespace Inferpal.Services.Tools;

internal class GetSolutionInfoTool : ITool
{
    private readonly VsContextHolder _contextHolder;

    public GetSolutionInfoTool(VsContextHolder contextHolder) => _contextHolder = contextHolder;

    // Solution folder pseudo-type — not a real project
    private const string SolutionFolderGuid = "2150E333-8FDC-42A3-9474-1A3956D46DE8";

    public string Name        => "get_solution_info";
    public string Description =>
        "Returns the structure of the Visual Studio solution: solution name, projects with their " +
        "target frameworks, output types, project references, and NuGet packages.";

    public object Parameters => new
    {
        type = "object",
        properties = new
        {
            path = new
            {
                type        = "string",
                description = "Path to a .sln file or a directory containing one (optional, auto-detects if omitted)."
            }
        },
        required = Array.Empty<string>()
    };

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        string? slnPath = null;

        if (args.TryGetProperty("path", out var p) && p.GetString() is string provided && !string.IsNullOrWhiteSpace(provided))
        {
            if (Directory.Exists(provided))
                slnPath = Directory.GetFiles(provided, "*.sln", SearchOption.TopDirectoryOnly).FirstOrDefault();
            else if (File.Exists(provided) && provided.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
                slnPath = provided;
            else
                return Strings.SolutionPathNotFound(provided);
        }

        slnPath ??= FindSolutionFile();
        if (slnPath is null)
            return Strings.SolutionNoSln;

        // Remember this resolution so a later /solution still works when no editor is open and the
        // in-process active-solution signal is absent (package not loaded / solution closed).
        LastKnownSolutionFile.Record(slnPath);

        var slnDir     = Path.GetDirectoryName(slnPath)!;
        var slnContent = await File.ReadAllTextAsync(slnPath, ct);
        var projects   = ParseSolutionProjects(slnContent, slnDir);

        var sb = new StringBuilder();
        sb.AppendLine($"Solution : {Path.GetFileName(slnPath)}");
        sb.AppendLine($"Location : {slnPath}");
        sb.AppendLine($"Projects : {projects.Count}");

        foreach (var proj in projects)
        {
            sb.AppendLine();
            sb.AppendLine($"── {proj.Name}");
            sb.AppendLine($"   File : {proj.RelativePath}");

            if (!File.Exists(proj.AbsolutePath))
            {
                sb.AppendLine("   [project file not found]");
                continue;
            }

            var info = await ReadProjectInfoAsync(proj.AbsolutePath, ct);

            if (info.TargetFramework is not null)
                sb.AppendLine($"   Framework : {info.TargetFramework}");
            if (info.OutputType is not null)
                sb.AppendLine($"   Output    : {info.OutputType}");
            if (info.ProjectRefs.Count > 0)
                sb.AppendLine($"   Refs      : {string.Join(", ", info.ProjectRefs)}");
            if (info.Packages.Count > 0)
                sb.AppendLine($"   Packages  : {string.Join(", ", info.Packages)}");
        }

        return sb.ToString().TrimEnd();
    }

    // ── Parsing .sln ──────────────────────────────────────────────────────────

    private static readonly Regex _projectLine = new(
        @"Project\(""\{([A-F0-9\-]+)\}""\)\s*=\s*""([^""]+)""\s*,\s*""([^""]+)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static List<ProjectEntry> ParseSolutionProjects(string slnContent, string slnDir)
    {
        var results = new List<ProjectEntry>();

        foreach (Match m in _projectLine.Matches(slnContent))
        {
            if (m.Groups[1].Value.Equals(SolutionFolderGuid, StringComparison.OrdinalIgnoreCase))
                continue;

            var name     = m.Groups[2].Value;
            var relPath  = m.Groups[3].Value.Replace('\\', Path.DirectorySeparatorChar);
            var absPath  = Path.GetFullPath(Path.Combine(slnDir, relPath));

            results.Add(new ProjectEntry(name, relPath, absPath));
        }

        return results;
    }

    // ── Parsing .csproj / .vbproj / .fsproj ──────────────────────────────────

    private static async Task<ProjectInfo> ReadProjectInfoAsync(string projPath, CancellationToken ct)
    {
        try
        {
            var xml = await File.ReadAllTextAsync(projPath, ct);
            var doc = XDocument.Parse(xml);

            var tf = doc.Descendants("TargetFramework").FirstOrDefault()?.Value
                  ?? doc.Descendants("TargetFrameworks").FirstOrDefault()?.Value;

            var outputType = doc.Descendants("OutputType").FirstOrDefault()?.Value ?? "Library";

            var projRefs = doc.Descendants("ProjectReference")
                .Select(e => Path.GetFileNameWithoutExtension(e.Attribute("Include")?.Value ?? ""))
                .Where(n => !string.IsNullOrEmpty(n))
                .OrderBy(n => n)
                .ToList();

            var packages = doc.Descendants("PackageReference")
                .Select(e => e.Attribute("Include")?.Value ?? "")
                .Where(n => !string.IsNullOrEmpty(n))
                .OrderBy(n => n)
                .ToList();

            return new ProjectInfo(tf, outputType, projRefs, packages);
        }
        catch
        {
            return new ProjectInfo(null, null, [], []);
        }
    }

    // ── Discovery ─────────────────────────────────────────────────────────────

    private string? FindSolutionFile()
    {
        // 0. Authoritative: the in-process package reports the actually-open solution. This is the
        //    only source that follows solution open/close in an OOP extension, so it wins, and it
        //    carries the exact .sln path (correct even when a directory holds several .sln files).
        var active = ActiveSolutionSignal.TryReadSolutionPath();
        if (active is not null) return active;

        // 1. Live probes via the shared, unit-tested locator: walk up from each open editor file,
        //    then a .sln search anchored near CWD. Returns the directory containing a .sln (or null
        //    when none is reachable) — we then pick the .sln inside it.
        var liveDir = new ProjectRootLocator().LocateReliable(
            _contextHolder.GetOpenPaths(),
            activeSolutionDir: null,                 // step 0 already handled the signal above
            Directory.GetCurrentDirectory());
        if (liveDir is not null &&
            Directory.GetFiles(liveDir, "*.sln", SearchOption.TopDirectoryOnly).FirstOrDefault() is { } liveSln)
            return liveSln;

        // 2. Durable last resort: the last solution Inferpal resolved this/previous session. Covers
        //    the common case where the user is in the chat window with no document open and the
        //    active-solution signal is absent — every live source above then comes up empty.
        return LastKnownSolutionFile.TryReadSolutionPath();
    }

    // ── Types ─────────────────────────────────────────────────────────────────

    private record ProjectEntry(string Name, string RelativePath, string AbsolutePath);
    private record ProjectInfo(string? TargetFramework, string? OutputType, List<string> ProjectRefs, List<string> Packages);
}
