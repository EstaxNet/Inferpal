using System.IO;

namespace Inferpal.Services.Debugging;

/// <summary>
/// Finds the built test assembly a failing test FQN lives in (§25 tranche B). Pure heuristics on
/// the workspace's <c>bin</c> outputs — no compiler, no editor: the assembly whose file name is
/// the <b>longest prefix</b> of the FQN wins (assembly and root namespace almost always agree),
/// newest write time as the tie-breaker. The `/tdd` loop just ran <c>run_tests</c>, so the
/// binaries are fresh by construction.
/// </summary>
internal static class TestAssemblyLocator
{
    /// <summary>
    /// The dll most likely to contain <paramref name="testFqn"/>, or <c>null</c> when nothing
    /// under <paramref name="projectRoot"/> matches — the capture then fails and says so.
    /// </summary>
    public static string? Locate(string projectRoot, string testFqn)
    {
        if (string.IsNullOrWhiteSpace(projectRoot) || !Directory.Exists(projectRoot)) return null;

        string? best = null;
        var bestLen = 0;
        var bestTime = DateTime.MinValue;
        foreach (var dll in EnumerateBinAssemblies(projectRoot))
        {
            var name = Path.GetFileNameWithoutExtension(dll);
            if (!testFqn.StartsWith(name + ".", StringComparison.OrdinalIgnoreCase)) continue;

            var time = File.GetLastWriteTimeUtc(dll);
            if (name.Length > bestLen || (name.Length == bestLen && time > bestTime))
            {
                best = dll; bestLen = name.Length; bestTime = time;
            }
        }
        return best;
    }

    /// <summary>Every dll under a <c>bin</c> directory, skipping <c>obj</c> and ref assemblies.</summary>
    private static IEnumerable<string> EnumerateBinAssemblies(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            string[] subdirs;
            try { subdirs = Directory.GetDirectories(dir); }
            catch { continue; }

            foreach (var sub in subdirs)
            {
                var leaf = Path.GetFileName(sub);
                if (leaf.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                    leaf.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                    leaf.Equals("node_modules", StringComparison.OrdinalIgnoreCase)) continue;

                if (leaf.Equals("bin", StringComparison.OrdinalIgnoreCase))
                {
                    IEnumerable<string> dlls;
                    try { dlls = Directory.EnumerateFiles(sub, "*.dll", SearchOption.AllDirectories); }
                    catch { continue; }
                    foreach (var dll in dlls)
                        if (!dll.Replace('/', '\\').Contains(@"\ref\", StringComparison.OrdinalIgnoreCase))
                            yield return dll;
                }
                else
                {
                    stack.Push(sub);
                }
            }
        }
    }
}
