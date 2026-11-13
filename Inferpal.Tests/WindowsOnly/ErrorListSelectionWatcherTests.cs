using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Inferpal.GhostText;
using Inferpal.Services.CodeActions;
using Microsoft.VisualStudio.Shell.Interop;
using Xunit;

namespace Inferpal.Tests;

/// <summary>A line of Visual Studio's Error List, read by the in-process half for "Fix with Inferpal".</summary>
public class ErrorListSelectionWatcherTests
{
    [Fact]
    public void AnErrorListLine_IsReadOneBased_WithItsSeverityInABuildsWords()
    {
        var columns = new Dictionary<string, object?>
        {
            [ErrorListSelectionWatcher.KeyDocument] = "C:\\p\\Foo.cs",
            [ErrorListSelectionWatcher.KeyLine]     = 11,
            [ErrorListSelectionWatcher.KeyColumn]   = 4,
            [ErrorListSelectionWatcher.KeySeverity] = __VSERRORCATEGORY.EC_WARNING,
            [ErrorListSelectionWatcher.KeyCode]     = "CS0168",
            [ErrorListSelectionWatcher.KeyText]     = "The variable 'e' is declared but never used",
        };

        Assert.Equal(new ProblemEntry("C:\\p\\Foo.cs", 12, 5, "warning", "CS0168", "The variable 'e' is declared but never used"),
                     ErrorListSelectionWatcher.ToProblem(k => columns.GetValueOrDefault(k)));

        // A NuGet warning on a project names no line: none is made up.
        columns.Remove(ErrorListSelectionWatcher.KeyLine);
        columns[ErrorListSelectionWatcher.KeySeverity] = __VSERRORCATEGORY.EC_MESSAGE;
        var noLine = ErrorListSelectionWatcher.ToProblem(k => columns.GetValueOrDefault(k));
        Assert.Equal((0, 0, "info"), (noLine.Line, noLine.Column, noLine.Severity));
    }

    [Fact]
    public void ASeverityMissingOrGivenAsANumber_IsNeverPromotedToAnError()
    {
        // Errors are listed first and fill the cap: a line of unknown severity must not pass ahead of them.
        Assert.Equal("info", ErrorListSelectionWatcher.ToProblem(_ => null).Severity);
        Assert.Equal("warning", ErrorListSelectionWatcher.ToProblem(
            k => k == ErrorListSelectionWatcher.KeySeverity ? (int)__VSERRORCATEGORY.EC_WARNING : null).Severity);
        Assert.Equal("error", ErrorListSelectionWatcher.ToProblem(
            k => k == ErrorListSelectionWatcher.KeySeverity ? __VSERRORCATEGORY.EC_ERROR : null).Severity);
    }

    [Fact]
    public void TheColumnKeys_AreTheTablesOwn()
    {
        // StandardTableKeyNames, read in the .NET Framework build of Microsoft.VisualStudio.Shell.Framework — the one devenv
        // loads; its .NET 8 build, which this leg compiles against, has no table API. A key the table does not use reads
        // every line as empty. The version is the referenced package's: bumped there, this test names the file it lacks.
        var packages = Environment.GetEnvironmentVariable("NUGET_PACKAGES")
                    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        var dll = Path.Combine(packages, "microsoft.visualstudio.shell.framework", "17.14.40264", "lib", "net472",
                               "Microsoft.VisualStudio.Shell.Framework.dll");
        Assert.True(File.Exists(dll), dll);

        using var pe = new PEReader(File.OpenRead(dll));
        var md   = pe.GetMetadataReader();
        var type = md.TypeDefinitions.Select(md.GetTypeDefinition)
                     .Single(t => md.GetString(t.Name) == "StandardTableKeyNames"
                               && md.GetString(t.Namespace) == "Microsoft.VisualStudio.Shell.TableManager");
        var values = type.GetFields().Select(md.GetFieldDefinition)
                         .Where(f => !f.GetDefaultValue().IsNil)
                         .ToDictionary(f => md.GetString(f.Name), f =>
                         {
                             var blob = md.GetBlobReader(md.GetConstant(f.GetDefaultValue()).Value);
                             return blob.ReadUTF16(blob.Length);
                         });

        Assert.Equal(values["DocumentName"],  ErrorListSelectionWatcher.KeyDocument);
        Assert.Equal(values["Line"],          ErrorListSelectionWatcher.KeyLine);
        Assert.Equal(values["Column"],        ErrorListSelectionWatcher.KeyColumn);
        Assert.Equal(values["ErrorSeverity"], ErrorListSelectionWatcher.KeySeverity);
        Assert.Equal(values["ErrorCode"],     ErrorListSelectionWatcher.KeyCode);
        Assert.Equal(values["Text"],          ErrorListSelectionWatcher.KeyText);
    }
}
