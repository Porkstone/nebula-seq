using System.Text.Json;
using Xunit;

namespace Nebula.Seq.Tests;

public class McpTests
{
    private static string Call(int id, string query, string? token = null) => Json.Write(new {
        jsonrpc = "2.0", id, method = "tools/call", @params = new { name = "search_jira", arguments = new { query, pageToken = token } }
    });

    [Fact]
    public async Task ImplementsStdioHandshakeAndAuditsRealSearch()
    {
        using var dir = new TestDirectory(); var jira = new FakeJira();
        var path = Path.Combine(dir.Path, "evidence.json");
        var input = """
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}
            {"jsonrpc":"2.0","method":"notifications/initialized"}
            {"jsonrpc":"2.0","id":2,"method":"tools/list"}
            """ + "\n" + Call(3, "missing order") + "\n";
        var output = new StringWriter();
        await new JiraMcpServer(jira, path).RunAsync(new StringReader(input), output, default);
        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        using var initialize = JsonDocument.Parse(lines[0]);
        Assert.Equal("2025-06-18", initialize.RootElement.GetProperty("result").GetProperty("protocolVersion").GetString());
        using var tools = JsonDocument.Parse(lines[1]);
        var list = tools.RootElement.GetProperty("result").GetProperty("tools");
        Assert.Single(list.EnumerateArray());
        Assert.True(list[0].GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
        Assert.True(Json.Read<SearchEvidence>(File.ReadAllText(path)).Complete);
        Assert.Equal(1, jira.Searches);
    }

    [Fact]
    public async Task UnreadPaginationAndToolErrorsBlockNewIssue()
    {
        using var dir = new TestDirectory(); var jira = new FakeJira { Page = new([], "next") };
        var path = Path.Combine(dir.Path, "evidence.json"); var server = new JiraMcpServer(jira, path);
        await server.RunAsync(new StringReader(Call(1, "order") + "\n"), new StringWriter(), default);
        Assert.False(Json.Read<SearchEvidence>(File.ReadAllText(path)).Complete);
        jira.Page = new([], null);
        await server.RunAsync(new StringReader(Call(2, "order", "next") + "\n"), new StringWriter(), default);
        Assert.True(Json.Read<SearchEvidence>(File.ReadAllText(path)).Complete);
        jira.SearchError = new HttpRequestException("Token: sensitive");
        var output = new StringWriter();
        await server.RunAsync(new StringReader(Call(3, "exception") + "\n"), output, default);
        Assert.DoesNotContain("sensitive", output.ToString());
        Assert.True(Json.Read<SearchEvidence>(File.ReadAllText(path)).HadError);
        Assert.False(Json.Read<SearchEvidence>(File.ReadAllText(path)).Complete);
    }

    [Fact]
    public async Task InvalidContinuationTokenCannotProduceSearchEvidence()
    {
        using var dir = new TestDirectory(); var jira = new FakeJira(); var path = Path.Combine(dir.Path, "evidence.json");
        await new JiraMcpServer(jira, path).RunAsync(new StringReader(Call(1, "order", "invented") + "\n"), new StringWriter(), default);
        Assert.Equal(0, jira.Searches);
        Assert.False(Json.Read<SearchEvidence>(File.ReadAllText(path)).Complete);
    }
}
