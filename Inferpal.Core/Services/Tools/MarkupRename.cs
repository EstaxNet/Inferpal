using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Inferpal.Services.Lsp;

namespace Inferpal.Services.Tools;

/// <summary>
/// The part of a rename that lands in markup — <c>.razor</c>, <c>.cshtml</c>, <c>.xaml</c>, <c>.axaml</c> — which the C#
/// compiler does not read.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ Markup is matched by NAME here, never compiled, so it is rewritten only at the positions that bind the renamed
/// symbol and whose binding this pass can read: a <c>{Binding}</c> under an <c>x:DataType</c> that has the member, a
/// type through the <c>clr-namespace</c> its prefix maps to, <c>Model.X</c> and <c>asp-for</c> under an <c>@model</c>
/// that has it, the code and the route of the component that declares it, its attributes on <c>&lt;X …&gt;</c>
/// elsewhere. Every other occurrence of the word is LEFT and named, with why: a word in a text node, another
/// component's member of the same name, a binding whose source is not declared.
/// </para>
/// <para>
/// ⚠ Matched by the word over the whole file, a rename rewrites another component's property of the same name and misses
/// the route template that binds the parameter (case-insensitively), so the page receives no id from its URL; left
/// unread, XAML and Razor views keep their bindings on the old name under "applied".
/// </para>
/// </remarks>
internal static class MarkupRename
{
    /// <summary>The markup files a rename reads.</summary>
    internal static readonly string[] Extensions = [".razor", ".cshtml", ".xaml", ".axaml"];

    public static bool IsMarkup(string path) =>
        Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>The symbol renamed, as markup can see it.</summary>
    /// <param name="Target">What the C# compiler resolved (the type, or the types that have the member); <c>null</c>
    /// when it resolved nothing.</param>
    /// <param name="OwnerComponent">The <c>.razor</c> file of the component the member belongs to — the one whose
    /// <c>@code</c> declares it, or whose code-behind does; <c>null</c> when it is no component's.</param>
    /// <param name="OwnerShown">That file as the report names it (from the workspace root): two components can share a
    /// name and a folder layout.</param>
    /// <param name="OwnerNamespace">The owner component's namespace; <c>null</c> when it cannot be read.</param>
    /// <param name="Namesakes">The namespaces of the other components that bear the owner's name (<c>null</c> for one
    /// that cannot be read): a tag <c>&lt;X …&gt;</c> is the owner's only where the file sees it and none of them.</param>
    internal sealed record Symbol(string OldName, string NewName, MarkupTarget? Target, string? OwnerComponent,
                                  string? OwnerShown = null, string? OwnerNamespace = null,
                                  IReadOnlyList<string?>? Namesakes = null)
    {
        /// <summary>The component's class name: its file name.</summary>
        public string? OwnerName => OwnerComponent is null ? null : ComponentName(OwnerComponent);

        /// <summary>Whether a type written in markup — simple or qualified — is for certain one that has the member.</summary>
        public bool HolderNamed(string written, MarkupScope? scope) =>
            Target is { IsType: false } t && t.Read(written, scope) == MarkupTarget.Reading.Holder;

        /// <summary>Which component a tag <c>&lt;X …&gt;</c> bearing the owner's name is, from a file that sees
        /// <paramref name="scope"/>.</summary>
        public MarkupTarget.Reading OwnerTag(MarkupScope? scope)
        {
            if (Namesakes is not { Count: > 0 } others) return MarkupTarget.Reading.Holder;
            if (scope is null || OwnerNamespace is null || others.Any(o => o is null)) return MarkupTarget.Reading.Ambiguous;
            var owner = scope.Sees(OwnerNamespace);
            var other = others.Any(o => scope.Sees(o!));
            return owner == other ? MarkupTarget.Reading.Ambiguous : owner ? MarkupTarget.Reading.Holder : MarkupTarget.Reading.Other;
        }
    }

    /// <summary>An occurrence of the old name left as it is, and why.</summary>
    internal sealed record Left(int Line, string Snippet, string Why);

    /// <summary>One file's rename: its new text, how many positions were rewritten, what was left.</summary>
    internal sealed record Outcome(string NewText, int Rewritten, IReadOnlyList<Left> Left);

    /// <summary>A component's class name: its <c>.razor</c> file name.</summary>
    public static string ComponentName(string razorPath) => Path.GetFileNameWithoutExtension(razorPath);

    /// <param name="scope">The namespaces the file sees (<see cref="ScopeOf"/>); <c>null</c> when they are not known,
    /// and a simple type name that several types bear is then left.</param>
    public static Outcome Apply(string path, string text, Symbol symbol, MarkupScope? scope = null)
    {
        var plan = new Plan(text, symbol, scope);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (symbol.Target is null && symbol.OwnerComponent is null)
            plan.FileWhy = "the C# compiler resolved no symbol of this name: markup is not rewritten by name alone";
        else if (ext is ".xaml" or ".axaml")
        {
            if (symbol.Target is null) plan.FileWhy = "a Razor component's member, which XAML cannot bind";
            else Xaml(plan);
        }
        else Razor(plan, isComponent: ext == ".razor",
                   isOwner: symbol.OwnerComponent is { } owner && PathComparer.Default.Equals(Path.GetFullPath(owner), Path.GetFullPath(path)));
        return plan.Finish();
    }

    // ── The plan of one file: positions to rewrite, reasons for the others ─────

    private sealed class Plan(string text, Symbol symbol, MarkupScope? scope)
    {
        public readonly string Text = text;
        public readonly Symbol Symbol = symbol;
        public readonly MarkupScope? Scope = scope;
        public readonly Regex Word = WordOf(symbol.OldName);
        private readonly List<int> _lineStarts = LineStarts(text);
        private readonly Dictionary<int, string> _rewrite = [];   // start offset → replacement (length = OldName)
        private readonly Dictionary<int, string> _why = [];       // start offset of a word occurrence left → why
        private readonly Dictionary<int, string> _notes = [];     // an occurrence the word scan does not see (another case)
        private readonly List<(int Start, int End)> _comments = [];
        public string? FileWhy;                                   // a reason for every word of the file

