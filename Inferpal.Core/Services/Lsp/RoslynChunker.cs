using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Inferpal.Services.Rag;

namespace Inferpal.Services.Lsp;

/// <summary>
/// Chunks C# source files using the Roslyn syntax tree for semantically accurate member
/// boundaries: one chunk per method/property/constructor, prefixed with its XML doc comments
/// and attributes (leading trivia).  Falls back to <see cref="CodeChunker"/> on any parse error
/// or when no type declarations are found (e.g. top-level-statement files).
/// </summary>
internal static class RoslynChunker
{
    /// <summary>Hard cap in estimated tokens (chars / 4) — same as LspChunker (2× RAG target).</summary>
    private const int MaxChunkTokens = 1000;
    private const int MinChunkLines  = 2;

    // ── Public API ─────────────────────────────────────────────────────────────

    public static List<RagChunk> Chunk(string filePath, string content, string rootDir)
    {
        try
        {
            var root    = CSharpSyntaxTree.ParseText(content, path: filePath).GetRoot();
            var lines   = content.Split('\n');
            var relPath = Path.GetRelativePath(rootDir, filePath);
            var chunks  = new List<RagChunk>();

            foreach (var node in root.DescendantNodes(ShouldDescend))
            {
                if (node is not BaseTypeDeclarationSyntax typeDecl) continue;
                if (typeDecl.Parent is BaseTypeDeclarationSyntax) continue; // nested — skip top-level scan

                EmitTypeChunks(typeDecl, lines, filePath, relPath, chunks);
            }

            return chunks.Count > 0 ? chunks : CodeChunker.Chunk(filePath, content, rootDir);
        }
        catch
        {
            return CodeChunker.Chunk(filePath, content, rootDir);
        }
    }

    // ── Traversal predicate ────────────────────────────────────────────────────

    // Only recurse into nodes that can contain type declarations.
    // Skipping method bodies, property accessors, etc. keeps traversal fast.
    private static bool ShouldDescend(SyntaxNode node) =>
        node is CompilationUnitSyntax or
        BaseNamespaceDeclarationSyntax or
        BaseTypeDeclarationSyntax;

    // ── Type → chunks ──────────────────────────────────────────────────────────

    private static void EmitTypeChunks(
        BaseTypeDeclarationSyntax typeDecl,
        string[] lines,
        string filePath,
        string relPath,
        List<RagChunk> chunks)
    {
        var typeName = typeDecl.Identifier.Text;

        var members = typeDecl switch
        {
            TypeDeclarationSyntax td => td.Members.ToList(),
            EnumDeclarationSyntax ed => ed.Members.Cast<MemberDeclarationSyntax>().ToList(),
            _                        => new List<MemberDeclarationSyntax>()
        };

        if (members.Count > 0)
        {
            // Header chunk: type declaration line (with XML docs/attributes) up to first member
            var typeStart  = NodeStartLine(typeDecl);
            var firstStart = NodeStartLine(members[0]);
            var headerEnd  = Math.Min(typeStart + 12, firstStart - 1);
            TryAddChunk(typeName, typeStart, headerEnd, lines, filePath, relPath, chunks);

            // One chunk per member — includes the member's XML doc trivia
            foreach (var member in members)
                TryAddChunk(
                    $"{typeName}.{MemberName(member)}",
                    NodeStartLine(member), NodeEndLine(member, lines),
                    lines, filePath, relPath, chunks);
        }
        else
        {
            // Whole type as one chunk (empty class, enum with no members, etc.)
            TryAddChunk(typeName,
                NodeStartLine(typeDecl), NodeEndLine(typeDecl, lines),
                lines, filePath, relPath, chunks);
        }
    }

    // ── Chunk assembly ─────────────────────────────────────────────────────────

    private static void TryAddChunk(
        string symbolName,
        int start0, int end0,
        string[] lines,
        string filePath,
        string relPath,
        List<RagChunk> chunks)
    {
        if (end0 < start0 || start0 >= lines.Length) return;
        end0 = Math.Min(end0, lines.Length - 1);

        int lineCount = end0 - start0 + 1;
        if (lineCount < MinChunkLines) return;

        // Hard cap: shrink by 25% steps until under budget
        while (lineCount > MinChunkLines && EstimateTokens(lines, start0, end0) > MaxChunkTokens)
        {
            int cut = Math.Max(1, lineCount / 4);
            end0      -= cut;
            lineCount  = end0 - start0 + 1;
        }

        var text = string.Join('\n', lines, start0, lineCount).Trim();
        if (string.IsNullOrWhiteSpace(text)) return;

        chunks.Add(new RagChunk
        {
            FilePath    = filePath,
            RelPath     = relPath,
            StartLine   = start0 + 1,   // 1-based
            EndLine     = end0   + 1,
            Content     = text,
            ContentHash = Md5Hex(text),
            TypeName    = symbolName,
        });
    }

    // ── Roslyn position helpers ────────────────────────────────────────────────

    /// <summary>
    /// 0-based start line of <paramref name="node"/>, walking back through leading trivia
    /// to include XML doc comment lines when present.
    /// </summary>
    private static int NodeStartLine(SyntaxNode node)
    {
        foreach (var trivia in node.GetLeadingTrivia())
        {
            if (trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) ||
                trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
            {
                return Math.Max(0, trivia.GetLocation().GetLineSpan().StartLinePosition.Line);
            }
        }
        return Math.Max(0, node.GetLocation().GetLineSpan().StartLinePosition.Line);
    }

    private static int NodeEndLine(SyntaxNode node, string[] lines) =>
        Math.Min(node.GetLocation().GetLineSpan().EndLinePosition.Line, lines.Length - 1);

    // ── Member name extraction ─────────────────────────────────────────────────

    private static string MemberName(MemberDeclarationSyntax member) => member switch
    {
        MethodDeclarationSyntax m       => m.Identifier.Text,
        PropertyDeclarationSyntax p     => p.Identifier.Text,
        ConstructorDeclarationSyntax c  => c.Identifier.Text,
        DestructorDeclarationSyntax d   => $"~{d.Identifier.Text}",
        IndexerDeclarationSyntax        => "this[]",
        OperatorDeclarationSyntax o     => $"operator{o.OperatorToken.Text}",
        FieldDeclarationSyntax f        => f.Declaration.Variables.FirstOrDefault()?.Identifier.Text ?? "field",
        EventDeclarationSyntax e        => e.Identifier.Text,
        EventFieldDeclarationSyntax ef  => ef.Declaration.Variables.FirstOrDefault()?.Identifier.Text ?? "event",
        EnumMemberDeclarationSyntax em  => em.Identifier.Text,
        BaseTypeDeclarationSyntax bt    => bt.Identifier.Text,
        _                               => "member"
    };

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static int EstimateTokens(string[] lines, int start, int end)
    {
        int total = 0;
        for (int i = start; i <= end && i < lines.Length; i++)
            total += Math.Max(1, (lines[i].Length + 1) / 4);
        return total;
    }

    private static string Md5Hex(string text)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
