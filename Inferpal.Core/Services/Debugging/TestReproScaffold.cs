using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Inferpal.Services.Debugging;

/// <summary>
/// Provides the repro runner the §25 capture launches under the editor's debugger: a tiny console
/// that loads the user's test assembly (with its own <c>deps.json</c> context) and invokes one
/// test method so the debugger observes the original throw site.
/// </summary>
/// <remarks>
/// <b>Built on demand, never shipped.</b> The runner is scaffolded to a per-source-hash folder
/// under the local app data and compiled once with the user's own SDK — which is present by
/// construction: the feature only triggers on <c>dotnet</c> test reports. This keeps both VSIX
/// packaging chains untouched; the one-time build (~5 s) is paid on the first capture only.
/// <para>
/// Two behaviours the probes measured are load-bearing in the generated source: the reflection
/// invoke uses <c>DoNotWrapExceptions</c> (a <c>TargetInvocationException</c> would break at the
/// Invoke frame with the original frames already unwound), and <c>INFERPAL_WAIT_DEBUGGER=1</c>
/// makes it wait for an attach — the Visual Studio recipe.
/// </para>
/// </remarks>
internal static class TestReproScaffold
{
    /// <summary>Overridable for tests: where the scaffold tree lives.</summary>
    internal static Func<string> BaseDir = () =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Inferpal", "testrepro");

    internal const string ProjectFile = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>net8.0</TargetFramework>
            <Nullable>enable</Nullable>
            <ImplicitUsings>enable</ImplicitUsings>
            <RollForward>LatestMajor</RollForward>
            <AssemblyName>InferpalTestRepro</AssemblyName>
          </PropertyGroup>
        </Project>
        """;

    internal const string ProgramFile = """
        // Inferpal test repro runner (§25): loads a test assembly in its own dependency context and
        // invokes one test method, so an attached or launching debugger observes the original throw
        // site with live locals. Generated and built on demand by TestReproScaffold — do not edit.
        using System.Reflection;
        using System.Runtime.Loader;

        if (args.Length != 2) { Console.Error.WriteLine("usage: InferpalTestRepro <testDll> <Full.Type.Method>"); return 2; }

        var dllPath = Path.GetFullPath(args[0]);
        var fqn     = args[1];
        var dot     = fqn.LastIndexOf('.');

        var resolver = new AssemblyDependencyResolver(dllPath);
        var alc = AssemblyLoadContext.Default;
        alc.Resolving += (ctx, name) =>
        {
            var p = resolver.ResolveAssemblyToPath(name);
            return p is null ? null : ctx.LoadFromAssemblyPath(p);
        };

