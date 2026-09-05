using System.IO;
using System.Text;

namespace Inferpal.Services.Governance;

/// <summary>
/// Reads the <c>*.md</c> files of a <c>.inferpal/</c> folder — rules, checks, prompt files — and
/// reports the ones that could <b>not</b> be read instead of letting them disappear.
/// </summary>
/// <remarks>
/// ⚠ The three services each did <c>try { … } catch { continue; }</c>. A file held open by an
/// editor, a permission problem, a network drive that dropped: the file simply left the list.
/// In order of severity:
/// <list type="bullet">
///   <item>a <b>rule</b> the user wrote to constrain the model stopped applying, and the model
///   answered as though it had never been written;</item>
///   <item><c>/check</c> reviewed a diff against fewer criteria than the user believed;</item>
///   <item>a <c>/yourcommand</c> ceased to exist — the only one of the three that announces itself.</item>
/// </list>
///
/// The repository had already written the right rule, in <c>PlanStore.List</c>: "A plan we cannot
/// read is still a plan the user has: list it rather than hide it, so a permission problem shows up
/// instead of a silently shorter list." It held for all four; it was applied to one.
///
/// ⚠ What stays a deliberate silent omission: a file that is <b>readable but empty</b>. That is not
/// a failure, it is a file with nothing in it — reporting it would be noise about a state the user
/// created on purpose. Only a READ failure is a fact worth reporting.
/// </remarks>
internal static class MarkdownFolder
{
    /// <summary>
    /// The <c>*.md</c> files of <paramref name="dir"/>, alphabetical, with their text.
    /// <paramref name="unreadable"/> receives the names of the files whose read failed — recorded
    /// in <c>/diagnostics</c> along the way, with their cause.
    /// </summary>
    internal static IReadOnlyList<(string Path, string Text)> ReadAll(
        string dir, string context, out IReadOnlyList<string> unreadable)
    {
        unreadable = [];
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return [];

        var read    = new List<(string, string)>();
        var failed  = new List<string>();

        foreach (var file in Directory.EnumerateFiles(dir, "*.md", SearchOption.TopDirectoryOnly)
                                      .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                read.Add((file, File.ReadAllText(file, Encoding.UTF8)));
            }
            catch (Exception ex)
            {
                // The name ALONE goes to the report (it is shown to the user), the cause to the trace.
                Diagnostics.Swallow($"{context}({Path.GetFileName(file)})", ex);
                failed.Add(Path.GetFileName(file));
            }
        }

        unreadable = failed;
        return read;
    }
}
