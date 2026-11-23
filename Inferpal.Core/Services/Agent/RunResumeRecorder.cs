using System.Text.Json;
using Inferpal.Models;

namespace Inferpal.Services.Agent;

/// <summary>
/// Keeps, as a turn goes, what its run carries to be resumed after the closing cut it (<see cref="RunResume"/>): wraps the
/// turn's callbacks, so the plan, each action and the last check reach the run's file as they happen. One recorder for
/// every front-end: Visual Studio's turn, the host's, ACP's.
/// </summary>
internal sealed class RunResumeRecorder
{
    private readonly FileHistoryService _history;
    private readonly List<ToolExecution> _executions = [];
    private AgentPlan? _plan;

    private RunResumeRecorder(FileHistoryService history) => _history = history;

    /// <summary>Starts the record for the run just begun: the question as the person typed it, and the mode — and, for a
    /// run that resumes another (<paramref name="resumes"/>), what that one did, so a second cut still knows all of it.</summary>
    internal static RunResumeRecorder Start(FileHistoryService history, string question, bool agent, RunResumeTurn? resumes = null)
    {
        history.NoteResumeStart(question, agent ? "agent" : "chat");
        if (resumes is not null) history.NoteResume(r => r.CarriedFrom(resumes.Carried, resumes.RunId, resumes.Files));
        return new(history);
    }

    /// <summary>The plan, as soon as it is ready.</summary>
    internal Action<AgentPlan> OnPlan(Action<AgentPlan>? inner) => plan =>
    {
        _plan = plan;
        NotePlan();
        inner?.Invoke(plan);
    };

    /// <summary>A step that changed: done or not.</summary>
    internal Action<int, AgentStepStatus> OnStep(Action<int, AgentStepStatus>? inner) => (index, status) =>
    {
        if (status is AgentStepStatus.Done or AgentStepStatus.Skipped) NotePlan();
        inner?.Invoke(index, status);
    };

    /// <summary>An action that ran: its line, and the last check when it proved one.</summary>
    internal Action<ToolExecution> OnTool(Action<ToolExecution>? inner) => execution =>
    {
        _executions.Add(execution);
        var line = Line(execution);
        var check = ChatTurnPolicy.LastCheck(_executions) is { Failed: bool failed } verdict
            ? (Passed: !failed, Kind: verdict.Kind == ChatTurnPolicy.CheckKind.Tests ? "tests" : "build")
            : ((bool Passed, string Kind)?)null;
        _history.NoteResume(r => check is { } c ? r.WithStep(line).WithCheck(c.Passed, c.Kind) : r.WithStep(line));
        inner?.Invoke(execution);
    };

    private void NotePlan()
    {
        // ⚠ The stand-in for a plan the model never wrote is no plan: resumed as a real one-step plan, its single step would
        // close on the first call. The run is resumed without one, and plans again.
        if (_plan is not { IsFallback: false } plan) return;
        // A skipped step counts as done: the run moved past it, and resuming must not send the model back to it. Its tool
        // goes with it: what tells a step that writes or checks from one that reads.
        _history.NoteResume(r => r.WithPlan(plan.Goal, plan.Steps.Select(s =>
            new ResumeStep(s.Description, s.Status is AgentStepStatus.Done or AgentStepStatus.Skipped, s.ToolHint))));
    }

    /// <summary>An action as one line: the tool, and what it acted on — a path, a command, a query —, or the tool alone.</summary>
    internal static string Line(ToolExecution execution)
    {
        var target = Target(execution.Input);
        var line = target is null ? execution.Name : $"{execution.Name} {target}";
        return execution.HasErrors ? line + " (failed)" : line;
    }

    private static readonly string[] TargetKeys = ["path", "file_path", "file", "command", "query", "url", "symbol", "name"];

    private static string? Target(string input)
    {
        try
        {
            using var doc = JsonDocument.Parse(input);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var key in TargetKeys)
                if (doc.RootElement.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
                    && value.GetString() is { Length: > 0 } text)
                    return text.Replace('\n', ' ');
        }
        catch (JsonException) { /* arguments the model wrote badly: the tool's name alone */ }
        return null;
    }
}
