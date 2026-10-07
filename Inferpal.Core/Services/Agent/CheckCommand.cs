using System.Text.Json;
using System.Text.RegularExpressions;

namespace Inferpal.Services.Agent;

/// <summary>
/// A shell command that runs tests or a build — a check whose verdict is its exit code — and that exit code, read from
/// what <c>run_command</c> returned.
/// </summary>
internal static class CheckCommand
{
    // The test runners and builders run_tests and Smart Fix know, as the shell is asked for them. A command that merely
    // MENTIONS one (an echo, a grep) is not a run of it: the runner must start a command segment.
    private static readonly Regex Check = new(
        @"(?:^|[;&|(]\s*|\bthen\s+|\bdo\s+)\s*(?:(?:[A-Za-z_][A-Za-z0-9_]*=\S*\s+)*)" +
        @"(?:dotnet\s+(?:test|build)|(?:python3?|py)\s+-m\s+pytest|pytest|(?:npm|pnpm|yarn)\s+(?:run\s+)?test|" +
        @"npx\s+(?:jest|vitest|mocha|tsc)|jest|vitest|mocha|tsc|cargo\s+(?:test|build|check)|go\s+(?:test|build|vet)|" +
        @"mvn\S*\s+(?:\S+\s+)*?(?:test|verify|package)|\.?/?gradlew?\s+(?:\S+\s+)*?(?:test|build|check)|make\s+(?:test|check))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexBudget.Default);

    // The note ShellSession appends after a non-zero exit (ShellStateProtocol.ExitNote). A -1 from a stopped pipeline is
    // not the command's verdict: its own sentence says the command did not run to completion.
    private static readonly Regex Exit = new(@"\n\[exit code (-?\d+)\]\s*$", RegexOptions.CultureInvariant,
        RegexBudget.Default);

    /// <summary>Whether <paramref name="command"/> starts a test run or a build.</summary>
    public static bool IsCheck(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        try { return Check.IsMatch(command); }
        catch (RegexMatchTimeoutException) { return false; }
    }

    /// <summary>The <c>command</c> argument of a run_command call, from its JSON input.</summary>
    public static string? CommandOf(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        try
        {
            using var doc = JsonDocument.Parse(input);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("command", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// The verdict of a run_command call: <c>true</c> a test run or build that failed, <c>false</c> one that passed,
    /// <c>null</c> when the call is not a check that RAN — another command, a refusal, an error before anything ran, a
    /// background job, a pipeline stopped early, a runner that never started.
    /// </summary>
    /// <remarks>
    /// ⚠ A runner that never STARTED proves nothing either way: <c>pytest: command not found</c> (exit 127), PowerShell's
    /// <c>CommandNotFoundException</c> (exit 1), <c>python -m pytest</c> without pytest installed (exit 1). Read as a
    /// failed run, a turn whose code passes its tests ended on "the last test or build run of this turn failed —
    /// whatever the answer says". Judged as Smart Fix judges a missing toolchain (what the shell says, never a build's
    /// words), and by run_tests' own reading of the interpreter's line.
    /// </remarks>
    public static bool? Failed(string? input, string output) =>
        IsCheck(CommandOf(input)) && ExitCode(output) is { } code && !NeverStarted(code, output) ? code != 0 : null;

    private static bool NeverStarted(int code, string output) =>
        code != 0 && (CodeActions.SmartFixValidator.IsToolMissing(code, output) || Tools.RunTestsTool.PytestModuleMissing(output));

    /// <summary>The exit code run_command reported: 0 when no note follows the output; null when nothing ran to its
    /// end (an error or refusal before the run, a background job, a pipeline stopped early).</summary>
    public static int? ExitCode(string output)
    {
        if (output.StartsWith("Error:", StringComparison.Ordinal)
            || string.Equals(output.Trim(), Localization.Strings.RunCancelled.Trim(), StringComparison.Ordinal)
            || output.Contains("Started background job '", StringComparison.Ordinal)
            || output.Contains("[exit code -1: Select-Object -First", StringComparison.Ordinal))
            return null;
        try
        {
            var m = Exit.Match(output.TrimEnd());
            return m.Success && int.TryParse(m.Groups[1].Value, out var code) ? code : 0;
        }
        catch (RegexMatchTimeoutException) { return null; }
    }
}