        /// <summary>A comment of the file: nothing in it is rewritten.</summary>
        public void Comment(int start, int end) => _comments.Add((start, end));

        /// <summary>An occurrence written in another case (a route parameter): listed unless rewritten.</summary>
        public void Note(int start, string why) => _notes.TryAdd(start, why);

        public void Rewrite(int start, string? replacement = null)
        {
            if (start < 0 || start + Symbol.OldName.Length > Text.Length) return;
            if (_comments.Any(c => start >= c.Start && start < c.End)) return;
            if (string.CompareOrdinal(Text, start, Symbol.OldName, 0, Symbol.OldName.Length) != 0
                && !string.Equals(Text.Substring(start, Symbol.OldName.Length), Symbol.OldName, StringComparison.OrdinalIgnoreCase))
                return;
            _rewrite[start] = replacement ?? Symbol.NewName;
            _why.Remove(start);
        }

        public void Why(int start, string why)
        {
            if (!_rewrite.ContainsKey(start)) _why.TryAdd(start, why);
        }

        public Outcome Finish()
        {
            var left = new List<Left>();
            foreach (Match m in Word.Matches(Text))
            {
                if (_rewrite.ContainsKey(m.Index)) continue;
                left.Add(new Left(LineOf(m.Index), SnippetAt(m.Index),
                    _why.GetValueOrDefault(m.Index) ?? FileWhy ?? "the word, where this rename cannot read what it names"));
            }
            foreach (var (start, why) in _notes)
                if (!_rewrite.ContainsKey(start) && !Word.IsMatch(Text.Substring(start, Symbol.OldName.Length)))
                    left.Add(new Left(LineOf(start), SnippetAt(start), why));
            if (_rewrite.Count == 0) return new Outcome(Text, 0, left);
            var sb = new StringBuilder(Text);
            foreach (var (start, replacement) in _rewrite.OrderByDescending(r => r.Key))
            {
                sb.Remove(start, Symbol.OldName.Length);
                sb.Insert(start, replacement);
            }
            return new Outcome(sb.ToString(), _rewrite.Count, left);
        }

        public int LineOf(int offset)
        {
            var i = _lineStarts.BinarySearch(offset);
            return i >= 0 ? i + 1 : ~i;
        }

        private string SnippetAt(int offset)
        {
            var start = Text.LastIndexOf('\n', Math.Max(0, offset - 1)) + 1;
            var end = Text.IndexOf('\n', offset);
            var line = Text[start..(end < 0 ? Text.Length : end)].Trim();
            return line.Length <= 140 ? line : line[..140] + "…";
        }
    }

    private static Regex WordOf(string name) =>
        new($@"(?<!\w){Regex.Escape(name)}(?!\w)", RegexOptions.CultureInvariant, RegexBudget.Default);

    /// <summary>Marks every word inside a comment of the file as left for that reason.</summary>
    private static void CommentsOf(Plan plan, Regex comments)
    {
        foreach (Match c in comments.Matches(plan.Text))
        {
            plan.Comment(c.Index, c.Index + c.Length);
            foreach (Match w in plan.Word.Matches(c.Value))
                plan.Why(c.Index + w.Index, "in a comment");
        }
    }

    private static readonly Regex XmlComments = new(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.CultureInvariant, RegexBudget.Default);

    // ── XAML ───────────────────────────────────────────────────────────────────

    private static readonly Regex MarkupExtension = new(@"^\s*\{\s*(?<name>[\w:]+)(?<args>.*)\}\s*$",
        RegexOptions.Singleline | RegexOptions.CultureInvariant, RegexBudget.Default);

