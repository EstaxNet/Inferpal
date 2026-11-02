using System.Diagnostics;
using System.IO;
using System.Text;

namespace Inferpal.Services.Inference;

/// <summary>What a machine where no model server answers is offered (<see cref="ServerInstall.OfferFor(string?)"/>).</summary>
internal enum ServerInstallKind
{
    /// <summary>Windows with winget: install Ollama for this account, on the person's click.</summary>
    Install,
    /// <summary>Ollama installed for this account and not answering: start it, nothing to download.</summary>
    Start,
    /// <summary>Windows without winget: nothing Inferpal can run; the links stay, and why is said.</summary>
    NoWinget,
    /// <summary>macOS, Linux, or an editor connected to another machine: a command to copy, never run by Inferpal.</summary>
    Command,
}

/// <summary>
/// The offer for this machine: what runs (<see cref="Program"/> with <see cref="Arguments"/>, for <see cref="ServerInstallKind.Install"/>
/// and <see cref="ServerInstallKind.Start"/>), and <see cref="Command"/>, the same thing as the card shows it — one source,
/// so the command shown is the command run.
/// </summary>
internal sealed record ServerInstallOffer(ServerInstallKind Kind, string? Program, IReadOnlyList<string> Arguments, string Command)
{
    /// <summary>Inferpal runs it on the person's click (otherwise the card only shows the command, or nothing).</summary>
    public bool Runs => Kind is ServerInstallKind.Install or ServerInstallKind.Start;
}

/// <summary>How an install or a start ended.</summary>
internal enum ServerInstallOutcome
{
    /// <summary>Ollama answers on its address: the first run goes on to the models.</summary>
    Answers,
    /// <summary>winget (or the start) succeeded, and nothing answered within <see cref="ServerInstall.ServerWait"/>.</summary>
    NoServer,
    /// <summary>winget ended with a failure code: <see cref="ServerInstallResult.ExitCode"/> and its last lines.</summary>
    Failed,
    /// <summary>The person stopped it: winget was ended; an installer it had already started finishes on its own.</summary>
    Stopped,
    /// <summary>Another window of this machine is installing: nothing was started.</summary>
    Busy,
    /// <summary>The program could not be started at all: <see cref="ServerInstallResult.Detail"/> says why.</summary>
    NotStarted,
}

/// <summary>The end of <see cref="ServerInstall.RunAsync"/>: its outcome, winget's exit code and last lines (the cause, in
/// the language winget speaks), or why nothing started.</summary>
internal sealed record ServerInstallResult(
    ServerInstallOutcome Outcome, int? ExitCode = null, IReadOnlyList<string>? LastLines = null, string? Detail = null);

/// <summary>
/// Installs or starts Ollama when no model server answers — the first step the first run could not take from the editor.
/// </summary>
/// <remarks>
/// <para>⚠ <b>Never bound to this process's lifetime</b> (<see cref="ProcessLifetime"/>, rule 34's exemption): the server
/// installed or started must outlive the editor. Visual Studio's extension host and VS Code's (libuv) run in jobs that
/// carry <c>SILENT_BREAKAWAY_OK</c>, so a process started here is born outside them; binding it would put winget, the
/// installer and the Ollama it launches in a job that kills them all when the editor closes.</para>
/// <para>⚠ winget writes no progress when its output is redirected, and writes its lines in the Windows display language:
/// the card shows its last line as written, never a parsed percentage. Its output is UTF-8.</para>
/// <para>⚠ The installer winget starts (and the Ollama the installer launches) may inherit winget's output pipes: the end
/// of winget is its exit, never the end of the pipe.</para>
/// </remarks>
internal static class ServerInstall
{
    /// <summary>The download the card announces, in GB.</summary>
    /// <remarks>shortcut: Ollama's installer, rounded; re-read its release when it grows past the rounding.</remarks>
    internal const double ApproxDownloadGb = 1.6;

    /// <summary>How long a server is waited for once winget (or the start) succeeded.</summary>
    internal static readonly TimeSpan ServerWait = TimeSpan.FromSeconds(60);

    /// <summary>How many of winget's last lines a failure shows.</summary>
    internal const int LastLineCount = 6;

    /// <summary>The address Ollama listens on, the first place the scan looks at.</summary>
    internal static string OllamaAddress =>
        $"http://localhost:{ServerScan.DefaultPlaces.First(p => p.Provider == InferenceProviderFactory.Ollama).Port}";

