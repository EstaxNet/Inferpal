using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Inferpal.Services.Tools;

/// <summary>
/// <c>http_request</c>: one HTTP request, any method — above all to the app the agent just started on the loopback.
/// </summary>
/// <remarks>
/// ⚠ An outbound request the model writes is an exfiltration channel. Every call goes through a card: on the loopback an
/// "Always" opens that ORIGIN only (a dev proxy, Docker in tcp or a debugging port on another port stay asked); anywhere else
/// the card is forced each time, unless one of the person's <c>allow http_request</c> rules matches the whole subject
/// (<see cref="Subject"/>). No redirect is followed: the next request is judged again.
/// </remarks>
/// <param name="internetOff">Whether the person keeps the Internet off now — <c>fetch_url</c> and <c>web_search</c> both turned
/// off, unless opened for this question: then only this machine is reached, before any card.</param>
internal sealed class HttpRequestTool(IApprovalService approval, Func<string?>? getWorkspaceRoot = null, Func<bool>? internetOff = null) : ITool
{
    // A page in a legacy code page (windows-1252, shift_jis) is read as it says, never as UTF-8.
    static HttpRequestTool() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    internal const int MaxBodyBytes = 1024 * 1024, MaxHeaders = 40, MaxHeaderChars = 300;
    internal const int DefaultTimeout = 30, MaxTimeout = 120, MaxWait = 120;

    public string Name => "http_request";

    /// <inheritdoc/>
    /// <remarks>The server it calls acts on its own: what it changes is beyond the request (<see cref="ToolEffect.Runs"/>).</remarks>
    public ToolEffect EffectOf(JsonElement args) =>
        ToolEffect.Network | ToolEffect.Runs | (args.Has("collection") ? ToolEffect.Reads : ToolEffect.None);

    public string Description =>
        "Sends one HTTP request (any method) and returns the status, headers and body — to call the app or API you are " +
        "running locally (http://localhost:5000/api/orders) and read its answer, a 500's stack trace included. The person " +
        "approves the first call to each local address; any other address is approved call by call. Redirects are not " +
        "followed. For a server still starting, set wait_seconds, or wait on its job: run_command action='wait'. To replay a " +
        "request the project keeps (.http/.rest file, Postman collection, Bruno collection), give collection and request " +
        "(its name; without it, the names are listed) and environment if it needs one. For a GraphQL API, give graphql (the " +
        "query or mutation) and variables instead of body, or introspect=true to read its schema; a mutation or a subscription " +
        "is approved by the person each time.";

    public object Parameters => new
    {
        type = "object",
        properties = new
        {
            url             = new { type = "string",  description = "Full URL (http or https), e.g. http://localhost:5000/api/orders." },
            method          = new { type = "string",  description = "GET (default), POST, PUT, PATCH, DELETE, HEAD or OPTIONS." },
            headers         = new { type = "object",  description = "Request headers, name → value (e.g. {\"Content-Type\": \"application/json\"})." },
            body            = new { type = "string",  description = "Request body (text, up to 1 MB). JSON when it starts with { or [ and no Content-Type is given." },
            wait_seconds    = new { type = "integer", description = $"Local addresses only: keep trying while nothing listens yet, up to this many seconds (0-{MaxWait})." },
            timeout_seconds = new { type = "integer", description = $"The longest the request may take, 1-{MaxTimeout} seconds (default {DefaultTimeout})." },
            collection      = new { type = "string",  description = "Instead of url: a .http or .rest file, a Postman collection (.json), a Bruno collection (folder or .bru) of the workspace." },
            request         = new { type = "string",  description = "With collection: the request's name." },
            environment     = new { type = "string",  description = "With collection: the environment whose variables it uses." },
            graphql         = new { type = "string",  description = "Instead of body: a GraphQL document (query, mutation), POSTed as JSON to url." },
            variables       = new { type = "object",  description = "With graphql: the operation's variables." },
            operation_name  = new { type = "string",  description = "With graphql: which operation of the document to run." },
            introspect      = new { type = "boolean", description = "Instead of body: read the GraphQL schema at url (its queries, mutations and types)." },
        },
    };