    private static void Xaml(Plan plan)
    {
        var s = plan.Symbol;
        var lineStarts = LineStarts(plan.Text);
        var scopes = new Stack<(int Depth, TypeName? Type, bool InTemplate)>();
        TypeName? xClass = null;
        CommentsOf(plan, XmlComments);
        try
        {
            using var reader = XmlReader.Create(new StringReader(plan.Text),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            var info = (IXmlLineInfo)reader;
            while (reader.Read())
            {
                if (reader.NodeType is not (XmlNodeType.Element or XmlNodeType.EndElement)) continue;
                var nameAt = Offset(lineStarts, info.LineNumber, info.LinePosition);
                var elementType = ClrType(reader.NamespaceURI, reader.LocalName.Split('.')[0]);
                ElementName(plan, reader.Prefix, reader.LocalName, reader.NamespaceURI, nameAt);
                if (reader.NodeType == XmlNodeType.EndElement) continue;

                var depth = reader.Depth;
                while (scopes.Count > 0 && scopes.Peek().Depth >= depth) scopes.Pop();
                var attributes = new List<(string Prefix, string Local, string Ns, string Value, int At)>();
                if (reader.MoveToFirstAttribute())
                {
                    do attributes.Add((reader.Prefix, reader.LocalName, reader.NamespaceURI, reader.Value,
                                       Offset(lineStarts, info.LineNumber, info.LinePosition)));
                    while (reader.MoveToNextAttribute());
                    reader.MoveToElement();
                }
                // The element's own x:DataType applies to its own bindings: read it first. A template's DataType
                // (Avalonia, WPF) is one too; a DataContext or BindingContext set here, without one, is a source this
                // pass does not read.
                var local = reader.LocalName;
                var isTemplate = local.EndsWith("DataTemplate", StringComparison.Ordinal);
                var inTemplate = isTemplate || scopes.Any(sc => sc.InTemplate);
                var dataType = attributes.FirstOrDefault(a => a.Local == "DataType"
                                                              && (IsXamlLanguage(a.Ns) || (a.Prefix.Length == 0 && isTemplate)));
                if (dataType.Value is { Length: > 0 } dt) scopes.Push((depth, TypeNamed(reader, dt), inTemplate));
                else if (attributes.Any(a => a.Prefix.Length == 0 && a.Local is "DataContext" or "BindingContext"))
                    scopes.Push((depth, null, inTemplate));
                if (depth == 0 && attributes.FirstOrDefault(a => a.Local == "Class" && IsXamlLanguage(a.Ns)).Value is { Length: > 0 } cls)
                    xClass = Split(cls);
                var scope = scopes.Count > 0 ? scopes.Peek().Type : null;
                // x:Bind reads the page's own members, except inside a template, whose x:DataType is its source.
                var bindScope = scopes.Count > 0 && scopes.Peek().InTemplate ? scope : xClass;

                foreach (var a in attributes)
                {
                    if (a.Prefix == "xmlns" || (a.Prefix.Length == 0 && a.Local == "xmlns")) continue;
                    var valueAt = ValueStart(plan.Text, a.At);
                    if (valueAt < 0) continue;
                    // A member set as an attribute of an element of a type that has it: <local:Card Title="…">.
                    if (!s.Target!.IsType && a.Local == s.OldName && a.Prefix.Length == 0
                        && elementType is { } et && s.Target.Holders.Contains(et))
                        plan.Rewrite(a.At);
                    else if (a.Local == s.OldName && a.Prefix.Length == 0)
                        plan.Why(a.At, "an attribute of an element whose type this rename does not know to have it");
                    // The value as written: XmlReader's has its line breaks and entities replaced, and the offsets
                    // of everything after them would move.
                    var close = plan.Text.IndexOf(plan.Text[valueAt - 1], valueAt);
                    if (close < 0) continue;
                    XamlValue(plan, reader, plan.Text[valueAt..close], valueAt, scope, bindScope, a.Local == "Class" && IsXamlLanguage(a.Ns));
                }
            }
        }
        catch (XmlException ex)
        {
            plan.FileWhy = $"the file is not well-formed XML ({ex.Message.Split('\n')[0]}): not read";
        }
    }

    private static void ElementName(Plan plan, string prefix, string local, string ns, int nameAt)
    {
        var s = plan.Symbol;
        var at = nameAt + (prefix.Length > 0 ? prefix.Length + 1 : 0);
        var parts = local.Split('.');
        var type = ClrType(ns, parts[0]);
        if (s.Target is { IsType: true } t && parts[0] == s.OldName && type is not null && t.Holders.Contains(type))
            plan.Rewrite(at);
        // A property element: <local:Card.Title>.
        if (s.Target is { IsType: false } m && parts.Length == 2 && parts[1] == s.OldName && type is not null && m.Holders.Contains(type))
            plan.Rewrite(at + parts[0].Length + 1);
    }

    /// <summary>The bindings and type names an attribute value holds.</summary>
    private static void XamlValue(Plan plan, XmlReader reader, string value, int valueAt, TypeName? scope, TypeName? bindScope, bool isClass)
    {
        var s = plan.Symbol;
        if (s.Target is null) return;
        if (isClass)
        {
            if (s.Target.IsType && Split(value) is var full && s.Target.Holders.Contains(full))
                plan.Rewrite(valueAt + value.LastIndexOf('.') + 1);
            return;
        }
        if (s.Target.IsType)
        {
            // prefix:Type, anywhere in the value — x:DataType, TargetType, {x:Type …}, {x:Static …}, {d:DesignInstance …}.
            foreach (Match m in Regex.Matches(value, $@"(?<![\w:.])(?<prefix>[A-Za-z_][\w]*):{Regex.Escape(s.OldName)}(?!\w)",
                                              RegexOptions.CultureInvariant, RegexBudget.Default))
                if (ClrType(reader.LookupNamespace(m.Groups["prefix"].Value) ?? string.Empty, s.OldName) is { } t
                    && s.Target.Holders.Contains(t))
                    plan.Rewrite(valueAt + m.Index + m.Groups["prefix"].Length + 1);
            return;
        }
        if (MarkupExtension.Match(value) is not { Success: true } ext) return;
        var name = ext.Groups["name"].Value;
        var isXBind = name is "x:Bind";
        if (!isXBind && name is not ("Binding" or "d:Binding" or "TemplateBinding" or "CompiledBinding" or "ReflectionBinding"))
        {
            // Nested extensions are read where they are bindings themselves: Converter={StaticResource …} holds none.
            return;
        }
        var argsAt = valueAt + ext.Groups["args"].Index;
        var args = TopLevelArguments(ext.Groups["args"].Value);
        var path = args.FirstOrDefault(a => a.Name is null || a.Name == "Path");
        if (path.Text is null) return;
        TypeName? source = isXBind ? bindScope : scope;
        var other = args.FirstOrDefault(a => a.Name is "Source" or "ElementName" or "RelativeSource");
        if (other.Name is not null)
        {
            source = other.Name == "RelativeSource"
                     && Regex.Match(other.Text, @"AncestorType\s*=\s*\{x:Type\s+(?<t>[\w:.]+)\s*\}", RegexOptions.CultureInvariant,
                                    RegexBudget.Default) is { Success: true } ancestor
                ? TypeNamed(reader, ancestor.Groups["t"].Value)
                : null;
        }
        var segments = PathSegments(path.Text);
        for (var i = 0; i < segments.Count; i++)
        {
            var (segment, offset) = segments[i];
            if (segment != s.OldName) continue;
            var at = argsAt + path.Offset + offset;
            if (i > 0)
                plan.Why(at, $"a binding path segment after `{segments[i - 1].Text}`, whose type this rename does not read");
            else if (source is null)
                plan.Why(at, other.Name is not null
                    ? $"a binding whose source ({other.Name}) this rename does not read"
                    : "a binding under no x:DataType: what it binds is decided at run time");
            else if (s.Target.Holders.Contains(source))
                plan.Rewrite(at);
            else
                plan.Why(at, $"binds `{source}`, which this rename does not give that member — another symbol");
        }
    }

    /// <summary>The comma-separated arguments of a markup extension, at the top level only.</summary>
    private static List<(string? Name, string Text, int Offset)> TopLevelArguments(string args)
    {
        var result = new List<(string?, string, int)>();
        int depth = 0, start = 0;
        var quoted = false;
        for (var i = 0; i <= args.Length; i++)
        {
            if (i < args.Length)
            {
                var c = args[i];
                // A single-quoted value holds commas and braces of its own: ConverterParameter='A, B'.
                if (c == '\'') quoted = !quoted;
                if (quoted) continue;
                if (c == '{') depth++;
                else if (c == '}') depth--;
                if (c != ',' || depth != 0) continue;
            }
            var part = args[start..i];
            var lead = part.Length - part.TrimStart().Length;
            var trimmed = part.Trim();
            if (trimmed.Length > 0)
            {
                var eq = trimmed.IndexOf('=');
                if (eq > 0 && !trimmed[..eq].Contains('{') && Regex.IsMatch(trimmed[..eq].Trim(), @"^[\w:.]+$", RegexOptions.CultureInvariant, RegexBudget.Default))
                {
                    var valueText = trimmed[(eq + 1)..];
                    var valueLead = valueText.Length - valueText.TrimStart().Length;
                    result.Add((trimmed[..eq].Trim(), valueText.Trim(), start + lead + eq + 1 + valueLead));
                }
                else result.Add((null, trimmed, start + lead));
            }
            start = i + 1;
        }
        return result;
    }

    /// <summary>A property path's segments with their offsets: <c>Order.Total</c>; indexers and attached properties
    /// (<c>(Grid.Row)</c>, <c>[0]</c>) end the readable part.</summary>
    private static List<(string Text, int Offset)> PathSegments(string path)
    {
        var result = new List<(string, int)>();
        var offset = 0;
        foreach (var part in path.Split('.'))
        {
            var name = part.Trim();
            if (name.Length == 0 || !Regex.IsMatch(name, @"^[A-Za-z_]\w*$", RegexOptions.CultureInvariant, RegexBudget.Default)) break;
            result.Add((name, offset + (part.Length - part.TrimStart().Length)));
            offset += part.Length + 1;
        }
        return result;
    }

    private static bool IsXamlLanguage(string ns) =>
        ns is "http://schemas.microsoft.com/winfx/2006/xaml" or "http://schemas.microsoft.com/winfx/2009/xaml";

    /// <summary>The CLR type a XAML namespace and name designate; <c>null</c> for a framework namespace.</summary>
    private static TypeName? ClrType(string ns, string name)
    {
        foreach (var head in (string[])["clr-namespace:", "using:"])
            if (ns.StartsWith(head, StringComparison.Ordinal))
                return new TypeName(ns[head.Length..].Split(';')[0].Trim(), name);
        return null;
    }

    private static TypeName? TypeNamed(XmlReader reader, string value)
    {
        var v = value.Trim();
        if (v.StartsWith('{') && Regex.Match(v, @"^\{x:Type\s+(?<t>[\w:.]+)\s*\}$", RegexOptions.CultureInvariant, RegexBudget.Default) is { Success: true } wrapped) v = wrapped.Groups["t"].Value;
        var colon = v.IndexOf(':');
        if (colon < 0) return null;
        return ClrType(reader.LookupNamespace(v[..colon]) ?? string.Empty, v[(colon + 1)..]);
    }

    private static TypeName Split(string full)
    {
        var dot = full.LastIndexOf('.');
        return dot < 0 ? new TypeName(string.Empty, full.Trim()) : new TypeName(full[..dot].Trim(), full[(dot + 1)..].Trim());
    }

    /// <summary>The offset of the value after an attribute name at <paramref name="nameAt"/>: past <c>=</c> and the
    /// quote; −1 when the text does not read that way.</summary>
    private static int ValueStart(string text, int nameAt)
    {
        var eq = text.IndexOf('=', nameAt);
        if (eq < 0) return -1;
        var q = eq + 1;
        while (q < text.Length && char.IsWhiteSpace(text[q])) q++;
        return q < text.Length && text[q] is '"' or '\'' ? q + 1 : -1;
    }

    private static List<int> LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++) if (text[i] == '\n') starts.Add(i + 1);
        return starts;
    }

    private static int Offset(List<int> lineStarts, int line, int column) =>
        line >= 1 && line <= lineStarts.Count ? lineStarts[line - 1] + column - 1 : -1;

    // ── Razor: components (.razor) and views (.cshtml) ─────────────────────────

    private static readonly Regex Directive = new(@"^[ \t]*@(?<kw>model|inherits|inject|page|implements|layout|typeparam)\b(?<rest>[^\r\n]*)",
        RegexOptions.Multiline | RegexOptions.CultureInvariant, RegexBudget.Default);

    private static readonly Regex InjectDirective = new(@"^\s*(?<t>[\w.<>,\s]+?)\s+(?<n>[A-Za-z_]\w*)\s*$",
        RegexOptions.CultureInvariant, RegexBudget.Default);

    private static readonly Regex CodeBlockStart = new(@"@(?:code|functions)\s*\{",
        RegexOptions.CultureInvariant, RegexBudget.Default);

    private static readonly Regex ImplicitExpression = new(@"(?<![\w@])@(?<expr>[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*)(?![\w=:-])",
        RegexOptions.CultureInvariant, RegexBudget.Default);

    private static readonly Regex AttributeExpression = new(
        @"@\((?<e>[^()]*(?:\([^()]*\)[^()]*)*)\)|@(?:bind[\w:-]*|on\w+|ref|key)\s*=\s*""(?<e>[^""]*)""",
        RegexOptions.CultureInvariant, RegexBudget.Default);

    private static readonly Regex Comments = new(@"@\*.*?\*@|<!--.*?-->",
        RegexOptions.Singleline | RegexOptions.CultureInvariant, RegexBudget.Default);

    /// <summary>An attribute of a markup tag: its name, and its value when it has one (quoted or bare).</summary>
    private static readonly Regex TagAttribute = new(@"(?<=\s)(?<name>[@\w:.-]+)(?:\s*=\s*(?:""[^""]*""|'[^']*'|[^\s>]+))?",
        RegexOptions.CultureInvariant, RegexBudget.Default);

    private static readonly HashSet<string> RazorKeywords = new(StringComparer.Ordinal)
    {
        "code", "functions", "page", "model", "inject", "using", "if", "else", "foreach", "for", "while", "do", "switch",
        "try", "catch", "finally", "lock", "inherits", "implements", "layout", "namespace", "attribute", "typeparam",
        "rendermode", "preservewhitespace", "section", "helper", "addTagHelper", "removeTagHelper", "tagHelperPrefix",
        "await",
    };

    /// <summary>A piece of C# in a Razor file: the members of an <c>@code</c> block, or one expression.</summary>
    private readonly record struct Segment(int At, string Code, bool Members);

    private static void Razor(Plan plan, bool isComponent, bool isOwner)
    {
        var s = plan.Symbol;
        var text = plan.Text;
        CommentsOf(plan, Comments);

        // @inject T Name, and the view's @model: the names whose type is known.
        var injected = new Dictionary<string, string>(StringComparer.Ordinal);
        string? model = null;
        var routes = new List<Group>();
        foreach (Match d in Directive.Matches(text))
        {
            var rest = d.Groups["rest"];
            var kw = d.Groups["kw"].Value;
            var typeLength = rest.Length;
            if (kw == "inject" && InjectDirective.Match(rest.Value) is { Success: true } inj)
            {
                injected[inj.Groups["n"].Value] = TypeWritten(inj.Groups["t"].Value);
                // The type only: the property it declares is a name of its own (@inject Weather Weather).
                typeLength = inj.Groups["t"].Index + inj.Groups["t"].Length;
            }
            else if (kw == "model") model = TypeWritten(rest.Value);
            else if (kw == "page") routes.Add(rest);
            if (s.Target is { IsType: true } && kw is "model" or "inherits" or "inject" or "implements" or "layout")
                foreach (Match w in plan.Word.Matches(rest.Value[..typeLength]))
                {
                    // The type as written around the word (App.Models.Login, Login): the one renamed, or a namesake.
                    var start = w.Index;
                    while (start > 0 && (char.IsLetterOrDigit(rest.Value[start - 1]) || rest.Value[start - 1] is '_' or '.')) start--;
                    var written = rest.Value[start..(w.Index + w.Length)];
                    var reading = s.Target.Read(written, plan.Scope);
                    if (reading == MarkupTarget.Reading.Holder) plan.Rewrite(rest.Index + w.Index);
                    else plan.Why(rest.Index + w.Index, reading == MarkupTarget.Reading.Ambiguous
                        ? $"the type `{written}`, a name several types bear that this file's usings do not tell apart"
                        : $"the type `{written}`, another type than the one renamed");
                }
        }

        // A route parameter binds by name, ignoring case: {itemId:int} is a component's ItemId, or a page model's
        // [BindProperty] ItemId under @model.
        foreach (var rest in routes)
            foreach (Match r in Regex.Matches(rest.Value, $@"\{{\*?(?<n>{Regex.Escape(s.OldName)})(?=[:?}}])",
                                              RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexBudget.Default))
            {
                var at = rest.Index + r.Groups["n"].Index;
                if (s.Target is { IsType: true }) continue;
                if (isComponent ? isOwner : model is not null && s.HolderNamed(model, plan.Scope))
                {
                    var written = r.Groups["n"].Value;
                    plan.Rewrite(at, char.IsLower(written[0]) ? char.ToLowerInvariant(s.NewName[0]) + s.NewName[1..] : s.NewName);
                }
                else plan.Note(at, isComponent ? "a route parameter of another component" : "a route parameter of a page whose model this rename does not place");
                plan.Why(at, isComponent ? "a route parameter of another component" : "a route parameter of a page whose model this rename does not place");
            }

        // The C# of the file — @code / @functions blocks and expressions — compiled together, so each identifier is known
        // for what it is: the component's own member, a local or a parameter, or a name the file does not declare.
        var segments = new List<Segment>();
        var blocks = new List<(int Start, int End)>();
        foreach (Match b in CodeBlockStart.Matches(text))
        {
            var open = b.Index + b.Length - 1;
            var close = MatchingBrace(text, open);
            if (close < 0) continue;
            blocks.Add((open + 1, close));
            segments.Add(new Segment(open + 1, text[(open + 1)..close], Members: true));
        }
        bool InBlock(int offset) => blocks.Any(b => offset >= b.Start && offset < b.End);

        // @{ … } blocks mix code and markup: not read.
        foreach (Match b in Regex.Matches(text, @"@\{", RegexOptions.CultureInvariant, RegexBudget.Default))
        {
            var close = MatchingBrace(text, b.Index + 1);
            if (close < 0 || InBlock(b.Index)) continue;
            foreach (Match w in plan.Word.Matches(text[(b.Index + 2)..close]))
                plan.Why(b.Index + 2 + w.Index, "inside a @{ } block, where code and markup mix: not read");
        }
        foreach (Match e in ImplicitExpression.Matches(text))
            if (!InBlock(e.Index) && !RazorKeywords.Contains(e.Groups["expr"].Value.Split('.')[0]))
                segments.Add(new Segment(e.Groups["expr"].Index, e.Groups["expr"].Value, Members: false));
        foreach (Match e in AttributeExpression.Matches(text))
            if (!InBlock(e.Index))
                segments.Add(new Segment(e.Groups["e"].Index, e.Groups["e"].Value, Members: false));
        ResolveCode(plan, segments, isOwner, injected, model);
        // A word of a @code block the compiler did not return as an identifier is in a string or a comment (the first
        // reason given stands, so this one comes after the identifiers').
        foreach (var (start, end) in blocks)
            foreach (Match w in plan.Word.Matches(text[start..end]))
                plan.Why(start + w.Index, "in a string or a comment of the component's code");

        // asp-for="X" in a view: a path from its @model.
        foreach (Match a in Regex.Matches(text, @"asp-(?:for|validation-for)\s*=\s*""(?:@?Model\.)?(?<p>[\w.]+)""",
                                          RegexOptions.CultureInvariant, RegexBudget.Default))
        {
            var p = a.Groups["p"];
            if (p.Value.Split('.')[0] != s.OldName) continue;
            if (s.Target is { IsType: false } && model is not null && s.HolderNamed(model, plan.Scope)) plan.Rewrite(p.Index);
            else plan.Why(p.Index, model is null ? "an asp-for under no @model" : TypeWhy(plan, model, "an asp-for of"));
        }

        // The component's parameters, set by other components: <X Old="…">, @bind-Old, Old:after — attribute NAMES only.
        if (s.OwnerName is { } owner && !isOwner && isComponent)
        {
            var reading = s.OwnerTag(plan.Scope);
            foreach (Match tag in Regex.Matches(text, $@"<(?:[\w]+\.)*{Regex.Escape(owner)}(?=[\s/>])(?<attrs>(?:[^>""']|""[^""]*""|'[^']*')*)",
                                                RegexOptions.CultureInvariant, RegexBudget.Default))
            {
                var attrs = tag.Groups["attrs"];
                foreach (Match attr in TagAttribute.Matches(attrs.Value))
                {
                    var name = attr.Groups["name"];
                    var bare = name.Value.StartsWith("@bind-", StringComparison.Ordinal) ? name.Value[6..] : name.Value;
                    var colon = bare.IndexOf(':');
                    if ((colon < 0 ? bare : bare[..colon]) != s.OldName) continue;
                    var at = attrs.Index + name.Index + (name.Value.Length - bare.Length);
                    if (reading == MarkupTarget.Reading.Holder) plan.Rewrite(at);
                    else plan.Why(at, reading == MarkupTarget.Reading.Other
                        ? $"an attribute of <{owner}>, another component of that name — the one this file's usings bring in"
                        : $"an attribute of <{owner}>, a name two components bear that this file's usings do not tell apart");
                }
            }
        }
    }

    /// <summary>The core library and the framework, for binding the C# of a Razor file.</summary>
    private static IReadOnlyList<MetadataReference> Framework => CSharpSemanticIndex.RuntimeReferences;

    /// <summary>
    /// Compiles the C# pieces of a Razor file as the members of one class — the component — and classifies every
    /// identifier spelled like the old name by what the compiler binds it to.
    /// </summary>
    private static void ResolveCode(Plan plan, List<Segment> segments, bool isOwner, Dictionary<string, string> injected, string? model)
    {
        var s = plan.Symbol;
        if (segments.Count == 0 || !segments.Any(seg => seg.Code.Contains(s.OldName, StringComparison.Ordinal))) return;
        var source = new StringBuilder("class __Razor\n{\n");
        // An injected name is a property of the component; its type is not read, so a member reached through it stays
        // unresolved and is judged by the type @inject names.
        foreach (var name in injected.Keys) source.Append("public object ").Append(name).Append(" { get; }\n");
        var declared = segments.Where(seg => seg.Members).Any(seg => DeclaredMembers(seg.Code).Contains(s.OldName));
        // A component whose member lives in its code-behind: the member is declared there, not in this file.
        if (isOwner && s.Target is not { IsType: true } && !declared)
            source.Append("public object ").Append(s.OldName).Append(" { get; set; }\n");
        var map = new List<(int Synthetic, int Length, int At)>();
        var n = 0;
        foreach (var seg in segments)
        {
            if (!seg.Members) source.Append("object __e").Append(n++).Append("() => (");
            map.Add((source.Length, seg.Code.Length, seg.At));
            source.Append(seg.Code).Append(seg.Members ? "\n" : ");\n");
        }
        source.Append("}\n");

        var tree = CSharpSyntaxTree.ParseText(source.ToString());
        var compilation = CSharpCompilation.Create("__razor", [tree], Framework,
                                                   new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var semantic = compilation.GetSemanticModel(tree);
        var root = tree.GetRoot();
        var componentDeclaration = root.DescendantNodes().OfType<ClassDeclarationSyntax>().First();
        var component = semantic.GetDeclaredSymbol(componentDeclaration);
        foreach (var token in root.DescendantTokens())
        {
            if (!token.IsKind(SyntaxKind.IdentifierToken) || token.ValueText != s.OldName) continue;
            var at = -1;
            foreach (var (synthetic, length, origin) in map)
                if (token.SpanStart >= synthetic && token.SpanStart < synthetic + length) { at = origin + token.SpanStart - synthetic; break; }
            if (at < 0) continue;   // the injected names and the stub: not in the file
            Classify(plan, token, semantic, componentDeclaration, component, at, isOwner, injected, model);
        }
    }

    private static void Classify(Plan plan, SyntaxToken token, SemanticModel semantic, ClassDeclarationSyntax componentDeclaration,
                                 INamedTypeSymbol? component, int at, bool isOwner, Dictionary<string, string> injected, string? model)
    {
        var s = plan.Symbol;
        var parent = token.Parent;
        if (s.Target is { IsType: true })
        {
            if (parent is IdentifierNameSyntax typeName && SyntaxFacts.IsInTypeOnlyContext(typeName)) plan.Rewrite(at);
            else plan.Why(at, "the type's name where it may name something else");
            return;
        }
        if (parent is PropertyDeclarationSyntax or MethodDeclarationSyntax or EventDeclarationSyntax
            || parent is VariableDeclaratorSyntax { Parent.Parent: FieldDeclarationSyntax or EventFieldDeclarationSyntax })
        {
            if (parent.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault() == componentDeclaration) Own(plan, at, isOwner);
            else plan.Why(at, "a member of a type declared inside the component's code, another symbol");
            return;
        }
        if (parent is VariableDeclaratorSyntax or ParameterSyntax or SingleVariableDesignationSyntax or ForEachStatementSyntax
            or CatchDeclarationSyntax or FromClauseSyntax or LetClauseSyntax)
        {
            plan.Why(at, "a local or a parameter of the code, not the member");
            return;
        }
        if (parent is not IdentifierNameSyntax id)
        {
            plan.Why(at, "a name this rename cannot place in the component's code");
            return;
        }
        if (id.Parent is NameColonSyntax)
        {
            plan.Why(at, "a named argument, the parameter of another method");
            return;
        }
        if (id.Parent is AssignmentExpressionSyntax { Parent: InitializerExpressionSyntax init } assign
            && init.IsKind(SyntaxKind.ObjectInitializerExpression) && assign.Left == id)
        {
            plan.Why(at, "a member of the object being initialized, another type's");
            return;
        }
        var info = semantic.GetSymbolInfo(id);
        var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
        if (symbol is ILocalSymbol or IParameterSymbol or IRangeVariableSymbol)
        {
            plan.Why(at, "a local or a parameter of the code, not the member");
            return;
        }
        if (symbol is not null && component is not null && SymbolEqualityComparer.Default.Equals(symbol.ContainingType, component))
        {
            Own(plan, at, isOwner);
            return;
        }
        // Not resolved here: what qualifies it says whose member it is.
        var qualifier = id.Parent is MemberAccessExpressionSyntax access && access.Name == id
            ? access.Expression is IdentifierNameSyntax q ? q.Identifier.ValueText : access.Expression.ToString()
            : null;
        if (qualifier is null)
            plan.Why(at, "a name this file's code does not declare, whose owner this rename does not read");
        else if (qualifier == "Model" && model is not null)
        {
            if (s.HolderNamed(model, plan.Scope)) plan.Rewrite(at);
            else plan.Why(at, TypeWhy(plan, model, "a member of the view's model"));
        }
        else if (injected.TryGetValue(qualifier, out var type))
        {
            if (s.HolderNamed(type, plan.Scope)) plan.Rewrite(at);
            else plan.Why(at, TypeWhy(plan, type, $"a member injected as {qualifier}, of"));
        }
        else plan.Why(at, $"a member of `{qualifier}`, whose type this rename does not read");
    }

    /// <summary>The component's own member: rewritten in the component renamed, named in any other.</summary>
    private static void Own(Plan plan, int at, bool isOwner)
    {
        var s = plan.Symbol;
        if (isOwner) plan.Rewrite(at);
        else plan.Why(at, s.OwnerComponent is null
            ? $"this file's own `{s.OldName}`, another symbol than the one renamed"
            : $"this file's own `{s.OldName}`, another symbol than the one renamed (declared in {s.OwnerShown ?? s.OwnerComponent})");
    }

    /// <summary>The names of the members a <c>@code</c> block declares, at its top level.</summary>
    private static HashSet<string> DeclaredMembers(string code)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var root = CSharpSyntaxTree.ParseText("class __Razor { " + code + "\n}").GetRoot();
        var type = root.DescendantNodes().OfType<ClassDeclarationSyntax>().First();
        foreach (var member in type.Members)
            switch (member)
            {
                case PropertyDeclarationSyntax p: names.Add(p.Identifier.ValueText); break;
                case MethodDeclarationSyntax m: names.Add(m.Identifier.ValueText); break;
                case EventDeclarationSyntax e: names.Add(e.Identifier.ValueText); break;
                case BaseFieldDeclarationSyntax f: foreach (var v in f.Declaration.Variables) names.Add(v.Identifier.ValueText); break;
            }
        return names;
    }

    /// <summary>The brace closing the one at <paramref name="open"/>, skipping C# strings, characters and comments; −1
    /// when there is none.</summary>
    private static int MatchingBrace(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/') { i = text.IndexOf('\n', i); if (i < 0) return -1; continue; }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*') { i = text.IndexOf("*/", i + 2, StringComparison.Ordinal); if (i < 0) return -1; i++; continue; }
            if (c is '"' or '\'')
            {
                var verbatim = i > 0 && text[i - 1] == '@' && c == '"';
                for (i++; i < text.Length; i++)
                {
                    if (!verbatim && text[i] == '\\') { i++; continue; }
                    if (text[i] == c)
                    {
                        if (verbatim && i + 1 < text.Length && text[i + 1] == '"') { i++; continue; }
                        break;
                    }
                }
                continue;
            }
            if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return i;
        }
        return -1;
    }

    /// <summary>Why a member of a type written in markup is not rewritten: another type, or a name several types bear.</summary>
    private static string TypeWhy(Plan plan, string written, string what) =>
        plan.Symbol.Target?.Read(written, plan.Scope) == MarkupTarget.Reading.Ambiguous
            ? $"{what} `{written}`, a name several types bear that this file's usings do not tell apart"
            : $"{what} `{written}`, another type than the one renamed";

    // ── The namespaces a Razor file sees ───────────────────────────────────────

    // ⚠ \r?$: in a multiline .NET regex $ stands before \n only, and most of these files end their lines in CRLF.
    private static readonly Regex UsingDirective = new(@"^[ \t]*@using\s+(?!static\b)(?<ns>[\w.]+)[ \t]*;?[ \t]*\r?$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant, RegexBudget.Default);

    private static readonly Regex NamespaceDirective = new(@"^[ \t]*@namespace\s+(?<ns>[\w.]+)",
        RegexOptions.Multiline | RegexOptions.CultureInvariant, RegexBudget.Default);

    /// <summary>
    /// The namespaces a <c>.razor</c> or <c>.cshtml</c> file sees: its <c>@using</c>s, those of the <c>_Imports.razor</c>
    /// or <c>_ViewImports.cshtml</c> of its folder and of every folder above it up to its project's, the project's
    /// <c>&lt;Using&gt;</c> items; and its namespace — its <c>@namespace</c>, that of the nearest imports file followed by
    /// the folders below it, or, for a component, the project's root namespace followed by its folders. <c>null</c> for
    /// XAML, whose types are written with their namespace.
    /// </summary>
    public static MarkupScope? ScopeOf(string path, string text, string workspaceRoot, Func<string, string?> read)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not (".razor" or ".cshtml")) return null;
        var imports = ext == ".razor" ? "_Imports.razor" : "_ViewImports.cshtml";
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var top = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));
        var chain = new List<string>();
        string? project = null;
        for (var d = dir; d is not null; d = Path.GetDirectoryName(d))
        {
            chain.Add(d);
            project = ProjectFileIn(d);
            if (project is not null || PathComparer.Default.Equals(Path.TrimEndingDirectorySeparator(d), top)) break;
        }
        chain.Reverse();

        var usings = new HashSet<string>(StringComparer.Ordinal);
        (string Ns, string Dir)? declared = null;
        foreach (var d in chain)
        {
            var file = Path.Combine(d, imports);
            if (PathComparer.Default.Equals(file, Path.GetFullPath(path)) || !File.Exists(file) || read(file) is not { } t) continue;
            foreach (Match u in UsingDirective.Matches(t)) usings.Add(u.Groups["ns"].Value);
            if (NamespaceDirective.Match(t) is { Success: true } n) declared = (n.Groups["ns"].Value, d);
        }
        foreach (Match u in UsingDirective.Matches(text)) usings.Add(u.Groups["ns"].Value);

        var projectText = project is null ? null : read(project);
        if (projectText is not null)
            foreach (Match u in Regex.Matches(projectText, @"<Using\s+Include\s*=\s*""(?<ns>[\w.]+)""\s*/?>",
                                              RegexOptions.CultureInvariant, RegexBudget.Default))
                usings.Add(u.Groups["ns"].Value);

        string? own = NamespaceDirective.Match(text) is { Success: true } self ? self.Groups["ns"].Value : null;
        if (own is null && declared is { } dn) own = Below(dn.Ns, dn.Dir, dir);
        if (own is null && ext == ".razor" && project is not null && RootNamespace(project, projectText) is { } rootNs)
            own = Below(rootNs, Path.GetDirectoryName(project)!, dir);
        return new MarkupScope(own, usings);
    }

    private static string? ProjectFileIn(string dir)
    {
        try { return Directory.EnumerateFiles(dir, "*.csproj").OrderBy(f => f, StringComparer.Ordinal).FirstOrDefault(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Swallow("MarkupRename.ProjectFile", ex);
            return null;
        }
    }

    /// <summary>A project's root namespace: its <c>&lt;RootNamespace&gt;</c>, else its file name; <c>null</c> when it is
    /// an MSBuild expression.</summary>
    private static string? RootNamespace(string project, string? text)
    {
        if (text is not null && Regex.Match(text, @"<RootNamespace>\s*(?<ns>[^<]*?)\s*</RootNamespace>",
                                            RegexOptions.CultureInvariant, RegexBudget.Default) is { Success: true } m)
            return m.Groups["ns"].Value.Contains("$(", StringComparison.Ordinal) ? null : m.Groups["ns"].Value;
        return Identifier(Path.GetFileNameWithoutExtension(project));
    }

    /// <summary>The namespace of a folder below the one that sets <paramref name="ns"/>: one segment per folder.</summary>
    private static string Below(string ns, string from, string to)
    {
        var rel = Path.GetRelativePath(from, to);
        if (rel == ".") return ns;
        var segments = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                          .Where(x => x.Length > 0).Select(Identifier);
        return string.Join('.', [ns, .. segments]);
    }

    /// <summary>A folder or project name as Razor writes it in a namespace: what is no identifier character becomes
    /// <c>_</c>, a leading digit is prefixed.</summary>
    private static string Identifier(string name)
    {
        var parts = name.Split('.').Select(p =>
        {
            var chars = p.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray();
            var id = new string(chars);
            return id.Length > 0 && char.IsDigit(id[0]) ? "_" + id : id;
        });
        return string.Join('.', parts);
    }

    /// <summary>A type as markup writes it, its generic arguments dropped: <c>App.Models.Login</c>, <c>Login</c>.</summary>
    private static string TypeWritten(string type)
    {
        var t = type.Trim();
        var lt = t.IndexOf('<');
        return (lt >= 0 ? t[..lt] : t).Trim();
    }

    // ── Who declares a member in a component ───────────────────────────────────

    /// <summary>A member a component's <c>@code</c> declares: the line of its name, and the lines of the whole
    /// declaration (its attributes included), any of which designates it.</summary>
    internal sealed record RazorDeclaration(string File, int Line, int FirstLine, int LastLine);

    /// <summary>
    /// The components whose <c>@code</c> or <c>@functions</c> declares a member named <paramref name="name"/>.
    /// </summary>
    public static List<RazorDeclaration> RazorDeclarations(IEnumerable<string> razorFiles, string name,
                                                           Func<string, string?> read)
    {
        var result = new List<RazorDeclaration>();
        foreach (var file in razorFiles)
        {
            if (!file.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)) continue;
            var text = read(file);
            if (text is null || !text.Contains(name, StringComparison.Ordinal)) continue;
            foreach (Match b in CodeBlockStart.Matches(text))
            {
                var open = b.Index + b.Length - 1;
                var close = MatchingBrace(text, open);
                if (close < 0) continue;
                var code = text[(open + 1)..close];
                var root = CSharpSyntaxTree.ParseText("class __Razor { " + code + "\n}").GetRoot();
                foreach (var member in root.DescendantNodes().OfType<MemberDeclarationSyntax>())
                {
                    SyntaxToken? identifier = member switch
                    {
                        PropertyDeclarationSyntax p => p.Identifier,
                        MethodDeclarationSyntax m => m.Identifier,
                        EventDeclarationSyntax e => e.Identifier,
                        BaseFieldDeclarationSyntax f => f.Declaration.Variables.FirstOrDefault(v => v.Identifier.ValueText == name)?.Identifier,
                        _ => null,
                    };
                    if (identifier?.ValueText != name || member.Parent is not ClassDeclarationSyntax { Identifier.ValueText: "__Razor" }) continue;
                    int LineAt(int synthetic) =>
                        1 + text[..Math.Clamp(open + 1 + synthetic - "class __Razor { ".Length, 0, text.Length)].Count(ch => ch == '\n');
                    result.Add(new RazorDeclaration(file, LineAt(identifier.Value.SpanStart), LineAt(member.SpanStart), LineAt(member.Span.End)));
                }
            }
        }
        return result;
    }
}
