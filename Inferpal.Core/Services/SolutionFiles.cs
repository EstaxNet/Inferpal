using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Inferpal.Services;

/// <summary>A project referenced by a solution, whatever the solution's format.</summary>
/// <param name="Name">Display name.</param>
/// <param name="RelativePath">Path as written in the solution, with native separators.</param>
/// <param name="AbsolutePath">Resolved against the solution's directory.</param>
internal sealed record SolutionProject(string Name, string RelativePath, string AbsolutePath);

/// <summary>
/// The single reader for "where is the solution, and what does it contain?" — across the two
/// formats Visual Studio ships: the classic text <c>.sln</c> and the XML <c>.slnx</c>.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ <b>Never look for a solution with the <c>"*.sln"</c> pattern</b> (issue #9, measured
/// 2026-09-10). On Windows, <c>Directory.GetFiles(dir, "*.sln")</c> <b>sometimes</b> returns
/// <c>.slnx</c> files too, through <b>8.3</b> short-name matching, never by intent. ⚠ And
/// "sometimes" is the word: measured the same evening on one machine, one <c>%TEMP%</c> directory
/// returned the <c>.slnx</c> for the <c>*.sln</c> pattern and another directory on the <b>same
/// volume</b> did not. Short-name generation is configured per volume
/// (<c>NtfsDisable8dot3NameCreation</c> = 2 there) and has never been a guarantee of anything —
/// which is why the "No <c>.sln</c> file found" report came from a <c>G:\</c> drive while the
/// maintainer's repository, on the system volume, looked fine. A result that depends on the volume
/// is a wrong answer to a question that does not.
/// </para>
/// <para>
/// Filtering is therefore <b>explicit on the extension</b>, never left to the pattern. And
/// <c>.slnx</c> is an <b>XML</b> format: the regex that reads the <c>Project(…)</c> lines of a
/// <c>.sln</c> finds nothing in it, which produced "Projects : 0" on a perfectly valid solution —
/// a wrong answer, not a missing capability.
/// </para>
/// </remarks>
internal static class SolutionFiles
{
    /// <summary>Solution folders are not projects (classic format only).</summary>
    private const string SolutionFolderGuid = "2150E333-8FDC-42A3-9474-1A3956D46DE8";

    /// <summary>True when the path names a solution in either format.</summary>
    public static bool IsSolution(string? path) =>
        path is not null
        && (path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Every solution file directly inside <paramref name="dir"/>, both formats, deterministically.
    /// Empty when the directory is unreadable — callers treat that as "no solution here".
    /// </summary>
    public static IReadOnlyList<string> FindIn(string? dir)
    {
        if (string.IsNullOrEmpty(dir)) return [];
        try
        {
            // "*" then an explicit extension test: see the remarks — a "*.sln" pattern is a
            // volume-dependent answer to a question that must not depend on the volume.
            return [.. Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly)
                                .Where(IsSolution)
                                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception ex)
        {
            Diagnostics.Swallow($"SolutionFiles.FindIn({dir})", ex);
            return [];
        }
    }

    /// <summary>True when <paramref name="dir"/> directly contains a solution of either format.</summary>
    public static bool DirectoryHasSolution(string? dir) => FindIn(dir).Count > 0;

    /// <summary>The first solution in <paramref name="dir"/>, or <c>null</c>.</summary>
    public static string? FirstIn(string? dir) => FindIn(dir).FirstOrDefault();

    // ── Parsing ───────────────────────────────────────────────────────────────

    private static readonly Regex _projectLine = new(
        @"Project\(""\{([A-F0-9\-]+)\}""\)\s*=\s*""([^""]+)""\s*,\s*""([^""]+)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexBudget.Default);

    /// <summary>
    /// The projects a solution references. Dispatches on the file's extension, not on a guess about
    /// the content: a <c>.slnx</c> is XML and yields nothing to the classic regex.
    /// </summary>
    public static IReadOnlyList<SolutionProject> ParseProjects(string solutionPath, string content)
    {
        var dir = Path.GetDirectoryName(solutionPath) ?? ".";
        return solutionPath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)
            ? ParseSlnx(content, dir)
            : ParseSln(content, dir);
    }

    private static List<SolutionProject> ParseSln(string content, string dir)
    {
        var results = new List<SolutionProject>();
        foreach (Match m in _projectLine.Matches(content))
        {
            if (m.Groups[1].Value.Equals(SolutionFolderGuid, StringComparison.OrdinalIgnoreCase))
                continue;
            results.Add(Entry(m.Groups[3].Value, dir, m.Groups[2].Value));
        }
        return results;
    }

    /// <summary>
    /// Reads the XML solution format. A <c>&lt;Project Path="App/App.csproj" /&gt;</c> may sit at the
    /// root or inside any depth of <c>&lt;Folder&gt;</c>, so descendants are read rather than direct
    /// children. The display name is the file name without its extension — the format does not carry
    /// one, and inventing one from the folder would rename projects that live in a differently named
    /// directory.
    /// </summary>
    private static List<SolutionProject> ParseSlnx(string content, string dir)
    {
        var results = new List<SolutionProject>();
        try
        {
            var doc = XDocument.Parse(content);
            foreach (var el in doc.Descendants().Where(e => e.Name.LocalName == "Project"))
            {
                var raw = el.Attribute("Path")?.Value;
                if (string.IsNullOrWhiteSpace(raw)) continue;
                results.Add(Entry(raw!, dir, Path.GetFileNameWithoutExtension(raw!.Replace('\\', '/'))));
            }
        }
        catch (Exception ex)
        {
            // An unreadable .slnx is not "zero projects": it is a failed read, and conflating the
            // two is exactly what this class exists to correct.
            Diagnostics.Swallow("SolutionFiles.ParseSlnx", ex);
        }
        return results;
    }

    private static SolutionProject Entry(string rawPath, string dir, string name)
    {
        var rel = rawPath.Replace('\\', Path.DirectorySeparatorChar)
                         .Replace('/', Path.DirectorySeparatorChar);
        return new SolutionProject(name, rel, Path.GetFullPath(Path.Combine(dir, rel)));
    }
}
