using System.IO;
using System.Text.Json;
using Inferpal.Services.CodeActions;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Locks <c>Fixtures/inline-diff-scenarios.json</c> — the scenarios used to validate the
/// in-editor renderer by hand (its WPF adornment lives in <c>devenv.exe</c>, out of reach of
/// xUnit). What a per-hunk decision is supposed to produce cannot be checked by looking at the
/// editor, so the maths is verified here: every scenario must plan the hunk count it advertises,
/// and its accepted subset must merge to the text it declares. A fixture that drifts from the
/// planner fails the suite instead of turning into a false "the preview is broken".
/// </summary>
public class InlineDiffScenarioFixturesTests
{
    private sealed record Scenario(
        string Id, string Title, string File, int Hunks, int[] Accept,
        string Expect, string Instruction,
        string[] Old, string[] New, string[]? Expected);

    private sealed record Fixtures(Scenario[] Scenarios);

    private static readonly Fixtures Data = Load();

    /// <summary>Repo root = first ancestor of the test bin folder containing README.md.</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !System.IO.File.Exists(Path.Combine(dir.FullName, "README.md")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static Fixtures Load()
    {
        var path = Path.Combine(RepoRoot(), "Inferpal.Tests", "Fixtures", "inline-diff-scenarios.json");
        var data = JsonSerializer.Deserialize<Fixtures>(
            System.IO.File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(data);
        return data!;
    }

    private static string Text(string[] lines) => string.Join('\n', lines);

    /// <summary>Declared merge result, or the implied one (reject-all = old, accept-all = new).</summary>
    private static string ExpectedText(Scenario s)
    {
        if (s.Expected is not null)       return Text(s.Expected);
        if (s.Accept.Length == 0)         return Text(s.Old);
        if (s.Accept.Length == s.Hunks)   return Text(s.New);
        Assert.Fail($"Scenario {s.Id}: a partial accept must declare its \"expected\" text.");
        return string.Empty;
    }

    [Fact]
    public void Fixtures_AreLoadable_AndSelfConsistent()
    {
        Assert.NotEmpty(Data.Scenarios);
        Assert.Equal(Data.Scenarios.Length, Data.Scenarios.Select(s => s.Id).Distinct().Count());
        Assert.Equal(Data.Scenarios.Length, Data.Scenarios.Select(s => s.File).Distinct().Count());

        foreach (var s in Data.Scenarios)
        {
            Assert.False(string.IsNullOrWhiteSpace(s.Title),       $"Scenario {s.Id}: missing title.");
            Assert.False(string.IsNullOrWhiteSpace(s.Expect),      $"Scenario {s.Id}: missing 'expect' (what the user should see).");
            Assert.False(string.IsNullOrWhiteSpace(s.Instruction), $"Scenario {s.Id}: missing 'instruction' (what the user should click).");
            Assert.NotEqual(Text(s.Old), Text(s.New));
            Assert.All(s.Accept, i => Assert.InRange(i, 1, s.Hunks));
            Assert.Equal(s.Accept.Length, s.Accept.Distinct().Count());
        }
    }

    [Fact]
    public void EveryScenario_PlansTheAdvertisedHunkCount()
    {
        foreach (var s in Data.Scenarios)
        {
            var plan = InlineDiffPlanner.Plan(Text(s.Old), Text(s.New));

            Assert.True(s.Hunks == plan.Hunks.Count,
                $"Scenario {s.Id} advertises {s.Hunks} hunk(s) but plans {plan.Hunks.Count} — " +
                "the harness instruction would tell the user to click buttons that do not exist.");
        }
    }

    [Fact]
    public void EveryScenario_AcceptedSubset_MergesToTheExpectedText()
    {
        foreach (var s in Data.Scenarios)
        {
            var plan = InlineDiffPlanner.Plan(Text(s.Old), Text(s.New));

            Assert.True(ExpectedText(s) == InlineDiffPlanner.Apply(plan, s.Accept),
                $"Scenario {s.Id}: accepting [{string.Join(", ", s.Accept)}] does not produce the declared text.");
        }
    }

    [Fact]
    public void EveryScenario_AcceptAllOrNothing_ReproducesNewOrOldText()
    {
        foreach (var s in Data.Scenarios)
        {
            var plan = InlineDiffPlanner.Plan(Text(s.Old), Text(s.New));
            var all  = plan.Hunks.Select(h => h.Index).ToArray();

            Assert.True(Text(s.New) == InlineDiffPlanner.Apply(plan, all),  $"Scenario {s.Id}: accept-all lost the new text.");
            Assert.True(Text(s.Old) == InlineDiffPlanner.Apply(plan, []),   $"Scenario {s.Id}: reject-all altered the old text.");
        }
    }

    [Fact]
    public void EveryScenario_CoversADistinctShape()
    {
        // The suite is a checklist: dropping the insertion-only or the EOF scenario would leave
        // the shapes that broke first during development unvalidated.
        var plans = Data.Scenarios.ToDictionary(
            s => s.Id, s => InlineDiffPlanner.Plan(Text(s.Old), Text(s.New)));

        Assert.Contains(plans.Values, p => p.Hunks.Any(h => h.OldLines.Count == 0));   // insertion only
        Assert.Contains(plans.Values, p => p.Hunks.Any(h => h.NewLines.Count == 0));   // deletion only
        Assert.Contains(plans.Values, p => p.Hunks.Count >= 3);                        // per-hunk decisions
        Assert.Contains(Data.Scenarios, s => s.Accept.Length > 0 && s.Accept.Length < s.Hunks);   // partial accept
        Assert.Contains(Data.Scenarios, s => s.Accept.Length == 0);                    // reject all

        // At least one hunk must reach EOF (the delicate offset case of ToEdits).
        Assert.Contains(plans.Values, p =>
            p.Hunks.Any(h => h.OldStart + h.OldLines.Count >= p.OldText.Split('\n').Length));
    }
}
