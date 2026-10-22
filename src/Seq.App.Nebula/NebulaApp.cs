using Nebula.Seq;
using Seq.Apps;
using System.Security.Cryptography;
using System.Text;

namespace Seq.App.Nebula;

[SeqApp("Nebula · Jira error triage", Description = "Search Jira for existing bugs using Codex CLI through OpenRouter, then raise new bugs with ready-4-robot.")]
public sealed class NebulaApp : SeqApp, ISubscribeToJsonAsync, IDisposable
{
    [SeqAppSetting(DisplayName = "OpenRouter API key", InputType = SettingInputType.Password)]
    public string OpenRouterApiKey { get; set; } = "";

    [SeqAppSetting(DisplayName = "OpenRouter model", HelpText = "Exact OpenRouter model ID. Must support Responses API, tools and structured JSON output.")]
    public string Model { get; set; } = "";

    [SeqAppSetting(DisplayName = "Jira Cloud URL", HelpText = "For example https://your-team.atlassian.net")]
    public string JiraUrl { get; set; } = "";

    [SeqAppSetting(DisplayName = "Jira account email")]
    public string JiraEmail { get; set; } = "";

    [SeqAppSetting(DisplayName = "Jira API token", InputType = SettingInputType.Password)]
    public string JiraApiToken { get; set; } = "";

    [SeqAppSetting(DisplayName = "Jira project key", HelpText = "Project to search and create bugs in, e.g. NEB.")]
    public string JiraProject { get; set; } = "";

    [SeqAppSetting(DisplayName = "Jira issue type", IsOptional = true)]
    public string JiraIssueType { get; set; } = "Bug";

    [SeqAppSetting(DisplayName = "Codex executable", IsOptional = true, HelpText = "Native codex executable on the Seq host. On Windows use the full path to codex.exe; .cmd/.ps1 wrappers are unsupported.")]
    public string CodexExecutable { get; set; } = "codex";

    [SeqAppSetting(DisplayName = ".NET executable", IsOptional = true, HelpText = "dotnet host for the bundled Jira search helper; .NET 8 or later is required.")]
    public string DotnetExecutable { get; set; } = "dotnet";

    [SeqAppSetting(DisplayName = "Dry run", IsOptional = true, HelpText = "Enabled by default. Search and analyze events without creating Jira tickets or recording them as processed.")]
    public bool DryRun { get; set; } = true;

    [SeqAppSetting(DisplayName = "Processing timeout (seconds)", IsOptional = true)]
    public int TimeoutSeconds { get; set; } = 180;

    [SeqAppSetting(DisplayName = "Maximum event characters", IsOptional = true)]
    public int MaxEventChars { get; set; } = 24000;

    [SeqAppSetting(DisplayName = "Redacted property names", IsOptional = true, HelpText = "Comma separated, case insensitive name fragments. Matching properties are removed recursively before sending to the model or Jira.")]
    public string RedactedProperties { get; set; } = "password,secret,token,authorization,cookie,apikey,api_key,connectionstring";

    [SeqAppSetting(DisplayName = "Public Seq URL", IsOptional = true, HelpText = "Optional HTTPS URL for links to source events in Jira tickets.")]
    public string SeqUrl { get; set; } = "";

    [SeqAppSetting(DisplayName = "Shared state directory", IsOptional = true, HelpText = "Default: this app instance's Seq storage. Multiple instances targeting the same Jira project must share a directory with reliable file locking.")]
    public string StateDirectory { get; set; } = "";

    private readonly CancellationTokenSource stopping = new();
    private HttpClient? http;
    private EventProcessor? processor;
    private Settings? settings;

    protected override void OnAttached()
    {
        settings = new Settings {
            OpenRouterApiKey = OpenRouterApiKey, Model = Model, JiraUrl = JiraUrl,
            JiraEmail = JiraEmail, JiraApiToken = JiraApiToken, JiraProject = JiraProject,
            JiraIssueType = string.IsNullOrWhiteSpace(JiraIssueType) ? "Bug" : JiraIssueType,
            CodexExecutable = string.IsNullOrWhiteSpace(CodexExecutable) ? "codex" : CodexExecutable,
            DotnetExecutable = string.IsNullOrWhiteSpace(DotnetExecutable) ? "dotnet" : DotnetExecutable,
            DryRun = DryRun, TimeoutSeconds = TimeoutSeconds == 0 ? 180 : TimeoutSeconds,
            MaxEventChars = MaxEventChars == 0 ? 24000 : MaxEventChars,
            RedactedProperties = RedactedProperties, SeqUrl = SeqUrl
        };
        settings.Validate();
        var bridge = Path.Combine(Path.GetDirectoryName(typeof(NebulaApp).Assembly.Location)!, "Nebula.Seq.JiraMcp.dll");
        if (!File.Exists(bridge)) throw new SeqAppException("Bundled Jira helper is missing. Reinstall the complete Seq.App.Nebula package.");
        var stateRoot = string.IsNullOrWhiteSpace(StateDirectory) ? Path.Combine(App.StoragePath, "nebula-state") : Path.GetFullPath(StateDirectory);
        // Reconfiguring the Jira destination must not reuse another project's receipts.
        var scope = Json.Write(new[] { settings.JiraUrl.TrimEnd('/').ToLowerInvariant(), settings.JiraProject, settings.JiraIssueType });
        var statePath = Path.Combine(stateRoot, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope))).ToLowerInvariant()[..24]);
        Directory.CreateDirectory(statePath);
        http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(45) };
        processor = new EventProcessor(settings, new StateStore(statePath), new JiraClient(http, settings),
            new CodexSearch(settings, bridge, Path.Combine(App.StoragePath, "nebula-runs")));
        Log.Information("Nebula Jira triage started for {JiraProject}; dry run: {DryRun}", JiraProject, DryRun);
        Log.Information("Nebula receipts and dry-run drafts are stored in {StatePath}", statePath);
    }

    public async Task OnAsync(string json)
    {
        if (settings is null || processor is null) throw new SeqAppException("Nebula is not initialized.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        try
        {
            var evt = EventReader.Read(json, settings);
            if (evt is null) return;
            var result = await processor.ProcessAsync(evt, deadline.Token);
            Log.Information("Nebula triage {Outcome}: event {EventId}, fingerprint {Fingerprint}, Jira {JiraIssueKey}",
                result.Outcome, evt.Id, result.Fingerprint, result.IssueKey);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            throw new SeqAppException("Nebula triage timed out or stopped. Any uncertain Jira creation is retained for reconciliation.");
        }
        catch (Exception ex)
        {
            // No raw event, model output, credential, or Jira response body is written to Seq diagnostics.
            throw new SeqAppException("Nebula triage failed: " + EventReader.RedactText(ex.Message));
        }
    }

    public void Dispose()
    {
        stopping.Cancel();
        http?.Dispose();
        stopping.Dispose();
    }
}
