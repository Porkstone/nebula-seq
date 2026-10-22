namespace Nebula.Seq;

public sealed record Receipt(string Status, string? IssueKey, DateTimeOffset UpdatedAt);

public sealed class StateStore(string directory)
{
    public async Task<FileStream> LockAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try { return new FileStream(Path.Combine(directory, "processing.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { await Task.Delay(100, ct); }
        }
    }

    public Receipt? Read(string fingerprint)
    {
        var path = PathFor(fingerprint);
        return File.Exists(path) ? Json.Read<Receipt>(File.ReadAllText(path)) : null;
    }

    public void Write(string fingerprint, string status, string? issueKey)
    {
        Directory.CreateDirectory(directory);
        AtomicWrite(PathFor(fingerprint), Json.Write(new Receipt(status, issueKey, DateTimeOffset.UtcNow)));
    }

    public void Remove(string fingerprint) => File.Delete(PathFor(fingerprint));

    public void WriteDraft(ErrorEvent evt, Decision decision)
    {
        var drafts = Path.Combine(directory, "drafts");
        Directory.CreateDirectory(drafts);
        AtomicWrite(Path.Combine(drafts, evt.Fingerprint + ".json"), Json.Write(new {
            generatedAt = DateTimeOffset.UtcNow, action = decision.Action,
            summary = EventReader.RedactText(decision.Summary), analysis = EventReader.RedactText(decision.Analysis),
            eventDetails = evt.Details, seqUrl = evt.Url, labels = new[] { "ready-4-robot", evt.Fingerprint }
        }));
    }
    private string PathFor(string fingerprint)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(fingerprint, "^nebula-[a-f0-9]{64}$")) throw new ArgumentException("Invalid fingerprint.");
        return Path.Combine(directory, fingerprint + ".json");
    }

    public static void AtomicWrite(string path, string data)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream)) { writer.Write(data); writer.Flush(); stream.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
