using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A test class that flips the process-wide language runs alone.
/// </summary>
/// <remarks>
/// <c>Strings.ApplyLanguage</c> / <c>Strings.OverrideCulture</c> is process-wide by design (the product never mutates
/// a thread's culture), so a class that goes English for one assertion does it for every class running beside it. Two
/// classes did it outside a non-parallel collection — <c>VsCodeConversationTests</c> went English while
/// <c>LastCheckFailedTests</c> and <c>SlashCommandCoverageTests</c> compared French text, each red one run in several.
/// The property, not a list: every test class whose code flips the language belongs to a collection whose definition
/// disables parallelization.
/// </remarks>
public class CultureFlipIsSerialTests
{
    private static string TestsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "Inferpal.Tests");
    }

    [Fact]
    public void EveryClassThatFlipsTheLanguage_RunsInANonParallelCollection()
    {
        var assembly = typeof(CultureFlipIsSerialTests).Assembly;
        // The collections whose definition disables parallelization, by name (the attribute keeps its name private).
        var serial = assembly.GetTypes()
            .SelectMany(t => t.GetCustomAttributesData())
            .Where(a => a.AttributeType == typeof(CollectionDefinitionAttribute)
                        && a.NamedArguments.Any(n => n.MemberName == nameof(CollectionDefinitionAttribute.DisableParallelization)
                                                     && n.TypedValue.Value is true))
            .Select(a => a.ConstructorArguments.FirstOrDefault().Value as string)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains(CultureSerialCollection.Name, serial);                // witness: the definitions are read

        var flipping = new List<Type>();
        foreach (var file in Directory.GetFiles(TestsDir(), "*.cs", SearchOption.TopDirectoryOnly))
        {
            var code = ConventionCoverageTests.CodeOnly(file);
            if (!Regex.IsMatch(code, @"\bApplyLanguage\s*\(|\bOverrideCulture\s*=(?!=)")) continue;
            foreach (Match m in Regex.Matches(code, @"\bclass\s+(\w+)"))
                if (assembly.GetType("Inferpal.Tests." + m.Groups[1].Value) is { } type
                    && type.GetMethods().Any(x => x.GetCustomAttribute<FactAttribute>() is not null))
                    flipping.Add(type);
        }

        // Witness, under the real count (16 classes): the scan reads the sources and finds the test classes.
        Assert.True(flipping.Count >= 10, $"only {flipping.Count} language-flipping test classes found: the scan is dead");

        var loose = flipping
            .Where(t => t.GetCustomAttributesData()
                          .FirstOrDefault(a => a.AttributeType == typeof(CollectionAttribute))
                          ?.ConstructorArguments.FirstOrDefault().Value is not string name
                        || !serial.Contains(name))
            .Select(t => t.Name)
            .ToList();
        Assert.True(loose.Count == 0,
            "These test classes flip the process-wide language outside a non-parallel collection — put them in "
            + "[Collection(CultureSerialCollection.Name)]: " + string.Join(", ", loose));
    }
}