    /// <summary>winget's arguments: Ollama from the community source, for this account, without a question.</summary>
    internal static readonly IReadOnlyList<string> WingetArguments =
    [
        "install", "--id", "Ollama.Ollama", "--exact", "--source", "winget", "--scope", "user", "--silent",
        "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity",
    ];

    /// <summary>The command shown on macOS (Homebrew's formula, started as a service). Never run by Inferpal.</summary>
    internal const string MacCommand = "brew install ollama && brew services start ollama";

    /// <summary>The command Ollama's site gives for Linux. Shown, never run: a script downloaded from the network.</summary>
    internal const string LinuxCommand = "curl -fsSL https://ollama.com/install.sh | sh";

    /// <summary>The operating system the host runs on (the other machine's, when the editor is connected to one).</summary>
    internal enum HostOs { Windows, MacOs, Linux }

    /// <summary>This machine's offer.</summary>
    /// <param name="remoteName">Where the host runs (<see cref="RemoteSide"/>): connected to another machine, installing
    /// there from a card that says "this computer" would mislead — the command is shown instead.</param>
    internal static ServerInstallOffer OfferFor(string? remoteName) => OfferFor(
        remoteName,
        OperatingSystem.IsWindows() ? HostOs.Windows : OperatingSystem.IsMacOS() ? HostOs.MacOs : HostOs.Linux,
        OperatingSystem.IsWindows() ? FindWinget() : null,
        OperatingSystem.IsWindows() ? InstalledOllama() : null);

    /// <summary>The offer for a machine described by its parts (the testable form of <see cref="OfferFor(string?)"/>).</summary>
    /// <param name="installed">Ollama on this machine: its application (<c>ollama app.exe</c>), else its <c>ollama.exe</c>
    /// (installed some other way); null when neither is found.</param>
    internal static ServerInstallOffer OfferFor(string? remoteName, HostOs os, string? winget, string? installed)
    {
        var wingetCommand = "winget " + string.Join(' ', WingetArguments);
        if (os != HostOs.Windows)
            return new(ServerInstallKind.Command, null, [], os == HostOs.MacOs ? MacCommand : LinuxCommand);
        if (RemoteSide.Of(remoteName) != RemoteKind.None) return new(ServerInstallKind.Command, null, [], wingetCommand);
        if (installed is not null)
        {
            // The application starts its own server; a bare ollama.exe (scoop, choco, a zip) is the server: `serve`.
            var app = Path.GetFileName(installed).Equals("ollama app.exe", StringComparison.OrdinalIgnoreCase);
            return app ? new(ServerInstallKind.Start, installed, [], $"\"{installed}\"")
                       : new(ServerInstallKind.Start, installed, ["serve"], $"\"{installed}\" serve");
        }
        return winget is null
            ? new(ServerInstallKind.NoWinget, null, [], "")
            : new(ServerInstallKind.Install, winget, WingetArguments, wingetCommand);
    }

    /// <summary>
    /// Whether the places the scan looked at leave room for an install or a start: nothing answers or asks for a key (a
    /// server runs there — the person's), and nothing else holds Ollama's port (installed, it could not listen there).
    /// </summary>
    internal static bool Offers(IReadOnlyList<ServerPlace> places) =>
        !places.Any(p => p.Outcome is ServerPlaceOutcome.Answers or ServerPlaceOutcome.NeedsKey)
        && !places.Any(p => p.Outcome == ServerPlaceOutcome.NotAModelServer && ServerScan.SameAddress(p.Url, OllamaAddress));

    /// <summary>winget on the PATH, else where Windows puts its alias for this account; null when absent.</summary>
    internal static string? FindWinget() =>
        Shell.ShellLauncher.FindOnPath("winget.exe") is { } onPath ? onPath
        : UserFolders.Of(Environment.SpecialFolder.LocalApplicationData) is { Length: > 0 } local
          && Path.Combine(local, "Microsoft", "WindowsApps", "winget.exe") is var alias && File.Exists(alias) ? alias
        : null;

    /// <summary>Ollama on this machine: its application where its installer puts it for this account, else <c>ollama.exe</c>
    /// on the PATH (installed some other way); null when neither is there.</summary>
    internal static string? InstalledOllama()
    {
        var local = UserFolders.Of(Environment.SpecialFolder.LocalApplicationData);
        var app = string.IsNullOrEmpty(local) ? null : Path.Combine(local, "Programs", "Ollama", "ollama app.exe");
        return app is not null && File.Exists(app) ? app : Shell.ShellLauncher.FindOnPath("ollama.exe");
    }

