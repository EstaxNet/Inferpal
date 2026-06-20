using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Inferpal.Services.Rag;

/// <summary>
/// Makes the native <c>e_sqlite3</c> library locatable when the extension runs
/// inside Visual Studio's out-of-process host.
/// </summary>
/// <remarks>
/// In a normal .NET app the runtime resolves the RID-specific native asset
/// (<c>runtimes/win-x64/native/e_sqlite3.dll</c>) using <c>*.deps.json</c>.
/// VS loads the extension through a plugin <see cref="System.Runtime.Loader.AssemblyLoadContext"/>
/// that does <b>not</b> consult our <c>Inferpal.deps.json</c> (the VSIX does
/// not even deploy it), so the default probing never finds the native DLL and
/// the <see cref="Microsoft.Data.Sqlite.SqliteConnection"/> type initializer
/// throws. We register a <see cref="NativeLibrary"/> resolver on the SQLitePCLRaw
/// provider assembly that loads the native DLL by absolute path next to our
/// managed assemblies — independent of the deployment layout.
/// </remarks>
internal static class SqliteBootstrapper
{
    private static int _initialized;

    /// <summary>
    /// Registers the native resolver exactly once, before any SQLite use.
    /// Safe to call from multiple threads.
    /// </summary>
    public static void EnsureInitialized()
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0) return;

        try
        {
            // The [DllImport("e_sqlite3")] declarations live in this assembly, so
            // the resolver must be registered on it. Load it explicitly so it is
            // present before SqliteConnection's static ctor runs the first P/Invoke.
            var provider = Assembly.Load("SQLitePCLRaw.provider.e_sqlite3");
            NativeLibrary.SetDllImportResolver(provider, Resolve);
        }
        catch
        {
            // If the provider assembly cannot be pre-loaded we fall back to the
            // default runtime behaviour — nothing worse than before.
        }
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        // Only intercept the SQLite native library; defer everything else.
        if (!libraryName.Equals("e_sqlite3", StringComparison.OrdinalIgnoreCase))
            return IntPtr.Zero;

        // 1. Let the default search win when a flat copy or deps.json actually works.
        if (NativeLibrary.TryLoad(libraryName, assembly, searchPath, out var handle))
            return handle;

        var baseDir = Path.GetDirectoryName(typeof(SqliteBootstrapper).Assembly.Location)
                      ?? AppContext.BaseDirectory;

        var rid = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64   => "win-x64",
            Architecture.X86   => "win-x86",
            Architecture.Arm64 => "win-arm64",
            Architecture.Arm   => "win-arm",
            _                  => "win-x64",
        };

        // 2. RID-specific asset shipped in the VSIX (runtimes/<rid>/native/e_sqlite3.dll).
        var ridPath = Path.Combine(baseDir, "runtimes", rid, "native", "e_sqlite3.dll");
        if (File.Exists(ridPath) && NativeLibrary.TryLoad(ridPath, out handle))
            return handle;

        // 3. Flat copy next to the managed assemblies (legacy band-aid layout).
        var flatPath = Path.Combine(baseDir, "e_sqlite3.dll");
        if (File.Exists(flatPath) && NativeLibrary.TryLoad(flatPath, out handle))
            return handle;

        return IntPtr.Zero;
    }
}
