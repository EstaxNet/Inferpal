using System.IO;
using System.Text;
using System.Text.Json;
using Inferpal.Services;
using Inferpal.Services.Agent;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>get_solution_info</c> listed every project with its references and packages. Eighty projects answered 30 140
/// characters — over the loop's cap, which cuts the MIDDLE: the projects in the middle of the solution vanished from
/// a list headed "Projects : 80". The blocks stop where they fit and the rest are named, with the way to read one.
/// </summary>
[Collection(SignalCollection.Name)]
public sealed class SolutionInfoBudgetTests : IDisposable
{
    private readonly SignalScratchDir _scratch = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"slnbudget-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
        _scratch.Dispose();
    }

    private async Task<string> SolutionOfAsync(int projects)
    {
        var sln = new StringBuilder("Microsoft Visual Studio Solution File, Format Version 12.00\n");
        for (var i = 0; i < projects; i++)
        {
            var name = $"Company.Product.Component{i:D2}";
            var dir  = Directory.CreateDirectory(Path.Combine(_root, "proj", name)).FullName;
            File.WriteAllText(Path.Combine(dir, name + ".csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup><ItemGroup>"
                + string.Concat(Enumerable.Range(0, 8).Select(k => $"<PackageReference Include=\"Vendor.Library.Package{k}\" Version=\"1.2.{k}\" />"))
                + "</ItemGroup></Project>");
            sln.Append($"Project(\"{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}\") = \"{name}\", \"proj\\{name}\\{name}.csproj\", "
                     + $"\"{{{Guid.NewGuid().ToString().ToUpperInvariant()}}}\"\nEndProject\n");
        }
        File.WriteAllText(Path.Combine(_root, "Big.sln"), sln.ToString());

        using var args = JsonDocument.Parse(JsonSerializer.Serialize(new { path = _root }));
        return await new GetSolutionInfoTool(new NullEditorSurface(), () => _root)
            .ExecuteAsync(args.RootElement, CancellationToken.None);
    }

    [Fact]
    public async Task ALargeSolution_ReachesTheModelWhole_EveryProjectNamed()
    {
        var info = await SolutionOfAsync(80);

        Assert.Contains("Company.Product.Component00", info);                                          // witness
        Assert.Matches(@"\(\+\d+ more project\(s\), named only", info);
        Assert.Contains("read a project file (its path is in the solution) with read_file", info);
        Assert.Contains("Company.Product.Component79", info);                       // the last one is still named
        Assert.Equal(info, AgentOrchestrator.CapForContext(info));
    }

    [Fact]
    public async Task ASmallSolution_ShowsEveryProjectInFull_WithoutANote()
    {
        var info = await SolutionOfAsync(3);

        Assert.Equal(3, info.Split("Packages  :").Length - 1);
        Assert.DoesNotContain("named only", info);
    }
}
