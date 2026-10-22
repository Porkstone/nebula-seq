using Nebula.Seq;
using System.Diagnostics;

// Offline executable fixture for testing process pipes, flags, cancellation and isolation.
if (args.Contains("--hold-workspace")) { await Task.Delay(1200); return 0; }
var prompt = await Console.In.ReadToEndAsync();
if (prompt.Contains("SIMULATE_TIMEOUT")) { await Task.Delay(TimeSpan.FromSeconds(30)); return 0; }
if (prompt.Contains("SIMULATE_FAILURE")) return 23;
if (args[0] != "exec" || !args.Contains("--ephemeral") || !args.Contains("read-only")) return 24;
if (Environment.GetEnvironmentVariable("UNRELATED_SERVER_SECRET") is not null) return 25;
var home = Environment.GetEnvironmentVariable("CODEX_HOME")!;
var config = File.ReadAllText(Path.Combine(home, "config.toml"));
if (config.Contains("dummy-openrouter") || config.Contains("dummy-jira") || !config.Contains("wire_api = \"responses\"")) return 26;
// Both streams exceed ordinary OS pipe buffers; the caller must drain them concurrently.
await Console.Out.WriteAsync(new string('o', 100000));
await Console.Error.WriteAsync(new string('e', 100000));
var output = args[Array.IndexOf(args, "--output-last-message") + 1];
await File.WriteAllTextAsync(output, Json.Write(new Decision("new", null, "Order fails", "Observed failure in order handler")));
var evidence = new SearchEvidence { SuccessfulSearches = 1, NextTokens = new() { ["order"] = null } };
await File.WriteAllTextAsync(Environment.GetEnvironmentVariable("NEBULA_EVIDENCE_PATH")!, Json.Write(evidence));
if (prompt.Contains("SIMULATE_LOCKED_EXIT"))
{
    // Keep the Windows working-directory handle open after the parent has exited.
    var start = new ProcessStartInfo(Environment.ProcessPath!) {
        WorkingDirectory = Environment.CurrentDirectory, UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
    };
    start.ArgumentList.Add("--hold-workspace");
    using var holder = Process.Start(start)!;
    holder.StandardInput.Close();
}
return 0;
