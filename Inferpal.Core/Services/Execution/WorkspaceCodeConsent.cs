using System.Collections.Concurrent;
using System.IO;
using System.Runtime.CompilerServices;
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
/// approvals turned off do not skip it. A build or a run the person started (<c>/build</c>, <c>/tdd</c>,
/// <c>/test coverage</c>, a click) is theirs, and counts as their agreement.
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
    // One card at a time PER WORKSPACE: shared, an ACP thread on another workspace waited on this one's card, unasked.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Asking = new(PathComparer.Default);

    /// <summary>Records that the person started a run of <paramref name="root"/>'s code themselves: their agreement.</summary>
    internal static void NoteThePersonsRun(string? root)
    {
        if (root is { Length: > 0 }) lock (Gate) Answered[Path.TrimEndingDirectorySeparator(root)] = true;
    }

    /// <summary><c>null</c> when the run may go ahead; <see cref="Refusal"/> when the person declined, or could not be
    /// asked.</summary>
    internal async Task<string?> RefusalAsync(string toolName, CancellationToken ct)
    {
        // No workspace: the tool's own refusal names what is missing.
        if (getRoot() is not { Length: > 0 } given) return null;
        var root = Path.TrimEndingDirectorySeparator(given);
        if (PackageCheck.IsThePersonsOwnCall)
        {
            NoteThePersonsRun(root);
            return null;
        }
        if (Known(root) is { } known) return known ? null : Refusal;
        if (approval is null) return Refusal;

        var asking = Asking.GetOrAdd(root, _ => new SemaphoreSlim(1, 1));
        await asking.WaitAsync(ct);
        try
        {
            if (Known(root) is { } answered) return answered ? null : Refusal;
            var name = Path.GetFileName(root) is { Length: > 0 } leaf ? leaf : root;
            var withdrawn = new StrongBox<bool>();
            bool agreed;
            // ⚠ Asked in the middle of a call (Smart Fix after a write): its answer is not the call's — a "No" here does
            // not make the write that already happened a refused call.
            using (ApprovalServiceBase.NotingWithdrawal(withdrawn))
            using (WorkTreeBracket.Aside())
            {
                try
                {
                    agreed = await approval.RequestApprovalAsync(toolName, Strings.ProjectCodeConsent(name), ct, subject: root,
                                                                 forcePrompt: true);
                }
                // A rule that denies the call: refused, never an exception thrown after a write that happened.
                catch (PermissionDeniedException) { return Refusal; }
            }
            // ⚠ A card a note took away is no answer: remembered as a "No", every later build of the workspace was refused.
            if (!withdrawn.Value) lock (Gate) Answered[root] = agreed;
            return agreed ? null : Refusal;
        }
        finally { asking.Release(); }
    }

    private static bool? Known(string root)
    {
        lock (Gate) return Answered.TryGetValue(root, out var agreed) ? agreed : null;
    }
}
