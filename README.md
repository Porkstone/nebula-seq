# Seq.App.Nebula

A configurable Seq app that sends Error and Fatal events to **Codex CLI using OpenRouter**, lets Codex search existing Jira Cloud bugs through a bundled read-only MCP tool, and creates a new bug only when no duplicate is identified. New tickets always carry **`ready-4-robot`**, a stable fingerprint label, the redacted event details, and an AI generated issue summary and analysis.

## Requirements

- A Seq server capable of loading .NET 8 apps. Seq hosts using an older .NET runtime are unsupported; verify your server's runtime before installation.
- The .NET 8 or later runtime, with a `dotnet` executable accessible to the Seq service account. The bundled helper permits major-version roll forward.
- A current **native Codex CLI** executable on the Seq host. Development validation uses `codex-cli 0.163.0`. On Windows, configure the absolute `codex.exe` path, not npm's `codex.cmd` or `codex.ps1`. The npm package normally contains it under `node_modules/@openai/codex/node_modules/@openai/codex-win32-x64/vendor/.../bin/codex.exe`.
- An OpenRouter API key with credit and a model supporting the Responses API, tool calls and structured JSON outputs. Configure the exact model ID from your OpenRouter account; no model is silently selected.
- A Jira Cloud account email and API token, and permission to **browse all bugs** and **create issues** in the target project. The project must permit the selected issue type (default `Bug`), summary, description and labels without extra required fields. Issue security must not hide potential duplicates from this account.
- Outbound HTTPS access to `openrouter.ai` and your Jira Cloud site. No OpenAI login or separate Jira MCP installation is required.

Codex, its selected model, and the installed Seq runtime are host prerequisites; they are not embedded in the NuGet package. For Docker deployments install Codex and .NET in a derived Seq image and give the Seq process access to them.

## Install in Seq

Package ID: **`Seq.App.Nebula`**.

1. For a local package, copy `artifacts/Seq.App.Nebula.0.1.0.nupkg` to a folder on the Seq server, e.g. `C:\Packages`.
2. In Seq **Settings → Feeds**, add that folder as a feed. For the public release use the NuGet.org feed instead.
3. Open **Settings → Apps → Install**, select the feed and install `Seq.App.Nebula`.
4. Start a new instance and configure the settings below. Keep **Dry run** enabled for the first event.
5. Send a known Error event using **Send to app**. Confirm the diagnostics show `existing` or `dry-run-would-create`. For a proposed new ticket, review the redacted JSON draft under the `drafts` subfolder of the state path reported in the startup diagnostics.
6. Disable **Dry run** to enable ticket creation. For continuous operation, enable **Stream incoming events** and select a signal such as `@Level in ['Error', 'Fatal']` with your application filter.

## Settings

| Setting | Purpose / default |
| --- | --- |
| OpenRouter API key | Required, password field. Passed to Codex via its child process environment. |
| OpenRouter model | Required exact model ID; Responses + tools + JSON schema support required. |
| Jira Cloud URL | Required, e.g. `https://team.atlassian.net`. HTTPS only. |
| Jira account email / API token | Required. Token is a password field. Jira Cloud basic authentication. |
| Jira project key | Required, e.g. `NEB`. Both searches and creation stay in this project. |
| Jira issue type | `Bug`. Must match the Jira type name. |
| Codex executable | `codex`; on Windows set the absolute native executable path. |
| .NET executable | `dotnet`; set an absolute path if absent from the service account's PATH. |
| Dry run | `true`. Runs model and Jira reads; saves a redacted draft for review, creates no ticket and does not mark the event processed. |
| Processing timeout | 180 seconds; configurable 10–1800. Cancels and kills the Codex process tree. |
| Maximum event characters | 24,000; configurable 2,000–60,000. Large events and values are explicitly truncated. Ticket details are limited to 20,000 characters. |
| Redacted property names | Comma separated name fragments: `password,secret,token,authorization,cookie,apikey,api_key,connectionstring`. Recursive, case insensitive. |
| Public Seq URL | Optional HTTPS address used for source event links. |
| Shared state directory | Defaults to this instance's persistent Seq app storage. Share this directory across app instances targeting the same project. |

Settings are configured in Seq; the app has no separate website or authentication service. Do not allow untrusted users to override executable paths, storage paths, project settings or credentials.

## Processing and duplicate handling

Only Error/Fatal CLEF events are processed. A SHA-256 fingerprint groups the application, source context, message template (rendered message when no template exists), and exception type. Event IDs, timestamps and parameter values are excluded when a message template is present. Template-less messages with variable values can yield separate fingerprints.

