using System.Text.Json.Serialization;

namespace Inferpal.Services.Lsp;

// ── LSP Protocol types ────────────────────────────────────────────────────────

internal sealed class LspPosition
{
    [JsonPropertyName("line")]      public int Line      { get; set; }
    [JsonPropertyName("character")] public int Character { get; set; }
}

internal sealed class LspRange
{
    [JsonPropertyName("start")] public LspPosition Start { get; set; } = new();
    [JsonPropertyName("end")]   public LspPosition End   { get; set; } = new();
}

/// <summary>
/// LSP symbol kinds (1-based, matches the LSP specification).
/// </summary>
internal enum LspSymbolKind
{
    File         = 1,
    Module       = 2,
    Namespace    = 3,
    Package      = 4,
    Class        = 5,
    Method       = 6,
    Property     = 7,
    Field        = 8,
    Constructor  = 9,
    Enum         = 10,
    Interface    = 11,
    Function     = 12,
    Variable     = 13,
    Constant     = 14,
    String       = 15,
    Number       = 16,
    Boolean      = 17,
    Array        = 18,
    Object       = 19,
    Key          = 20,
    Null         = 21,
    EnumMember   = 22,
    Struct       = 23,
    Event        = 24,
    Operator     = 25,
    TypeParameter = 26,
}

/// <summary>
/// A hierarchical document symbol as returned by <c>textDocument/documentSymbol</c>
/// when the server supports <c>hierarchicalDocumentSymbolSupport</c>.
/// </summary>
internal sealed class LspDocumentSymbol
{
    [JsonPropertyName("name")]           public string              Name           { get; set; } = string.Empty;
    [JsonPropertyName("detail")]         public string?             Detail         { get; set; }
    [JsonPropertyName("kind")]           public LspSymbolKind       Kind           { get; set; }
    [JsonPropertyName("range")]          public LspRange            Range          { get; set; } = new();
    [JsonPropertyName("selectionRange")] public LspRange            SelectionRange { get; set; } = new();
    [JsonPropertyName("children")]       public LspDocumentSymbol[]? Children      { get; set; }
}

// ── JSON-RPC envelope types ───────────────────────────────────────────────────

internal sealed class LspRequest
{
    [JsonPropertyName("jsonrpc")] public string  JsonRpc { get; set; } = "2.0";
    [JsonPropertyName("id")]      public int?    Id      { get; set; }
    [JsonPropertyName("method")]  public string  Method  { get; set; } = string.Empty;
    [JsonPropertyName("params")]  public object? Params  { get; set; }
}

internal sealed class LspResponse
{
    [JsonPropertyName("jsonrpc")] public string                        JsonRpc { get; set; } = "2.0";
    [JsonPropertyName("id")]      public int?                          Id      { get; set; }
    [JsonPropertyName("result")]  public System.Text.Json.JsonElement? Result  { get; set; }
    [JsonPropertyName("error")]   public System.Text.Json.JsonElement? Error   { get; set; }
}
