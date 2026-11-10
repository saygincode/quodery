using Quodery.Protocol;
using Quodery.Web.Runtime;
using Quodery.Web.Storage;

namespace Quodery.Web;

public sealed record SessionDetail(InvestigationSession Session, IReadOnlyList<ProtocolEnvelope> Events,
    IReadOnlyList<SessionDetail> Related)
{
    public string Id => Session.Id;
    public TestingJob? Job => Session.Job;
    public string Status => Session.Status;
    public bool IsActive => Status is SessionStatuses.Queued or SessionStatuses.Running or SessionStatuses.AwaitingInput;
}

public sealed record DiscoveryHistory(string Status, int TestCount);

public sealed record SessionHistoryItem(string Id, string ProjectId, string Goal, string Status, DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt, string? TaskType, string? LatestTaskType, bool LatestIsAddedTest,
    PublicCompletion? Result, DiscoveryHistory? Discovery, TestStateUpdate? TestState = null);

public sealed class SessionService(ISessionStore store, RuntimeHub hub)
{
    public async Task<Result<SessionHistoryItem[]>> HistoryAsync(string? projectId, CancellationToken ct)
    {
        var history = new List<SessionHistoryItem>();
        foreach (var session in await store.ListSessionsAsync(projectId, 100, ct))
        {
            var displayed = session;
            if (session.Job?.Type == TestingJob.DiscoverTests)
                displayed = (await store.ListRelatedSessionsAsync(session.ProjectId, session.Id, ct)).LastOrDefault() ?? session;
            PublicCompletion? completion = null;
            DiscoveryHistory? discovery = null;
            TestStateUpdate? testState = null;
            foreach (var envelope in await store.ListEventsAsync(displayed.Id, ct))
            {
                if (envelope.Type == RuntimeEventTypes.SessionCompleted &&
                    ProtocolValidator.TryReadCompletion(envelope.Payload, out var value, out _)) completion = value;
                if (envelope.Type == RuntimeEventTypes.SessionDiscovery &&
                    ProtocolValidator.TryReadDiscovery(envelope.Payload, out var found, out _))
                    discovery = new(found!.Status, found.Tests.Count);
                if (envelope.Type == RuntimeEventTypes.SessionTestState &&
                    ProtocolValidator.TryReadTestState(envelope.Payload, out var state, out _)) testState = state;
            }
            history.Add(new(session.Id, session.ProjectId, session.Goal, displayed.Status, session.CreatedAt,
                displayed.UpdatedAt, session.Job?.Type, displayed.Job?.Type,
                displayed.Job?.Type == TestingJob.AddTest, completion, discovery, testState));
        }
        return history.ToArray();
    }

    public async Task<Result<SessionDetail>> GetAsync(string id, CancellationToken ct)
    {
        var session = await store.GetSessionAsync(id, ct);
        if (session is null) return Refusal.NotFound("This test run was not found.");
        var related = new List<SessionDetail>();
        if (session.Job?.Type == TestingJob.DiscoverTests && session.Job.RunId is null)
            foreach (var step in await store.ListRelatedSessionsAsync(session.ProjectId, id, ct))
                if (step.ProjectId == session.ProjectId)
                    related.Add(new SessionDetail(step, await store.ListEventsAsync(step.Id, ct), []));
        return new SessionDetail(session, await store.ListEventsAsync(id, ct), related);
    }

    public async Task<Result<InvestigationSession>> StartAsync(BrowserAccess access, string? projectId, TestingJob job,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(projectId)) return Refusal.Rejected("Choose a workspace.");
        if (!job.TryValidate(out var jobError)) return Refusal.Rejected(jobError);
        if (job.RunId is not null || job.TestIds is not null || job.TestId is not null ||
            job.Type == TestingJob.DiscoverTests && job.SourceSessionId is not null)
            return Refusal.Rejected("Use the saved discovery list to refine a run or select tests.");
        if (job.Type == TestingJob.RunTests)
            return Refusal.Rejected("Discover relevant tests first, then select the tests to run.");

