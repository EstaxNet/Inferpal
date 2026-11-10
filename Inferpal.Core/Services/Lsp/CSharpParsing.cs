using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis.CSharp;

namespace Inferpal.Services.Lsp;

/// <summary>The language every C# parse of the Core reads at: the one place that answers "which C# is this?".</summary>
/// <remarks>
/// ⚠ Never Roslyn's default, which is neither the project's language nor the newest: an older language reads a C# 14
/// <c>extension(string s) { … }</c> block as a constructor and a C# 15 <c>union</c> as a top-level statement.
/// ⚠ Two answers, by what the reader does with the tree:
/// <list type="bullet">
/// <item>what reads SYNTAX alone — <see cref="Tools.SyntaxGuard"/>, which must not refuse on a doubt, and the RAG chunker —
/// reads at <see cref="Newest"/>;</item>
/// <item>what BINDS or renames identifiers reads at <see cref="For"/>, the language the projects compile: the newest
/// one binds differently — inside an accessor, <c>field</c> is C# 14's keyword and no longer a member named
/// <c>field</c>, and an overload taking a span wins over one taking an enumerable — so a rename at the newest language
/// leaves those uses on the old name in a .NET 8 project.</item>
/// </list>
/// File-based programs (.NET 10's <c>#:package</c>, <c>#:sdk</c>) read as the SDK reads them.
/// </remarks>
internal static class CSharpParsing
{
    /// <summary>The newest language this parser knows.</summary>
    internal static readonly CSharpParseOptions Newest = At(LanguageVersion.Preview);

    /// <summary>The language the C# projects under <paramref name="root"/> compile, <see cref="Newest"/> when none says.</summary>
    /// <remarks>One language for the whole workspace, because a compilation refuses trees parsed at two: the newest of
    /// its projects' — in a workspace that mixes .NET 8 and .NET 10, a .NET 8 member named <c>field</c> read inside an
    /// accessor is the residual case.</remarks>
    internal static CSharpParseOptions For(string root) => At(LanguageOf(root));

    private static CSharpParseOptions At(LanguageVersion version) => new CSharpParseOptions(version)
        .WithFeatures([new KeyValuePair<string, string>("FileBasedProgram", "true")]);

    internal static LanguageVersion LanguageOf(string root)
    {
        LanguageVersion? newest = null;
        foreach (var project in WorkspaceScan.EnumerateFiles(root, "*.csproj"))
            if (ProjectLanguage(project) is { } language && (newest is null || language > newest)) newest = language;
        return newest ?? LanguageVersion.Preview;
    }

    /// <summary>A project's language: its <c>LangVersion</c>, else the oldest default among its target frameworks (its
    /// code compiles for each); <c>null</c> when neither can be read.</summary>
    /// <remarks>Read where MSBuild reads it: the project, then the nearest <c>Directory.Build.props</c> above it, and
    /// that file's own parent only when it imports it. The nearest value wins, and a value set but not readable here —
    /// under a condition, or made of a property — stops the search: MSBuild takes that one, never one further up.</remarks>
    internal static LanguageVersion? ProjectLanguage(string csproj)
    {
        string? lang = null, frameworks = null;   // "" = set, but not readable here
        for (var file = csproj; file is not null && (lang is null || frameworks is null);)
        {
            XDocument doc;
            try { doc = XDocument.Parse(Tools.TextFileEncoding.ReadText(file)); }
            catch (Exception ex) { Diagnostics.Swallow($"CSharpParsing.Read({Path.GetFileName(file)})", ex); break; }
            lang ??= Property(doc, "LangVersion");
            frameworks ??= Property(doc, "TargetFrameworks") ?? Property(doc, "TargetFramework");
            file = ReferenceEquals(file, csproj) || ImportsItsParent(doc) ? PropsAbove(file) : null;
        }
        if (lang is { Length: > 0 } && LanguageVersionFacts.TryParse(lang, out var declared))
            return declared.MapSpecifiedToEffectiveVersion();

        LanguageVersion? oldest = null;
        foreach (var framework in frameworks?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [])
            if (DefaultFor(framework) is { } language && (oldest is null || language < oldest)) oldest = language;
        return oldest;
    }

    /// <summary>The C# a target framework compiles by default; <c>null</c> for one not read (a property, a typo).</summary>
    internal static LanguageVersion? DefaultFor(string framework)
    {
        var tfm = framework.ToLowerInvariant();
        if (Regex.Match(tfm, @"^net(\d{1,3})\.\d+", RegexOptions.None, RegexBudget.Default) is { Success: true } modern)
            return int.Parse(modern.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) switch
            {
                5 => LanguageVersion.CSharp9,
                6 => LanguageVersion.CSharp10,
                7 => LanguageVersion.CSharp11,
                8 => LanguageVersion.CSharp12,
                9 => LanguageVersion.CSharp13,
                10 => LanguageVersion.CSharp14,
                > 10 => LanguageVersion.Preview,   // newer than this parser's newest released language
                _ => null,
            };
        if (tfm.StartsWith("netcoreapp3.", StringComparison.Ordinal) || tfm == "netstandard2.1") return LanguageVersion.CSharp8;
        if (tfm.StartsWith("netcoreapp", StringComparison.Ordinal) || tfm.StartsWith("netstandard", StringComparison.Ordinal)
            || Regex.IsMatch(tfm, @"^net\d{2,3}$", RegexOptions.None, RegexBudget.Default))
            return LanguageVersion.CSharp7_3;
        return null;
    }

    /// <summary>The value of property <paramref name="name"/> as written: <c>null</c> when the file does not set it,
    /// <c>""</c> when it sets it under a condition. A value made of another property is returned as it is, and parses as
    /// no language and no framework.</summary>
    private static string? Property(XDocument doc, string name)
    {
        string? value = null;
        foreach (var element in doc.Descendants().Where(e => e.Name.LocalName == name))
        {
            if (element.Attribute("Condition") is not null || element.Parent?.Attribute("Condition") is not null) return "";
            value = element.Value.Trim();   // the last one set wins, as in MSBuild
        }
        return value;
    }

    private static bool ImportsItsParent(XDocument doc) =>
        doc.Descendants().Any(e => e.Name.LocalName == "Import"
            && (e.Attribute("Project")?.Value.Contains("Directory.Build.props", StringComparison.OrdinalIgnoreCase) ?? false));

    /// <summary>The nearest <c>Directory.Build.props</c> in a folder above <paramref name="file"/>'s own, the project's own
    /// folder included when <paramref name="file"/> is the project.</summary>
    private static string? PropsAbove(string file)
    {
        var dir = Path.GetDirectoryName(file);
        if (Path.GetFileName(file).Equals("Directory.Build.props", StringComparison.OrdinalIgnoreCase))
            dir = Path.GetDirectoryName(dir);
        for (; dir is not null; dir = Path.GetDirectoryName(dir))
        {
            var props = Path.Combine(dir, "Directory.Build.props");
            if (File.Exists(props)) return props;
        }
        return null;
    }
}