    /// <summary>The methods a request may use, as a regular expression's alternatives.</summary>
    internal const string MethodNames = "GET|POST|PUT|PATCH|DELETE|HEAD|OPTIONS";

    private static readonly HashSet<string> Methods = new(MethodNames.Split('|'), StringComparer.Ordinal);

    // Headers that would change WHERE or HOW the request goes, not what it says: Host reroutes through local forward proxies
    // and virtual hosts, Upgrade opens a websocket (a debugger's port), the framing ones are the client's to write.
    private static readonly HashSet<string> RefusedHeaders = new(StringComparer.OrdinalIgnoreCase)
        { "Host", "Connection", "Upgrade", "Transfer-Encoding", "Content-Length", "Expect", "TE", "Keep-Alive", "Trailer",
          // The body is text sent as it is: an encoding declared over it would have the server read something else.
          "Content-Encoding", "Content-Transfer-Encoding" };

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        if (args.Has("collection")) return await ReplayAsync(args, ct);
        var url = args.Str("url") ?? throw new ArgumentException("url is required (or collection and request).");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return $"Error: '{url}' is not an http or https URL.";
        var method = (args.Str("method") ?? (args.Str("graphql") is not null || args.Bool("introspect", false) ? "POST" : "GET")).Trim().ToUpperInvariant();
        if (!Methods.Contains(method)) return $"Error: unknown method '{method}' (expected {string.Join(", ", Methods)}).";

        if (IsUnspecified(uri))
            return $"Error: '{uri.Host}' is no address to call — the server listens on every interface; call it at 127.0.0.1.";
        var local = IsLoopback(uri);
        // ⚠ "No Internet" means no request out, not "a card for each": answered before the card, and said so the model does
        // not look for another way out. A local API is no Internet.
        if (!local && internetOff?.Invoke() == true)
            return ToolFailure.Said($"Error: {uri.Host} is not this machine, and the person's settings keep the Internet off (fetch_url and " +
                   "web_search are turned off): http_request reaches only this machine (localhost, 127.0.0.1). The call was NOT executed. " +
                   "Do not look for another way out: answer without it, and say what you could not reach.");