        var project = await store.GetProjectAsync(projectId, ct);
        if (project is null) return Refusal.NotFound("The workspace was not found.");
        var environment = await store.GetProjectConnectionAsync(project.Id, ct);
        if (environment is null || environment.ProjectId != project.Id || environment.RevokedAt is not null)
            return Refusal.NotFound("The workspace connection was not found.");
        if (environment.ActivatedAt is null)
            return Refusal.Rejected("The workspace has not been connected yet.");

        if (job.Type == TestingJob.InvestigateFailure)
        {
            var source = await store.GetSessionAsync(job.SourceSessionId!, ct);
            if (source is null || source.ProjectId != project.Id)
                return Refusal.NotFound("The test run to investigate was not found in this workspace.");
            if (source.Status != SessionStatuses.Completed)
                return Refusal.Rejected("The test run to investigate must have finished.");
            if (source.Job is null)
                return Refusal.Rejected("The test run to investigate has no result.");
            if (job.Target != source.Job.Target)
                return Refusal.Rejected("The area must match the test run being investigated.");
        }

        var now = DateTimeOffset.UtcNow;
        var session = new InvestigationSession($"ses_{Guid.NewGuid():N}", project.Id, environment.RuntimeId,
            job.Description, SessionStatuses.Queued, now, now, job);
        if (!await store.CreateSessionAsync(session, ct))
            return Refusal.Rejected("Too many test runs are active or were started recently in this workspace. Try again later.");
        await Audit.RecordAsync(store, project.Id, session.Id, access.Actor, "session.started", ct);
        await hub.SendAsync(ProtocolEnvelopeFactory.CreateCommand(session.RuntimeId, session.Id,
            CloudCommandTypes.SessionStart, new { project_id = project.Id, job }), ct, retainWhenDisconnected: true);
        return session;
    }

    public async Task<Result<RuntimeSubscription>> SubscribeAsync(string id, CancellationToken ct)
    {
        var session = await store.GetSessionAsync(id, ct);
        if (session is null) return Refusal.NotFound("This test run was not found.");
        return hub.Subscribe(id) is { } subscription
            ? subscription : Refusal.Rejected("Too many pages are following this test run.");
    }

    public async Task<Refusal?> CancelAsync(BrowserAccess access, string id, CancellationToken ct)
    {
        var session = await store.GetSessionAsync(id, ct);
        if (session is null) return Refusal.NotFound("This test run was not found.");
        if (session.Status is SessionStatuses.Completed or SessionStatuses.Cancelled) return null;
        await store.UpdateSessionStatusAsync(id, SessionStatuses.Cancelled, ct);
        await store.DiscardCommandsForSessionAsync(id, ct);
        await Audit.RecordAsync(store, session.ProjectId, session.Id, access.Actor, "session.cancelled", ct);
        await hub.SendAsync(ProtocolEnvelopeFactory.CreateCommand(session.RuntimeId, session.Id,
            CloudCommandTypes.SessionCancel, new { }), ct, retainWhenDisconnected: true);
        return null;
    }

    public async Task<Refusal?> AnswerAsync(BrowserAccess access, string id, string? questionId, string? answer,
        CancellationToken ct)
    {
        questionId = questionId?.Trim();
        answer = answer?.Trim().ReplaceLineEndings("\n");
        if (string.IsNullOrWhiteSpace(questionId) || !ProtocolValidator.IsQuestionId(questionId) ||
            string.IsNullOrWhiteSpace(answer) || answer.Length > ProtocolConstants.MaximumAnswerLength ||
            answer.Any(character => char.IsControl(character) && character is not '\n' and not '\t'))
            return Refusal.Rejected("Enter an answer of up to 2,000 characters.");
        var session = await store.GetSessionAsync(id, ct);
        if (session is null) return Refusal.NotFound("This test run was not found.");
        if (session.Status != SessionStatuses.AwaitingInput)
            return Refusal.Rejected("The test run is not waiting for an answer.");

        AgentQuestion? outstanding = null;
        foreach (var envelope in await store.ListEventsAsync(id, ct))
            if (envelope.Type == RuntimeEventTypes.SessionQuestion &&
                ProtocolValidator.TryReadSessionQuestion(envelope.Payload, out var question, out _))
                outstanding = question;
        if (outstanding is null || outstanding.QuestionId != questionId ||
            !await store.TryUpdateSessionStatusAsync(id, SessionStatuses.AwaitingInput, SessionStatuses.Running, ct))
            return Refusal.Rejected("The question is no longer waiting for an answer.");

        var delivered = await hub.SendAsync(ProtocolEnvelopeFactory.CreateCommand(session.RuntimeId, session.Id,
            CloudCommandTypes.SessionAnswerQuestion, new { question_id = questionId, answer }), ct);
        if (!delivered)
        {
            await store.TryUpdateSessionStatusAsync(id, SessionStatuses.Running, SessionStatuses.AwaitingInput, ct);
            return Refusal.Rejected("The workspace connection is offline. Send your answer again when it reconnects.");
        }
        await Audit.RecordAsync(store, session.ProjectId, session.Id, access.Actor, "question.answered", ct);
        return null;
    }

    public async Task<Result<InvestigationSession>> RetryDiscoveryAsync(BrowserAccess access, string runId, CancellationToken ct)
    {
        var found = await RootAsync(runId, "Retry discovery from the original run.", ct);
        if (found.Refusal is { } refusal) return refusal;
        var root = found.Value!;
        var latest = (await store.ListRelatedSessionsAsync(root.ProjectId, root.Id, ct))
            .LastOrDefault(step => step.Job?.Type == TestingJob.DiscoverTests) ?? root;
        if (latest.Status is not (SessionStatuses.Completed or SessionStatuses.Cancelled))
            return Refusal.Rejected("Wait for discovery to finish or stop it before retrying.");
        var last = (await store.ListEventsAsync(latest.Id, ct)).LastOrDefault(item => item.Type == RuntimeEventTypes.SessionDiscovery);
        if (last is not null && ProtocolValidator.TryReadDiscovery(last.Payload, out var discovery, out _) &&
            discovery!.Status == "ready")
            return Refusal.Rejected("Discovery completed. Start a new run to search again.");
        return await StartStepAsync(root, root.Job! with { SourceSessionId = root.Id, RunId = root.Id }, access.Actor, ct);
    }

    public async Task<Result<InvestigationSession>> FindGapsAsync(BrowserAccess access, string runId, CancellationToken ct)
    {
        var found = await RootAsync(runId, "Find gaps from the original run.", ct);
        if (found.Refusal is { } refusal) return refusal;
        var root = found.Value!;
        return await StartStepAsync(root, new TestingJob(TestingJob.AnalyseTests, root.Job!.Target,
            Focus: root.Job.Focus, SourceSessionId: root.Id, RunId: root.Id), access.Actor, ct);
    }

    public async Task<Result<InvestigationSession>> AddTestAsync(BrowserAccess access, string runId, string? description,
        CancellationToken ct)
    {
        var found = await RootAsync(runId, "Add a test from the original run.", ct);
        if (found.Refusal is { } refusal) return refusal;
        var root = found.Value!;
        if (string.IsNullOrWhiteSpace(description)) return Refusal.Rejected("Enter a description for the new test.");
        return await StartStepAsync(root, new TestingJob(TestingJob.AddTest, root.Job!.Target,
            ExpectedBehaviour: description.Trim().ReplaceLineEndings("\n"), SourceSessionId: root.Id, RunId: root.Id), access.Actor, ct);
    }

    public async Task<Result<InvestigationSession>> InvestigateAsync(BrowserAccess access, string runId, string? sourceSessionId,
        CancellationToken ct)
    {
        var found = await RootAsync(runId, "Choose a completed result from the original run.", ct);
        if (found.Refusal is { } refusal) return refusal;
        var root = found.Value!;
        if (!TestingJob.IsSessionId(sourceSessionId)) return Refusal.Rejected("Choose a completed result from the original run.");
        var source = await store.GetSessionAsync(sourceSessionId!, ct);
        if (source is null || source.ProjectId != root.ProjectId || source.RuntimeId != root.RuntimeId ||
            source.Status != SessionStatuses.Completed || source.Job is null ||
            source.Id != root.Id && source.Job.RunId != root.Id || source.Job.Target != root.Job!.Target)
            return Refusal.Rejected("Choose a completed result from this run.");
        return await StartStepAsync(root, new TestingJob(TestingJob.InvestigateFailure, root.Job.Target,
            SourceSessionId: source.Id, RunId: root.Id), access.Actor, ct);
    }

    public async Task<Result<InvestigationSession>> AddGapTestAsync(BrowserAccess access, string runId, string? gapSessionId,
        int gapIndex, string batchId, CancellationToken ct)
    {
        var found = await RootAsync(runId, "Add gap tests from the original run.", ct);
        if (found.Refusal is { } refusal) return refusal;
        var root = found.Value!;
        return await AddGapTestAsync(root, gapSessionId, gapIndex, batchId, access.Actor, ct);
    }

    internal async Task<Result<InvestigationSession>> AddGapTestAsync(InvestigationSession root, string? gapSessionId,
        int gapIndex, string batchId, string actor, CancellationToken ct)
    {
        if (!Guid.TryParse(batchId, out _) || !TestingJob.IsSessionId(gapSessionId) ||
            gapIndex < 0 || gapIndex >= ProtocolConstants.MaximumSafeFindings)
            return Refusal.Rejected("Choose a valid gap.");
        var gap = await store.GetSessionAsync(gapSessionId!, ct);
        if (gap?.Job?.Type != TestingJob.AnalyseTests || gap.Job.RunId != root.Id ||
            gap.ProjectId != root.ProjectId || gap.RuntimeId != root.RuntimeId || gap.Status != SessionStatuses.Completed)
            return Refusal.Rejected("Choose a completed gap review from this run.");
        var completionEvent = (await store.ListEventsAsync(gap.Id, ct))
            .LastOrDefault(item => item.Type == RuntimeEventTypes.SessionCompleted);
        if (completionEvent is null || !ProtocolValidator.TryReadCompletion(completionEvent.Payload, out var completion, out _) ||
            completion!.ResultCode != ResultCodes.PotentialGapsFoundLocally || gapIndex >= completion.SafeFindings.Count)
            return Refusal.Rejected("Choose a gap found in this review.");
        var existing = (await store.ListRelatedSessionsAsync(root.ProjectId, root.Id, ct))
            .FirstOrDefault(step => step.Job?.Type == TestingJob.AddTest &&
                step.Job.SourceSessionId == gap.Id && step.Job.GapIndex == gapIndex && step.Job.GapBatchId == batchId);
        if (existing is not null) return existing;
        return await StartStepAsync(root, new TestingJob(TestingJob.AddTest, root.Job!.Target,
            ExpectedBehaviour: completion.SafeFindings[gapIndex], SourceSessionId: gap.Id,
            RunId: root.Id, GapIndex: gapIndex, GapBatchId: batchId), actor, ct);
    }

    public async Task<Result<InvestigationSession>> RunSelectedAsync(BrowserAccess access, string discoveryId,
        IReadOnlyList<string>? testIds, CancellationToken ct)
    {
        var discovery = await store.GetSessionAsync(discoveryId, ct);
        if (discovery is null) return Refusal.NotFound("The discovery was not found.");
        if (discovery.Job?.Type != TestingJob.DiscoverTests ||
            discovery.Status is not (SessionStatuses.Completed or SessionStatuses.Cancelled))
            return Refusal.Rejected("Wait for discovery to finish or stop it before selecting tests.");
        var root = discovery.Job.RunId is { } runId ? await store.GetSessionAsync(runId, ct) : discovery;
        if (root is null || root.ProjectId != discovery.ProjectId || root.RuntimeId != discovery.RuntimeId)
            return Refusal.NotFound("The original run was not found.");
        var latestDiscovery = (await store.ListRelatedSessionsAsync(root.ProjectId, root.Id, ct))
            .LastOrDefault(step => step.Job?.Type == TestingJob.DiscoverTests) ?? root;
        if (latestDiscovery.Id != discovery.Id)
            return Refusal.Rejected("These discovery results were replaced. Select tests from the latest discovery.");
        var job = new TestingJob(TestingJob.RunTests, discovery.Job.Target,
            SourceSessionId: discovery.Id, TestIds: testIds, RunId: root.Id);
        if (testIds is null || !job.TryValidate(out _)) return Refusal.Rejected("Select between 1 and 100 distinct tests.");
        var last = (await store.ListEventsAsync(discoveryId, ct)).LastOrDefault(item => item.Type == RuntimeEventTypes.SessionDiscovery);
        if (last is null || !ProtocolValidator.TryReadDiscovery(last.Payload, out var list, out _) ||
            testIds.Any(testId => !list!.Tests.Any(test => test.Id == testId)))
            return Refusal.Rejected("Choose tests from this discovery list.");
        return await StartStepAsync(root, job, access.Actor, ct);
    }

    public async Task<Result<InvestigationSession>> RerunSelectedAsync(BrowserAccess access, string discoveryId, string? testId,
        CancellationToken ct)
    {
        var discovery = await store.GetSessionAsync(discoveryId, ct);
        if (discovery is null) return Refusal.NotFound("The discovery was not found.");
        if (discovery.Job?.Type != TestingJob.DiscoverTests ||
            discovery.Status is not (SessionStatuses.Completed or SessionStatuses.Cancelled) || !TestingJob.IsTestId(testId))
            return Refusal.Rejected("Choose a test from a completed discovery.");
        var root = discovery.Job.RunId is { } runId ? await store.GetSessionAsync(runId, ct) : discovery;
        if (root?.Job?.Type != TestingJob.DiscoverTests || root.Job.RunId is not null ||
            root.ProjectId != discovery.ProjectId || root.RuntimeId != discovery.RuntimeId ||
            root.Job.Target != discovery.Job.Target)
            return Refusal.Rejected("The original run is unavailable.");
        var last = (await store.ListEventsAsync(discovery.Id, ct)).LastOrDefault(item => item.Type == RuntimeEventTypes.SessionDiscovery);
        if (last is null || !ProtocolValidator.TryReadDiscovery(last.Payload, out var list, out _) ||
            !list!.Tests.Any(test => test.Id == testId))
            return Refusal.Rejected("This test is unavailable in the saved discovery.");
        return await StartStepAsync(root, new TestingJob(TestingJob.RunTests, discovery.Job.Target,
            SourceSessionId: discovery.Id, TestIds: [testId!], RunId: root.Id), access.Actor, ct);
    }

    private async Task<Result<InvestigationSession>> RootAsync(string runId, string notRootMessage, CancellationToken ct)
    {
        var root = await store.GetSessionAsync(runId, ct);
        if (root is null) return Refusal.NotFound("This run was not found.");
        if (root.Job?.Type != TestingJob.DiscoverTests || root.Job.RunId is not null) return Refusal.Rejected(notRootMessage);
        return root;
    }

    internal async Task<Result<InvestigationSession>> StartStepAsync(InvestigationSession root, TestingJob job, string actor,
        CancellationToken ct)
    {
        if (!job.TryValidate(out var error)) return Refusal.Rejected(error);
        if (!hub.IsConnected(root.RuntimeId, root.ProjectId))
            return Refusal.Rejected("The run's runtime is offline. Reconnect it before continuing.");
        var now = DateTimeOffset.UtcNow;
        var step = new InvestigationSession($"ses_{Guid.NewGuid():N}", root.ProjectId, root.RuntimeId,
            job.Description, SessionStatuses.Queued, now, now, job);
        if (!await store.CreateSessionAsync(step, ct))
            return Refusal.Rejected("Another step is active in this run, or the run limit was reached.");
        var action = job.Type switch
        {
            TestingJob.DiscoverTests => "run.discovery_started",
            TestingJob.RunTests => "run.tests_selected",
            TestingJob.AnalyseTests => "run.gaps_started",
            _ => "run.test_creation_started"
        };
        await Audit.RecordAsync(store, root.ProjectId, step.Id, actor, action, ct);
        await hub.SendAsync(ProtocolEnvelopeFactory.CreateCommand(root.RuntimeId, step.Id,
            CloudCommandTypes.SessionStart, new { project_id = root.ProjectId, job }), ct, retainWhenDisconnected: true);
        return step;
    }
}