    /// <summary>
    /// Runs <paramref name="offer"/> (<see cref="ServerInstallOffer.Runs"/>), then waits for Ollama to answer.
    /// </summary>
    /// <param name="said">Each line winget writes, as it writes it.</param>
    /// <param name="serverAnswers">Whether Ollama answers now (default: probed at <see cref="OllamaAddress"/>).</param>
    /// <param name="ct">The person's Stop: ends winget only — an installer it had started finishes on its own.</param>
    /// <param name="serverWait">Test seam: how long the server is waited for (default <see cref="ServerWait"/>).</param>
    /// <param name="run">Test seam: runs the program and returns its exit code (default: the real process).</param>
    /// <param name="lockPath">Test seam: the file that keeps a second window from installing at the same time.</param>
    internal static async Task<ServerInstallResult> RunAsync(
        ServerInstallOffer offer, Action<string> said, CancellationToken ct,
        Func<CancellationToken, Task<bool>>? serverAnswers = null, TimeSpan? serverWait = null,
        Func<ServerInstallOffer, Action<string>, CancellationToken, Task<int>>? run = null, string? lockPath = null)
    {
        if (!offer.Runs) throw new ArgumentException($"Nothing to run for {offer.Kind}.", nameof(offer));
        lockPath ??= Path.Combine(Path.GetTempPath(), "Inferpal", "server-install.lock");
        var (held, lockError) = TryHold(lockPath);
        using var heldLock = held;
        if (lockError is not null) return new(ServerInstallOutcome.NotStarted, Detail: lockError);
        // ⚠ Busy is winget's own lifetime, not only this lock's: the lock ends with the editor that took it, winget does
        // not (it is never bound) — a window reloaded during the download would start a second one.
        if (held is null || WingetStillRuns(lockPath + ".pid")) return new(ServerInstallOutcome.Busy);
        run ??= (o, s, c) => RunProgramAsync(o, s, c, lockPath + ".pid");

        var last = new Queue<string>();
        void Said(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            lock (last)
            {
                last.Enqueue(line.Trim());
                while (last.Count > LastLineCount) last.Dequeue();
            }
            said(line.Trim());
        }
        IReadOnlyList<string> LastLines() { lock (last) return [.. last]; }

        int exit;
        try
        {
            exit = await run(offer, Said, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new(ServerInstallOutcome.Stopped, LastLines: LastLines());
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return new(ServerInstallOutcome.NotStarted, Detail: ex.Message);
        }
        if (exit != 0) return new(ServerInstallOutcome.Failed, exit, LastLines());

        serverAnswers ??= static async token =>
            await ProviderProbe.DetectAsync(OllamaAddress, null, token) == InferenceProviderFactory.Ollama;
        var deadline = DateTime.UtcNow + (serverWait ?? ServerWait);
        while (true)
        {
            try
            {
                if (await serverAnswers(ct)) return new(ServerInstallOutcome.Answers, exit, LastLines());
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return new(ServerInstallOutcome.Stopped, exit, LastLines());
            }
            if (DateTime.UtcNow >= deadline) return new(ServerInstallOutcome.NoServer, exit, LastLines());
            try { await Task.Delay(TimeSpan.FromSeconds(1), ct); }
            catch (OperationCanceledException) { return new(ServerInstallOutcome.Stopped, exit, LastLines()); }
        }
    }

    /// <summary>
    /// The real run: Ollama's application started and left running (<see cref="ServerInstallKind.Start"/>), or winget run
    /// to its end with its lines read as they come. On <paramref name="ct"/>, winget alone is ended.
    /// </summary>
    /// <param name="pidPath">Where winget's process is named while it runs (<see cref="WingetStillRuns"/>).</param>
    private static async Task<int> RunProgramAsync(ServerInstallOffer offer, Action<string> said, CancellationToken ct,
                                                   string pidPath)
    {
        // What runs here stays after the editor: it must hold neither the host's own pipes (VS Code's protocol) nor the
        // workspace folder (locked against a rename or a delete for as long as Ollama runs).
        KeepOwnStdioOutOfChildren();
        if (offer.Kind == ServerInstallKind.Start)
        {
            var folder = Path.GetDirectoryName(offer.Program!)!;
            var server = offer.Arguments.Count == 0
                // The desktop application, as a person would start it: by the shell, which hands it none of our handles.
                ? new ProcessStartInfo(offer.Program!) { UseShellExecute = true, WorkingDirectory = folder }
                : new ProcessStartInfo(offer.Program!) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = folder };
            foreach (var a in offer.Arguments) server.ArgumentList.Add(a);
            // Started, never waited for: it starts its server and stays.
            using var started = Process.Start(server) ?? throw new InvalidOperationException($"{offer.Program} did not start.");
            return 0;
        }

        var start = new ProcessStartInfo(offer.Program!)
        {
            UseShellExecute        = false,
            CreateNoWindow         = true,
            WorkingDirectory       = Path.GetTempPath(),
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding  = Encoding.UTF8,
        };
        foreach (var a in offer.Arguments) start.ArgumentList.Add(a);
        // Lines reach the caller only while this run is its: the pipes may outlive winget, held by what it started.
        var open = 1;
        void Said(string line) { if (Volatile.Read(ref open) == 1) said(line); }
        using var winget = Process.Start(start) ?? throw new InvalidOperationException($"{offer.Program} did not start.");
        try
        {
            NameWinget(pidPath, winget);
            winget.StandardInput.Close();
            var outLines = ReadLinesAsync(winget.StandardOutput, Said);
            var errLines = ReadLinesAsync(winget.StandardError, Said);
            try
            {
                await winget.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                // winget only, never its tree: a download stops; an installer already running finishes rather than being
                // cut halfway through its files.
                try { winget.Kill(entireProcessTree: false); } catch { /* already gone */ }
                throw;
            }
            // A short grace for winget's last lines, never the pipes' end.
            await Task.WhenAny(Task.WhenAll(outLines, errLines), Task.Delay(ChildProcess.PipeGraceAfterExit, CancellationToken.None));
            return winget.ExitCode;
        }
        finally
        {
            Volatile.Write(ref open, 0);
            if (winget.HasExited) try { File.Delete(pidPath); } catch { /* the next run judges it by its process */ }
        }
    }

