using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Inferpal.Config;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>run_command action='wait'</c>: a server started in the background is waited for until its "listening" line, instead of
/// poll after poll. Returns at the line, at the job's end, or at the deadline — said as such.
/// </summary>
public sealed class RunCommandWaitTests
{
    private static JsonElement Args(object o) => JsonSerializer.SerializeToElement(o);

    private static async Task<(RunCommandTool Tool, string Id)> StartAsync(string command)
    {
        var tool = new RunCommandTool(new NoopApproval(), new InferpalConfig(), () => Path.GetTempPath());
        var started = await tool.ExecuteAsync(Args(new { command, background = true }), default);
        return (tool, Regex.Match(started, "background job '([^']+)'").Groups[1].Value);
    }

    // The shell's own sleep, in both dialects the session may resolve to (PowerShell on Windows, a POSIX shell elsewhere).
    private static string Sleep(int s) => OperatingSystem.IsWindows() ? $"Start-Sleep -Seconds {s}" : $"sleep {s}";

    /// <summary>A command that creates <paramref name="path"/>: the job's own word that it got that far.</summary>
    private static string Touch(string path) =>
        OperatingSystem.IsWindows() ? $"New-Item -ItemType File -Path '{path}' | Out-Null" : $"touch '{path}'";

    // ⚠ A loaded runner can take tens of seconds to start a shell: a wait has the budget of a test that waits, and what it
    // proves holds whatever the start costs — the job sleeps far longer than the wait's own budget.
    private const int WaitBudget = 120;

    [Fact]
    public async Task ReturnsAtTheLine_BeforeTheDeadline()
    {
        var (tool, id) = await StartAsync($"echo booting; {Sleep(1)}; echo 'Now listening on: http://localhost:5000'; {Sleep(200)}");
        using (tool)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var said = await tool.ExecuteAsync(Args(new { action = "wait", id, until = "Now listening", timeout_seconds = WaitBudget }), default);

            Assert.Contains("appeared; still running", said);
            Assert.Contains("http://localhost:5000", said);
            // At the line, not at the deadline: the job itself runs on for 200 s.
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(WaitBudget - 10), $"returned after {watch.Elapsed}, not at the line");
            await tool.ExecuteAsync(Args(new { action = "stop", id }), default);
        }
    }

    [Fact]
    public async Task WhatFollowsTheLine_IsReturnedToo_NeverLostToTheNextPoll()
    {
        var printed = Path.Combine(Path.GetTempPath(), "inferpal-wait-" + Guid.NewGuid().ToString("N"));
        var (tool, id) = await StartAsync($"echo 'Now listening'; echo 'Unhandled exception: port 5000 in use'; {Touch(printed)}; {Sleep(200)}");
        using (tool)
        {
            // Both lines printed before the wait reads them: the job says so, never a fixed delay a slow start outlasts.
            var deadline = DateTime.UtcNow.AddSeconds(WaitBudget);
            while (!File.Exists(printed) && DateTime.UtcNow < deadline) await Task.Delay(200);
            try { File.Delete(printed); } catch { }
            var said = await tool.ExecuteAsync(Args(new { action = "wait", id, until = "Now listening", timeout_seconds = WaitBudget }), default);
            Assert.Contains("Unhandled exception", said);
            await tool.ExecuteAsync(Args(new { action = "stop", id }), default);
        }
    }

    [Fact]
    public async Task AJobThatEndsFirst_IsSaid_WithItsExitCode()
    {
        var (tool, id) = await StartAsync("echo 'port in use'; exit 3");
        using (tool)
        {
            var said = await tool.ExecuteAsync(Args(new { action = "wait", id, until = "listening", timeout_seconds = WaitBudget }), default);
            Assert.Contains("finished (exit code 3) before 'listening' appeared", said);
            Assert.Contains("port in use", said);
        }
    }

    [Fact]
    public async Task TheDeadline_IsSaidAsStillStarting_NotAFailure()
    {
        var (tool, id) = await StartAsync(Sleep(30));
        using (tool)
        {
            var said = await tool.ExecuteAsync(Args(new { action = "wait", id, until = "listening", timeout_seconds = 1 }), default);
            Assert.Contains("still running", said);
            Assert.Contains("may still be starting", said);
            await tool.ExecuteAsync(Args(new { action = "stop", id }), default);
        }
    }
}
