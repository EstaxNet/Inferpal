using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Inferpal.Localization;

namespace Inferpal.Services.Tools;

internal class GetSolutionInfoTool : ITool
{
    private readonly IEditorSurface _editor;
    private readonly Func<string?>  _workspaceRoot;

    public GetSolutionInfoTool(IEditorSurface editor, Func<string?>? workspaceRoot = null)
    {
        _editor        = editor;
        _workspaceRoot = workspaceRoot ?? (() => null);
    }

    // Solution folder pseudo-type — not a real project

    public string Name        => "get_solution_info";
    public string Description =>
        "Returns the structure of the .NET solution open in the workspace: solution name, projects with " +
        "their target frameworks, output types, project references, and NuGet packages.";

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

        if (args.Trimmed("path") is { } provided)
        {
            if (Directory.Exists(provided))
                slnPath = SolutionFiles.FirstIn(provided);
            else if (File.Exists(provided) && SolutionFiles.IsSolution(provided))
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
        // ⚠ The format picks the parser: a .slnx is XML, and the regex reading a .sln's
        // Project(...) lines finds nothing in it -- "Projects : 0" on a valid solution
        // (issue #9). See SolutionFiles, the single reader for both formats.
        var projects   = SolutionFiles.ParseProjects(slnPath, slnContent);

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
        //    The search starts from the workspace root: the process's current directory is only a
        //    stand-in when no root is known — the out-of-process host under VS never sits there.
        var root    = _workspaceRoot();
        var liveDir = new ProjectRootLocator().LocateReliable(
            _editor.GetOpenDocumentPaths(),
            activeSolutionDir: null,                 // step 0 already handled the signal above
            string.IsNullOrWhiteSpace(root) ? Directory.GetCurrentDirectory() : root);
        if (liveDir is not null && SolutionFiles.FirstIn(liveDir) is { } liveSln)
            return liveSln;

        // 2. Durable last resort: the last solution Inferpal resolved this/previous session. Covers
        //    the common case where the user is in the chat window with no document open and the
        //    active-solution signal is absent — every live source above then comes up empty.
        //    ⚠ The cache is machine-wide: under a known root, a solution recorded elsewhere is another
        //    project's, and "no solution" is the true answer.
        return LastKnownSolutionFile.TryReadSolutionPath() is { } known && LastKnownApplies(known, root)
            ? known
            : null;
    }

    /// <summary>
    /// Whether the machine-wide last-known solution may stand for this workspace.
    /// </summary>
    /// <remarks>
    /// <c>last_solution.json</c> means "the last solution Inferpal knew about", on any project and in
    /// either editor. Under a known workspace root, a solution recorded outside it belongs to another
    /// project: reporting it would describe that project as this one. The comparison is the
    /// sandbox's own (<see cref="PathSanitizer.AssertUnderRoot"/>, links and case included); a path
    /// that cannot be resolved counts as outside. An unknown root keeps the fallback it exists for.
    /// </remarks>
    internal static bool LastKnownApplies(string solutionPath, string? workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot)) return true;
        try
        {
            PathSanitizer.AssertUnderRoot(solutionPath, workspaceRoot);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // ── Types ─────────────────────────────────────────────────────────────────

    private record ProjectInfo(string? TargetFramework, string? OutputType, List<string> ProjectRefs, List<string> Packages);
}
