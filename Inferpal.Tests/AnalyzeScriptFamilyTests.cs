using System.IO;
using System.Text.Json;
using Inferpal.Services;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  analyze_code sees a script file's consumers whatever their extension.
//
//  Both analysis tools scanned the target's exact extension: a .tsx component had its importers
//  searched in *.ts only — "0 dependants · Risk: LOW · safe to refactor freely" on a component used
//  by every page — and a .ts hook was never seen used by the .tsx components importing it. The scan
//  coverage counted only what it read, so it announced a complete scan.
// ──────────────────────────────────────────────────────────────────────────────────────────────
[Collection(CultureSerialCollection.Name)]
public sealed class AnalyzeScriptFamilyTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("inferpal-scriptfamily-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private string Write(string rel, string src)
    {
        var full = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, src);
        return full;
    }

    private static JsonElement Args(object o) => JsonSerializer.SerializeToElement(o);

    [Fact]
    public async Task AReactComponent_IsSeenImportedByThePages()
    {
        var button = Write("src/components/Button.tsx", "export function Button() { return <button/>; }\n");
        Write("src/pages/Home.tsx", "import { Button } from '../components/Button';\nexport const Home = () => <Button/>;\n");

        var report = await new AnalyzeImpactTool(() => _root).ExecuteAsync(Args(new { path = button }), CancellationToken.None);

        Assert.Contains("Home.tsx", report);
        Assert.DoesNotContain("safe to refactor freely", report);
    }

    [Fact]
    public async Task ATsHook_IsSeenUsedByATsxComponent()
    {
        var hook = Write("src/useCart.ts", "export function useCart() { return 1; }\n");
        Write("src/Cart.tsx", "import { useCart } from './useCart';\nexport const Cart = () => <p>{useCart()}</p>;\n");

        var report = await new AnalyzeImpactTool(() => _root).ExecuteAsync(Args(new { path = hook }), CancellationToken.None);

        Assert.Contains("Cart.tsx", report);
    }

    [Fact]
    public void TheFamilies_AreTheLanguages()
    {
        Assert.Contains("*.tsx", WorkspaceScan.SourcePatterns(".ts"));
        Assert.Contains("*.ts", WorkspaceScan.SourcePatterns(".jsx"));
        // Reference arm: a language without siblings is scanned by its own extension.
        Assert.Equal(["*.py"], WorkspaceScan.SourcePatterns(".py"));
        Assert.Equal(["*.cs"], WorkspaceScan.SourcePatterns(".cs"));
    }
}
