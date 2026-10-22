using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Nebula.Seq;

public static partial class EventReader
{
    public static ErrorEvent? Read(string clef, Settings settings)
    {
        var obj = JsonNode.Parse(clef)?.AsObject() ?? throw new InvalidDataException("Expected a CLEF object.");
        string Get(string key) => obj[key]?.ToString() ?? "";
        var level = Get("@l");
        if (!level.Equals("Error", StringComparison.OrdinalIgnoreCase) &&
            !level.Equals("Fatal", StringComparison.OrdinalIgnoreCase)) return null;
        // Stable across different timestamps, parameter values and individual event IDs.
        var exceptionType = Get("@x").Split(new[] { ':', '\r', '\n' }, 2)[0];
        var identity = Json.Write(new[] { Get("Application"), Get("ApplicationName"), Get("SourceContext"),
            Get("@mt") is { Length: > 0 } mt ? mt : Get("@m"), exceptionType });
        var fingerprint = "nebula-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        var sensitive = settings.RedactedProperties.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var clean = Clean(obj, sensitive, 0)!.AsObject();
        var details = clean.ToJsonString(Json.Options);
        if (details.Length > settings.MaxEventChars)
        {
            // Keep valid JSON when shrinking a very large event.
            var excerpt = details[..Math.Min(details.Length, settings.MaxEventChars / 2)];
            details = Json.Write(new { truncated = true, excerpt });
            while (details.Length > settings.MaxEventChars)
            {
                excerpt = excerpt[..(excerpt.Length * 3 / 4)];
                details = Json.Write(new { truncated = true, excerpt });
            }
        }
        // @i is the CLEF event type/hash. Seq's unique event identifier is @seqid.
        var id = Get("@seqid");
        var url = string.IsNullOrWhiteSpace(settings.SeqUrl) || id.Length == 0 ? null :
            settings.SeqUrl.TrimEnd('/') + "/#/events?filter=" + Uri.EscapeDataString("@Id = '" + id.Replace("'", "''") + "'");
        return new ErrorEvent(id, level, fingerprint, details, url);
    }

    private static JsonNode? Clean(JsonNode? node, string[] sensitive, int depth)
    {
        if (depth > 12) return JsonValue.Create("[DEPTH LIMIT]");
        if (node is JsonObject obj)
        {
            var result = new JsonObject();
            foreach (var pair in obj.Take(128))
                result[RedactText(pair.Key)] = sensitive.Any(s => pair.Key.Contains(s, StringComparison.OrdinalIgnoreCase))
                    ? JsonValue.Create("[REDACTED]") : Clean(pair.Value, sensitive, depth + 1);
            if (obj.Count > 128) result["_propertiesTruncated"] = true;
            return result;
        }
        if (node is JsonArray array) return new JsonArray(array.Take(64).Select(x => Clean(x, sensitive, depth + 1)).ToArray());
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            var clean = RedactText(text);
            return JsonValue.Create(clean.Length > 8000 ? clean[..8000] + " [TRUNCATED]" : clean);
        }
        return node?.DeepClone();
    }

    public static string RedactText(string text) => SecretPattern().Replace(text, "[REDACTED]");

    [GeneratedRegex(@"(?i)(?:bearer\s+[A-Za-z0-9._~+/=-]+|sk-(?:or-)?[A-Za-z0-9_-]{12,}|(?:password|api[_-]?key|token|secret)\s*[:=]\s*[^\s,;""}]+)", RegexOptions.CultureInvariant)]
    private static partial Regex SecretPattern();
}
