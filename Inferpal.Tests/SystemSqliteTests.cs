using System.IO;
using System.Runtime.InteropServices;
using Inferpal.Services.Rag;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Where no e_sqlite3 is found (the ACP archive's native file not kept beside the agent), the semantic index opens on
/// the system's SQLite.
/// </summary>
/// <remarks>⚠ Without that fallback, an agent copied without that file has no code search at all, and says so only in
/// <c>/diagnostics</c>. Every leg of the CI has its system's SQLite (winsqlite3, libsqlite3.so.0, the macOS dylib).</remarks>
public sealed class SystemSqliteTests
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int LibVersionNumber();

    [Fact]
    public void AFolderWithoutESqlite3_LoadsTheSystemsSqlite_RecentEnoughForWal()
    {
        var empty = Directory.CreateTempSubdirectory("inferpal-nosqlite-").FullName;
        try
        {
            var handle = SqliteBootstrapper.LoadOutsideTheDefaultSearch(empty);

            Assert.NotEqual(IntPtr.Zero, handle);
            Assert.True(NativeLibrary.TryGetExport(handle, "sqlite3_libversion_number", out var export));
            var version = Marshal.GetDelegateForFunctionPointer<LibVersionNumber>(export)();
            Assert.True(version >= 3_007_000, $"SQLite {version} predates WAL");
            // The provider binds the whole API by these names; the index needs at least these.
            foreach (var name in new[] { "sqlite3_open_v2", "sqlite3_prepare_v2", "sqlite3_step", "sqlite3_close_v2" })
                Assert.True(NativeLibrary.TryGetExport(handle, name, out _), name);
        }
        finally { try { Directory.Delete(empty, true); } catch { } }
    }
}
