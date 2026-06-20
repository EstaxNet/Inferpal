using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Inferpal.Services.Lsp;

/// <summary>
/// Manages per-language LSP server sessions and exposes semantic symbol extraction
/// for TypeScript, JavaScript, Python, Go, and Rust source files.
/// </summary>
/// <remarks>
/// <para>
/// One <see cref="LspServerSession"/> is maintained per (rootDir, languageId) pair.
/// Sessions are started lazily on first use and kept alive for the extension lifetime.
/// </para>
/// <para>
/// All public members are thread-safe.  If a language server is unavailable or
/// returns an error, <see cref="GetSymbolsAsync"/> returns <c>null</c> and the caller
/// should fall back to regex-based chunking.
/// </para>
/// </remarks>
internal sealed class LspSemanticProvider : IDisposable
{
    // key = "rootDir|languageId"
    private readonly ConcurrentDictionary<string, LspServerSession> _sessions = new(StringComparer.OrdinalIgnoreCase);

    // ── Public API ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the LSP <c>languageId</c> for the given file extension, or <c>null</c>
    /// if the language is handled by another provider (e.g. C# by Roslyn) or unsupported.
    /// </summary>
    public static string? GetLanguageId(string ext) => ext.ToLowerInvariant() switch
    {
        ".ts" or ".tsx"    => "typescript",
        ".js" or ".jsx"    => "javascript",
        ".py"              => "python",
        ".go"              => "go",
        ".rs"              => "rust",
        _                  => null,          // C#/.fs/.razor → Roslyn/regex
    };

    /// <summary>
    /// Extracts document symbols from <paramref name="filePath"/> via LSP.
    /// Returns <c>null</c> when no suitable language server is installed or reachable.
    /// </summary>
    public async Task<LspDocumentSymbol[]?> GetSymbolsAsync(
        string filePath, string content, string rootDir, CancellationToken ct)
    {
        var ext    = Path.GetExtension(filePath);
        var langId = GetLanguageId(ext);
        if (langId is null) return null;

        var key     = $"{rootDir}|{langId}";
        var session = _sessions.GetOrAdd(key, _ => new LspServerSession(langId, rootDir));

        return await session.GetSymbolsAsync(filePath, content, ct);
    }

    public void Dispose()
    {
        foreach (var s in _sessions.Values)
            s.Dispose();
        _sessions.Clear();
    }

    // ── Inner class: one language server instance ──────────────────────────────

    private sealed class LspServerSession : IDisposable
    {
        private readonly string     _languageId;
        private readonly string     _rootDir;
        private readonly SemaphoreSlim _initLock = new(1, 1);
        private Process?     _process;
        private LspJsonRpc?  _rpc;
        private bool         _initialized;
        private bool         _failed;           // permanently unavailable
        private int          _initFailures;     // consecutive init attempts that failed transiently

        private static readonly TimeSpan InitTimeout    = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

        internal LspServerSession(string languageId, string rootDir)
        {
            _languageId = languageId;
            _rootDir    = rootDir;
        }

        // ── Public ─────────────────────────────────────────────────────────────

        internal async Task<LspDocumentSymbol[]?> GetSymbolsAsync(
            string filePath, string content, CancellationToken ct)
        {
            if (_failed) return null;

            try
            {
                if (!await EnsureInitializedAsync(ct)) return null;

                var uri = PathToUri(filePath);

                // Open document
                await _rpc!.SendNotificationAsync("textDocument/didOpen", new
                {
                    textDocument = new
                    {
                        uri,
                        languageId = _languageId,
                        version    = 1,
                        text       = content,
                    }
                }, ct);

                // Request symbols
                var result = await _rpc.SendRequestAsync(
                    "textDocument/documentSymbol",
                    new { textDocument = new { uri } },
                    ct,
                    RequestTimeout);

                // Close document (fire-and-forget — never blocks)
                _ = _rpc.SendNotificationAsync(
                    "textDocument/didClose",
                    new { textDocument = new { uri } },
                    CancellationToken.None);

                if (result is null) return null;

                // The server may return DocumentSymbol[] or SymbolInformation[].
                // We only handle DocumentSymbol[] (hierarchical).  Both start with
                // an array; DocumentSymbol has a "range" member, SymbolInformation has "location".
                return result.Value.ValueKind == JsonValueKind.Array
                    ? JsonSerializer.Deserialize<LspDocumentSymbol[]>(result.Value)
                    : null;
            }
            catch (OperationCanceledException) { return null; }
            catch
            {
                // Server crashed or misbehaved — mark permanently failed so we
                // don't keep spawning processes on every file.
                _failed = true;
                CleanupProcess();
                return null;
            }
        }

        // ── Initialization ─────────────────────────────────────────────────────

