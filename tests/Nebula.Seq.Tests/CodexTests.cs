using Xunit;

namespace Nebula.Seq.Tests;

public class CodexTests
{
    private static Settings Settings => Samples.Settings with {
        CodexExecutable = Path.Combine(AppContext.BaseDirectory, "fake-codex", OperatingSystem.IsWindows() ? "Nebula.Seq.FakeCodex.exe" : "Nebula.Seq.FakeCodex")
    };

    [Fact]
    public async Task ProcessReturnsStructuredDecisionAndCleansTemporaryData()
    {
        using var dir = new TestDirectory();
        var runner = new CodexSearch(Settings, "unused-bridge.dll", dir.Path);
        var result = await runner.SearchAsync(Samples.Event, default);
        Assert.Equal("new", result.Decision.Action); Assert.True(result.Evidence.Complete);
        Assert.Empty(Directory.GetDirectories(dir.Path));
    }

    [Fact]
    public async Task NonzeroExitFailsClosed()
    {
        using var dir = new TestDirectory();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new CodexSearch(Settings, "unused", dir.Path)
            .SearchAsync(Samples.Event with { Details = "SIMULATE_FAILURE" }, default));
        Assert.Contains("23", error.Message);
        Assert.Empty(Directory.GetDirectories(dir.Path));
    }

    [Fact]
    public async Task CleanupRetriesWhenExitedProcessWorkspaceIsStillLocked()
    {
        using var dir = new TestDirectory();
        var result = await new CodexSearch(Settings, "unused", dir.Path)
            .SearchAsync(Samples.Event with { Details = "SIMULATE_LOCKED_EXIT" }, default);
        Assert.Equal("new", result.Decision.Action);
        Assert.Empty(Directory.GetDirectories(dir.Path));
    }

    [Fact]
    public async Task TimeoutKillsProcessAndCleansFiles()
    {
        using var dir = new TestDirectory(); using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(700));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CodexSearch(Settings, "unused", dir.Path)
            .SearchAsync(Samples.Event with { Details = "SIMULATE_TIMEOUT" }, cts.Token));
        Assert.Empty(Directory.GetDirectories(dir.Path));
    }

    [Fact]
    public void ConfigHasNoSecretsAndIsolatesTools()
    {
        var config = CodexSearch.BuildConfig(Samples.Settings, @"C:\Program Files\Nebula\helper.dll", @"C:\work\日本語");
        Assert.DoesNotContain(Samples.Settings.OpenRouterApiKey, config);
        Assert.DoesNotContain(Samples.Settings.JiraApiToken, config);
        Assert.Contains("shell_tool = false", config);
        Assert.Contains("approval_policy = \"never\"", config);
        Assert.Contains("enabled_tools = [\"search_jira\"]", config);
        Assert.Contains("model_provider = \"openrouter\"", config);
    }
}
