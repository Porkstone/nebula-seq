using Xunit;

namespace Nebula.Seq.Tests;

public class ProcessorTests
{
    [Fact]
    public async Task CreatesOnceAcrossRestart()
    {
        using var dir = new TestDirectory();
        var jira = new FakeJira(); var codex = new FakeCodex();
        var first = new EventProcessor(Samples.Settings, new StateStore(dir.Path), jira, codex);
        Assert.Equal("created", (await first.ProcessAsync(Samples.Event, default)).Outcome);
        var restarted = new EventProcessor(Samples.Settings, new StateStore(dir.Path), jira, codex);
        Assert.Equal("already-processed", (await restarted.ProcessAsync(Samples.Event, default)).Outcome);
        Assert.Equal(1, jira.Creates); Assert.Equal(1, codex.Calls);
    }

    [Fact]
    public async Task ConcurrentInstancesShareLock()
    {
        using var dir = new TestDirectory();
        var jira = new FakeJira(); var codex = new FakeCodex();
        var a = new EventProcessor(Samples.Settings, new StateStore(dir.Path), jira, codex);
        var b = new EventProcessor(Samples.Settings, new StateStore(dir.Path), jira, codex);
        await Task.WhenAll(a.ProcessAsync(Samples.Event, default), b.ProcessAsync(Samples.Event, default));
        Assert.Equal(1, jira.Creates);
    }

    [Fact]
    public async Task DryRunDoesNotConsumeEvent()
    {
        using var dir = new TestDirectory();
        var jira = new FakeJira(); var codex = new FakeCodex(); var state = new StateStore(dir.Path);
        var dry = new EventProcessor(Samples.Settings with { DryRun = true }, state, jira, codex);
        Assert.Equal("dry-run-would-create", (await dry.ProcessAsync(Samples.Event, default)).Outcome);
        Assert.Null(state.Read(Samples.Event.Fingerprint)); Assert.Equal(0, jira.Creates);
        var draft = Path.Combine(dir.Path, "drafts", Samples.Event.Fingerprint + ".json");
        Assert.Contains("Order processing fails", File.ReadAllText(draft));
        var live = new EventProcessor(Samples.Settings, state, jira, codex);
        Assert.Equal("created", (await live.ProcessAsync(Samples.Event, default)).Outcome);
    }

    [Fact]
    public async Task ExactFingerprintSkipsModel()
    {
        using var dir = new TestDirectory(); var jira = new FakeJira(); var codex = new FakeCodex();
        jira.Exact.Add(new("NEB-1", "Existing", "", "Closed"));
        var processor = new EventProcessor(Samples.Settings, new StateStore(dir.Path), jira, codex);
        Assert.Equal("NEB-1", (await processor.ProcessAsync(Samples.Event, default)).IssueKey);
        Assert.Equal(0, codex.Calls); Assert.Equal(0, jira.Creates);
    }

    [Fact]
    public async Task SemanticDuplicateRequiresRealSearchEvidence()
    {
        using var dir = new TestDirectory(); var jira = new FakeJira(); var codex = new FakeCodex();
        codex.Result = new(new Decision("duplicate", "NEB-4", "Existing", "Same failure"), new SearchEvidence { SuccessfulSearches = 1 });
        var processor = new EventProcessor(Samples.Settings, new StateStore(dir.Path), jira, codex);
        await Assert.ThrowsAsync<InvalidDataException>(() => processor.ProcessAsync(Samples.Event, default));
        codex.Result.Evidence.IssueKeys.Add("NEB-4");
        Assert.Equal("NEB-4", (await processor.ProcessAsync(Samples.Event, default)).IssueKey);
        Assert.Equal(0, jira.Creates);
    }

    [Theory]
    [InlineData("no-search")]
    [InlineData("search-error")]
    [InlineData("unread-page")]
    [InlineData("review")]
    [InlineData("invalid-key")]
    [InlineData("blank-summary")]
    public async Task UnsafeDecisionsNeverCreate(string scenario)
    {
        using var dir = new TestDirectory(); var jira = new FakeJira(); var codex = new FakeCodex();
        var result = Samples.NewResult;
        switch (scenario)
        {
            case "no-search": result.Evidence.SuccessfulSearches = 0; break;
            case "search-error": result.Evidence.HadError = true; break;
            case "unread-page": result.Evidence.NextTokens["order missing"] = "more"; break;
            case "review": result = result with { Decision = result.Decision with { Action = "review" } }; break;
            case "invalid-key": result = result with { Decision = result.Decision with { ExistingIssueKey = "NEB-1" } }; break;
            case "blank-summary": result = result with { Decision = result.Decision with { Summary = " " } }; break;
        }
        codex.Result = result;
        await Assert.ThrowsAsync<InvalidDataException>(() => new EventProcessor(Samples.Settings, new StateStore(dir.Path), jira, codex).ProcessAsync(Samples.Event, default));
        Assert.Equal(0, jira.Creates);
    }

    [Fact]
    public async Task LostCreateResponseBlocksRetryThenReconciles()
    {
        using var dir = new TestDirectory(); var jira = new FakeJira { CreateError = new HttpRequestException("Lost response") }; var codex = new FakeCodex();
        var state = new StateStore(dir.Path);
        var processor = new EventProcessor(Samples.Settings, state, jira, codex);
        await Assert.ThrowsAsync<HttpRequestException>(() => processor.ProcessAsync(Samples.Event, default));
        Assert.Equal("pending", state.Read(Samples.Event.Fingerprint)!.Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => processor.ProcessAsync(Samples.Event, default));
        Assert.Equal(1, jira.Creates);
        jira.Exact.Add(new("NEB-7", "Created", "", "Open"));
        Assert.Equal("existing", (await processor.ProcessAsync(Samples.Event, default)).Outcome);
        Assert.Equal("complete", state.Read(Samples.Event.Fingerprint)!.Status);
    }

    [Fact]
    public async Task DefiniteClientRejectionAllowsRetry()
    {
        using var dir = new TestDirectory(); var jira = new FakeJira { CreateError = new JiraRejectedException(400) }; var state = new StateStore(dir.Path);
        var processor = new EventProcessor(Samples.Settings, state, jira, new FakeCodex());
        await Assert.ThrowsAsync<JiraRejectedException>(() => processor.ProcessAsync(Samples.Event, default));
        Assert.Null(state.Read(Samples.Event.Fingerprint));
        jira.CreateError = null;
        Assert.Equal("created", (await processor.ProcessAsync(Samples.Event, default)).Outcome);
    }

    [Fact]
    public async Task FailedExactSearchNeverRunsModelOrCreates()
    {
        using var dir = new TestDirectory(); var jira = new FakeJira { SearchError = new HttpRequestException("Unavailable") }; var codex = new FakeCodex();
        await Assert.ThrowsAsync<HttpRequestException>(() => new EventProcessor(Samples.Settings, new StateStore(dir.Path), jira, codex).ProcessAsync(Samples.Event, default));
        Assert.Equal(0, jira.Creates); Assert.Equal(0, codex.Calls);
    }
}
