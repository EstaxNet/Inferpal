using System.IO;
using System.Text.Json;
using Inferpal.Services.Agent;
using Inferpal.Services.CodeActions;
using Inferpal.Services.Execution;
using Inferpal.Services.Prompting;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  A rename goes through rename_symbol, and a hand rename is told so.
//
//  On the real-condition battery (510 rename runs, every model): applied by rename_symbol a rename
//  succeeds 95 % of the time, made by hand 62 % — an old_content that no longer matches refuses the
//  whole batch, or an occurrence is left behind under "renamed everywhere". rename_symbol now applies
//  in one call (the approval shows the diff), the plan names it, and the edit tools point at it.
// ──────────────────────────────────────────────────────────────────────────────────────────────
[Collection(CultureSerialCollection.Name)]
public sealed class RenameThroughTheRightToolTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rename-right-tool-" + Guid.NewGuid().ToString("N"));

    public RenameThroughTheRightToolTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string Write(string rel, string text)
    {
        var full = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
        return full;
    }

    private sealed class Approve : IApprovalService
    {
        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct,
            string? subject = null, DiffInfo? diff = null, bool forcePrompt = false) => Task.FromResult(true);
    }

    private static JsonElement Json(object o) => JsonSerializer.SerializeToElement(o);

    // ── The detector ─────────────────────────────────────────────────────────

    [Fact]
    public void EditsThatOnlySwapOneIdentifier_AreARename()
    {
        Assert.Equal(("ComputeTotal", "CalculateTotal"), RenameIntent.Of([
            ("public decimal ComputeTotal() => _prices.Sum();", "public decimal CalculateTotal() => _prices.Sum();"),
            ("$\"Total: {cart.ComputeTotal():0.00}\"", "$\"Total: {cart.CalculateTotal():0.00}\""),
        ]));
        Assert.Equal(("compute_total", "calculate_total"),
                     RenameIntent.Of([("return cart.compute_total()", "return cart.calculate_total()")]));
    }

    [Fact]
    public void EditsThatChangeMoreThanAName_AreNot()
    {
        // Reference arms: real code changes, two different renames, nothing changed at all.
        Assert.Null(RenameIntent.Of([("price * percent / 10m", "price * percent / 100m")]));
        Assert.Null(RenameIntent.Of([("a.Foo()", "a.Bar(1)")]));
        Assert.Null(RenameIntent.Of([("Foo()", "Bar()"), ("Baz()", "Qux()")]));
        Assert.Null(RenameIntent.Of([("same", "same")]));
    }

    // ── apply_edits ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ARefusedHandRename_NamesRenameSymbol_WithItsArguments()
    {
        var cart = Write("src/Cart.cs", "class Cart { public decimal ComputeTotal() => 0; }\n");
        var receipt = Write("src/Receipt.cs", "class Receipt { string F(Cart c) => $\"{c.ComputeTotal():0.00}\"; }\n");
        var tool = new ApplyEditsTool(new Approve(), new FileHistoryService(), () => _root);

        var reply = await tool.ExecuteAsync(Json(new
        {
            edits = new object[]
            {
                new { path = cart, old_content = "public decimal ComputeTotal() => 0;", new_content = "public decimal CalculateTotal() => 0;" },
                // The model's copy of the line does not match the file (escaping): the batch is refused.
                new { path = receipt, old_content = "{c.ComputeTotal()}", new_content = "{c.CalculateTotal()}" },
            },
        }), CancellationToken.None);

        Assert.Contains("no exact or fuzzy match for old_content", reply);                  // WITNESS: refused
        Assert.Contains("rename_symbol(old_name: \"ComputeTotal\", new_name: \"CalculateTotal\")", reply);
    }

    [Fact]
    public async Task ARefusedEditThatIsNotARename_SaysNothingAboutRenaming()
    {
        var file = Write("src/Pricing.cs", "class P { decimal D(decimal p) => p / 10m; }\n");
        var tool = new ApplyEditsTool(new Approve(), new FileHistoryService(), () => _root);

        var reply = await tool.ExecuteAsync(Json(new
        {
            edits = new object[] { new { path = file, old_content = "p / 1m", new_content = "p / 100m" } },
        }), CancellationToken.None);

        Assert.Contains("no exact or fuzzy match for old_content", reply);
        Assert.DoesNotContain("rename_symbol", reply);
    }

    [Fact]
    public async Task AHandRenameThatLeavesTheOldNameElsewhere_SaysWhere()
    {
        var cart = Write("src/Cart.cs", "class Cart { public decimal ComputeTotal() => 0; }\n");
        Write("tests/CartTests.cs", "class T { void A() => new Cart().ComputeTotal(); }\n");
        var tool = new ApplyEditsTool(new Approve(), new FileHistoryService(), () => _root);

        var reply = await tool.ExecuteAsync(Json(new
        {
            edits = new object[] { new { path = cart, old_content = "ComputeTotal()", new_content = "CalculateTotal()" } },
        }), CancellationToken.None);

        Assert.Contains("CalculateTotal", File.ReadAllText(cart));                         // WITNESS: written
        Assert.Contains("`ComputeTotal` still appears in 1 file(s)", reply);
        Assert.Contains("CartTests.cs", reply);
    }

    [Fact]
    public async Task AHandRenameThatIsComplete_AddsNoNote()
    {
        var cart = Write("src/Cart.cs", "class Cart { public decimal ComputeTotal() => 0; }\n");
        var tool = new ApplyEditsTool(new Approve(), new FileHistoryService(), () => _root);

        var reply = await tool.ExecuteAsync(Json(new
        {
            edits = new object[] { new { path = cart, old_content = "ComputeTotal()", new_content = "CalculateTotal()" } },
        }), CancellationToken.None);

        Assert.Contains("CalculateTotal", File.ReadAllText(cart));
        Assert.DoesNotContain("still appears", reply);
    }

    // ── apply_diff ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ApplyDiff_SaysTheSameTwoThings()
    {
        var cart = Write("src/Cart.cs", "class Cart { public decimal ComputeTotal() => 0; }\n");
        Write("src/Receipt.cs", "class R { decimal F(Cart c) => c.ComputeTotal(); }\n");
        var tool = new ApplyDiffTool(new Approve(), new FileHistoryService(), () => _root);

        var refused = await tool.ExecuteAsync(Json(new
        {
            path = cart, old_content = "decimal ComputeTotal(int x)", new_content = "decimal CalculateTotal(int x)",
        }), CancellationToken.None);
        var applied = await tool.ExecuteAsync(Json(new
        {
            path = cart, old_content = "decimal ComputeTotal()", new_content = "decimal CalculateTotal()",
        }), CancellationToken.None);

        Assert.Contains("rename_symbol(old_name: \"ComputeTotal\", new_name: \"CalculateTotal\")", refused);
        Assert.Contains("`ComputeTotal` still appears in 1 file(s): src", applied);
    }

    // ── rename_symbol applies in one call ────────────────────────────────────

    [Fact]
    public async Task RenameSymbol_WithoutDryRun_AppliesTheRename()
    {
        var cart = Write("src/Cart.cs", "namespace S; public class Cart { public decimal ComputeTotal() => 0; }\n");
        var use = Write("src/Use.cs", "namespace S; public class Use { decimal F(Cart c) => c.ComputeTotal(); }\n");
        var tool = new RenameSymbolTool(new Approve(), new FileHistoryService(), () => _root);

        await tool.ExecuteAsync(Json(new { old_name = "ComputeTotal", new_name = "CalculateTotal" }), CancellationToken.None);

        Assert.Contains("CalculateTotal", File.ReadAllText(cart));
        Assert.Contains("CalculateTotal", File.ReadAllText(use));
        Assert.Contains("rename", tool.Description[..60], StringComparison.OrdinalIgnoreCase);   // said first: what it is for
    }

    // ── A root narrowed to a folder ──────────────────────────────────────────

    [Fact]
    public async Task ACSharpRenameNarrowedToAFolder_WithCallersOutsideIt_IsRefused()
    {
        // The battery's case: the model passed root = the folder of the interface, the callers lived elsewhere.
        var iface = Write("src/Rules/IRule.cs", "namespace S; public interface IRule { decimal Apply(decimal a); }\n");
        Write("src/Rules/Half.cs", "namespace S; public sealed class Half : IRule { public decimal Apply(decimal a) => a / 2; }\n");
        var caller = Write("src/Order.cs", "namespace S; public sealed class Order(IRule r) { public decimal Pay(decimal a) => r.Apply(a); }\n");
        var tool = new RenameSymbolTool(new Approve(), new FileHistoryService(), () => _root);

        var reply = await tool.ExecuteAsync(Json(new
        {
            root = Path.Combine(_root, "src", "Rules"), old_name = "Apply", new_name = "ApplyTo", declaring_file = "IRule.cs",
        }), CancellationToken.None);

        Assert.Contains("is also used outside", reply);
        Assert.Contains("Order.cs", reply);
        Assert.Contains("decimal Apply(", File.ReadAllText(iface));     // nothing was written
        Assert.Contains("r.Apply(a)", File.ReadAllText(caller));
    }

    [Fact]
    public async Task ACSharpRenameNarrowedToAFolder_ThatHoldsEveryReference_IsApplied()
    {
        // Reference arm: narrowing is legitimate when the symbol lives entirely inside the folder.
        var iface = Write("src/Rules/IRule.cs", "namespace S; public interface IRule { decimal Apply(decimal a); }\n");
        Write("src/Other.cs", "namespace S; public sealed class Coupon { public bool Apply(string c) => c.Length > 0; }\n");
        var tool = new RenameSymbolTool(new Approve(), new FileHistoryService(), () => _root);

        var reply = await tool.ExecuteAsync(Json(new
        {
            root = Path.Combine(_root, "src", "Rules"), old_name = "Apply", new_name = "ApplyTo", declaring_file = "IRule.cs",
        }), CancellationToken.None);

        Assert.DoesNotContain("is also used outside", reply);
        Assert.Contains("decimal ApplyTo(", File.ReadAllText(iface));
    }

    [Fact]
    public async Task ATextRenameNarrowedToAFolder_SaysWhereTheOldNameIsLeft()
    {
        var cart = Write("shop/cart.py", "def compute_total(prices):\n    return sum(prices)\n");
        Write("app/report.py", "from shop.cart import compute_total\nprint(compute_total([1]))\n");
        var tool = new RenameSymbolTool(new Approve(), new FileHistoryService(), () => _root);

        var reply = await tool.ExecuteAsync(Json(new
        {
            root = Path.Combine(_root, "shop"), old_name = "compute_total", new_name = "calculate_total",
        }), CancellationToken.None);

        Assert.Contains("def calculate_total(", File.ReadAllText(cart));   // WITNESS: applied inside the folder
        Assert.Contains("`compute_total` still appears in 1 file(s)", reply);
        Assert.Contains("report.py", reply);
    }

    // ── The plan ─────────────────────────────────────────────────────────────

    [Fact]
    public void ThePlan_NamesRenameSymbol_OnlyWhenItIsOffered()
    {
        Assert.EndsWith(ModelPrompts.AgentPlanRename, AgentOrchestrator.PlanPrompt(new Names("read_file", "rename_symbol")));
        Assert.DoesNotContain(ModelPrompts.AgentPlanRename, AgentOrchestrator.PlanPrompt(new Names("read_file")));
    }

    private sealed class Names(params string[] names) : IToolRegistry
    {
        public IReadOnlyList<Inferpal.Models.ToolDefinition> Definitions { get; } =
            names.Select(n => new Inferpal.Models.ToolDefinition("function", new Inferpal.Models.ToolFunction(n, "", new { }))).ToList();
        public DiffInfo? ConsumeDiff() => null;
        public Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct) => Task.FromResult("");
    }
}
