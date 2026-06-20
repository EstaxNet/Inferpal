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
        // 0. Authoritative: the in-process package reports the actually-open solution.
        //    This is the only source that follows solution open/close in an OOP extension.
        var active = ActiveSolutionSignal.TryReadSolutionPath();
        if (active is not null) return active;

        // 1. Walk up from each open editor file. The open documents reflect the solution the user
        //    is actually working in, so they are far more reliable than CWD (which, in an OOP
        //    extension host, never follows solution open/close and may sit near an unrelated .sln).
        foreach (var p in _contextHolder.GetOpenPaths())
        {
            var dir = Path.GetDirectoryName(p);
            if (string.IsNullOrEmpty(dir)) continue;
            var hit = FindSlnInTree(dir);
            if (hit is not null) return hit;
        }

        // 2. Last resort: walk up from CWD. Unreliable in out-of-process extensions, hence last.
        return FindSlnInTree(Directory.GetCurrentDirectory());
    }

    private static string? FindSlnInTree(string startDir)
    {
        var dir = startDir;
        for (int depth = 0; depth < 8; depth++)
        {
            var found = Directory.GetFiles(dir, "*.sln", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (found is not null) return found;

            foreach (var sub in Directory.GetDirectories(dir))
            {
                found = Directory.GetFiles(sub, "*.sln", SearchOption.TopDirectoryOnly).FirstOrDefault();
                if (found is not null) return found;
            }

            var parent = Directory.GetParent(dir)?.FullName;
            if (parent is null || parent == dir) break;
            dir = parent;
        }
        return null;
    }

    // ── Types ─────────────────────────────────────────────────────────────────

    private record ProjectEntry(string Name, string RelativePath, string AbsolutePath);
    private record ProjectInfo(string? TargetFramework, string? OutputType, List<string> ProjectRefs, List<string> Packages);
}