        var asm    = alc.LoadFromAssemblyPath(dllPath);
        var type   = asm.GetType(fqn[..dot]) ?? throw new InvalidOperationException($"type not found: {fqn[..dot]}");
        var method = type.GetMethod(fqn[(dot + 1)..], BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                     ?? throw new InvalidOperationException($"method not found: {fqn[(dot + 1)..]}");

        if (Environment.GetEnvironmentVariable("INFERPAL_WAIT_DEBUGGER") == "1")
        {
            Console.WriteLine($"waiting for debugger (pid {Environment.ProcessId})...");
            for (int i = 0; i < 600 && !System.Diagnostics.Debugger.IsAttached; i++) Thread.Sleep(100);
            if (!System.Diagnostics.Debugger.IsAttached) { Console.Error.WriteLine("no debugger after 60 s"); return 3; }
        }

        // DoNotWrapExceptions + no catch: the failure must escape from its original frame.
        var instance = method.IsStatic ? null : Activator.CreateInstance(type);
        var result = method.Invoke(instance, BindingFlags.DoNotWrapExceptions, binder: null, parameters: null, culture: null);
        if (result is Task task) task.GetAwaiter().GetResult();
        Console.WriteLine("PASS (no exception)");
        return 0;
        """;

    /// <summary>Version folder: changing the generated source yields a fresh build, older ones stay.</summary>
    internal static string SourceHash()
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(ProjectFile + "\n" + ProgramFile));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    /// <summary>Where the built runner lands for the current source version.</summary>
    internal static string RunnerDllPath() =>
        Path.Combine(BaseDir(), SourceHash(), "bin", "Release", "net8.0", "InferpalTestRepro.dll");

    // Purge once per process: the per-source-hash layout means every shipped version leaves a
    // ~1 MB build behind forever; the first EnsureBuiltAsync of a session sweeps the others.
    private static int _purgeDone;

    /// <summary>
    /// Whether <paramref name="path"/> is a managed assembly rather than the debris of an
    /// interrupted build: it exists, it is not empty, and it starts with the <c>MZ</c> of a PE
    /// image. Cheap on purpose — this runs before every capture, and the failure it screens for is
    /// truncation, not a subtly invalid image.
    /// </summary>
    internal static bool LooksLikeAssembly(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < 128) return false;

            using var stream = File.OpenRead(path);
            return stream.ReadByte() == 'M' && stream.ReadByte() == 'Z';
        }
        catch (Exception ex)
        {
            Diagnostics.Swallow($"TestReproScaffold.LooksLikeAssembly({path})", ex);
            return false;
        }
    }

    /// <summary>Drops a build folder so the next call rebuilds it. Never throws.</summary>
    private static void DiscardBuild(string? dir)
    {
        if (string.IsNullOrEmpty(dir)) return;
        try { Directory.Delete(dir!, recursive: true); }
        catch (Exception ex) { Diagnostics.Swallow($"TestReproScaffold.DiscardBuild({dir})", ex); }
    }

    /// <summary>
    /// Best-effort removal of build folders for other source versions under
    /// <paramref name="baseDir"/>. A folder still in use (an older host mid-build on Windows)
    /// fails its delete and is retried at the next boot — never an error.
    /// </summary>
    internal static void PurgeStaleBuilds(string baseDir, string keepHash)
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(baseDir))
            {
                if (Path.GetFileName(dir).Equals(keepHash, StringComparison.OrdinalIgnoreCase))
                    continue;
                try { Directory.Delete(dir, recursive: true); }
                catch { /* held by another (older) host — swept next boot */ }
            }
        }
        catch (Exception ex) { Diagnostics.Swallow("TestReproScaffold.Purge", ex); }
    }

    /// <summary>
    /// Cross-process build lock: VS and the VS Code host share the same scaffold folder, and two
    /// concurrent <c>dotnet build</c> in one directory corrupt each other's obj/. FileShare.None
    /// is enforced between .NET processes on all supported OSes; the stream IS the lock —
    /// dispose to release. <c>null</c> when the lock stays held past <paramref name="timeout"/>.
    /// </summary>
    internal static async Task<FileStream?> AcquireBuildLockAsync(
        string dir, TimeSpan timeout, CancellationToken ct)
    {
        var lockPath = Path.Combine(dir, ".build.lock");
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            if (DateTime.UtcNow >= deadline) return null;
            await Task.Delay(250, ct);
        }
    }

    /// <summary>
    /// Scaffolds and builds the runner once; later calls return the cached dll. <c>null</c> when
    /// the build failed (no SDK, offline restore…) — the capture then fails and says so.
    /// </summary>
    public static async Task<string?> EnsureBuiltAsync(CancellationToken ct)
    {
        try
        {
            var dll = RunnerDllPath();
            if (LooksLikeAssembly(dll)) return dll;
            // Present but not an assembly: a build killed at the wrong moment (the machine went to
            // sleep, the user closed VS) leaves a zero-byte or half-written dll in a folder keyed
            // by SOURCE hash, so nothing ever invalidates it — every §25 capture on that machine
            // failed from then on, until someone deleted %AppData% by hand. Dropping the folder
            // costs one rebuild; keeping it costs the feature (revue post-1.6.0, item 4.3).
            if (File.Exists(dll)) DiscardBuild(Path.GetDirectoryName(dll));

            var baseDir = BaseDir();
            var hash    = SourceHash();
            var dir     = Path.Combine(baseDir, hash);
            Directory.CreateDirectory(dir);

            if (Interlocked.Exchange(ref _purgeDone, 1) == 0)
                PurgeStaleBuilds(baseDir, hash);

            // Budget = build timeout + margin: if another host is mid-build, waiting for its
            // result is exactly what we want (the re-check below then returns its dll).
            await using var buildLock = await AcquireBuildLockAsync(dir, TimeSpan.FromMinutes(4), ct);
            if (buildLock is null)
            {
                Diagnostics.Swallow("TestReproScaffold.Lock",
                    new TimeoutException("build lock still held after 4 min"));
                return null;
            }

            // Même contrôle qu'à l'entrée : l'autre hôte a pu être tué en cours de build, et son
            // reliquat est alors exactement le fichier qu'on refuse de servir.
            if (LooksLikeAssembly(dll)) return dll; // the other host built it while we waited

            await File.WriteAllTextAsync(Path.Combine(dir, "InferpalTestRepro.csproj"), ProjectFile, ct);
            await File.WriteAllTextAsync(Path.Combine(dir, "Program.cs"), ProgramFile, ct);

            var psi = new ProcessStartInfo
            {
                FileName  = "dotnet",
                Arguments = "build -c Release --nologo -v q -nodeReuse:false",
                WorkingDirectory = dir,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute = false,
                CreateNoWindow  = true,
            };
            var run = await ChildProcess.RunAsync(psi, TimeSpan.FromMinutes(3), ct);
            if (run.TimedOut || run.ExitCode != 0 || !LooksLikeAssembly(dll))
            {
                Diagnostics.Swallow("TestReproScaffold.Build",
                    new InvalidOperationException($"exit {run.ExitCode}: {Truncate(run.Combined)}"));
                // Ne pas laisser derrière soi ce qu'on vient de refuser : sans ça, un build
                // interrompu redevient le cache d'entrée du prochain appel. ⚠ Le dossier de
                // SORTIE, pas le dossier de hash : ce dernier porte `.build.lock`, encore ouvert
                // ici — sous Windows sa suppression échouerait, en silence, et la garde serait
                // décorative.
                DiscardBuild(Path.GetDirectoryName(dll));
                return null;
            }
            return dll;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Diagnostics.Swallow("TestReproScaffold", ex);
            return null;
        }
    }

    private static string Truncate(string s) => s.Length <= 800 ? s : s[..800];
}