        var headers = new List<KeyValuePair<string, string>>();
        if (args.Has("headers") && !args.Object("headers", out _))
            return "Error: 'headers' must be an object, name → value (e.g. {\"Content-Type\": \"application/json\"}).";
        if (args.Object("headers", out var h))
            foreach (var p in h.EnumerateObject())
            {
                if (RefusedHeaders.Contains(p.Name) || p.Name.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase))
                    return $"Error: the header '{p.Name}' is not one a request may set here (it changes where or how the request goes).";
                headers.Add(new(p.Name, p.Value.AsText() ?? p.Value.GetRawText()));
            }
        var body = args.Str("body");
        var introspect = args.Bool("introspect", false);
        var graphQl = args.Str("graphql");
        if (graphQl is null && !introspect && (args.Has("variables") || args.Has("operation_name")))
            return "Error: variables and operation_name go with graphql; in a body of your own, write them into its JSON.";
        if (graphQl is not null || introspect)
        {
            if (body is not null) return "Error: give either graphql (or introspect) or body — not both.";
            if (graphQl is not null && introspect) return "Error: give either graphql or introspect — not both.";
            if (method != HttpMethod.Post.Method) return "Error: a GraphQL request is sent with POST (leave method out).";
            if (args.Has("variables") && !args.Object("variables", out _)) return "Error: 'variables' must be an object, name → value.";
            body = GraphQlRequest.Body(introspect ? GraphQlRequest.IntrospectionQuery : graphQl!,
                                       args.Object("variables", out var vars) ? vars : null, args.Str("operation_name"));
            if (!headers.Any(x => x.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)))
                headers.Add(new("Content-Type", "application/json"));
        }
        if (body is not null && Encoding.UTF8.GetByteCount(body) > MaxBodyBytes)
            return $"Error: the body is larger than {MaxBodyBytes / (1024 * 1024)} MB.";

        // ⚠ What leaves the machine is shown whole on its card: a body longer than a card can show is not sent.
        if (!local && body is not null && body.Length > MaxCardBody)
            return $"Error: a request to another machine carries at most {MaxCardBody} characters of body, so that the card shows all of it.";

        var (timeout, timeoutNote) = ClampedArgument.Read(args, "timeout_seconds", DefaultTimeout, 1, MaxTimeout);
        var (wait, waitNote) = ClampedArgument.Read(args, "wait_seconds", 0, 0, MaxWait);
        if (wait > 0 && !local) return "Error: wait_seconds is for a local address only.";

        var subject = Subject(method, uri);
        // ⚠ A mutation or a subscription is asked every time, however it is spelled: no "Always", no rule lets it through.
        var changes = GraphQlRequest.MayChange(uri, body,
            headers.LastOrDefault(x => x.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)).Value);
        // A request that may change data shows its whole body, a local one too: the operation may sit past any cut.
        var details = Details(method, uri, headers, body, local && changes is null);
        var approved = changes is not null
            ? await approval.RequestApprovalAsync(Name, details + "\n\n" + Localization.Strings.HttpRequestMayChangeData(changes), ct,
                                                  subject: subject, forcePrompt: true)
            : local
            ? await approval.RequestScopedApprovalAsync(Name, details, subject, grantKey: $"{Name} {Origin(uri)}", ct)
            // Anywhere else: a card every time, unless the person's own rule names this whole subject — and even then nothing
            // a card could answer is filed under the tool's name (a rule changed between the two reads draws a card whose
            // grant, if any, names this origin, which no other call reads).
            : approval.Allows(Name, subject)
                ? await approval.RequestScopedApprovalAsync(Name, details, subject, grantKey: $"{Name} elsewhere {Origin(uri)}", ct)
                : await approval.RequestApprovalAsync(Name, details, ct, subject: subject, forcePrompt: true);
        if (!approved) return Execution.ToolFailure.CancelledByUser;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(timeout + wait));
        var notes = string.Join("\n\n", new[] { timeoutNote, waitNote }.Where(n => n is not null));
        try
        {
            // A GraphQL answer is read whole before any cut: its errors and its schema are judged on all of it.
            var graphQlAnswer = introspect || graphQl is not null || GraphQlRequest.IsGraphQlBody(body)
                ? (introspect ? GraphQlAnswer.Schema : GraphQlAnswer.Errors) : GraphQlAnswer.None;
            var report = await SendAsync(uri, method, headers, body, local, wait, graphQlAnswer, deadline.Token);
            return notes.Length == 0 ? report : notes + "\n\n" + report;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return $"The request to {Origin(uri)} did not finish within {timeout + wait} s: it was stopped.";
        }
        catch (HttpRequestException ex) when (ex.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionRefused })
        {
            return $"Nothing listens on {Origin(uri)} (connection refused). If the server is starting, call again with " +
                   "wait_seconds, or wait for its \"listening\" line: run_command action='wait'.";
        }
        catch (HttpRequestException ex)
        {
            return $"The request to {Origin(uri)} failed: {Diagnostics.RootMessage(ex)}";
        }
    }

    /// <summary>
    /// A request of the project's collection, replayed through the very checks, card and rules of one written by hand: it
    /// becomes the arguments the model would have written, and the call is made again with them.
    /// </summary>
    /// <remarks>⚠ What the variables hold (a token, a password) goes on the card the person reads, never back to the
    /// model: the reply names the request and its address with what looks like a credential masked, and a refusal of
    /// the address is masked the same way.</remarks>
    private async Task<string> ReplayAsync(JsonElement args, CancellationToken ct)
    {
        foreach (var own in new[] { "url", "method", "headers", "body", "graphql", "variables", "operation_name", "introspect" })
            if (args.Has(own)) return $"Error: give either collection and request, or url and its own '{own}' — not both.";
        var root = getWorkspaceRoot?.Invoke();
        var path = PathSanitizer.Sanitize(args.Str("collection"), root);
        PathSanitizer.AssertUnderRoot(path, root);
        var shown = string.IsNullOrEmpty(root) ? path : Path.GetRelativePath(root, path);
        var name = args.Str("request");
        if (string.IsNullOrWhiteSpace(name))
        {
            var (names, kind, environments, listError) = RequestCollection.List(path, root);
            if (listError is not null) return "Error: " + listError + ".";
            return $"{shown} ({kind}) holds {names.Count} request{(names.Count == 1 ? "" : "s")}: "
                 + string.Join(", ", names.Take(100).Select(n => "'" + n + "'")) + (names.Count > 100 ? ", …" : "") + "."
                 + (environments.Count > 0 ? $" Environments: {string.Join(", ", environments)}." : "")
                 + " Call again with request (and environment) to send one.";
        }
        var (request, error) = RequestCollection.Resolve(path, name, args.Str("environment"), root);
        if (request is null) return "Error: " + error;

        var replayed = new Dictionary<string, object?> { ["url"] = request.Url, ["method"] = request.Method };
        if (request.Headers.Count > 0)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in request.Headers) headers[key] = value;   // a name given twice: the last one, as a client sends one
            replayed["headers"] = headers;
        }
        if (request.Body is not null) replayed["body"] = request.Body;
        foreach (var kept in new[] { "wait_seconds", "timeout_seconds" })
            if (args.Has(kept)) replayed[kept] = args.Int(kept, 0);
        // Everything that goes back — the answer, its headers and body, a refusal that quotes a header — has the values the
        // environment gave masked by name: an echo endpoint or a .NET message carries them as they were sent.
        var answer = request.Mask(await ExecuteAsync(JsonSerializer.SerializeToElement(replayed), ct));
        if (answer.StartsWith("Error:", StringComparison.Ordinal)) answer = SecretRedactor.Redact(answer);
        var said = $"Replayed '{request.Name}' from {shown}: {request.Method} {SecretRedactor.Redact(request.Mask(request.Url))}"
                 + (request.Notes.Count > 0 ? " (" + string.Join("; ", request.Notes) + ")" : "") + "."
                 + (request.Secrets.Count > 0 ? " The values its environment gave are shown by their {{name}}." : "");
        return said + "\n\n" + answer;
    }

    /// <summary>How an answer is read as GraphQL: not at all, its errors said above it (a server sends them under HTTP 200),
    /// or an introspection read as its schema rather than its raw JSON.</summary>
    private enum GraphQlAnswer { None, Errors, Schema }

    private static async Task<string> SendAsync(Uri uri, string method, List<KeyValuePair<string, string>> headers, string? body,
                                                bool local, int wait, GraphQlAnswer graphQl, CancellationToken ct)
    {
        var waitUntil = DateTime.UtcNow.AddSeconds(wait);
        while (true)
        {
            using var handler = HandlerFor(local);
            using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            var (built, refused) = RequestOf(uri, method, headers, body);
            if (refused is not null) return refused;
            using var request = built!;
            try
            {
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                return await ReportAsync(response, graphQl, ct);
            }
            catch (HttpRequestException ex) when (ex.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionRefused }
                                                  && DateTime.UtcNow < waitUntil)
            {
                await Task.Delay(RetryDelay, ct);
            }
        }
    }

    /// <summary>How long a refused connection waits before the next try, while the app being called starts.</summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>The handler of one call: no redirect followed, no cookie, no credential; a proxy only off the loopback, and
    /// on the loopback a connection only to a loopback address.</summary>
    /// <remarks>A handler per call: a pooled connection would skip the loopback check of the connection.</remarks>
    private static SocketsHttpHandler HandlerFor(bool local)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect      = false,
            UseCookies             = false,
            Credentials            = null,
            AutomaticDecompression = DecompressionMethods.All,
            // ⚠ No proxy on the loopback: through one, the loopback check would judge the proxy, not the target.
            UseProxy               = !local,
            Proxy                  = NetworkProxy.Shared,
            PooledConnectionLifetime = TimeSpan.Zero,
        };
        if (local) handler.ConnectCallback = (c, token) => ConnectLoopbackAsync(c.DnsEndPoint.Host, c.DnsEndPoint.Port, token);
        return handler;
    }

    /// <summary>The request: its body typed as JSON or text, its headers added with validation; or why a header could not be
    /// set.</summary>
    private static (HttpRequestMessage? Request, string? Refusal) RequestOf(Uri uri, string method, List<KeyValuePair<string, string>> headers,
                                                                             string? body)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), uri);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(body.TrimStart().StartsWith('{') || body.TrimStart().StartsWith('[')
                ? "application/json" : "text/plain") { CharSet = "utf-8" };
        }
        foreach (var (name, value) in headers)
        {
            try
            {
                // Validating adds: a CR or LF in a value is refused, never a second header smuggled in.
                if (request.Content is not null && name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
                {
                    request.Content.Headers.Remove(name);
                    request.Content.Headers.Add(name, value);
                }
                else request.Headers.Add(name, value);
            }
            catch (Exception ex) when (ex is FormatException or InvalidOperationException)
            {
                request.Dispose();
                return (null, $"Error: the header '{name}' could not be set ({ex.Message}).");
            }
        }
        return (request, null);
    }

    /// <summary>Connects only to a loopback address the name resolves to: a name rewritten elsewhere (hosts file, DNS) is
    /// never reached as "local".</summary>
    internal static async ValueTask<Stream> ConnectLoopbackAsync(string name, int port, CancellationToken ct)
    {
        var host = name.Trim('[', ']');
        var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, ct);
        var loopback = addresses.Select(a => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a).Where(IPAddress.IsLoopback).ToList();
        if (loopback.Count == 0)
            throw new HttpRequestException($"'{host}' does not lead to this machine's loopback address: not called as a local address.");
        SocketException? last = null;
        foreach (var address in loopback)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException ex) { socket.Dispose(); last = ex; }
            catch { socket.Dispose(); throw; }
        }
        // The socket error itself: the client wraps it, and "connection refused" is read on it (wait_seconds, the message).
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(last!);
        throw last;
    }

    private static async Task<string> ReportAsync(HttpResponseMessage response, GraphQlAnswer graphQl, CancellationToken ct)
    {
        var sb = new StringBuilder($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}\n");
        var all = response.Headers.Concat(response.Content.Headers).ToList();
        foreach (var (name, values) in all.Take(MaxHeaders))
        {
            var value = string.Join(", ", values);
            sb.Append(name).Append(": ").Append(value.Length > MaxHeaderChars ? value[..MaxHeaderChars] + "…" : value).Append('\n');
        }
        if (all.Count > MaxHeaders) sb.Append($"({all.Count - MaxHeaders} more headers not shown)\n");
        if ((int)response.StatusCode is >= 300 and < 400)
            sb.Append("(Redirect not followed: request the Location above if it is the one wanted.)\n");

        // Bounded by the bytes READ, after decompression: a Content-Length that lies, or a compressed bomb, stops at the cap.
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[MaxBodyBytes + 1];
        var read = 0;
        while (read < buffer.Length && await stream.ReadAsync(buffer.AsMemory(read), ct) is var n and > 0) read += n;
        var cut = read > MaxBodyBytes;
        var bytes = buffer.AsSpan(0, Math.Min(read, MaxBodyBytes));
        sb.Append('\n');
        if (bytes.Length == 0) return sb.Append("(empty body)").ToString();
        var charset = response.Content.Headers.ContentType?.CharSet?.Trim('"');
        if (!(charset?.StartsWith("utf-16", StringComparison.OrdinalIgnoreCase) == true
              || charset?.StartsWith("utf-32", StringComparison.OrdinalIgnoreCase) == true) && TextFileEncoding.IsBinary(bytes))
            return sb.Append($"(binary body: {(cut ? "more than " : "")}{bytes.Length} bytes, {response.Content.Headers.ContentType?.MediaType ?? "no content type"})").ToString();

        var text = WebPage.Decode(bytes.ToArray(), charset);
        if (graphQl != GraphQlAnswer.None)
        {
            // Judged on the whole answer read, before the cut below: a real schema is far past any page.
            if (GraphQlRequest.Errors(text) is { } errors) sb.Insert(sb.Length - 1, errors + "\n");
            if (graphQl == GraphQlAnswer.Schema)
            {
                if (GraphQlRequest.Schema(text, Agent.AgentOrchestrator.MaxToolResultCharsInContext - sb.Length - 200) is { } schema)
                    return sb.Append(schema).ToString();
                if (!cut) sb.Insert(sb.Length - 1, "(The answer holds no schema: its errors, if any, are above.)\n");
                else sb.Insert(sb.Length - 1, $"(The answer is larger than the {MaxBodyBytes / (1024 * 1024)} MB read: its schema could not be read whole.)\n");
            }
        }
        var max = Agent.AgentOrchestrator.MaxToolResultCharsInContext - sb.Length - 200;
        if (text.Length > max)
            return sb.Append(text.AsSpan(0, Math.Max(0, max))).Append($"\n\n[body cut: {max} of {text.Length} characters shown{(cut ? $", and only the first {MaxBodyBytes / (1024 * 1024)} MB were read" : "")} — ask the API for less (a filter, a page)]").ToString();
        return sb.Append(text).Append(cut ? $"\n\n[body cut: only the first {MaxBodyBytes / (1024 * 1024)} MB were read]" : "").ToString();
    }

    /// <summary>A loopback address as written: <c>localhost</c>, <c>*.localhost</c>, 127.0.0.0/8, <c>::1</c>. The connection
    /// itself is held to the loopback (<see cref="ConnectLoopbackAsync"/>), whatever the name resolves to.</summary>
    internal static bool IsLoopback(Uri uri)
    {
        var host = Host(uri);
        if (host == "localhost" || host.EndsWith(".localhost", StringComparison.Ordinal)) return true;
        return IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip);
    }

    // 0.0.0.0 and :: reach the loopback on Linux and macOS while reading as another address.
    private static bool IsUnspecified(Uri uri) =>
        IPAddress.TryParse(Host(uri).Trim('[', ']'), out var ip)
        && ((ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip) is var a && (a.Equals(IPAddress.Any) || a.Equals(IPAddress.IPv6Any)
            || (a.AddressFamily == AddressFamily.InterNetwork && a.GetAddressBytes()[0] == 0)));

    private static string Host(Uri uri) =>
        (uri.HostNameType == UriHostNameType.IPv6 ? uri.Host : uri.IdnHost).TrimEnd('.').ToLowerInvariant();

    /// <summary><c>scheme://host:port</c>, port explicit, host canonical: what an "Always" opens.</summary>
    internal static string Origin(Uri uri) => $"{uri.Scheme}://{Host(uri)}:{uri.Port}";

    /// <summary>What the permission rules read: <c>METHOD scheme://host:port</c> — no path, query, header or body, so a rule
    /// is about where and how, and nothing the model writes into the request can make it match.</summary>
    internal static string Subject(string method, Uri uri) => $"{method} {Origin(uri)}";

    /// <summary>The most body a request to another machine carries: all of it is on its card.</summary>
    internal const int MaxCardBody = 16_000;

    /// <remarks>A local body is cut on its card past 4 000 characters (an "Always" for that origin shows none of the next
    /// ones anyway); a body leaving the machine never is (<see cref="MaxCardBody"/>).</remarks>
    private static string Details(string method, Uri uri, List<KeyValuePair<string, string>> headers, string? body, bool local)
    {
        var sb = new StringBuilder($"{method} {uri.AbsoluteUri}");
        foreach (var (name, value) in headers) sb.Append('\n').Append(name).Append(": ").Append(value);
        if (body is not null)
            sb.Append("\n\n").Append(local && body.Length > 4000 ? body[..4000] + $"\n… ({body.Length - 4000} more characters)" : body);
        return sb.ToString();
    }
}
