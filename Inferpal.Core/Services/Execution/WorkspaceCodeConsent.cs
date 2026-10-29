using System.IO;
using Inferpal.Localization;

namespace Inferpal.Services.Execution;

/// <summary>
/// The person's agreement to run the workspace's own code when the assistant builds or tests it: asked once per
/// workspace, for as long as this process lives — and a refusal holds as long.
/// </summary>
/// <remarks>
/// ⚠ A build or a test run executes what the repository wrote — an <c>&lt;Exec&gt;</c> in a project file, a
/// <c>build.rs</c>, a <c>package.json</c> test script, the project's own <c>.venv</c> interpreter. Without this card, a
/// cloned repository whose README says "run the tests first" has its code run by a model's call, unasked — the commands
/// of <c>.inferpal/validators.json</c> already ask. Forced like them: an "Always" grant, an <c>allow</c> rule or the
/// approvals turned off do not skip it. A build or a run the person started (<c>/build</c>, <c>/tdd</c>, a click) is
/// theirs, and counts as their agreement.
/// </remarks>
internal sealed class WorkspaceCodeConsent(IApprovalService? approval, Func<string?> getRoot)
{
    /// <summary>What a tool answers when the person declined: nothing was built or run.</summary>
    internal const string Refusal =
        "⚠ Nothing was built or run: the person declined to run this workspace's own code (its build and test scripts) — "
        + "do not ask again; they can run /build or /tdd themselves.";

    // ⚠ Per workspace for the whole process, never per instance: /tdd, a mode and each ACP thread build their own
    // registry — kept there, each of them asked again.
    private static readonly Dictionary<string, bool> Answered = new(PathComparer.Default);
    private static readonly object Gate = new();
    // One card at a time: two calls of one parallel batch would show two.
    private static readonly SemaphoreSlim Asking = new(1, 1);

    /// <summary><c>null</c> when the run may go ahead; <see cref="Refusal"/> when the person declined, or could not be
    /// asked.</summary>
    internal async Task<string?> RefusalAsync(string toolName, CancellationToken ct)
    {
        // No workspace: the tool's own refusal names what is missing.
        if (getRoot() is not { Length: > 0 } given) return null;
        var root = Path.TrimEndingDirectorySeparator(given);
        if (PackageCheck.IsThePersonsOwnCall)
        {
            lock (Gate) Answered[root] = true;
            return null;
        }
        if (Known(root) is { } known) return known ? null : Refusal;
        if (approval is null) return Refusal;

        await Asking.WaitAsync(ct);
        try
        {
            if (Known(root) is { } answered) return answered ? null : Refusal;
            var name = Path.GetFileName(root) is { Length: > 0 } leaf ? leaf : root;
            var agreed = await approval.RequestApprovalAsync(toolName, Strings.ProjectCodeConsent(name), ct, subject: root,
                                                             forcePrompt: true);
            lock (Gate) Answered[root] = agreed;
            return agreed ? null : Refusal;
        }
        finally { Asking.Release(); }
    }

    private static bool? Known(string root)
    {
        lock (Gate) return Answered.TryGetValue(root, out var agreed) ? agreed : null;
    }
}