The app checks its persistent receipt, then Jira for the exact fingerprint label. If neither matches, Codex receives the redacted event and searches existing bugs by distinctive keywords. Its tool uses Jira's current `/rest/api/3/search/jql` API, includes closed bugs, and limits every query to the configured project and issue type. Search responses include summaries, description excerpts and status. Codex must follow every continuation token; an independently recorded search audit verifies actual successful calls and pagination. No successful search, an HTTP error, an unread page, an invented issue key or an ambiguous `review` response blocks creation. Searches have a twelve-call budget per event; broad queries may require manual review.

The app creates tickets through Jira REST v3 with an Atlassian Document Format description. The model cannot choose the labels, project or issue type. Jira reads retry bounded rate limits and server errors. Creation is never automatically retried.

A file lock serializes processing in the selected state directory. Receipts and drafts live in a subdirectory scoped to the Jira URL, project and issue type, so changing the destination does not suppress tickets in a different project. A flushed `pending` receipt is written **before** ticket creation; successful creation changes it to `complete`. Repeated events and restarts reuse complete receipts. An uncertain outcome (timeout, lost response, crash, server error) retains the pending receipt and blocks a second create. An exact fingerprint match in Jira automatically reconciles it on the next attempt. Definite Jira client rejections remove the pending receipt so a configuration fix can be retried.

If a pending receipt persists, search Jira manually for its `nebula-...` label. If a ticket exists, keep the label and retry the event. If you have confirmed no ticket exists, stop all instances sharing the state directory, delete **only that fingerprint's pending JSON file**, restart and replay. Do not clear receipts just to suppress errors.

Use **one app instance per project**, or a shared persistent state directory with reliable file locking. Independent state directories, unrelated integrations, Jira search indexing delay, invisible issues and imperfect semantic matching mean global exactly-once creation cannot be guaranteed. Closed duplicates are reused; reopening or adding comments is not automatic. Receipts do not expire. Seq does not provide a durable job queue through this app: failed events require an operator to replay them after resolving the problem. Filter incoming traffic to control model cost and processing backlog.

## Data handling

Event properties matching the configured name fragments are redacted recursively. Common bearer tokens, OpenRouter-style API keys and textual credential assignments are also masked. Redaction is best effort: arbitrary secrets or personal data in free text may remain. Filter sensitive events at source and customize the property list before enabling streaming. Jira search excerpts also pass through the free-text redactor.

The redacted event and search excerpts leave the server for your selected OpenRouter model/provider. A ticket stores the redacted event, capped analysis and source link. Codex uses an isolated temporary home and work directory per event, read-only sandbox, disabled shell tools and web search, no user/project instructions, and only the bundled read-only Jira MCP server. Credentials are passed via child process environment variables, not prompt text, command arguments or config files. Seq password settings mask credentials in the UI; Seq administrators and the host service account are trusted.

Run files are deleted after each normal attempt; a hard server crash can leave `nebula-runs` directories in the app's storage, which an administrator can remove while the app is stopped. Persistent receipts contain only status, fingerprint (filename), issue key and timestamp. Dry-run drafts additionally contain the redacted event and proposed ticket text; remove drafts when no longer needed. Diagnostics record the outcome and identifiers; raw event details, model reasoning and Jira response bodies are not logged.

## Build and verify

Requires .NET SDK 9 (tests run on .NET 9; the app and helper target .NET 8):

```powershell
./Build.ps1
```

The script restores, builds, runs offline integration/unit tests, creates the `.nupkg`, and verifies that it bundles the helper and core library without `Seq.Apps.dll`, `Serilog.dll` or external package dependencies. Use `-Version 0.1.1` for a subsequent release. The tests use fake Jira HTTP responses and a fake Codex process; live credentials and model requests are not required.

## Publishing

Publish the verified file with a NuGet.org API key scoped to `Seq.App.Nebula` (`Push new packages and package versions`). Keep credentials outside this repository. Version numbers are immutable after publishing.

```powershell
dotnet nuget push ./artifacts/Seq.App.Nebula.0.1.0.nupkg --source https://api.nuget.org/v3/index.json --api-key $env:NUGET_API_KEY
```

## References

- [Seq app development and packaging](https://datalust.co/docs/writing-seq-apps)
- [Seq app installation and local folder feeds](https://datalust.co/docs/installing-seq-apps)
- [Codex non-interactive execution](https://developers.openai.com/codex/noninteractive)
- [Codex configuration reference](https://developers.openai.com/codex/config-reference)
- [OpenRouter's Codex integration](https://openrouter.ai/docs/cookbook/coding-agents/codex-cli)
- [OpenRouter Responses API](https://openrouter.ai/docs/api_reference/responses/overview)
- [Jira Cloud JQL search](https://developer.atlassian.com/cloud/jira/platform/rest/v3/api-group-issue-search/)
- [Jira issue creation](https://developer.atlassian.com/cloud/jira/platform/rest/v3/api-group-issues/)

Live Seq hosting, your model's compatibility and your Jira project's permissions/required fields must be verified with the initial dry run on your server.
