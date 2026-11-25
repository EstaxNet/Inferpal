using Inferpal.Services;

namespace Inferpal.Tests;

/// <summary>
/// The git a test runs to SET UP its repository (init, config, add, commit, branch, clone): under the wait budget of this
/// suite, never the product's own reading budget (<see cref="GitProcess.Timeout"/>, 15 s) — a loaded runner can take
/// longer than that to commit, and the test then goes red before it has tested anything. A git call that IS the subject
/// (a read the test asserts on, a budget the test measures) keeps the product's runner.
/// </summary>
internal static class FixtureGit
{
    /// <summary>A fixture's git waits as long as a test waits: never a few seconds.</summary>
    internal static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    /// <summary>Runs <c>git <paramref name="args"/></c> in <paramref name="workDir"/> (the current folder when <c>null</c>).</summary>
    internal static Task<(string Output, int ExitCode)> RunAsync(string args, string? workDir) =>
        GitProcess.RunAsync(args, workDir, CancellationToken.None, Budget);
}