    /// <summary>Writes which winget runs the install, for a window that would start another (<see cref="WingetStillRuns"/>).</summary>
    private static void NameWinget(string pidPath, Process winget)
    {
        try { File.WriteAllText(pidPath, FormattableString.Invariant($"{winget.Id}\n{winget.StartTime.ToUniversalTime().Ticks}")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Diagnostics.Swallow("ServerInstall.NameWinget", ex);
        }
    }

    /// <summary>
    /// Whether the winget named in <paramref name="pidPath"/> still runs: the same process id AND the same start time — a
    /// process id is reused once its process ended.
    /// </summary>
    internal static bool WingetStillRuns(string pidPath)
    {
        try
        {
            if (!File.Exists(pidPath)) return false;
            var parts = File.ReadAllText(pidPath).Split('\n');
            if (parts.Length < 2 || !int.TryParse(parts[0], out var pid)
                || !long.TryParse(parts[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var ticks))
                return false;
            using var process = Process.GetProcessById(pid);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == ticks;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException
                                      or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return false;   // gone, or not ours to read: nothing holds the install
        }
    }

    /// <summary>
    /// Makes this process's standard handles non-inheritable, so what it starts and leaves running never holds them. In
    /// the VS Code host they are the protocol's pipes: a long-lived child holding them keeps a dead host's pipe open, and
    /// anything it writes lands in the protocol. Every other child of the Core redirects its own streams.
    /// </summary>
    private static void KeepOwnStdioOutOfChildren()
    {
        if (!OperatingSystem.IsWindows()) return;
        foreach (var std in new[] { StdInput, StdOutput, StdError })
        {
            var handle = GetStdHandle(std);
            if (handle != IntPtr.Zero && handle != new IntPtr(-1)) SetHandleInformation(handle, HandleFlagInherit, 0);
        }
    }

    private const int  StdInput = -10, StdOutput = -11, StdError = -12;
    private const uint HandleFlagInherit = 0x1;

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(IntPtr hObject, uint dwMask, uint dwFlags);

    /// <summary>Each line of <paramref name="reader"/> to <paramref name="said"/>, until the pipe ends or breaks.</summary>
    private static async Task ReadLinesAsync(StreamReader reader, Action<string> said)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line) said(line);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    /// <summary>The machine-wide install lock; <c>Stream</c> null when another window holds it; <c>Error</c> when the lock
    /// could not be taken for another cause (a temporary folder that cannot be written), which is not "another window".</summary>
    private static (FileStream? Stream, string? Error) TryHold(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            return (new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose), null);
        }
        // ERROR_SHARING_VIOLATION, ERROR_LOCK_VIOLATION: held by another process.
        catch (IOException ex) when ((ex.HResult & 0xFFFF) is 32 or 33) { return (null, null); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (null, ex.Message); }
    }
}
