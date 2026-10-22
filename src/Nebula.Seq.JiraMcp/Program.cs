using System.Text;
using Nebula.Seq;

Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = new UTF8Encoding(false);
string Required(string name) => Environment.GetEnvironmentVariable(name)
    ?? throw new ArgumentException($"Missing environment variable: {name}");
try
{
    var settings = new Settings {
        OpenRouterApiKey = "unused", Model = "unused",
        JiraUrl = Required("NEBULA_JIRA_URL"), JiraEmail = Required("NEBULA_JIRA_EMAIL"),
        JiraApiToken = Required("NEBULA_JIRA_TOKEN"), JiraProject = Required("NEBULA_JIRA_PROJECT"),
        JiraIssueType = Required("NEBULA_JIRA_ISSUE_TYPE")
    };
    settings.Validate();
    using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(45) };
    var server = new JiraMcpServer(new JiraClient(http, settings), Required("NEBULA_EVIDENCE_PATH"));
    await server.RunAsync(Console.In, Console.Out, CancellationToken.None);
    return 0;
}
catch (Exception ex)
{
    // Never print configuration values, credentials or HTTP response bodies.
    Console.Error.WriteLine("Nebula Jira MCP stopped: " + ex.GetType().Name);
    return 1;
}
