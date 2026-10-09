using System.IO;
using System.Text.Json;
using Inferpal.Config;

namespace Inferpal.Services.Tools;

/// <summary>
/// <c>read_skill_file</c>: reads a file of a skill (<c>SKILL.md</c> folders of the repository or of the user) —
/// confined to that skill's folder, which <c>read_file</c> cannot reach when it lies outside the workspace
/// (<c>~/.claude/skills</c>).
/// </summary>
/// <remarks>
/// ⚠ Confined to the ONE skill's folder, links resolved (<see cref="PathSanitizer.AssertUnderRoot"/>): a skill is
/// written by a repository, and a path it names (<c>../../.ssh/id_rsa</c>, a link) must not read the machine. Read-only,
/// so no approval — like <c>read_file</c>. Offered only while a skill exists (<see cref="ITool.IsOffered"/>).
/// </remarks>
internal sealed class ReadSkillFileTool(Func<string?> getWorkspaceRoot, InferpalConfig config, Func<string?>? getHome = null) : ITool
{
    public string Name => "read_skill_file";

    public string Description =>
        "Reads a file of a skill — its SKILL.md to load it, or a file its instructions name — by the skill's name and the "
        + "file's path inside the skill's folder. Use start_line to continue a long file.";

    public object Parameters => new
    {
        type = "object",
        properties = new
        {
            skill      = new { type = "string",  description = "The skill's name." },
            path       = new { type = "string",  description = "The file's path, relative to the skill's folder (e.g. scripts/extract.py)." },
            start_line = new { type = "integer", description = "First line to read, 1-based (optional) — to continue a long file." },
            end_line   = new { type = "integer", description = "Last line to read, inclusive (optional)." },
        },
        required = new[] { "skill", "path" }
    };

    public bool IsOffered => Catalog().Skills.Count > 0;

    private SkillCatalog Catalog() =>
        RepoSkills.Load(getWorkspaceRoot(), RepoInstructionFormats.Families(config.RepoInstructionFamilies).On, getHome?.Invoke());

    public Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var catalog = Catalog();
        var name = args.Str("skill");
        if (string.IsNullOrWhiteSpace(name)) return Task.FromResult("Error: 'skill' is required");
        if (catalog.Find(name) is not { } skill)
            return Task.FromResult(catalog.Skills.Count == 0
                ? $"Error: no skill named '{name}' — there is no skill in this workspace."
                : $"Error: no skill named '{name}'. Skills: {string.Join(", ", catalog.Skills.Select(s => s.Name))}.");

        var relative = args.Str("path");
        if (string.IsNullOrWhiteSpace(relative)) return Task.FromResult("Error: 'path' is required");

        string full;
        try
        {
            full = Path.GetFullPath(Path.Combine(skill.Folder, relative));
            PathSanitizer.AssertUnderRoot(full, skill.Folder);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Task.FromResult($"Error: '{relative}' is not inside the skill's folder ({skill.Folder}); read_skill_file reads only the skill's own files.");
        }
        if (Directory.Exists(full))
        {
            var (files, _) = RepoSkills.FilesOf(skill);
            return Task.FromResult($"Error: '{relative}' is a folder. The skill's files: {string.Join(", ", files)}.");
        }
        if (!File.Exists(full)) return Task.FromResult($"Error: '{relative}' does not exist in the skill's folder ({skill.Folder}).");

        if (TextFileEncoding.IsBinaryFile(full)) return Task.FromResult($"'{relative}' is a binary file: it has no text to read.");
        var content = TextFileEncoding.ReadText(full);
        var start = args.Int("start_line", 1);
        var end   = args.Int("end_line", int.MaxValue);
        return Task.FromResult(ReadFileTool.Page(content, relative, start, end));
    }
}
