using System.IO;

namespace Inferpal.Services.Commands;

/// <summary>
/// Approval decorator for the `/tdd` fix rounds (fiche §25): a write aimed at a <b>test file</b>
/// is always force-prompted — no allow rule, session grant or disabled-alerts setting can wave
/// it through. Reading and writes to production code go through the normal pipeline unchanged.
/// </summary>
/// <remarks>
/// Born from a measured failure mode, not caution: during the §25 gate pass, the loop's most
/// natural way to "go green" was to rewrite the failing assertion to the observed buggy value —
/// three different attempts on the same case, all aimed at the test file. The prompt now forbids
/// it (see <see cref="TddCommandHandler.BuildFixPrompt"/>), and this guard makes the human read
/// any test-file write that slips through anyway. Force-prompt, not deny: a test can genuinely
/// be wrong, and the loop's rules say the model must say so — the human is the judge.
/// </remarks>
internal sealed class TestFileWriteGuard(IApprovalService inner) : IApprovalService
{
    public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct,
        string? subject = null, Services.CodeActions.DiffInfo? diff = null, bool forcePrompt = false)
        => inner.RequestApprovalAsync(toolName, details, ct, subject, diff,
            forcePrompt || TargetsTestFile(subject ?? details));

    /// <summary>
    /// Heuristic on the approval subject (a path for file writes, possibly several for
    /// <c>apply_edits</c>): any segment or file name carrying "test"/"tests"/"spec" counts.
    /// A false positive costs one extra prompt — the cheap side of the error.
    /// </summary>
    internal static bool TargetsTestFile(string subject)
    {
        if (string.IsNullOrWhiteSpace(subject)) return false;
        foreach (var token in subject.Split('\n', ';', ','))
        {
            // Separators are normalized BEFORE anything reads a file name: on POSIX a backslash is
            // an ordinary character, so Path.GetFileNameWithoutExtension of a Windows-style path
            // returns the whole path and "test_parser.py" stops being recognised. The subject can
            // carry either flavour on either platform - it comes from a model, and from MCP tools.
            var path = token.Trim().Replace('\\', '/');
            if (path.Length == 0) continue;
            var name = Path.GetFileNameWithoutExtension(path);
            // Case-sensitive on purpose: "CalculatorTests" is the convention, "Contest" is a word.
            if (name.EndsWith("Test", StringComparison.Ordinal) ||
                name.EndsWith("Tests", StringComparison.Ordinal) ||
                name.EndsWith(".spec", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".test", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("test_", StringComparison.OrdinalIgnoreCase))
                return true;

            foreach (var segment in path.Split('/'))
                if (segment.Equals("test", StringComparison.OrdinalIgnoreCase) ||
                    segment.Equals("tests", StringComparison.OrdinalIgnoreCase) ||
                    segment.Equals("__tests__", StringComparison.OrdinalIgnoreCase) ||
                    // .NET convention "<Project>.Tests" — the test directory of THIS very repo was
                    // under the radar (pre-1.6.0 architecture review): any file in such a folder is test
                    // collateral (fixtures, fakes), exactly what the loop likes to bend.
                    segment.EndsWith(".tests", StringComparison.OrdinalIgnoreCase) ||
                    segment.EndsWith(".test", StringComparison.OrdinalIgnoreCase))
                    return true;
        }
        return false;
    }
}