        private async Task<bool> EnsureInitializedAsync(CancellationToken ct)
        {
            // Fast path — already running
            if (_initialized && _process?.HasExited == false) return true;

            await _initLock.WaitAsync(ct);
            try
            {
                if (_initialized && _process?.HasExited == false) return true;
                if (_failed) return false;

                // Discover and launch the server
                var cmd = FindServerCommand(_languageId);
                if (cmd is null)
                {
                    _failed = true;
                    return false;
                }

                CleanupProcess();
                _process = StartProcess(cmd.Value.name, cmd.Value.args);
                if (_process is null)
                {
                    _failed = true;
                    return false;
                }

                // Drain stderr to prevent pipe deadlocks
                _ = DrainStreamAsync(_process.StandardError.BaseStream);

                _rpc = new LspJsonRpc(_process.StandardInput.BaseStream,
                                       _process.StandardOutput.BaseStream);

                // LSP handshake
                using var initCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                initCts.CancelAfter(InitTimeout);

                var initResult = await _rpc.SendRequestAsync("initialize", BuildInitParams(), initCts.Token);
                if (initResult is null)
                {
                    // Null response means the server timed out — transient: allow up to 3 retries.
                    CleanupProcess();
                    if (++_initFailures >= 3) _failed = true;
                    return false;
                }

                await _rpc.SendNotificationAsync("initialized", new { }, ct);
                _initFailures = 0; // reset on success
                _initialized  = true;
                return true;
            }
            catch
            {
                // Transient crash / pipe error — allow up to 3 retries before giving up.
                CleanupProcess();
                if (++_initFailures >= 3) _failed = true;
                return false;
            }
            finally
            {
                _initLock.Release();
            }
        }

        private object BuildInitParams() => new
        {
            processId = Environment.ProcessId,
            rootUri   = PathToUri(_rootDir),
            workspaceFolders = new[]
            {
                new { uri = PathToUri(_rootDir), name = Path.GetFileName(_rootDir) }
            },
            capabilities = new
            {
                textDocument = new
                {
                    documentSymbol = new
                    {
                        hierarchicalDocumentSymbolSupport = true,
                        symbolKind = new
                        {
                            valueSet = Enumerable.Range(1, 26).ToArray()
                        }
                    }
                }
            }
        };

        // ── Server discovery ───────────────────────────────────────────────────

        private static (string name, string args)? FindServerCommand(string languageId)
        {
            // Ordered by preference — first found on PATH wins.
            // Using classic switch + explicit arrays to avoid C# collection-expression
            // type-inference limitations inside switch expressions.
            string[] names;
            string   args;

            switch (languageId)
            {
                case "typescript":
                case "javascript":
                    names = new[] { "typescript-language-server" };
                    args  = "--stdio";
                    break;
                case "python":
                    names = new[] { "pylsp", "pyright-langserver" };
                    args  = "--stdio";
                    break;
                case "go":
                    names = new[] { "gopls" };
                    args  = string.Empty;    // gopls defaults to stdio
                    break;
                case "rust":
                    names = new[] { "rust-analyzer" };
                    args  = "--stdio";
                    break;
                default:
                    return null;
            }

            foreach (var name in names)
                if (IsOnPath(name)) return (name, args);

            return null;
        }

        /// <summary>
        /// Checks whether <paramref name="name"/> resolves to an executable on PATH.
        /// On Windows, also checks for <c>.cmd</c>, <c>.bat</c>, <c>.exe</c> extensions.
        /// </summary>
        private static bool IsOnPath(string name)
        {
            var dirs = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

            foreach (var dir in dirs)
            {
                try
                {
                    if (OperatingSystem.IsWindows())
                    {
                        foreach (var ext in new[] { ".exe", ".cmd", ".bat" })
                            if (File.Exists(Path.Combine(dir.Trim(), name + ext)))
                                return true;
                    }
                    else
                    {
                        if (File.Exists(Path.Combine(dir.Trim(), name)))
                            return true;
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Diagnostics.Swallow("LspSemanticProvider.WhichSearch", ex); }
            }

            return false;
        }

        /// <summary>
        /// Starts the language server process.
        /// On Windows, wraps the invocation in <c>cmd.exe /c</c> so that <c>.cmd</c>
        /// scripts (typical for npm-installed tools) are resolved correctly.
        /// </summary>
        private static Process? StartProcess(string name, string args)
        {
            try
            {
                string fileName, arguments;

                if (OperatingSystem.IsWindows())
                {
                    fileName  = "cmd.exe";
                    arguments = string.IsNullOrEmpty(args)
                        ? $"/c {name}"
                        : $"/c {name} {args}";
                }
                else
                {
                    fileName  = name;
                    arguments = args;
                }

                var psi = new ProcessStartInfo
                {
                    FileName               = fileName,
                    Arguments              = arguments,
                    UseShellExecute        = false,
                    RedirectStandardInput  = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    CreateNoWindow         = true,
                    // UTF-8 for all streams
                    StandardInputEncoding  = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    StandardErrorEncoding  = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                };

                return Process.Start(psi);
            }
            catch { return null; }
        }

        // ── Helpers ────────────────────────────────────────────────────────────

        private static string PathToUri(string path) => new Uri(path).AbsoluteUri;

        private static async Task DrainStreamAsync(Stream stream)
        {
            try
            {
                var buf = new byte[4096];
                while (await stream.ReadAsync(buf) > 0) { }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Diagnostics.Swallow("LspSemanticProvider.DrainStderr", ex); }
        }

        private void CleanupProcess()
        {
            _rpc?.Dispose();
            _rpc = null;

            try { _process?.Kill(); } catch { }
            _process?.Dispose();
            _process     = null;
            _initialized = false;
        }

        public void Dispose()
        {
            CleanupProcess();
            _initLock.Dispose();
        }
    }
}
