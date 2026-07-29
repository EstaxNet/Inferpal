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
        => InferpalConfig.OverridePathForTests =
            Path.Combine(Path.GetTempPath(), "inferpal-tests", $"config-{Environment.ProcessId}.json");
}
