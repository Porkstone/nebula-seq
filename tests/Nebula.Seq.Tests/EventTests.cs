using System.Text.Json;
using Xunit;

namespace Nebula.Seq.Tests;

public class EventTests
{
    [Theory]
    [InlineData("Information")]
    [InlineData("Warning")]
    [InlineData("Debug")]
    [InlineData("")]
    public void IgnoresNonErrors(string level) => Assert.Null(EventReader.Read(Samples.Clef.Replace("\"Error\"", Json.Write(level)), Samples.Settings));

    [Theory]
    [InlineData("Error")]
    [InlineData("Fatal")]
    public void AcceptsErrors(string level) => Assert.NotNull(EventReader.Read(Samples.Clef.Replace("\"Error\"", Json.Write(level)), Samples.Settings));

    [Fact]
    public void FingerprintGroupsRepeatedTemplateButSeparatesApplication()
    {
        var first = Samples.Event;
        var second = EventReader.Read(Samples.Clef.Replace("event-123", "event-456").Replace("42", "99"), Samples.Settings)!;
        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.NotEqual(first.Fingerprint, EventReader.Read(Samples.Clef.Replace("\"Orders\"", "\"Payments\""), Samples.Settings)!.Fingerprint);
    }

    [Fact]
    public void RedactsNestedSecretsAndText()
    {
        var clef = """
            {"@l":"Error","@mt":"Bearer abcdef1234567890 password=hidden sk-or-abcdefghijklmnop","nested":{"AccessToken":"hidden","deep":[{"Password":"hidden"}]},"connectionString":"hidden"}
            """;
        var evt = EventReader.Read(clef, Samples.Settings)!;
        Assert.DoesNotContain("hidden", evt.Details);
        Assert.DoesNotContain("abcdef1234567890", evt.Details);
        Assert.DoesNotContain("sk-or-", evt.Details);
        Assert.Contains("REDACTED", evt.Details);
        using var parsed = JsonDocument.Parse(evt.Details);
    }

    [Fact]
    public void LargeEventsRemainValidBoundedJson()
    {
        var clef = Json.Write(new Dictionary<string, object> { ["@l"] = "Error", ["@mt"] = "Failed", ["Data"] = new string('"', 50000) });
        var evt = EventReader.Read(clef, Samples.Settings with { MaxEventChars = 2000 })!;
        using var parsed = JsonDocument.Parse(evt.Details);
        Assert.True(evt.Details.Length <= 2000, $"Length was {evt.Details.Length}");
        Assert.Contains("truncated", evt.Details);
    }

    [Fact]
    public void SourceLinkUsesSeqIdRatherThanEventType()
    {
        var evt = EventReader.Read(Samples.Clef, Samples.Settings with { SeqUrl = "https://seq.example.test" })!;
        Assert.Equal("event-123", evt.Id);
        Assert.Contains("event-123", evt.Url);
        Assert.DoesNotContain("a1234567", evt.Url);
        Assert.Null(EventReader.Read(Samples.Clef.Replace("\"@seqid\":\"event-123\",", ""), Samples.Settings with { SeqUrl = "https://seq.example.test" })!.Url);
    }

    [Theory]
    [InlineData("http://example.test", "NEB")]
    [InlineData("https://user:pass@example.test", "NEB")]
    [InlineData("https://example.test?secret=x", "NEB")]
    [InlineData("https://example.test", "X OR project = Y")]
    public void RejectsUnsafeConfiguration(string url, string project) =>
        Assert.Throws<ArgumentException>(() => (Samples.Settings with { JiraUrl = url, JiraProject = project }).Validate());
}
