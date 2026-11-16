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

    [Fact]
    public async Task ReturnsAtTheLine_BeforeTheDeadline()
    {
        var (tool, id) = await StartAsync($"echo booting; {Sleep(1)}; echo 'Now listening on: http://localhost:5000'; {Sleep(20)}");
        using (tool)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var said = await tool.ExecuteAsync(Args(new { action = "wait", id, until = "Now listening", timeout_seconds = 30 }), default);

            Assert.Contains("appeared; still running", said);
            Assert.Contains("http://localhost:5000", said);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), $"returned after {watch.Elapsed}, not at the line");
            await tool.ExecuteAsync(Args(new { action = "stop", id }), default);
        }
    }

    [Fact]
    public async Task WhatFollowsTheLine_IsReturnedToo_NeverLostToTheNextPoll()
    {
        var (tool, id) = await StartAsync($"echo 'Now listening'; echo 'Unhandled exception: port 5000 in use'; {Sleep(20)}");
        using (tool)
        {
            await Task.Delay(1500);   // both lines printed before the wait reads them
            var said = await tool.ExecuteAsync(Args(new { action = "wait", id, until = "Now listening", timeout_seconds = 30 }), default);
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
            var said = await tool.ExecuteAsync(Args(new { action = "wait", id, until = "listening", timeout_seconds = 30 }), default);
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
