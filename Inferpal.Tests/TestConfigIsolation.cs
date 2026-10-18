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
    }
}
