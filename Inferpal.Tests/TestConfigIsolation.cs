using System.IO;
using System.Runtime.CompilerServices;
using Inferpal.Config;

namespace Inferpal.Tests;

/// <summary>
/// Runs before any test: redirects <see cref="InferpalConfig"/> persistence to a per-run temp
/// file. Command handlers legitimately call <c>Config.Save()</c> (e.g. <c>/model</c>,
/// <c>config/update</c>) — without this seam a unit test would overwrite the developer's real
/// <c>%AppData%\Inferpal\config.json</c> with test defaults.
/// </summary>
internal static class TestConfigIsolation
{
    [ModuleInitializer]
    internal static void Init()
    {
        var root = Path.Combine(Path.GetTempPath(), "inferpal-tests");

        InferpalConfig.OverridePathForTests = Path.Combine(root, $"config-{Environment.ProcessId}.json");

        // Same seam for the session store: /branch, /history and the host's session RPCs all write
        // real files, and the suite would otherwise create, rewrite and delete entries in the
        // developer's own %AppData%\Inferpal\sessions folder.
        Inferpal.Services.Persistence.ConversationStore.OverrideDirForTests =
            Path.Combine(root, $"sessions-{Environment.ProcessId}");

        // And for the reading speeds every client learns: a test's turn against a loopback server is a read too.
        Inferpal.Services.Inference.PrefillRates.Shared.File.PathOverride =
            Path.Combine(root, $"prefill-rates-{Environment.ProcessId}.json");

        // And for the lock that keeps the machine awake while the model works: every lease of the suite would take the
        // real one — a power request, or a `caffeinate` / `systemd-inhibit` process per lease on the CI legs.
        Inferpal.Services.Hardware.SleepGuard.Shared =
            new Inferpal.Services.Hardware.SleepGuard(new Inferpal.Services.Hardware.SleepGuard.Silent(), TimeSpan.Zero);

        // And for the editors an ACP agent has told what it does not do in them.
        Inferpal.Host.Acp.AcpAgent.LimitsToldShared.PathOverride =
            Path.Combine(root, $"acp-limits-told-{Environment.ProcessId}.json");

        // And for the approvals every prompt remembers: each test that answers a card would count in the real history,
        // and /permissions suggest would offer the suite's commands to the developer.
        Inferpal.Services.Execution.ApprovalHistory.Shared.File.PathOverride =
            Path.Combine(root, $"approval-history-{Environment.ProcessId}.json");

        // And for the check of the packages a call adds: every registry the suite builds would start `dotnet package
        // search` or `npm view`, or ask PyPI, for each install a test runs.
        Inferpal.Services.Execution.PackageCheck.Shared = new Inferpal.Services.Execution.PackageCheck(new NoAnswerPackageLookup());

        // And for the code index and the debugger's test scaffold: a test that builds a registry or an index without
        // redirecting them itself writes an empty database in the developer's own profile at every run.
        TestRagStore.Redirect();
        Inferpal.Services.Debugging.TestReproScaffold.BaseDir = () => Path.Combine(root, $"testrepro-{Environment.ProcessId}");
        RagDirAtStart      = Inferpal.Services.Rag.RagDatabase.BaseDir();
        ScaffoldDirAtStart = Inferpal.Services.Debugging.TestReproScaffold.BaseDir();
    }

    /// <summary>Where the code index points once the suite is set up, before any test moves it.</summary>
    internal static string RagDirAtStart { get; private set; } = string.Empty;

    /// <summary>Where the debugger's test scaffold points once the suite is set up, before any test moves it.</summary>
    internal static string ScaffoldDirAtStart { get; private set; } = string.Empty;
}

/// <summary>The suite's package source: it never answers, so no call a test makes is refused or reaches a registry.</summary>
internal sealed class NoAnswerPackageLookup : Inferpal.Services.Execution.IPackageLookup
{
    public Task<Inferpal.Services.Execution.PackageLookupResult> LookUpAsync(Inferpal.Services.Execution.AddedPackage package, CancellationToken ct) =>
        Task.FromResult(Inferpal.Services.Execution.PackageLookupResult.Unknown("the test suite asks no registry"));

    public Task<IReadOnlyList<string>> CloseNamesAsync(Inferpal.Services.Execution.AddedPackage package, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>([]);
}
