namespace Nebula.Seq;

public sealed class EventProcessor(Settings settings, StateStore state, IJiraClient jira, ICodexSearch codex)
{
    public async Task<ProcessingResult> ProcessAsync(ErrorEvent evt, CancellationToken ct)
    {
        await using var lease = await state.LockAsync(ct);
        var receipt = state.Read(evt.Fingerprint);
        if (receipt?.Status == "complete") return new("already-processed", receipt.IssueKey, evt.Fingerprint);
        var existing = await jira.FindFingerprintAsync(evt.Fingerprint, ct);
        if (existing.Count > 0)
        {
            if (!settings.DryRun) state.Write(evt.Fingerprint, "complete", existing[0].Key);
            return new("existing", existing[0].Key, evt.Fingerprint);
        }
        if (receipt is not null)
            throw new InvalidOperationException($"Uncertain earlier creation for {evt.Fingerprint}. Check Jira and reconcile its state receipt before retrying.");

        var result = await codex.SearchAsync(evt, ct);
        var decision = result.Decision;
        if (decision.Action == "duplicate")
        {
            if (result.Evidence.HadError || string.IsNullOrWhiteSpace(decision.ExistingIssueKey) ||
                !result.Evidence.IssueKeys.Contains(decision.ExistingIssueKey))
                throw new InvalidDataException("Codex's duplicate decision was not backed by a successful Jira search.");
            if (!settings.DryRun) state.Write(evt.Fingerprint, "complete", decision.ExistingIssueKey);
            return new("existing", decision.ExistingIssueKey, evt.Fingerprint);
        }
        if (decision.Action != "new" || !result.Evidence.Complete || decision.ExistingIssueKey is not null ||
            string.IsNullOrWhiteSpace(decision.Summary) || string.IsNullOrWhiteSpace(decision.Analysis))
            throw new InvalidDataException("Codex did not establish a complete, successful Jira search and a valid new-issue decision. Review required.");
        if (settings.DryRun)
        {
            state.WriteDraft(evt, decision);
            return new("dry-run-would-create", null, evt.Fingerprint);
        }
        // A second exact check closes the window while the LLM was running.
        existing = await jira.FindFingerprintAsync(evt.Fingerprint, ct);
        if (existing.Count > 0)
        {
            state.Write(evt.Fingerprint, "complete", existing[0].Key);
            return new("existing", existing[0].Key, evt.Fingerprint);
        }
        state.Write(evt.Fingerprint, "pending", null);
        try
        {
            var key = await jira.CreateAsync(evt, decision, ct);
            state.Write(evt.Fingerprint, "complete", key);
            return new("created", key, evt.Fingerprint);
        }
        catch (JiraRejectedException) { state.Remove(evt.Fingerprint); throw; }
        // Timeouts, crashes and lost responses retain the durable pending record.
    }
}
