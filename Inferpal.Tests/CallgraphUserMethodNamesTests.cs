using System.IO;
using System.Text.Json;
using Inferpal.Localization;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>analyze_code mode=callgraph</c> sees the user's own methods named like a keyword or a framework member — Add, Get,
/// Set, Remove, Value, Select, Count, Max, Match — and still leaves the framework's calls out of the tree.
/// </summary>
/// <remarks>
/// One list held C# keywords and framework names, compared without case, and was applied to declarations as well as to
/// calls: <c>get</c>, <c>add</c>, <c>remove</c>, <c>value</c> also hid <c>Get</c>, <c>Add</c>, <c>Remove</c>,
/// <c>Value</c>, and the framework entries hid a user's <c>Count</c> or <c>Max</c>. Asked about <c>Add</c>, the tool
/// answered "Symbol 'Add' not found"; a method whose calls were <c>Add</c> and <c>Count</c> had "no outgoing calls".
/// </remarks>
public sealed class CallgraphUserMethodNamesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "inferpal-callnames-" + Guid.NewGuid().ToString("N"));
    private readonly string _cart;

    public CallgraphUserMethodNamesTests()
    {
        Directory.CreateDirectory(_root);
        _cart = Path.Combine(_root, "Cart.cs");
        File.WriteAllText(_cart, string.Join('\n',
            "using System.Collections.Generic;",
            "using System.Linq;",
            "class Cart",
            "{",
            "    private readonly List<int> _items = new List<int>();",
            "    public void Add(int x) { _items.Add(x); Audit(); }",
            "    public int Count() { return _items.Count; }",
            "    public void Checkout() { Add(1); var n = Count(); Audit(); }",
            "    private void Audit() { var copy = _items.ToList(); }",
            "}"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private Task<string> Analyze(object args) =>
        new TraceDependencyTool(() => _root).ExecuteAsync(JsonSerializer.SerializeToElement(args), CancellationToken.None);

    [Fact]
    public async Task AMethodNamedAdd_IsFound()
    {
        var report = await Analyze(new { path = _cart, symbol = "Add", direction = "callees" });

        Assert.DoesNotContain(Strings.TraceDepsSymbolNotFound("Add", "Cart.cs"), report);
        Assert.Contains("Audit()", report, StringComparison.Ordinal);   // Add's own call, resolved in this file
    }

    [Fact]
    public async Task CallsToTheUsersAddAndCount_AreInTheTree_TheFrameworksToListIsNot()
    {
        var report = await Analyze(new { path = _cart, symbol = "Checkout", direction = "callees" });

        Assert.Contains("Add()  [this file:", report, StringComparison.Ordinal);
        Assert.Contains("Count()  [this file:", report, StringComparison.Ordinal);
        // Reference arm: a framework call the workspace does not define stays out of the tree.
        var audit = await Analyze(new { path = _cart, symbol = "Audit", direction = "callees" });
        Assert.DoesNotContain("ToList()", audit, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCallersOfTheUsersAdd_AreFound()
    {
        var other = Path.Combine(_root, "Shop.cs");
        File.WriteAllText(other, "class Shop\n{\n    public void Buy(Cart c) { c.Add(2); }\n}\n");

        var report = await Analyze(new { path = _cart, symbol = "Add", direction = "callers" });

        Assert.Contains("Buy", report, StringComparison.Ordinal);
    }
}
