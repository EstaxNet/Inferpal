using System.IO;
using System.Text.Json;
using Inferpal.Services.Documents;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// An API description reads as what calling it needs (pari 138): an OpenAPI or Swagger specification as its operations
/// then its schemas, a browser's network capture (.har) as its requests, the failed ones first — and both stay text files,
/// read as lines on request. The two OpenAPI fixtures are the OpenAPI Initiative's own examples (Apache 2.0).
/// </summary>
public class ApiTextTests
{
    private static string Fixture(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "Inferpal.Tests", "Fixtures", "api", name);
    }

    [Fact]
    public void AnOpenApiSpecInYaml_ReadsAsItsOperations_ThenItsSchemas()
    {
        var text = ApiText.Summarize("petstore-expanded.yaml", File.ReadAllText(Fixture("petstore-expanded.yaml")), 60_000)!;

        Assert.StartsWith("[API specification petstore-expanded.yaml (OpenAPI 3.0.0): Swagger Petstore — 4 operation(s), 3 schema(s)", text);
        Assert.Contains("read its text with start_line=1", text);
        Assert.Contains("Servers: https://petstore.swagger.io/v2", text);
        Assert.Contains("GET /pets — findPets", text.Replace("Returns all pets from the system that the user has access to", "findPets"));
        Assert.Contains("tags (query, string[])", text);
        Assert.Contains("POST /pets", text);
        Assert.Contains("body: application/json NewPet", text);
        Assert.Contains("answers: 200 Pet, default Error", text);
        Assert.Contains("DELETE /pets/{id}", text);
        Assert.Contains("id (path, required, integer(int64))", text);
        Assert.Contains("## Schemas", text);
        Assert.Contains("NewPet: name* string, tag string", text);
    }

    [Fact]
    public void TheSmallerExample_ReadsToo()
    {
        var text = ApiText.Summarize("petstore.yaml", File.ReadAllText(Fixture("petstore.yaml")), 60_000)!;
        Assert.Contains("operation(s)", text);
        Assert.Contains("GET /pets/{petId}", text);
    }

    [Fact]
    public void ASwagger2SpecInJson_ReadsItsHostAndBodyParameter()
    {
        const string swagger = """
            { "swagger": "2.0", "info": { "title": "Orders" }, "host": "api.local", "basePath": "/v1",
              "paths": { "/orders": { "post": { "operationId": "createOrder",
                "parameters": [ { "name": "order", "in": "body", "required": true, "schema": { "$ref": "#/definitions/Order" } } ],
                "responses": { "201": { "description": "made", "schema": { "$ref": "#/definitions/Order" } } } } } },
              "definitions": { "Order": { "required": ["total"], "properties": { "total": { "type": "number" } } } } }
            """;
        var text = ApiText.Summarize("swagger.json", swagger, 60_000)!;
        Assert.Contains("Servers: api.local/v1", text);
        Assert.Contains("POST /orders — createOrder", text);
        Assert.Contains("order (body, required, Order)", text);
        Assert.Contains("answers: 201 Order", text);
        Assert.Contains("Order: total* number", text);
    }

    [Theory]
    [InlineData("package.json", """{ "name": "shop", "scripts": { "test": "jest" } }""")]
    [InlineData("config.yaml", "models:\n  - name: qwen\n")]
    [InlineData("notes.txt", "openapi: 3.0.0")]
    public void AFileThatIsNoApiDescription_IsReadAsItIs(string name, string text) =>
        Assert.Null(ApiText.Summarize(name, text, 60_000));

    private const string Har = """
        { "log": { "version": "1.2", "entries": [
          { "startedDateTime": "2026-11-21T15:20:00Z", "time": 41.6,
            "request": { "method": "GET", "url": "http://localhost:5173/api/cart",
                         "headers": [ { "name": "Authorization", "value": "Bearer SECRET-TOKEN" } ],
                         "cookies": [ { "name": "session", "value": "SECRET-COOKIE" } ] },
            "response": { "status": 200, "statusText": "OK", "content": { "mimeType": "application/json", "text": "{\"items\":2}" } } },
          { "startedDateTime": "2026-11-21T15:20:01Z", "time": 120,
            "request": { "method": "POST", "url": "http://localhost:5173/api/checkout", "postData": { "text": "{\"card\":\"4242\"}" } },
            "response": { "status": 500, "statusText": "Internal Server Error", "content": { "mimeType": "text/plain", "text": "NullReferenceException at Checkout.Pay" } } },
          { "startedDateTime": "2026-11-21T15:20:02Z", "time": 5,
            "request": { "method": "GET", "url": "http://localhost:5173/logo.png" },
            "response": { "status": 200, "statusText": "OK", "content": { "mimeType": "image/png", "text": "iVBORw0KGgo=", "encoding": "base64" } } }
        ] } }
        """;

    [Fact]
    public void ANetworkCapture_ReadsAsItsRequests_TheFailedOnesFirst_WithoutHeadersOrCookies()
    {
        var text = ApiText.Summarize("session.har", Har, 60_000)!;

        Assert.StartsWith("[Network capture session.har (HAR): 3 request(s), 1 failed or unanswered.", text);
        Assert.Contains("headers and cookies are not part of it", text);
        var failed = text.IndexOf("2. POST http://localhost:5173/api/checkout → 500 Internal Server Error · 120 ms", StringComparison.Ordinal);
        var ok = text.IndexOf("1. GET http://localhost:5173/api/cart → 200 OK · 42 ms · application/json", StringComparison.Ordinal);
        Assert.True(failed > 0 && ok > failed, text);
        Assert.Contains("sent: {\"card\":\"4242\"}", text);
        Assert.Contains("got: NullReferenceException at Checkout.Pay", text);
        Assert.Contains("got: (encoded content, not shown)", text);   // a picture's base64 is never the model's text
        Assert.DoesNotContain("SECRET-TOKEN", text);
        Assert.DoesNotContain("SECRET-COOKIE", text);
    }

    [Fact]
    public void ALongList_IsCutBetweenTwoEntries_AndTheRestCounted()
    {
        var entries = string.Join(",", Enumerable.Range(1, 400).Select(i =>
            $$"""{ "time": 1, "request": { "method": "GET", "url": "http://localhost/api/{{i}}" }, "response": { "status": 200, "statusText": "OK" } }"""));
        var text = ApiText.Summarize("big.har", $$"""{ "log": { "entries": [ {{entries}} ] } }""", 4_000)!;
        Assert.True(text.Length < 4_500, text.Length.ToString());
        Assert.Matches(@"\n\d+ more request\(s\) not shown here: search_in_files finds an address in the JSON\.$", text);
    }

    [Fact]
    public async Task ReadFile_ShowsTheOperationsFirst_AndTheLinesOnRequest()
    {
        var dir = Directory.CreateTempSubdirectory("inferpal-api-test-").FullName;
        try
        {
            File.Copy(Fixture("petstore-expanded.yaml"), Path.Combine(dir, "openapi.yaml"));
            var tool = new ReadFileTool(() => dir);
            var first = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { path = "openapi.yaml" }), CancellationToken.None);
            Assert.StartsWith("[API specification openapi.yaml", first);
            // REFERENCE ARM: asked for lines, the file is its text — what an edit quotes.
            var lines = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { path = "openapi.yaml", start_line = 1, end_line = 2 }), CancellationToken.None);
            Assert.Contains("openapi: 3.0.0", lines);
            Assert.DoesNotContain("[API specification", lines);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* cleanup */ }
        }
    }
    [Fact]
    public void ACapture_IsASummaryNotAProtection_AndMasksWhatLooksLikeACredential()
    {
        const string har = """
            { "log": { "entries": [
              { "time": 10, "request": { "method": "GET", "url": "http://localhost/api?access_token=eyJhbGciOiJIUzI1NiJ9.SECRET" },
                "response": { "status": 200, "statusText": "OK" } },
              { "time": 10, "request": { "method": "POST", "url": "http://localhost/login", "postData": { "text": "user=a&password=hunter2" } },
                "response": { "status": 200, "statusText": "OK", "content": { "text": "{\"access_token\":\"eyJTOKEN\",\"ok\":true}" } } }
            ] } }
            """;
        var text = ApiText.Summarize("login.har", har, 60_000)!;

        Assert.Contains("A summary, not a protection", text);       // never said to make a capture safe
        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiJ9.SECRET", text);
        Assert.DoesNotContain("hunter2", text);
        Assert.DoesNotContain("eyJTOKEN", text);
        Assert.Contains("http://localhost/api?access_token=", text);  // the shape stays readable
    }

    [Fact]
    public void ARequestWithoutAStatusAServerGives_IsListedWithTheUnanswered()
    {
        const string har = """{ "log": { "entries": [ { "request": { "method": "GET", "url": "http://localhost/aborted" }, "response": { "status": -1 } } ] } }""";
        var text = ApiText.Summarize("aborted.har", har, 60_000)!;
        Assert.Contains("1 request(s), 1 failed or unanswered", text);
        Assert.Contains("1. GET http://localhost/aborted → no answer", text);
    }

    [Fact]
    public void AReferencedParameterOrBody_IsReadWhereItIsDefined_AndACompositionByItsParts()
    {
        const string spec = """
            { "openapi": "3.1.0", "info": { "title": "Items" },
              "paths": { "/items": { "get": { "parameters": [ { "$ref": "#/components/parameters/Limit" } ],
                                              "responses": { "200": { "$ref": "#/components/responses/Page" } } },
                                     "post": { "requestBody": { "$ref": "#/components/requestBodies/NewItem" },
                                               "responses": { "201": { "description": "made" } } } },
                         "/users": { "$ref": "./users.yaml" } },
              "components": {
                "parameters": { "Limit": { "name": "limit", "in": "query", "schema": { "type": "integer" } } },
                "responses": { "Page": { "content": { "application/json": { "schema": { "oneOf": [ { "$ref": "#/components/schemas/A" }, { "$ref": "#/components/schemas/B" } ] } } } } },
                "requestBodies": { "NewItem": { "content": { "application/json": { "schema": { "$ref": "#/components/schemas/A" } } } } },
                "schemas": { "A": { "type": "object" }, "B": { "type": "object" } } } }
            """;
        var text = ApiText.Summarize("items.json", spec, 60_000)!;
        Assert.Contains("limit (query, integer)", text);
        Assert.Contains("answers: 200 oneOf(A|B)", text);
        Assert.Contains("body: application/json A", text);
        Assert.Contains("1 path(s) are described in other files ($ref) and not read here", text);
    }

    [Theory]
    [InlineData("""{ "openapi": "3.0.0", "info": "My API", "paths": { "/x": { "get": { "responses": { "x-note": "text" } } } } }""")]
    [InlineData("""{ "openapi": "3.0.0", "paths": { "/x": { "get": { "requestBody": { "content": [ 1, 2 ] } } } } }""")]
    [InlineData("""{ "openapi": "3.0.0", "openapi": "3.1.0" }""")]
    public void ASpecOfAnUnexpectedShape_IsReadAsItsLines_NeverAFailedRead(string spec)
    {
        var text = ApiText.Summarize("odd.json", spec, 60_000);   // never throws
        Assert.True(text is null || text.StartsWith("[API specification", StringComparison.Ordinal));
    }

    [Fact]
    public void AYamlBillionLaughs_IsRefused_NotExpanded()
    {
        var yaml = new System.Text.StringBuilder("openapi: 3.0.0\na0: &a0 [x, x, x, x, x, x, x, x, x, x]\n");
        for (var level = 1; level < 9; level++)
            yaml.Append($"a{level}: &a{level} [{string.Join(", ", Enumerable.Repeat($"*a{level - 1}", 10))}]\n");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Throws<FormatException>(() => Inferpal.Services.Mcp.MiniYaml.Parse(yaml.ToString()));
        Assert.Null(ApiText.Summarize("openapi.yaml", yaml.ToString(), 60_000));   // read as its lines instead
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), "the aliases were expanded");
    }

    [Fact]
    public void OperationsCutShort_SayTheSchemasAreNotShownEither()
    {
        var paths = string.Join(",", Enumerable.Range(1, 300).Select(i => $$"""
            "/r{{i}}": { "get": { "summary": "route number {{i}} with a long enough summary", "responses": { "200": { "description": "ok" } } } }
            """));
        var text = ApiText.Summarize("big.json", $$"""{ "openapi": "3.0.0", "paths": { {{paths}} }, "components": { "schemas": { "A": { "type": "string" } } } }""", 4_000)!;
        Assert.Contains("more operation(s) not shown here", text);
        Assert.Contains("Its 1 schema(s) are not shown either", text);
    }

    [Fact]
    public void AHugeAddress_IsCutToOneLine_NeverPastTheBudget()
    {
        var url = "http://localhost/?q=" + new string('a', 200_000);
        var text = ApiText.Summarize("long.har", $$"""{ "log": { "entries": [ { "request": { "method": "GET", "url": "{{url}}" }, "response": { "status": 500 } } ] } }""", 8_000)!;
        Assert.True(text.Length < 8_000, text.Length.ToString());
    }
}
