using System.IO;
using System.Text.Json;
using Inferpal.Localization;
using Inferpal.Services.Execution;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  A rename reaches the markup that binds the symbol, and only there — and says what it left.
//
//  The C# compiler reads no .razor, .cshtml or .xaml. Matched by the word over a whole .razor, a rename rewrites another
//  component's property of the same name and misses the route that binds the parameter; left unread, XAML bindings and
//  Razor views keep the old name under "applied". Markup is rewritten where it binds the symbol renamed — a {Binding}
//  under an x:DataType that has it, Model.X and asp-for under an @model that has it, the component's own code and
//  route, its attributes on <X …> elsewhere — and every other occurrence is LISTED, with why.
// ──────────────────────────────────────────────────────────────────────────────────────────────
[Collection(CultureSerialCollection.Name)]
public sealed class MarkupRenameTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ip-markup-" + Guid.NewGuid().ToString("N")[..8]);

    public MarkupRenameTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void Write(string rel, string text)
    {
        var full = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    private string Read(string rel) => File.ReadAllText(Path.Combine(_root, rel));

    private sealed class Spy : IApprovalService
    {
        public readonly List<string> Cards = [];

        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct, string? subject = null,
                                               Services.CodeActions.DiffInfo? diff = null, bool forcePrompt = false)
        {
            Cards.Add(details);
            return Task.FromResult(true);
        }
    }

    private async Task<(string Report, Spy Approval)> Rename(string oldName, string newName, string? declaringFile = null,
                                                            int? declaringLine = null)
    {
        var approval = new Spy();
        var tool = new RenameSymbolTool(approval, new FileHistoryService(), () => _root);
        var args = new Dictionary<string, object?> { ["old_name"] = oldName, ["new_name"] = newName, ["dry_run"] = false };
        if (declaringFile is not null) args["declaring_file"] = declaringFile;
        if (declaringLine is not null) args["declaring_line"] = declaringLine;
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(args));
        return (await tool.ExecuteAsync(doc.RootElement, CancellationToken.None), approval);
    }

    // ── XAML ───────────────────────────────────────────────────────────────────

    private void XamlProject()
    {
        Write("Vm/ViewModelBase.cs", """
            namespace App.Vm;
            public class ViewModelBase { public bool IsBusy { get; set; } }
            """);
        Write("Vm/SettingsViewModel.cs", """
            namespace App.Vm;
            public class SettingsViewModel : ViewModelBase
            {
                public bool UseFake { get; set; }
                public Order Order { get; } = new();
                public void Toggle() => UseFake = !UseFake;
            }
            """);
        Write("Vm/Order.cs", """
            namespace App.Vm;
            public class Order { public bool UseFake { get; set; } }
            """);
        Write("Vm/OtherViewModel.cs", """
            namespace App.Vm;
            public class OtherViewModel { public bool UseFake { get; set; } }
            """);
        Write("Views/SettingsView.xaml", """
            <ContentPage xmlns="http://schemas.microsoft.com/dotnet/2021/maui"
                         xmlns:x="http://schemas.microsoft.com/winfx/2009/xaml"
                         xmlns:vm="clr-namespace:App.Vm"
                         x:Class="App.Views.SettingsView"
                         x:DataType="vm:SettingsViewModel">
                <StackLayout>
                    <Switch IsToggled="{Binding UseFake, Mode=TwoWay}" />
                    <Label IsVisible="{Binding UseFake}" Text="UseFake" />
                    <Label IsVisible="{Binding IsBusy}" />
                    <!-- <Label IsVisible="{Binding UseFake}" /> -->
                    <Label IsVisible="{Binding Order.UseFake}" />
                    <Label IsVisible="{Binding Source={x:Reference other}, Path=UseFake}" />
                    <CollectionView>
                        <CollectionView.ItemTemplate>
                            <DataTemplate x:DataType="vm:OtherViewModel">
                                <Label IsVisible="{Binding UseFake}" />
                            </DataTemplate>
                        </CollectionView.ItemTemplate>
                    </CollectionView>
                </StackLayout>
            </ContentPage>
            """);
        Write("Views/LooseView.xaml", """
            <Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                <CheckBox IsChecked="{Binding UseFake}" />
            </Window>
            """);
    }

    [Fact]
    public async Task AXamlBinding_UnderAnXDataTypeThatHasTheMember_FollowsTheRename()
    {
        XamlProject();
        var (report, approval) = await Rename("UseFake", "UseSimulated", "Vm/SettingsViewModel.cs");

        var view = Read("Views/SettingsView.xaml");
        Assert.Contains("IsToggled=\"{Binding UseSimulated, Mode=TwoWay}\"", view);
        Assert.Contains("<Label IsVisible=\"{Binding UseSimulated}\" Text=\"UseFake\" />", view);   // the text is not a binding
        Assert.Contains("public bool UseSimulated { get; set; }", Read("Vm/SettingsViewModel.cs"));
        // Reference arm: what binds another type's UseFake, or what cannot be read, keeps the old name — and is said.
        Assert.Contains("<!-- <Label IsVisible=\"{Binding UseFake}\" /> -->", view);
        Assert.Contains("{Binding Order.UseFake}", view);
        Assert.Contains("Path=UseFake}", view);
        Assert.Contains("<Label IsVisible=\"{Binding UseFake}\" />\n", view.Replace("\r\n", "\n"));   // the DataTemplate's
        Assert.Contains("{Binding UseFake}", Read("Views/LooseView.xaml"));
        Assert.Contains("Left as they are", report);
        foreach (var why in (string[])["in a comment", "binding path segment after `Order`", "a binding whose source (Source)",
                                       "binds `App.Vm.OtherViewModel`", "under no x:DataType",
                                       "the word, where this rename cannot read what it names"])
            Assert.Contains(why, report);
        Assert.Contains("(markup: matched by name, not compiled)", report);
        // The card says it too, in the user's language.
        var card = Assert.Single(approval.Cards);
        Assert.Contains(Strings.RenameMarkupByName(2), card);
        Assert.Contains(Strings.RenameLeftAsTheyAre(6, "UseFake"), card);
    }

    [Fact]
    public async Task AnXBindInsideATemplate_ReadsTheTemplatesDataType_NotThePage()
    {
        Write("App/Item.cs", "namespace App; public class Item { public string Name { get; set; } = \"\"; }");
        Write("App/MainPage.xaml.cs", "namespace App; public partial class MainPage { public string Name { get; set; } = \"\"; }");
        Write("App/MainPage.xaml", """
            <Page x:Class="App.MainPage"
                  xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                  xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                  xmlns:local="using:App">
                <StackPanel>
                    <TextBlock Text="{x:Bind Name}" />
                    <ListView>
                        <ListView.ItemTemplate>
                            <DataTemplate x:DataType="local:Item">
                                <TextBlock Text="{x:Bind Name}" />
                            </DataTemplate>
                        </ListView.ItemTemplate>
                    </ListView>
                </StackPanel>
            </Page>
            """);
        await Rename("Name", "Title", "App/MainPage.xaml.cs");
        var page = Read("App/MainPage.xaml").Replace("\r\n", "\n");
        Assert.Contains("<TextBlock Text=\"{x:Bind Title}\" />\n        <ListView>", page);           // the page's own
        Assert.Contains("<TextBlock Text=\"{x:Bind Name}\" />\n                </DataTemplate>", page);   // the template's Item
    }

    [Fact]
    public async Task AMemberOfABaseClass_FollowsInTheViewsOfItsDerivedViewModels()
    {
        XamlProject();
        await Rename("IsBusy", "IsWorking");
        Assert.Contains("{Binding IsWorking}", Read("Views/SettingsView.xaml"));
    }

    // ── Razor components ───────────────────────────────────────────────────────

    private void RazorProject()
    {
        Write("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Razor\" />");
        Write("Components/CatalogSearch.razor", """
            <div>
                <p>BrandIds</p>
                <span>@BrandIds</span>
                <button @onclick="() => Show(BrandIds)">show</button>
            </div>
            @code {
                [Parameter] public int[]? BrandIds { get; set; }
                private int Count => BrandIds?.Length ?? 0; // BrandIds, a comment
                private string Label = "BrandIds";
                private void Show(int[]? ids) { }
            }
            """);
        Write("Pages/Catalog.razor", """
            @page "/catalog"
            <CatalogSearch BrandIds="@BrandIds" />
            @code {
                public int[]? BrandIds { get; set; }
            }
            """);
        Write("Pages/Item.razor", """
            @page "/item/{itemId:int}"
            <h1>@ItemId</h1>
            @code {
                [Parameter] public int ItemId { get; set; }
            }
            """);
    }

    [Fact]
    public async Task AnAvaloniaTemplatesDataType_AndABindingContextWithoutOne_AreScopesToo()
    {
        XamlProject();
        Write("Views/Avalonia.axaml", """
            <UserControl xmlns="https://github.com/avaloniaui"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         xmlns:vm="using:App.Vm"
                         x:DataType="vm:SettingsViewModel">
                <StackPanel>
                    <CheckBox IsChecked="{Binding UseFake}" />
                    <ItemsControl>
                        <ItemsControl.ItemTemplate>
                            <DataTemplate DataType="vm:OtherViewModel">
                                <CheckBox IsChecked="{Binding UseFake}" />
                            </DataTemplate>
                        </ItemsControl.ItemTemplate>
                    </ItemsControl>
                    <StackPanel DataContext="{Binding Order}">
                        <CheckBox IsChecked="{Binding UseFake}" />
                    </StackPanel>
                </StackPanel>
            </UserControl>
            """);
        var (report, _) = await Rename("UseFake", "UseMock", "Vm/SettingsViewModel.cs");
        var text = Read("Views/Avalonia.axaml");
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, "UseMock"));
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(text, @"\bUseFake\b").Count);
        Assert.Contains("Avalonia.axaml", report);
    }

    [Fact]
    public async Task AConverterParameter_IsNoPath_AndAnEntityOrACrlfKeepsThePositions()
    {
        XamlProject();
        Write("Views/Format.xaml", "<ContentPage xmlns=\"http://schemas.microsoft.com/dotnet/2021/maui\"\r\n"
            + "             xmlns:x=\"http://schemas.microsoft.com/winfx/2009/xaml\"\r\n"
            + "             xmlns:vm=\"clr-namespace:App.Vm\"\r\n"
            + "             x:DataType=\"vm:SettingsViewModel\">\r\n"
            + "    <Label Text=\"{Binding UseFake, StringFormat='&lt;{0}&gt;'}\"\r\n"
            + "           IsVisible=\"{Binding IsBusy, ConverterParameter='A, UseFake, B'}\" />\r\n"
            // No path: a comma inside the quoted value does not make one.
            + "    <Label Text=\"{Binding ConverterParameter='A, UseFake, B'}\" />\r\n"
            + "    <Label Text=\"{Binding StringFormat='&lt;{0}&gt;',\r\n                   Path=UseFake}\" />\r\n"
            + "</ContentPage>\r\n");
        await Rename("UseFake", "UseMock", "Vm/SettingsViewModel.cs");
        var text = Read("Views/Format.xaml");
        Assert.Contains("Text=\"{Binding UseMock, StringFormat='&lt;{0}&gt;'}\"\r\n", text);
        Assert.Contains("ConverterParameter='A, UseFake, B'", text);
        Assert.Contains("{Binding ConverterParameter='A, UseFake, B'}", text);
        // After an entity and a line break in the value, the position is still the name's.
        Assert.Contains("StringFormat='&lt;{0}&gt;',\r\n                   Path=UseMock}", text);
    }

    [Fact]
    public async Task TwoComponentsDeclaringTheName_AreListed_AndNothingIsRenamed()
    {
        RazorProject();
        var (report, approval) = await Rename("BrandIds", "BrandFilter");
        Assert.Contains("names 2 different symbols", report);
        Assert.Contains("component CatalogSearch (@code) — Components/CatalogSearch.razor:7", report);
        Assert.Contains("component Catalog (@code) — Pages/Catalog.razor:4", report);
        Assert.Empty(approval.Cards);
        Assert.Contains("[Parameter] public int[]? BrandIds", Read("Components/CatalogSearch.razor"));
    }

    [Fact]
    public async Task AComponentsParameter_FollowsInItsCodeAndOnItsTag_AndAnotherComponentsPropertyStays()
    {
        RazorProject();
        var (report, _) = await Rename("BrandIds", "BrandFilter", "Components/CatalogSearch.razor");

        var search = Read("Components/CatalogSearch.razor");
        Assert.Contains("[Parameter] public int[]? BrandFilter { get; set; }", search);
        Assert.Contains("private int Count => BrandFilter?.Length ?? 0;", search);
        Assert.Contains("<span>@BrandFilter</span>", search);
        Assert.Contains("@onclick=\"() => Show(BrandFilter)\"", search);
        // Reference arm, in the same file: text, a string, a comment.
        Assert.Contains("<p>BrandIds</p>", search);
        Assert.Contains("\"BrandIds\"", search);
        Assert.Contains("// BrandIds, a comment", search);

        var catalog = Read("Pages/Catalog.razor");
        Assert.Contains("<CatalogSearch BrandFilter=\"@BrandIds\" />", catalog);    // the attribute is the parameter
        Assert.Contains("public int[]? BrandIds { get; set; }", catalog);           // Catalog's own property is not
        Assert.Contains("this file's own `BrandIds`, another symbol than the one renamed (declared in Components/CatalogSearch.razor)", report);
        Assert.Contains("in a string or a comment of the component's code", report);
    }

    [Fact]
    public async Task InTheComponentsOwnCode_OnlyTheMemberFollows_NotLocalsArgumentsOrAnotherTypesMembers()
    {
        RazorProject();
        Write("Components/CatalogSearch.razor", """
            <CatalogSearchBox BrandIds="1" />
            @code {
                [Parameter] public int[]? BrandIds { get; set; }
                private Filter Make() => new Filter { BrandIds = BrandIds };
                private int Count(int[] BrandIds2) => Show(BrandIds: BrandIds);
                private int Show(int[]? BrandIds) => BrandIds?.Length ?? 0;
            }
            """);
        Write("Pages/Catalog.razor", """
            @page "/catalog"
            <CatalogSearch BrandIds="@ids" Title="Edit BrandIds here" />
            """);
        var (report, _) = await Rename("BrandIds", "BrandFilter", "Components/CatalogSearch.razor");
        var search = Read("Components/CatalogSearch.razor");
        Assert.Contains("[Parameter] public int[]? BrandFilter { get; set; }", search);
        Assert.Contains("new Filter { BrandIds = BrandFilter }", search);              // Filter's member stays
        Assert.Contains("Show(BrandIds: BrandFilter)", search);                         // the named argument stays
        Assert.Contains("private int Show(int[]? BrandIds) => BrandIds?.Length ?? 0;", search);   // the parameter stays
        Assert.Contains("<CatalogSearchBox BrandIds=\"1\" />", search);                 // another component's tag
        Assert.Contains("<CatalogSearch BrandFilter=\"@ids\" Title=\"Edit BrandIds here\" />", Read("Pages/Catalog.razor"));
        foreach (var why in (string[])["a member of the object being initialized", "a named argument", "a local or a parameter"])
            Assert.Contains(why, report);
    }

    [Fact]
    public async Task ATypeRename_InAnInject_RenamesTheType_NotThePropertyItDeclares()
    {
        Write("Services/WeatherService.cs", "namespace App; public class WeatherService { }");
        Write("Pages/Weather.razor", """
            @inject WeatherService WeatherService
            <p>@WeatherService</p>
            """);
        await Rename("WeatherService", "ForecastService");
        var page = Read("Pages/Weather.razor");
        Assert.Contains("@inject ForecastService WeatherService", page);
        Assert.Contains("<p>@WeatherService</p>", page);
    }

    [Fact]
    public async Task ACSharpNameThatIsAlsoAComponentsFileName_IsRenamed()
    {
        // Reference arm of the component-name refusal: Error.razor does not own every `Error` of the workspace.
        Write("Pages/Error.razor", "<h1>Error</h1>");
        Write("Models/Result.cs", "namespace App; public class Result { public string Error { get; set; } = \"\"; public bool Failed => Error.Length > 0; }");
        var (report, _) = await Rename("Error", "Problem");
        Assert.DoesNotContain("is a Razor component", report);
        Assert.Contains("public string Problem { get; set; }", Read("Models/Result.cs"));
    }

    [Fact]
    public async Task AComponentOwnedMember_IsNotRenamedInScriptsEither()
    {
        RazorProject();
        Write("wwwroot/app.js", "export const BrandIds = [];");
        var (report, _) = await Rename("BrandIds", "BrandFilter", "Components/CatalogSearch.razor");
        Assert.Contains("export const BrandIds = [];", Read("wwwroot/app.js"));
        Assert.Contains("another language's word", report);
    }

    [Fact]
    public async Task AComponentsRouteParameter_FollowsTheRename_InItsCase()
    {
        RazorProject();
        await Rename("ItemId", "ProductId");
        var item = Read("Pages/Item.razor");
        Assert.Contains("@page \"/item/{productId:int}\"", item);
        Assert.Contains("[Parameter] public int ProductId { get; set; }", item);
        Assert.Contains("<h1>@ProductId</h1>", item);
    }

    [Fact]
    public async Task AComponentsName_IsItsFiles_AndIsRefused()
    {
        RazorProject();
        var (report, approval) = await Rename("CatalogSearch", "ProductSearch");
        Assert.Contains("is a Razor component: its name is its file's", report);
        Assert.Contains("ProductSearch.razor", report);
        Assert.Empty(approval.Cards);
        Assert.Contains("<CatalogSearch", Read("Pages/Catalog.razor"));
    }

    [Fact]
    public async Task AComponentOwnedMember_IsNotRenamedInCSharpFilesByItsNameAlone()
    {
        RazorProject();
        Write("Services/Filters.cs", "namespace App; public class Filters { public int[]? BrandIds { get; set; } }");
        Write("Pages/Catalog.razor", "@page \"/catalog\"\n<CatalogSearch BrandIds=\"@ids\" />\n");
        var (report, _) = await Rename("BrandIds", "BrandFilter", "Components/CatalogSearch.razor");
        Assert.Contains("public int[]? BrandIds { get; set; }", Read("Services/Filters.cs"));
        Assert.Contains("C#, where `CatalogSearch`'s @code members are not compiled: not rewritten", report);
    }

    [Fact]
    public async Task TwoComponentsOfOneName_LeaveTheirTagsListed()
    {
        RazorProject();
        Write("Admin/CatalogSearch.razor", "<p>admin</p>\n@code { [Parameter] public int Size { get; set; } }\n");
        var (report, _) = await Rename("BrandIds", "BrandFilter", "Components/CatalogSearch.razor");
        Assert.Contains("<CatalogSearch BrandIds=\"@BrandIds\" />", Read("Pages/Catalog.razor"));
        Assert.Contains("an attribute of <CatalogSearch>, a name two components bear", report);
        Assert.Contains("[Parameter] public int[]? BrandFilter", Read("Components/CatalogSearch.razor"));
    }

    [Fact]
    public async Task TwoComponentsOfOneName_TheOneTheUsingsBringIn_IsTheTagsComponent()
    {
        // eShop: a page of one app imports the shared library's CatalogSearch; another app has its own.
        RazorProject();
        Write("Admin/Admin.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Razor\"><PropertyGroup><RootNamespace>Shop.Admin</RootNamespace></PropertyGroup></Project>");
        Write("Admin/Widgets/CatalogSearch.razor", "<p>admin</p>\n@code { [Parameter] public int[]? BrandIds { get; set; } }\n");
        Write("Admin/Pages/Home.razor", "<CatalogSearch BrandIds=\"@ids\" />\n");
        Write("Admin/_Imports.razor", "@using Shop.Admin.Widgets\n@using App.Components\n");
        Write("Pages/_Imports.razor", "@using System.Net.Http\r\n@using App.Components\r\n");   // CRLF
        // No using: App.Components.Inner is inside App.Components, whose components are in scope.
        Write("Components/Inner/Use.razor", "<CatalogSearch BrandIds=\"@ids\" />\n");
        var (report, _) = await Rename("BrandIds", "BrandFilter", "Components/CatalogSearch.razor");
        Assert.Contains("<CatalogSearch BrandFilter=\"@BrandIds\" />", Read("Pages/Catalog.razor"));
        Assert.Contains("<CatalogSearch BrandFilter=\"@ids\" />", Read("Components/Inner/Use.razor"));
        Assert.Contains("<CatalogSearch BrandIds=\"@ids\" />", Read("Admin/Pages/Home.razor"));   // both are imported there
        Assert.Contains("a name two components bear that this file's usings do not tell apart", report);
        Assert.Contains("[Parameter] public int[]? BrandIds", Read("Admin/Widgets/CatalogSearch.razor"));
    }

    [Fact]
    public async Task AParameterAttributeOnItsOwnLine_DesignatesTheMember()
    {
        RazorProject();
        Write("Components/CatalogSearch.razor", "<span>@BrandIds</span>\n@code {\n    [Parameter]\n    public int[]? BrandIds { get; set; }\n}\n");
        var (report, approval) = await Rename("BrandIds", "BrandFilter", "Components/CatalogSearch.razor", declaringLine: 3);
        Assert.NotEmpty(approval.Cards);
        Assert.Contains("public int[]? BrandFilter", Read("Components/CatalogSearch.razor"));
        Assert.DoesNotContain("designates nothing", report);
    }

    [Fact]
    public async Task ARazorComment_IsNotRewritten()
    {
        RazorProject();
        Write("Components/CatalogSearch.razor", "@* @BrandIds is the filter *@\n<span>@BrandIds</span>\n<!-- BrandIds -->\n@code { [Parameter] public int[]? BrandIds { get; set; } }\n");
        await Rename("BrandIds", "BrandFilter", "Components/CatalogSearch.razor");
        var text = Read("Components/CatalogSearch.razor");
        Assert.Contains("@* @BrandIds is the filter *@", text);
        Assert.Contains("<!-- BrandIds -->", text);
        Assert.Contains("<span>@BrandFilter</span>", text);
    }

    [Fact]
    public async Task AModelThatSharesAComponentsName_IsNotThatComponent()
    {
        // Reference arm of the code-behind owner: a model Product beside a component Product is no component member.
        Write("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Razor\" />");
        Write("Models/Product.cs", "namespace App.Models; public class Product { public string Title { get; set; } = \"\"; }");
        Write("Services/Shop.cs", "namespace App.Services; public class Shop { public string Show(App.Models.Product p) => p.Title; }");
        Write("Pages/Product.razor", "<h1>@Title</h1>\n");
        Write("Pages/Product.razor.cs", "namespace App.Pages; public partial class Product { public string Title { get; set; } = \"\"; }");
        var (report, _) = await Rename("Title", "Name", "Models/Product.cs");
        Assert.Contains("p.Name", Read("Services/Shop.cs"));
        Assert.Contains("<h1>@Title</h1>", Read("Pages/Product.razor"));
        Assert.DoesNotContain("are not compiled: not rewritten", report);
    }

    // ── Razor views ────────────────────────────────────────────────────────────

    private void ViewsProject()
    {
        Write("Models/LoginModel.cs", """
            namespace App.Models;
            public class LoginModel { public string Username { get; set; } = ""; }
            """);
        Write("Models/OtherModel.cs", """
            namespace App.Models;
            public class OtherModel { public string Username { get; set; } = ""; }
            """);
        Write("Views/Login.cshtml", """
            @model LoginModel
            <label asp-for="Username"></label>
            <input placeholder="Username" asp-for="Username" />
            <p>@Model.Username</p>
            """);
        Write("Views/Other.cshtml", """
            @model OtherModel
            <p>@Model.Username</p>
            """);
        Write("Views/Injected.cshtml", """
            @inject LoginModel Login
            <p>@Login.Username</p>
            """);
    }

    [Fact]
    public async Task AViewsModelMember_FollowsInModelAndAspFor_AndAnotherModelsStays()
    {
        ViewsProject();
        var (report, _) = await Rename("Username", "LoginName", "Models/LoginModel.cs");

        var login = Read("Views/Login.cshtml");
        Assert.Contains("<label asp-for=\"LoginName\"></label>", login);
        Assert.Contains("<input placeholder=\"Username\" asp-for=\"LoginName\" />", login);   // the placeholder is text
        Assert.Contains("<p>@Model.LoginName</p>", login);
        Assert.Contains("<p>@Login.LoginName</p>", Read("Views/Injected.cshtml"));
        Assert.Contains("<p>@Model.Username</p>", Read("Views/Other.cshtml"));
        Assert.Contains("a member of the view's model `OtherModel`, another type than the one renamed", report);
    }

    private void RazorPagesProject()
    {
        Write("Pages/Orders/Index.cshtml.cs", "namespace App.Pages.Orders; public class IndexModel { public string Title { get; set; } = \"\"; }");
        Write("Pages/Products/Index.cshtml.cs", "namespace App.Pages.Products; public class IndexModel { public string Title { get; set; } = \"\"; }");
        Write("Pages/Orders/Index.cshtml", "@page\n@model IndexModel\n<h1>@Model.Title</h1>\n");
        Write("Pages/Products/Index.cshtml", "@page\n@model IndexModel\n<h1>@Model.Title</h1>\n");
        Write("Pages/Orders/Qualified.cshtml", "@page\n@model App.Pages.Orders.IndexModel\n<h1>@Model.Title</h1>\n");
        Write("Pages/Products/Qualified.cshtml", "@page\n@model App.Pages.Products.IndexModel\n<h1>@Model.Title</h1>\n");
    }

    [Fact]
    public async Task AModelNamedLikeAnotherFoldersOne_IsListed_AndAQualifiedOneIsRead()
    {
        // Razor Pages: one IndexModel per folder. With no namespace to read, @model IndexModel cannot say which.
        RazorPagesProject();
        var (report, _) = await Rename("Title", "Heading", "Pages/Orders/Index.cshtml.cs");
        Assert.Contains("@Model.Title", Read("Pages/Orders/Index.cshtml"));
        Assert.Contains("@Model.Title", Read("Pages/Products/Index.cshtml"));
        Assert.Contains("a name several types bear that this file's usings do not tell apart", report);
        Assert.Contains("@Model.Heading", Read("Pages/Orders/Qualified.cshtml"));
        Assert.Contains("@Model.Title", Read("Pages/Products/Qualified.cshtml"));
    }

    [Fact]
    public async Task APagesNamespace_SaysWhichIndexModelItsModelIs()
    {
        // The usual Razor Pages layout: @namespace in Pages/_ViewImports.cshtml, one more segment per folder.
        RazorPagesProject();
        Write("Pages/_ViewImports.cshtml", "@namespace App.Pages\n@addTagHelper *, Microsoft.AspNetCore.Mvc.TagHelpers\n");
        var (report, _) = await Rename("Title", "Heading", "Pages/Orders/Index.cshtml.cs");
        Assert.Contains("@Model.Heading", Read("Pages/Orders/Index.cshtml"));
        Assert.Contains("@Model.Title", Read("Pages/Products/Index.cshtml"));
        Assert.Contains("a member of the view's model `IndexModel`, another type than the one renamed", report);
    }

    [Fact]
    public async Task AViewModelNamedLikeOneOfAnotherApp_IsTheOneTheViewImportsBringIn()
    {
        // eShop: Identity.API's LoginViewModel, and the mobile app's, in one workspace.
        ViewsProject();
        Write("Mobile/LoginModel.cs", "namespace Mobile.ViewModels;\npublic class LoginModel { public string Username { get; set; } = \"\"; }");
        Write("Views/Login.cshtml", "@model LoginModel\n<input asp-for=\"Username\" />\n<p>@Model.Username</p>\n");
        Write("Views/_ViewImports.cshtml", "@using App.Models\r\n@addTagHelper *, Microsoft.AspNetCore.Mvc.TagHelpers\r\n");   // CRLF, like most
        var (report, _) = await Rename("Username", "LoginName", "Models/LoginModel.cs");
        var login = Read("Views/Login.cshtml");
        Assert.Contains("asp-for=\"LoginName\"", login);
        Assert.Contains("@Model.LoginName", login);
        Assert.Contains("public string Username", Read("Mobile/LoginModel.cs"));

        // Reference arm: a view that imports both cannot say which.
        Write("Views/Both.cshtml", "@using Mobile.ViewModels\n@model LoginModel\n<p>@Model.LoginName</p>\n");
        Inferpal.Services.Lsp.CSharpSemanticIndex.NotifyFileChanged(Path.Combine(_root, "Models", "LoginModel.cs"));   // the watcher
        (report, _) = await Rename("LoginName", "Username", "Models/LoginModel.cs");
        Assert.Contains("@Model.LoginName", Read("Views/Both.cshtml"));
        Assert.Contains("@Model.Username", Read("Views/Login.cshtml"));
        Assert.Contains("a name several types bear that this file's usings do not tell apart", report);
    }

    [Fact]
    public async Task ATypeRename_InAModelDirective_FollowsTheTypeTheViewSees()
    {
        ViewsProject();
        Write("Mobile/LoginModel.cs", "namespace Mobile.ViewModels;\npublic class LoginModel { }");
        Write("Views/_ViewImports.cshtml", "@using App.Models\r\n");
        Write("Views/Both.cshtml", "@using Mobile.ViewModels\n@model LoginModel\n");
        var (report, _) = await Rename("LoginModel", "SignInModel", "Models/LoginModel.cs");
        Assert.Contains("@model SignInModel", Read("Views/Login.cshtml"));
        Assert.Contains("@model LoginModel", Read("Views/Both.cshtml"));
        Assert.Contains("the type `LoginModel`, a name several types bear that this file's usings do not tell apart", report);
    }

    [Fact]
    public async Task APagesRoute_FollowsWhenItsModelIsTheHolder_AndIsListedOtherwise()
    {
        Write("Pages/Edit.cshtml.cs", "namespace App.Pages; public class EditModel { public int Id { get; set; } }");
        Write("Pages/Other.cshtml.cs", "namespace App.Pages; public class OtherModel { public int Id { get; set; } }");
        Write("Pages/Edit.cshtml", "@page \"{id:int}\"\n@model EditModel\n<p>@Model.Id</p>\n");
        Write("Pages/Other.cshtml", "@page \"{id:int}\"\n@model OtherModel\n<p>@Model.Id</p>\n");
        var (report, _) = await Rename("Id", "ProductId", "Pages/Edit.cshtml.cs");
        Assert.Contains("@page \"{productId:int}\"", Read("Pages/Edit.cshtml"));
        Assert.Contains("@page \"{id:int}\"", Read("Pages/Other.cshtml"));
        Assert.Contains("a route parameter of a page whose model this rename does not place", report);
    }

    // ── Reference arm, and impact ──────────────────────────────────────────────

    [Fact]
    public async Task WithoutMarkup_TheReportSaysNothingOfIt()
    {
        Write("A.cs", "namespace App; public class A { public int Count { get; set; } public int Twice => Count * 2; }");
        var (report, approval) = await Rename("Count", "Total");
        Assert.DoesNotContain("markup", report, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Left as they are", report);
        Assert.DoesNotContain(Strings.RenameMarkupByName(1).Split(':')[0], Assert.Single(approval.Cards));
    }

    private async Task<string> Impact(string path, string symbol)
    {
        var tool = new AnalyzeImpactTool(() => _root);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { path = Path.Combine(_root, path), symbol }));
        return await tool.ExecuteAsync(doc.RootElement, CancellationToken.None);
    }

    [Fact]
    public async Task AComponentUsedOnlyInMarkup_IsNoLongerSafeToRefactorFreely()
    {
        RazorProject();
        var report = await Impact("Components/CatalogSearch.razor", "CatalogSearch");
        Assert.Contains("## Markup · matched by name, not compiled  (1)", report);
        Assert.Contains("Pages", report);
        Assert.DoesNotContain("safe to refactor freely", report);
    }

    [Fact]
    public async Task AViewModelUsedThroughXDataType_IsListed()
    {
        XamlProject();
        var report = await Impact("Vm/OtherViewModel.cs", "OtherViewModel");
        Assert.Contains("SettingsView.xaml", report);
        Assert.Contains("vm:OtherViewModel", report);
    }

    [Fact]
    public async Task AFrameworkTagOfTheSameName_IsNotAUse_AndAComponentsCodeBehindIsListedByItsTag()
    {
        XamlProject();
        // A project type named Label: XAML's <Label> is the framework's, a project type is written with its prefix.
        Write("Vm/Label.cs", "namespace App.Vm;\npublic class Label { }");
        Write("Views/Uses.xaml", """
            <ContentView xmlns="http://schemas.microsoft.com/dotnet/2021/maui" xmlns:vm="clr-namespace:App.Vm">
                <vm:Label />
            </ContentView>
            """);
        var labels = await Impact("Vm/Label.cs", "Label");
        Assert.Contains("Uses.xaml", labels);
        Assert.DoesNotContain("SettingsView.xaml", labels);

        RazorProject();
        Write("Components/CatalogSearch.razor.cs", "namespace App.Components;\npublic partial class CatalogSearch { }");
        var report = await Impact("Components/CatalogSearch.razor.cs", "CatalogSearch");
        Assert.Contains("## Markup", report);
        Assert.Contains("<CatalogSearch", report);

        // A component written as a class.
        Write("Components/Spinner.cs", "namespace App.Components;\npublic class Spinner : ComponentBase { }");
        Write("Pages/Wait.razor", "<Spinner />\n");
        Assert.Contains("<Spinner />", await Impact("Components/Spinner.cs", "Spinner"));
    }

    [Fact]
    public async Task ATypeNoMarkupNames_HasNoMarkupSection()
    {
        // Reference arm.
        XamlProject();
        Write("Vm/Unused.cs", "namespace App.Vm; public class Unused { }");
        Assert.DoesNotContain("## Markup", await Impact("Vm/Unused.cs", "Unused"));
    }
}
