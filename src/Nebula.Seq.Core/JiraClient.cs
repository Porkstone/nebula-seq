using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Nebula.Seq;

public sealed class JiraRejectedException(int status) : Exception($"Jira rejected ticket creation (HTTP {status}).");

public sealed partial class JiraClient(HttpClient http, Settings settings) : IJiraClient
{
    public async Task<SearchPage> SearchAsync(string terms, string? pageToken, CancellationToken ct)
    {
        var words = Words().Matches(terms).Select(m => m.Value).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToArray();
        if (words.Length == 0) throw new ArgumentException("Search needs at least one word of two or more characters.");
        var text = string.Join(" OR ", words.Select(w => $"text ~ {Quote(w)}"));
        return await SearchJqlAsync($"{Scope} AND ({text}) ORDER BY updated DESC", pageToken, ct);
    }

    public async Task<IReadOnlyList<Issue>> FindFingerprintAsync(string fingerprint, CancellationToken ct)
    {
        if (!FingerprintPattern().IsMatch(fingerprint)) throw new ArgumentException("Invalid fingerprint.");
        return (await SearchJqlAsync($"{Scope} AND labels = {Quote(fingerprint)}", null, ct)).Issues;
    }

    private string Scope => $"project = {Quote(settings.JiraProject)} AND issuetype = {Quote(settings.JiraIssueType)}";
    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private async Task<SearchPage> SearchJqlAsync(string jql, string? pageToken, CancellationToken ct)
    {
        var payload = new { jql, maxResults = 50, fields = new[] { "summary", "description", "status" }, nextPageToken = pageToken };
        using var response = await ReadRequestAsync(() => Request(HttpMethod.Post, "rest/api/3/search/jql", payload), ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = doc.RootElement;
        var issues = root.GetProperty("issues").EnumerateArray().Select(i =>
        {
            var fields = i.GetProperty("fields");
            return new Issue(i.GetProperty("key").GetString()!,
                Limit(EventReader.RedactText(fields.GetProperty("summary").GetString() ?? ""), 500),
                Limit(EventReader.RedactText(fields.TryGetProperty("description", out var d) ? AdfText(d) : ""), 4000),
                fields.TryGetProperty("status", out var status) ? status.GetProperty("name").GetString() ?? "" : "");
        }).ToArray();
        var token = root.TryGetProperty("nextPageToken", out var next) ? next.GetString() : null;
        // Do not let an incomplete or unexpected response masquerade as a completed search.
        if (root.TryGetProperty("isLast", out var last) && last.ValueKind == JsonValueKind.False && string.IsNullOrEmpty(token))
            throw new InvalidDataException("Jira returned an incomplete page without a continuation token.");
        return new SearchPage(issues, string.IsNullOrEmpty(token) ? null : token);
    }

    public async Task<string> CreateAsync(ErrorEvent evt, Decision decision, CancellationToken ct)
    {
        object Paragraph(string text) => new { type = "paragraph", content = new[] { new { type = "text", text } } };
        var content = new List<object> {
            Paragraph("Issue analysis (AI generated; verify before implementing)"),
            Paragraph(Limit(EventReader.RedactText(decision.Analysis), 6000)),
            Paragraph($"Seq event: {evt.Id}; level: {evt.Level}; fingerprint: {evt.Fingerprint}")
        };
        if (evt.Url is not null) content.Add(Paragraph("Seq event URL: " + evt.Url));
        content.Add(Paragraph("Event details (redacted; large values may be truncated)"));
        var details = evt.Details.Length <= 20000 ? evt.Details : evt.Details[..20000] + "\n[TRUNCATED]";
        content.Add(new { type = "codeBlock", attrs = new { language = "json" }, content = new[] { new { type = "text", text = details } } });
        var body = new { fields = new {
            project = new { key = settings.JiraProject }, issuetype = new { name = settings.JiraIssueType },
            summary = Limit(EventReader.RedactText(decision.Summary).Replace('\n', ' ').Replace('\r', ' '), 255),
            description = new { type = "doc", version = 1, content }, labels = new[] { "ready-4-robot", evt.Fingerprint }
        } };
        using var request = Request(HttpMethod.Post, "rest/api/3/issue", body);
        // Never retry POST /issue: a lost response may still have created the ticket.
        using var response = await http.SendAsync(request, ct);
        if ((int)response.StatusCode is >= 400 and < 500 && response.StatusCode != HttpStatusCode.RequestTimeout)
            throw new JiraRejectedException((int)response.StatusCode);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Jira creation outcome is uncertain (HTTP {(int)response.StatusCode}).");
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var key = result.GetProperty("key").GetString();
        if (key is null || !Regex.IsMatch(key, "^" + Regex.Escape(settings.JiraProject) + "-[0-9]+$"))
            throw new InvalidDataException("Jira created an issue but returned an unexpected key; reconcile before retrying.");
        return key;
    }

    private HttpRequestMessage Request(HttpMethod method, string path, object payload)
    {
        var request = new HttpRequestMessage(method, settings.JiraUrl.TrimEnd('/') + "/" + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(settings.JiraEmail + ":" + settings.JiraApiToken)));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = JsonContent.Create(payload);
        return request;
    }

    private async Task<HttpResponseMessage> ReadRequestAsync(Func<HttpRequestMessage> request, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var message = request();
            var response = await http.SendAsync(message, ct);
            if (response.IsSuccessStatusCode) return response;
            var status = (int)response.StatusCode;
            var delay = response.Headers.RetryAfter?.Delta ??
                (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : TimeSpan.FromSeconds(Math.Pow(2, attempt)));
            response.Dispose();
            if (attempt >= 2 || (status != 429 && status < 500))
                throw new HttpRequestException($"Jira search failed (HTTP {status}); no ticket will be created.");
            if (delay > TimeSpan.FromSeconds(30)) throw new HttpRequestException("Jira requested a longer backoff; retry this event later.");
            await Task.Delay(delay < TimeSpan.Zero ? TimeSpan.Zero : delay, ct);
        }
    }

    private static string AdfText(JsonElement element) => element.ValueKind switch {
        JsonValueKind.String => element.GetString() ?? "",
        JsonValueKind.Array => string.Join(" ", element.EnumerateArray().Select(AdfText)),
        JsonValueKind.Object => (element.TryGetProperty("text", out var t) ? t.GetString() : "") + " " +
            (element.TryGetProperty("content", out var c) ? AdfText(c) : ""),
        _ => ""
    };
    private static string Limit(string text, int max) => text.Length <= max ? text : text[..max];
    [GeneratedRegex(@"[\p{L}\p{N}_]{2,80}", RegexOptions.CultureInvariant)] private static partial Regex Words();
    [GeneratedRegex("^nebula-[a-f0-9]{64}$")] private static partial Regex FingerprintPattern();
}
