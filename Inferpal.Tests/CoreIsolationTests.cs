using System.Reflection;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Architecture guard: Inferpal.Core must stay editor-agnostic. If one of these tests fails,
/// a Visual Studio (or WPF) dependency leaked into the core library — put the offending code
/// behind an editor port (IEditorSurface / ApprovalServiceBase…) in the VS adapter instead.
/// </summary>
public class CoreIsolationTests
{
    private static Assembly CoreAssembly => typeof(AgentOrchestrator).Assembly;

    [Fact]
    public void Core_lives_in_its_own_assembly()
    {
        Assert.Equal("Inferpal.Core", CoreAssembly.GetName().Name);
    }

    [Fact]
    public void Core_references_no_visual_studio_assembly()
    {
        var offenders = CoreAssembly.GetReferencedAssemblies()
            .Where(r => r.Name!.StartsWith("Microsoft.VisualStudio", StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Name)
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Core_references_no_wpf_assembly()
    {
        string[] wpf = ["PresentationFramework", "PresentationCore", "WindowsBase", "System.Xaml"];

        var offenders = CoreAssembly.GetReferencedAssemblies()
            .Where(r => wpf.Contains(r.Name, StringComparer.OrdinalIgnoreCase))
            .Select(r => r.Name)
            .ToList();

        Assert.Empty(offenders);
    }
}
