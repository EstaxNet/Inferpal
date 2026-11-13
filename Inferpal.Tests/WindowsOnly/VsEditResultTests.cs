using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Inferpal.Services.VsIntegration;
using Microsoft.VisualStudio.RpcContracts.Editor;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  An edit Visual Studio refuses is read as refused.
//
//  The editor reports a refused edit (document changed under it, closed, too many versions ahead) in
//  the response of EditAsync, without throwing. Five sites discarded that response: a /refactor whose
//  target region the user kept typing in reported nothing, "Edit with AI" closed its spinner on an
//  unchanged document, /test said "tests extended", and insert_at_cursor / replace_selection told the
//  model the edit was made.
// ──────────────────────────────────────────────────────────────────────────────────────────────
public sealed class VsEditResultTests
{
    [Fact]
    public void AnEditEveryDocumentTook_IsApplied()
    {
        Assert.True(VsEditResult.Applied(true, [EditResult.Success]));
        Assert.True(VsEditResult.Applied(true, []));
    }

    [Theory]
    [InlineData(EditResult.DocumentChanged)]
    [InlineData(EditResult.DocumentNotOpen)]
    [InlineData(EditResult.DocumentVersionTooOld)]
    [InlineData(EditResult.Aborted)]
    public void AnEditADocumentRefused_IsNotApplied_EvenUnderASucceededResponse(EditResult refusal)
    {
        Assert.False(VsEditResult.Applied(true, [EditResult.Success, refusal]));
    }

    [Fact]
    public void AFailedResponse_IsNotApplied()
    {
        Assert.False(VsEditResult.Applied(false, [EditResult.Success]));
    }

    [Fact]
    public void EveryEditInTheVisualStudioAdapter_ReadsItsResponse()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var files = Directory.EnumerateFiles(Path.Combine(dir!.FullName, "Inferpal"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToList();

        var calls = 0;
        var offenders = new System.Collections.Generic.List<string>();
        foreach (var file in files)
        {
            var code = ConventionCoverageTests.CodeOnly(file);
            foreach (Match m in Regex.Matches(code, @"await\s+[\w.()]+\.EditAsync\("))
            {
                calls++;
                if (!code[..m.Index].TrimEnd().EndsWith('=')) offenders.Add(Path.GetFileName(file));
            }
        }

        // WITNESS: the scan reads the edits it is about (below the real count, so a removed site does not redden it).
        Assert.True(calls >= 3, $"Only {calls} EditAsync call(s) found: the scan no longer reads the adapter.");
        Assert.True(offenders.Count == 0,
            "EditAsync calls whose response is discarded — a refused edit is returned, not thrown: "
            + string.Join(", ", offenders));
    }
}
