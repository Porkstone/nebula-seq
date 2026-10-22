using System.Text.Json;

namespace Nebula.Seq;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, Options)
        ?? throw new InvalidDataException("Empty JSON result.");
}

public sealed record ErrorEvent(string Id, string Level, string Fingerprint, string Details, string? Url);
public sealed record Issue(string Key, string Summary, string Description, string Status);
public sealed record SearchPage(IReadOnlyList<Issue> Issues, string? NextPageToken);
public sealed record Decision(string Action, string? ExistingIssueKey, string Summary, string Analysis);
public sealed record SearchResult(Decision Decision, SearchEvidence Evidence);
public sealed record ProcessingResult(string Outcome, string? IssueKey, string Fingerprint);

public sealed record SearchEvidence
{
    public int SuccessfulSearches { get; set; }
    public bool HadError { get; set; }
    public HashSet<string> IssueKeys { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string?> NextTokens { get; init; } = new();
    public bool Complete => SuccessfulSearches > 0 && !HadError && NextTokens.Count > 0 && NextTokens.Values.All(x => x is null);
}

public interface IJiraClient
{
    Task<SearchPage> SearchAsync(string terms, string? pageToken, CancellationToken ct);
    Task<IReadOnlyList<Issue>> FindFingerprintAsync(string fingerprint, CancellationToken ct);
    Task<string> CreateAsync(ErrorEvent evt, Decision decision, CancellationToken ct);
}

public interface ICodexSearch
{
    Task<SearchResult> SearchAsync(ErrorEvent evt, CancellationToken ct);
}
