using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Locks the packaging invariants that decide whether the product is <b>translated at all</b>.
///
/// <para>
/// They share the property that makes them worth a test: when they break, <b>nothing says so</b>.
/// The VSIX builds, installs, the chat window opens and answers - and the entire interface is in
/// English, in all nine languages at once. Both were already paid for once, and neither was held
/// by a test.
/// </para>
/// </summary>
public class VsixPackagingTests
{
    internal const string VersionToken = "|Inferpal;GetVsixVersion|";

    [Theory]
    // The MEF asset: without it, GhostTextViewListener never enters the catalogue.
    [InlineData("<Asset Type=\"Microsoft.VisualStudio.MefComponent\"")]
    [InlineData("Path=\"Inferpal.InProc.dll\"")]
    // The pkgdef asset: without it, GhostTextPackage is neither registered nor auto-loaded.
    [InlineData("<Asset Type=\"Microsoft.VisualStudio.VsPackage\"")]
    [InlineData("Path=\"Inferpal.pkgdef\"")]
    public void PackagedManifest_DeclaresTheInProcAssets(string fragment)
    {
        var manifest = File.ReadAllText(ManifestPath());
        Assert.True(manifest.Contains(fragment),
            $"source.extension.vsixmanifest no longer carries '{fragment}': that is the manifest " +
            "actually packaged, and without its two assets the in-proc half (ghost text, inline " +
            "edit, /tdd's debugger driver) loads nowhere — without the slightest message.");
    }

    [Fact]
    public void PackagedManifest_DeclaresTheHybridExtensionType()
    {
        // with "VisualStudio.Extensibility" alone, the VSIX installs under
        // Common7\IDE\VSExtensions\ (the out-of-proc root) and the <Assets> section is processed by
        // nobody — Inferpal stays at zero occurrences in the MEF catalogue. The two Microsoft
        // hybrids in VS 18 (Copilot Build Analyzer, Copilot testing) declare the value below and
        // live under Common7\IDE\Extensions\.
        Assert.Contains("ExtensionType=\"VSSDK+VisualStudio.Extensibility\"", File.ReadAllText(ManifestPath()));
    }

    [Fact]
    public void PackagedManifest_DerivesItsVersionFromTheBuild()
    {
        // The version had been copied by hand into the manifest and had drifted there: it announced
        // 1.0.1.0 while the product was at 1.5.2.
        Assert.Contains(VersionToken, File.ReadAllText(ManifestPath()));
    }

    [Fact]
    public void Project_BuildsAsAVssdkCompatibleExtension()
    {
        var csproj = File.ReadAllText(Path.Combine(RepoRoot(), "Inferpal", "Inferpal.csproj"));
        Assert.Contains("<VssdkCompatibleExtension>true</VssdkCompatibleExtension>", csproj);
        // The version token is resolved only if this target exists (the VSSDK provides it only to
        // classic VSIX projects).
        Assert.Contains("Name=\"GetVsixVersion\"", csproj);
    }

