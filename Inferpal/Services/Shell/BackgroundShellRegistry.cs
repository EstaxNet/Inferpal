using System.Diagnostics;
using System.IO;
using System.Text;

namespace Inferpal.Services.Shell;

/// <summary>
/// Tracks detached background commands launched by <see cref="RunCommandTool"/>. Each job runs in
/// its own <c>powershell.exe</c> whose stdout/stderr are streamed into a growing buffer; the model
/// reads new output incrementally via <c>poll</c> and terminates a job via <c>stop</c>. One
/// instance per workspace (held by the tool registry).
/// </summary>
internal sealed class BackgroundShellRegistry
{
    private sealed class Job
    {
        public required string Id;
        public required Process Process;
        public required string Command;
        public readonly StringBuilder Buffer = new();
        public int ReadOffset;
        public volatile bool Exited;
        public int? ExitCode;
        public readonly object Lock = new();
    }

    private readonly Dictionary<string, Job> _jobs = new();
    private readonly object _lock = new();
    private int _seq;

    /// <summary>Result of a <c>poll</c>: the output produced since the previous poll, plus status.</summary>
    public readonly record struct PollResult(bool Found, string NewOutput, bool Running, int? ExitCode);

    /// <summary>
    /// Launches <paramref name="script"/> in a detached powershell, registers it under a short id
    /// (<c>bg1</c>, <c>bg2</c>, …) and starts capturing its output. Returns the id.
    /// </summary>
    public string Start(string script, string command, string workingDirectory)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = "powershell.exe",
            Arguments              = $"-NoProfile -NonInteractive -EncodedCommand {ShellSession.Encode(script)}",
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        if (Directory.Exists(workingDirectory))
            psi.WorkingDirectory = workingDirectory;

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        string id;
        lock (_lock)
        {
            id = "bg" + (++_seq);
        }
        var job = new Job { Id = id, Process = process, Command = command };

        process.OutputDataReceived += (_, e) => Append(job, e.Data);
        process.ErrorDataReceived  += (_, e) => Append(job, e.Data);
        process.Exited += (_, _) =>
        {
            lock (job.Lock)
            {
                job.Exited   = true;
                try { job.ExitCode = process.ExitCode; } catch { job.ExitCode = null; }
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        lock (_lock) _jobs[id] = job;
        return id;
    }

    private static void Append(Job job, string? data)
    {
        if (data is null) return;
        lock (job.Lock) job.Buffer.Append(data).Append('\n');
    }

    /// <summary>
    /// Returns the output produced since the previous poll and the job status. When the job has
    /// exited and all of its output has been drained, it is removed from the registry.
    /// </summary>
    public PollResult Poll(string id)
    {
        Job? job;
        lock (_lock) _jobs.TryGetValue(id, out job);
        if (job is null) return new PollResult(false, string.Empty, false, null);

        string chunk;
        bool exited;
        int? exit;
        lock (job.Lock)
        {
            chunk = job.Buffer.ToString(job.ReadOffset, job.Buffer.Length - job.ReadOffset);
            job.ReadOffset = job.Buffer.Length;
            exited = job.Exited;
            exit   = job.ExitCode;
        }
        if (exited)
            lock (_lock) _jobs.Remove(id);

        return new PollResult(true, chunk, !exited, exit);
    }

    /// <summary>Terminates a background job (whole process tree) and removes it. Returns false if unknown.</summary>
    public bool Stop(string id)
    {
        Job? job;
        lock (_lock)
        {
            if (!_jobs.TryGetValue(id, out job)) return false;
            _jobs.Remove(id);
        }
        try { job!.Process.Kill(entireProcessTree: true); } catch { }
        try { job!.Process.Dispose(); } catch { }
        return true;
    }

    /// <summary>Snapshot of the currently tracked job ids and their running state (for diagnostics).</summary>
    public IReadOnlyList<(string Id, string Command, bool Running)> List()
    {
        lock (_lock)
            return _jobs.Values.Select(j => (j.Id, j.Command, !j.Exited)).ToList();
    }
}
