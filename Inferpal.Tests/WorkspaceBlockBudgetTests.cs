using System.Text;
using Inferpal.Services.Prompting;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ The workspace block goes into the first question of every conversation, unasked — and it carried
/// the WHOLE of <c>get_solution_info</c>: on a solution of a hundred projects, tens of thousands of
/// characters, enough to fill the default context window on its own before the question is even read.
/// The other block injected unasked, the RAG auto-context, has a budget for exactly that reason.
/// </summary>
public class WorkspaceBlockBudgetTests
{
    private static string BigSolution(int projects)
    {
        var sb = new StringBuilder($"Solution : Big.sln\nLocation : C:\\src\\Big.sln\nProjects : {projects}\n");
        for (var i = 0; i < projects; i++)
            sb.Append($"\n── Project{i:D3}\n   File : src\\Project{i:D3}\\Project{i:D3}.csproj\n   Framework : net8.0\n")
              .Append("   Refs      : Core, Shared, Contracts\n   Packages  : Newtonsoft.Json, Serilog, Polly, Dapper\n");
        return sb.ToString();
    }

    [Fact]
    public void ALargeSolution_DoesNotFillTheWindowBeforeTheQuestion_AndTheCutIsSaid()
    {
        var info  = BigSolution(120);                            // ~24,000 characters
        var block = WorkspaceContext.Compose(info, "C:\\src\\A.cs\nC:\\src\\B.cs");

        Assert.True(info.Length > 20_000);                       // witness: the input is the large case
        Assert.True(block.Length <= WorkspaceContext.BudgetChars + 300, $"block is {block.Length} characters");
        Assert.Contains("Project000", block);                    // the head is kept
        Assert.Contains("get_solution_info", block);             // and the way to the rest is named
        Assert.Contains("C:\\src\\A.cs", block);                 // the open editors still get their room
    }

    [Fact]
    public void AnOrdinarySolution_IsInjectedWhole_WithNothingAdded()
    {
        // Reference arm: a solution of a few projects.
        var info  = BigSolution(4);
        var block = WorkspaceContext.Compose(info, null);

        Assert.Contains(info.TrimEnd(), block);
        Assert.DoesNotContain("get_solution_info", block);
    }
}
