using Nebula.Seq;

namespace Nebula.Seq.Tests;

public sealed class TestDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nebula-tests", Guid.NewGuid().ToString("N"));
    public TestDirectory() => Directory.CreateDirectory(Path);
    public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
}

internal static class Samples
{
    public static readonly Settings Settings = new() {
        OpenRouterApiKey = "dummy-openrouter", Model = "test/model", JiraUrl = "https://example.atlassian.net",
        JiraEmail = "robot@example.test", JiraApiToken = "dummy-jira", JiraProject = "NEB", DryRun = false
    };
    public const string Clef = """
        {"@t":"2026-10-22T12:00:00Z","@i":"a1234567","@seqid":"event-123","@l":"Error","@mt":"Order {OrderId} failed","@m":"Order 42 failed","@x":"System.InvalidOperationException: missing order\n at Orders.Run()","Application":"Orders","SourceContext":"Orders.Worker","OrderId":42}
        """;
    public static ErrorEvent Event => EventReader.Read(Clef, Settings)!;
    public static SearchResult NewResult => new(new Decision("new", null, "Order processing fails with missing order", "Observed: InvalidOperationException in Orders.Run. Investigate missing order handling."),
        new SearchEvidence { SuccessfulSearches = 1, NextTokens = new() { ["order missing"] = null } });
}

internal sealed class FakeJira : IJiraClient
{
    public int Creates;
    public int Searches;
    public Exception? CreateError;
    public Exception? SearchError;
    public List<Issue> Exact { get; } = [];
    public SearchPage Page { get; set; } = new([], null);
    public async Task<string> CreateAsync(ErrorEvent evt, Decision decision, CancellationToken ct)
    {
        Interlocked.Increment(ref Creates);
        await Task.Delay(10, ct);
        if (CreateError is not null) throw CreateError;
        return "NEB-7";
    }
    public Task<IReadOnlyList<Issue>> FindFingerprintAsync(string fingerprint, CancellationToken ct)
    {
        if (SearchError is not null) throw SearchError;
        return Task.FromResult<IReadOnlyList<Issue>>(Exact);
    }
    public Task<SearchPage> SearchAsync(string terms, string? pageToken, CancellationToken ct)
    {
        Searches++;
        if (SearchError is not null) throw SearchError;
        return Task.FromResult(Page);
    }
}

internal sealed class FakeCodex : ICodexSearch
{
    public int Calls;
    public SearchResult Result { get; set; } = Samples.NewResult;
    public Task<SearchResult> SearchAsync(ErrorEvent evt, CancellationToken ct) { Calls++; return Task.FromResult(Result); }
}
