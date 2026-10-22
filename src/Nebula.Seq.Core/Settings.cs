using System.Text.RegularExpressions;

namespace Nebula.Seq;

public sealed record Settings
{
    public required string OpenRouterApiKey { get; init; }
    public required string Model { get; init; }
    public required string JiraUrl { get; init; }
    public required string JiraEmail { get; init; }
    public required string JiraApiToken { get; init; }
    public required string JiraProject { get; init; }
    public string JiraIssueType { get; init; } = "Bug";
    public string CodexExecutable { get; init; } = "codex";
    public string DotnetExecutable { get; init; } = "dotnet";
    public bool DryRun { get; init; } = true;
    public int TimeoutSeconds { get; init; } = 180;
    public int MaxEventChars { get; init; } = 24000;
    public string RedactedProperties { get; init; } = "password,secret,token,authorization,cookie,apikey,api_key,connectionstring";
    public string? SeqUrl { get; init; }

    public void Validate()
    {
        foreach (var (name, value) in new[] {
            (nameof(OpenRouterApiKey), OpenRouterApiKey), (nameof(Model), Model),
            (nameof(JiraEmail), JiraEmail), (nameof(JiraApiToken), JiraApiToken),
            (nameof(JiraIssueType), JiraIssueType), (nameof(CodexExecutable), CodexExecutable),
            (nameof(DotnetExecutable), DotnetExecutable) })
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"{name} is required.");
        ValidateUrl(JiraUrl, nameof(JiraUrl));
        if (!string.IsNullOrWhiteSpace(SeqUrl)) ValidateUrl(SeqUrl, nameof(SeqUrl));
        if (!Regex.IsMatch(JiraProject ?? "", "^[A-Z][A-Z0-9_]*$"))
            throw new ArgumentException("JiraProject must be a Jira project key, e.g. NEB.");
        if (TimeoutSeconds is < 10 or > 1800) throw new ArgumentException("TimeoutSeconds must be 10–1800.");
        if (MaxEventChars is < 2000 or > 60000) throw new ArgumentException("MaxEventChars must be 2000–60000.");
        if (CodexExecutable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
            CodexExecutable.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase) ||
            CodexExecutable.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Use the native codex executable, not a shell wrapper (.cmd/.ps1/.bat).");
    }

    private static void ValidateUrl(string url, string name)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException($"{name} must be an absolute HTTPS URL without credentials, query, or fragment.");
    }
}