    [Fact]
    public void Extension_RequiresInProcessHostingAndNoMetadata()
    {
        // The two go together: the SDK refuses to compile VssdkCompatibleExtension without
        // RequiresInProcessHosting (VSEXT0007), and forbids Metadata when it is true.
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "Inferpal", "InferpalExtension.cs"));
        Assert.Contains("RequiresInProcessHosting = true", source);
        Assert.DoesNotContain("Metadata = new(", source);
    }

    // ── The in-process half's TFM ─────────────────────────────────────────────────
    // Verdict of 2026-08-23 (docs\probes\inproc-net8-verdict.md): VS 18's devenv.exe is a
    // .NETFramework 4.7.2 process. Its MEF discovery reflects over the assembly declared as an
    // asset; on an assembly linked against .NET 8 the reference closure is unresolvable on that
    // side, and EVERY type ends in a PartDiscoveryException — 19 errors, zero parts, no visible
    // message. Out of VS 18's 211 MEF components, Inferpal.dll was the only assembly actually
    // linked against .NET 8; Microsoft's ".NET Core components" reference mscorlib or
    // netstandard2.0, so the Framework CLR loads them.
    //
    // These three tests are the guard for that verdict: the day someone "simplifies" by repointing
    // the asset or the pkgdef at Inferpal.dll, the in-proc half dies again in silence.

    [Fact]
    public void PackagedManifest_DoesNotPointTheMefAssetAtTheNet8Assembly()
    {
        var manifest = File.ReadAllText(ManifestPath());
        Assert.DoesNotContain("Path=\"Inferpal.dll\"", manifest);
    }

    [Fact]
    public void Pkgdef_RegistersTheInProcAssembly()
    {
        var pkgdef = File.ReadAllText(Path.Combine(RepoRoot(), "Inferpal", "Inferpal.pkgdef"));

        // The in-process package's CodeBase designates the net472 assembly.
        // ⚠ The VisualStudio\Extensibility\Extensions entry, by contrast, does designate
        // Inferpal.dll: that is the OUT-of-process host, the one VS launches alongside — it is not
        // concerned, hence the split: we look only at what follows the package declaration.
        var inProcSection = pkgdef.Substring(pkgdef.IndexOf("[$RootKey$\\Packages\\{", StringComparison.Ordinal));

        Assert.DoesNotContain("$PackageFolder$\\Inferpal.dll", inProcSection);
        Assert.Contains("\"CodeBase\"=\"$PackageFolder$\\Inferpal.InProc.dll\"", inProcSection);

        // ⚠ And the dead key does not come back. [$RootKey$\MEFComponent] lived there from 23 to
        // 25/08 2026: measured to have no effect (VS 18 does not fill its MEF catalogue from the
        // registry), it wrote a default value on a root VS owns. Now, importing pkgdef files is
        // GLOBAL and stops at the first file it cannot process — measured on 23/08, the import
        // aborted at the 2nd file out of 465 (E_ACCESSDENIED), and Inferpal.pkgdef was never
        // reached. A null effect is not worth that risk for the other 464.
        // ⚠ On the DIRECTIVES only: the pkgdef names the removed key in a comment, so that nobody
        // rewrites it — exactly as it names the autoload typo. A DoesNotContain read over the whole
        // file would fail on the explanation, not on the mistake (verified while writing this test:
        // that is what it did).
        Assert.DoesNotContain(
            File.ReadAllLines(Path.Combine(RepoRoot(), "Inferpal", "Inferpal.pkgdef"))
                .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)),
            line => line.Contains("[$RootKey$\\MEFComponent]", StringComparison.Ordinal));
    }

    [WindowsBuildOutputFact]
    public void InProcAssembly_IsLoadableByADotNetFrameworkHost()
    {
        var dll = InProcAssemblyPath();
        Assert.True(File.Exists(dll),
            $"The in-process assembly is missing ({dll}). That is the one devenv loads: " +
            "without it the VSIX installs and the chat works, but ghost text, the inline diff " +
            "preview and /tdd's debugger driver no longer exist.");

        using var stream = File.OpenRead(dll);
        using var pe     = new PEReader(stream);
        var reader       = pe.GetMetadataReader();

        var references = reader.AssemblyReferences
            .Select(handle => reader.GetAssemblyReference(handle))
            .Select(reference => (Name: reader.GetString(reference.Name), reference.Version))
            .ToList();

        // The criterion is the one that was measured, not a version number: the Framework CLR
        // resolves mscorlib, not System.Runtime 8.0.0.0. That reference, and it alone, is what got
        // each of our types rejected. (System.Text.Json 9.x or the VS SDK 17.x are, for their part,
        // net472 assemblies that devenv provides — see devenv.exe.config's bindingRedirects.)
        var coreOnly = references
            .Where(reference => reference.Name is "System.Runtime" or "System.Private.CoreLib"
                                && reference.Version.Major >= 5)
            .Select(reference => $"{reference.Name} {reference.Version}")
            .ToList();

        Assert.True(coreOnly.Count == 0,
            "Inferpal.InProc.dll is linked against the modern .NET BCL: " + string.Join(", ", coreOnly) +
            ". devenv's MEF discovery runs on .NET Framework and cannot resolve that closure — it " +
            "would reject every one of our types, in silence (see " +
            "docs\\probes\\inproc-net8-verdict.md).");

        Assert.True(references.Any(reference => reference.Name == "mscorlib"),
            "The PruneFlattenedCoreSatelliteFromVsix target is gone: the duplicate satellite comes "
            + "back to the root of the VSIX and all ten languages fall back to English, silently.");
    }

    // ── What VS reads to decide where to load what ────────────────────────────────
    // Two files, two halves, and seven false verdicts came out of confusing them.

    [Fact]
    public void Pkgdef_UsesTheRegistryValueNameVsActuallyReads()
    {
        // The pkgdef wrote "AllowsBackgroundLoading" — that is the name of
        // PackageRegistrationAttribute's C# PROPERTY, not that of the registry value it writes. Out
        // of VS 18's 465 pkgdef files, 59 write "AllowsBackgroundLoad" and the ONLY file in the
        // whole tree writing the other one was ours. The consequence, readable in the ActivityLog:
        // "Autoload request for package {6a7b2c3d-…} is ignored because package does not support
        // background loading" — the two AutoLoadPackages below were ignored, GhostTextPackage was
        // never loaded, and nothing else said so.
        // This pkgdef is written by hand (GeneratePkgDefFile=false, CreatePkgDef.exe cannot read
        // .NET 8): no generator will catch the typo for us.
        // Comments are set aside: the pkgdef NAMES the mistake so that nobody makes it again, which
        // would fail a DoesNotContain read over the whole file.
        var directives = File.ReadAllLines(Path.Combine(RepoRoot(), "Inferpal", "Inferpal.pkgdef"))
            .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal))
            .ToList();

        Assert.Contains(directives, line => line.Contains("\"AllowsBackgroundLoad\"=dword:00000001"));
        Assert.DoesNotContain(directives, line => line.Contains("AllowsBackgroundLoading"));
    }

    [WindowsBuildOutputFact]
    public void PackagedExtensionJson_HostsEveryPartOutOfProcess()
    {
        // The other half of 2026-08-24. RequiresInProcessHosting=true (imposed by
        // VssdkCompatibleExtension, VSEXT0007) makes it write "allowHostingInProcess": true for
        // every service — that is, for Inferpal.dll, which is net8. VS takes it at its word and
        // tries to activate it INSIDE devenv: "FileNotFoundException: System.Runtime,
        // Version=8.0.0.0" from InferpalExtension..cctor(), and every part dies.
        // The ForceOutOfProcessHostingInExtensionJson target (Inferpal.csproj) sets the field back
        // to false; this test reads the produced artifact, not the target, because the artifact is
        // what VS reads. What devenv must load is Inferpal.InProc.dll (net472), through the
        // manifest's <Assets> — the other half, checked above.
        var json = File.ReadAllText(GeneratedExtensionJsonPath());

        Assert.Contains("allowHostingInProcess", json);   // the field still exists: otherwise there is nothing to hold
        Assert.DoesNotContain("\"allowHostingInProcess\": true", json);
    }

    /// <summary>The extension.json produced by the build, the one that goes into the VSIX.</summary>
    private static string GeneratedExtensionJsonPath()
    {
        var config = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
        var path   = Path.Combine(RepoRoot(), "Inferpal", "bin", config, "net8.0-windows",
                                  ".vsextension", "extension.json");
        Assert.True(File.Exists(path),
            $"extension.json introuvable ({path}) — construire Inferpal\\Inferpal.csproj d'abord.");
        return path;
    }

    /// <summary>The in-process project's net472 output, in the current configuration.</summary>
    private static string InProcAssemblyPath()
    {
        // .../Inferpal.Tests/bin/<Config>/net8.0-windows/ → on reprend <Config>.
        var config = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
        return Path.Combine(RepoRoot(), "Inferpal.InProc", "bin", config, "net472", "Inferpal.InProc.dll");
    }

    private static string ManifestPath() =>
        Path.Combine(RepoRoot(), "Inferpal", "source.extension.vsixmanifest");

    /// <summary>Repo root = first ancestor of the test bin containing README.md.</summary>
    /// <summary>
    /// The licence shipped in the VSIX must carry the GPL v3 text <b>in full</b>.
    ///
    /// GPL v3 §4 requires shipping a copy of the licence with the program: a link to gnu.org is not
    /// one. Until 2026-08-31 the three files were a 33-line notice pointing at the text by URL —
    /// hence a distribution that did not satisfy the licence it claims, and a GitHub repository on
    /// which no licence was detected.
    ///
    /// The rule lives here rather than in a csproj comment because it carries <b>three</b> files
    /// that must move together, and a rule written in a comment is a rule that drifts:
    /// <c>LICENSE</c> stays verbatim (that is what GitHub can recognise), <c>NOTICE</c> carries the
    /// copyright and the §7 additional terms, and the embedded <c>LICENSE.txt</c> carries both — a
    /// VSIX exposes a single licence file at install time.
    /// </summary>
    [Fact]
    public void TheShippedLicence_CarriesTheFullGplText_NotJustANotice()
    {
        var root = RepoRoot();
        var licence = File.ReadAllText(Path.Combine(root, "LICENSE"));
        var notice = File.ReadAllText(Path.Combine(root, "NOTICE"));
        var embedded = File.ReadAllText(Path.Combine(root, "Inferpal", "LICENSE.txt"));
        // ⚠ The VS Code VSIX embeds one TOO, and this rule did not look at it: it carried only the
        // notice and a link to gnu.org — published that way on three OSes since 1.5.0. "A VSIX
        // exposes a single licence file" holds for BOTH packages.
        var embeddedCode = File.ReadAllText(Path.Combine(root, "vscode", "LICENSE.txt"));

        // Three markers from the body of the licence, absent from the notice: if they are there, it
        // is the text and not a summary. A line count alone would prove nothing.
        foreach (var marker in new[] { "TERMS AND CONDITIONS", "0. Definitions.", "17. Interpretation of Sections 15 and 16." })
        {
            Assert.Contains(marker, licence);
            Assert.Contains(marker, embedded);
            Assert.Contains(marker, embeddedCode);
        }

        // LICENSE must stay VERBATIM: adding the project's copyright or the §7 terms to it would
        // lose the "GPL-3.0" detection on GitHub, which compares against the reference text.
        Assert.DoesNotContain("ADDITIONAL TERMS", licence);
        Assert.StartsWith("                    GNU GENERAL PUBLIC LICENSE", licence);

        // The embedded file is the ONLY one the installing user sees: it must carry the additional
        // terms AND the licence, in that order.
        Assert.Contains("ADDITIONAL TERMS", embedded);
        Assert.Contains("ADDITIONAL TERMS", embeddedCode);
        Assert.Contains("ADDITIONAL TERMS", notice);
        Assert.True(
            embedded.IndexOf("ADDITIONAL TERMS", StringComparison.Ordinal)
                < embedded.IndexOf("TERMS AND CONDITIONS", StringComparison.Ordinal),
            "The additional terms must come before the GPL text in the embedded LICENSE.txt.");
        Assert.True(
            embeddedCode.IndexOf("ADDITIONAL TERMS", StringComparison.Ordinal)
                < embeddedCode.IndexOf("TERMS AND CONDITIONS", StringComparison.Ordinal),
            "Same order in the LICENSE.txt embedded by the VS Code package.");

        // ⚠ And both Marketplace listings: the one that used to live here (EstaxNet.Inferpal)
        // was deleted, so every package published since embeds a licence whose attribution points
        // at a 404 page. Measured: 404 for the old one, 200 for the two current ones.
        foreach (var text in new[] { notice, embedded, embeddedCode })
        {
            Assert.Contains("itemName=EstaxNet.inferpal-vs", text, StringComparison.Ordinal);
            Assert.Contains("itemName=EstaxNet.inferpal-vscode", text, StringComparison.Ordinal);
            // ORDINAL comparison: only the case distinguishes the dead listing (".Inferpal") from
            // deux vivantes (« .inferpal-vs », « .inferpal-vscode »).
            Assert.DoesNotContain("itemName=EstaxNet.Inferpal\"", text, StringComparison.Ordinal);
        }
    }

    // ── The two lines the product being TRANSLATED depends on ─────────────────────
    //
    // The same property as the whole of this file — when they break, nothing says so — but the
    // symptom is not the in-proc half: it is the entire interface falling back to English, in all
    // nine languages at once, on a VSIX that builds and installs without a warning. Both have
    // already been paid for (the "broken UI localization" trap in CLAUDE.md), and neither was held
    // by a test.

    [Fact]
    public void TheVsix_StillCarriesTheSatelliteAssemblies()
    {
        // Without that switch, dependencies resolved by NuGet are not copied next to the output -
        // and the nine Inferpal.Core.resources.dll are among them. The VSIX builds perfectly well
        // without them; it simply contains no translation at all, and the ResourceManager falls
        // back to English for everyone without throwing anything.
        var csproj = File.ReadAllText(Path.Combine(RepoRoot(), "Inferpal", "Inferpal.csproj"));
        Assert.Contains("<CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>", csproj);

        // Witness: the satellites really are produced by the build. Without it, the rule above
        // would be guarding a switch that no longer has an object, and its green would mean
        // nothing - it would be reduced to "this text is in this file".
        var produced = new[] { "fr", "de", "es", "it", "ru", "ja", "ko", "pl", "zh-CN" }
            .Where(l => File.Exists(Path.Combine(AppContext.BaseDirectory, l, "Inferpal.Core.resources.dll")))
            .ToList();
        Assert.True(produced.Count == 9,
            $"Only {produced.Count} satellite(s) built out of 9 ({string.Join(", ", produced)}): "
            + "the packaging switch is no longer the thing to look at, resource generation itself "
            + "is.");
    }

    [Fact]
    public void TheFlattenedSatellitePrune_RunsLateEnoughToSeeTheDuplicate()
    {
        // VSSDK packaging adds a DUPLICATE of the `de` satellite, flattened at the root of the
        // VSIX (/Inferpal.Core.resources.dll, Culture=de, no metadata entry in files.json). The
        // Extensibility host's ALC resolves that one FIRST, the culture does not match, the bind
        // fails and EVERY language falls back to English. The symptom reads as "localization is
        // broken", never as "there is one file too many".
        //
        // And the hook is the half that matters: the duplicate only appears AFTER
        // GetVsixSourceItems, so a target wired one hook earlier sees nothing, removes nothing,
        // and passes.
        var csproj = File.ReadAllText(Path.Combine(RepoRoot(), "Inferpal", "Inferpal.csproj"));

        var target = System.Text.RegularExpressions.Regex.Match(
            csproj, @"<Target\s+Name=""PruneFlattenedCoreSatelliteFromVsix""(?<attrs>[^>]*)>");
        Assert.True(target.Success,
            "The PruneFlattenedCoreSatelliteFromVsix target is gone: the duplicate satellite comes "
            + "back to the root of the VSIX and all ten languages fall back to English, silently.");

        foreach (var hook in new[] { "GenerateTemplatesManifest", "GenerateFileManifest" })
            Assert.True(target.Groups["attrs"].Value.Contains(hook, StringComparison.Ordinal),
                $"PruneFlattenedCoreSatelliteFromVsix no longer hooks onto {hook}. The duplicate "
                + "only exists after GetVsixSourceItems: wired earlier, the target removes nothing "
                + "and its success proves nothing.");
    }

    [Fact]
    public void VsCodeChangelog_HasASectionForTheVersionBeingShipped()
    {
        // ⚠ The VS Code Marketplace page IS the package: its "Changelog" tab renders the
        // EMBEDDED vscode/CHANGELOG.md, and it cannot be edited on the web — fixing it after the
        // fact is a PUBLICATION, so a burnt version number. The same is already true of the
        // embedded README.
        //
        // Measured 2026-09-10: 1.6.10 shipped with a changelog that stops at 1.6.9. Nothing could
        // say so — the publisher checks that the VSIX CONTAINS the file, not that it mentions the
        // version being published: the tool did not fail, so the artifact looked finished.
        //
        // This test is red exactly between the version bump and the writing of the notes, and the
        // tag's CI replays it: that is where the question belongs.
        var props   = File.ReadAllText(Path.Combine(RepoRoot(), "Directory.Build.props"));
        var version = System.Text.RegularExpressions.Regex.Match(props, @"<Version>([^<]+)</Version>");
        Assert.True(version.Success, "No <Version> in Directory.Build.props.");

        var path      = Path.Combine(RepoRoot(), "vscode", "CHANGELOG.md");
        var changelog = File.ReadAllText(path);

        // Witness: the file must carry version sections at all, otherwise "ours is missing" means
        // nothing — the failure mode of a scan that reads nothing.
        var sections = System.Text.RegularExpressions.Regex.Matches(changelog, @"(?m)^##\s+\d+\.\d+");
        Assert.True(sections.Count >= 5,
            $"Only {sections.Count} version section(s) in vscode/CHANGELOG.md: the format changed "
            + "and this test no longer measures anything.");

        Assert.True(
            System.Text.RegularExpressions.Regex.IsMatch(
                changelog, @"(?m)^##\s+" + System.Text.RegularExpressions.Regex.Escape(version.Groups[1].Value) + @"\s*$"),
            $"vscode/CHANGELOG.md has no \"## {version.Groups[1].Value}\" section. That file is "
            + "embedded in all three VSIXes and IS the Marketplace's Changelog tab: publishing "
            + "without it ships a page that stops at the previous version, and that can only be "
            + "corrected by burning a version number.");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
