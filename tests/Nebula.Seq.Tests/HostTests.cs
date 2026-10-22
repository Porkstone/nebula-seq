using System.Diagnostics;
using System.Text.Json;
using Seq.App.Nebula;
using Seq.Apps;
using Serilog;
using Xunit;

namespace Nebula.Seq.Tests;

public class HostTests
{
    private sealed class TestAppHost(string storage) : IAppHost
    {
        public global::Seq.Apps.App App { get; } = new("app-1", "Nebula test", new Dictionary<string, string>(), storage);
        public global::Seq.Apps.Host Host { get; } = new("test", "https://seq.example.test");
        public ILogger Logger { get; } = new LoggerConfiguration().CreateLogger();
        public string StoragePath => storage;
    }

    [Fact]
    public async Task SeqSdkAttachesAppAndIgnoresInformationEvent()
    {
        using var dir = new TestDirectory();
        using var app = new NebulaApp {
            OpenRouterApiKey = Samples.Settings.OpenRouterApiKey, Model = Samples.Settings.Model,
            JiraUrl = Samples.Settings.JiraUrl, JiraEmail = Samples.Settings.JiraEmail,
            JiraApiToken = Samples.Settings.JiraApiToken, JiraProject = Samples.Settings.JiraProject
        };
        Assert.True(app.DryRun);
        app.Attach(new TestAppHost(dir.Path));
        await app.OnAsync("""{"@l":"Information","@mt":"Should not call any external service"}""");
        Assert.True(Directory.Exists(Path.Combine(dir.Path, "nebula-state")));
    }

    [Fact]
    public async Task BundledHelperStartsAndNegotiatesMcpWithoutNetwork()
    {
        using var dir = new TestDirectory();
        var dotnetRoot = Directory.GetParent(typeof(object).Assembly.Location)!.Parent!.Parent!.Parent!.FullName;
        var start = new ProcessStartInfo(Path.Combine(dotnetRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet")) {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Nebula.Seq.JiraMcp.dll"));
        start.Environment["NEBULA_JIRA_URL"] = Samples.Settings.JiraUrl;
        start.Environment["NEBULA_JIRA_EMAIL"] = Samples.Settings.JiraEmail;
        start.Environment["NEBULA_JIRA_TOKEN"] = Samples.Settings.JiraApiToken;
        start.Environment["NEBULA_JIRA_PROJECT"] = Samples.Settings.JiraProject;
        start.Environment["NEBULA_JIRA_ISSUE_TYPE"] = "Bug";
        start.Environment["NEBULA_EVIDENCE_PATH"] = Path.Combine(dir.Path, "evidence.json");
        using var process = Process.Start(start)!;
        await process.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""");
        await process.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
        process.StandardInput.Close();
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, error);
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        using var tools = JsonDocument.Parse(lines[1]);
        Assert.Equal("search_jira", tools.RootElement.GetProperty("result").GetProperty("tools")[0].GetProperty("name").GetString());
    }
}
