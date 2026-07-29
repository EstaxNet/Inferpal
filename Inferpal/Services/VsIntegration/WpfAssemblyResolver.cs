using System.IO;
using System.Reflection;

namespace Inferpal.Services;

/// <summary>
/// Last-chance resolver for WPF's lazily-loaded companion assembly
/// <c>System.Windows.Extensions</c> inside the out-of-process Extensibility host.
/// </summary>
/// <remarks>
/// PresentationFramework / WindowsBase / System.Xaml all reference
/// <c>System.Windows.Extensions</c> lazily (SystemSounds, XamlAccessLevel…). Inside the
/// Extensibility host the extension runs in an isolated AssemblyLoadContext that fails to
/// resolve it: the DLL is not in the VSIX (the SDK prunes it from the output as
/// "framework-provided", so it never reaches the extension folder nor its deps.json) and
/// the host's own probing misses it too. First victim: the in-place code-action spinner
/// (a real WPF <c>Window</c>) died with <c>FileNotFoundException: System.Windows.Extensions</c>.
/// The fix: on resolution failure, load the DLL from the directory WPF itself was loaded
/// from — the shared-framework folder that provided PresentationFramework is guaranteed
/// to carry it.
/// </remarks>
internal static class WpfAssemblyResolver
{
    private const string AssemblyName = "System.Windows.Extensions";

    private static int _installed;

    /// <summary>Installs the AppDomain-wide last-chance hook. Idempotent, never throws.</summary>
    internal static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) != 0) return;

        AppDomain.CurrentDomain.AssemblyResolve += static (_, args) =>
        {
            try
            {
                // Exact-name match only: satellite requests ("….resources") must keep
                // returning null so the loader falls back to the neutral resources.
                var requested = new AssemblyName(args.Name).Name;
                if (!string.Equals(requested, AssemblyName, StringComparison.OrdinalIgnoreCase))
                    return null;

                var wpfDir = Path.GetDirectoryName(typeof(System.Windows.Window).Assembly.Location);
                if (string.IsNullOrEmpty(wpfDir)) return null;

                var path = Path.Combine(wpfDir, AssemblyName + ".dll");
                if (!File.Exists(path)) return null;

                Diagnostics.Record("WpfAssemblyResolver", $"resolved {AssemblyName} from {path}");
                return Assembly.LoadFrom(path);
            }
            catch (Exception ex)
            {
                // A resolver that throws would turn one missing assembly into a host crash.
                Diagnostics.Swallow("WpfAssemblyResolver", ex);
                return null;
            }
        };
    }
}
