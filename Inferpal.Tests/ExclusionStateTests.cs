using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A test class that forgets every declared workspace (<c>ContentExclusions.ForgetAllForTests</c>) runs alone.
/// </summary>
/// <remarks>⚠ The declared roots are process-wide: run beside other classes, forgetting them drops their rules mid-test —
/// an intermittent red, or a false green where a test asserts that something is NOT read. The property, not a list: every
/// class whose code calls it belongs to a collection whose definition disables parallelization.</remarks>
public class ExclusionStateTests
{
    [Fact]
    public void EveryClassThatForgetsEveryRoot_RunsInANonParallelCollection()
    {
        var assembly = typeof(ExclusionStateTests).Assembly;
        var serial = assembly.GetTypes()
            .SelectMany(t => t.GetCustomAttributesData())
            .Where(a => a.AttributeType == typeof(CollectionDefinitionAttribute)
                        && a.NamedArguments.Any(n => n.MemberName == nameof(CollectionDefinitionAttribute.DisableParallelization)
                                                     && n.TypedValue.Value is true))
            .Select(a => a.ConstructorArguments.FirstOrDefault().Value as string)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains(ContentExclusionSerialCollection.Name, serial);   // witness: the definitions are read

        var tests = Path.Combine(ConversationPersistenceSilenceTests.RepoRoot(), "Inferpal.Tests");
        var callers = new List<Type>();
        foreach (var file in Directory.GetFiles(tests, "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                                 && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            var code = ConventionCoverageTests.CodeOnly(file);
            // The class each call is written in: the nearest class declared before it (a file may hold several).
            var classes = Regex.Matches(code, @"\bclass\s+(\w+)").Cast<Match>().ToList();
            foreach (Match call in Regex.Matches(code, @"\bForgetAllForTests\s*\("))
                if (classes.LastOrDefault(c => c.Index < call.Index) is { } holder
                    && assembly.GetType("Inferpal.Tests." + holder.Groups[1].Value) is { } type
                    && type.GetMethods().Any(x => x.GetCustomAttribute<FactAttribute>() is not null || x.GetCustomAttribute<TheoryAttribute>() is not null))
                    callers.Add(type);
        }
        Assert.NotEmpty(callers);   // witness: ContentExclusionTests calls it

        var loose = callers
            .Where(t => t.GetCustomAttributesData()
                          .FirstOrDefault(a => a.AttributeType == typeof(CollectionAttribute))
                          ?.ConstructorArguments.FirstOrDefault().Value is not string name
                        || !serial.Contains(name))
            .Select(t => t.Name)
            .ToList();
        Assert.True(loose.Count == 0,
            "These test classes forget every declared workspace outside a non-parallel collection — put them in "
            + "[Collection(ContentExclusionSerialCollection.Name)]: " + string.Join(", ", loose));
    }
}
