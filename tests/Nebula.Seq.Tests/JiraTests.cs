using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Nebula.Seq.Tests;

public class JiraTests
{
    private sealed class Handler(Func<HttpRequestMessage, int, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => handle(request, ++Calls);
    }
    private static HttpResponseMessage Response(string json, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task UsesEnhancedSearchAndRestrictsProjectAndType()
    {
        var handler = new Handler(async (request, _) => {
            Assert.Equal("/rest/api/3/search/jql", request.RequestUri!.AbsolutePath);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("Basic", request.Headers.Authorization!.Scheme);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var jql = body.RootElement.GetProperty("jql").GetString()!;
            Assert.Contains("project = \"NEB\" AND issuetype = \"Bug\" AND (", jql);
            Assert.DoesNotContain("status", jql);
            Assert.DoesNotContain("project = EVIL", jql);
            Assert.Equal("page-two", body.RootElement.GetProperty("nextPageToken").GetString());
            return Response("""{"issues":[{"key":"NEB-4","fields":{"summary":"Missing order","description":{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"Observed failure"}]}]},"status":{"name":"Closed"}}}],"isLast":false,"nextPageToken":"page-three"}""");
        });
        using var http = new HttpClient(handler);
        var page = await new JiraClient(http, Samples.Settings).SearchAsync("order\" OR project = EVIL", "page-two", default);
        Assert.Equal("page-three", page.NextPageToken);
        Assert.Contains("Observed failure", page.Issues[0].Description);
        Assert.Equal("Closed", page.Issues[0].Status);
    }

    [Fact]
    public async Task CreationEnforcesLabelsProjectAdfAndSummaryLimit()
    {
        var handler = new Handler(async (request, _) => {
            Assert.Equal("/rest/api/3/issue", request.RequestUri!.AbsolutePath);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var fields = body.RootElement.GetProperty("fields");
            Assert.Equal("NEB", fields.GetProperty("project").GetProperty("key").GetString());
            Assert.Equal("Bug", fields.GetProperty("issuetype").GetProperty("name").GetString());
            Assert.Equal(255, fields.GetProperty("summary").GetString()!.Length);
            var labels = fields.GetProperty("labels").EnumerateArray().Select(x => x.GetString()).ToArray();
            Assert.Contains("ready-4-robot", labels); Assert.Contains(Samples.Event.Fingerprint, labels);
            var adf = fields.GetProperty("description");
            Assert.Equal("doc", adf.GetProperty("type").GetString()); Assert.Equal(1, adf.GetProperty("version").GetInt32());
            Assert.Contains("InvalidOperationException", adf.ToString());
            return Response("""{"key":"NEB-7"}""", HttpStatusCode.Created);
        });
        using var http = new HttpClient(handler);
        Assert.Equal("NEB-7", await new JiraClient(http, Samples.Settings).CreateAsync(Samples.Event, Samples.NewResult.Decision with { Summary = new string('x', 400) }, default));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task SearchRetriesRateLimit()
    {
        var handler = new Handler((_, call) => {
            var result = call == 1 ? Response("{}", HttpStatusCode.TooManyRequests) : Response("""{"issues":[],"isLast":true}""");
            result.Headers.RetryAfter = new(System.TimeSpan.Zero);
            return Task.FromResult(result);
        });
        using var http = new HttpClient(handler);
        Assert.Empty((await new JiraClient(http, Samples.Settings).SearchAsync("missing order", null, default)).Issues);
        Assert.Equal(2, handler.Calls);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task NeverRetriesCreation(int status)
    {
        var handler = new Handler((_, _) => Task.FromResult(Response("sensitive error body", (HttpStatusCode)status)));
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => new JiraClient(http, Samples.Settings).CreateAsync(Samples.Event, Samples.NewResult.Decision, default));
        Assert.DoesNotContain("sensitive", error.Message);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task RejectsIncompleteSearchWithoutCursor()
    {
        var handler = new Handler((_, _) => Task.FromResult(Response("""{"issues":[],"isLast":false}""")));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => new JiraClient(http, Samples.Settings).SearchAsync("missing order", null, default));
    }
}
