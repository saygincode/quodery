using Quodery.Protocol;
using Quodery.Web.Storage;

namespace Quodery.Web.Pages.Sessions;

public sealed class SessionView
{
    public SessionView(SessionDetail session)
    {
        Session = session;
        CompletedAt = session.CompletedAt();
        Question = session.Status == SessionStatuses.AwaitingInput ? session.Question() : null;
        var seen = session.Stages().ToHashSet(StringComparer.Ordinal);
        Stages = Display.StagesFor(session.Job?.Type).Select(stage => (Display.StageLabels[stage], seen.Contains(stage))).ToArray();
        Result = session.Completion() is { } completion ? ForDisplay(completion)
            : session.Status == SessionStatuses.Cancelled ? Synthetic(PublicOutcomes.Stopped, ResultCodes.Stopped)
            : session.Status == SessionStatuses.Completed ? Synthetic(PublicOutcomes.CouldNotRun, ResultCodes.RequestCouldNotBeResolved)
            : null;
    }

    public SessionDetail Session { get; }
    public TestingJob? Job => Session.Job;
    public string Id => Session.Id;
    public DateTimeOffset? CompletedAt { get; }
    public AgentQuestion? Question { get; }
    public IReadOnlyList<(string Label, bool Done)> Stages { get; }
    public PublicCompletion? Result { get; }

    public bool CanStop => Session.IsActive;
    public string StatusText => Result is not null ? ResultTitle : Display.Status(Session.Status);
    public string StatusValue => Result is not null ? SessionStatuses.Completed : Session.Status;

    private TestStateUpdate? TestState => Result?.Outcome == PublicOutcomes.Proposed ? Session.TestState() : null;
    public string ResultTitle => TestState is { } state ? Display.ProposedTestLabel(state) : Display.Result(Result?.ResultCode).Title;
    public string ResultSummary => Display.ProposedTestSummary(TestState) ??
        (string.IsNullOrWhiteSpace(Result?.SafeSummary) ? Display.Result(Result?.ResultCode).Summary : Result.SafeSummary);
    public string ResultIcon => Result?.Outcome switch { PublicOutcomes.Passed => "✓", PublicOutcomes.Failed => "×", PublicOutcomes.Proposed => "↗", _ => "!" };
    public string? ResultCounts => Result is { Tested: true } result ? Display.Counts(result) : null;

    public string? LocalEvidence => Result?.ResultCode == ResultCodes.PotentialGapsFoundLocally
        ? $"Detailed gap evidence is on the connected runtime at ~/.quodery/runtime/*/session-reports/{Id}.json."
        : Result?.LocalEvidenceAvailable == true
            ? "Detailed evidence stays with the connected runtime and was not uploaded to Quodery." : null;

    public IEnumerable<(string Term, string Value)> Request
    {
        get
        {
            var job = Job;
            (string, string?)[] rows =
            [
                ("Task", job is null ? "Test" : Display.TaskName(job.Type)), ("Area", job?.Target),
                ("Starting situation", job?.StartingSituation), ("Action", job?.Action),
                (job?.Type == TestingJob.AddTest ? "Description" : "Expected result", job?.ExpectedBehaviour), ("Focus", job?.Focus)
            ];
            return rows.Where(row => !string.IsNullOrEmpty(row.Item2)).Select(row => (row.Item1, row.Item2!));
        }
    }

    private bool Failed => Result?.Outcome == PublicOutcomes.Failed;
    public bool CanInvestigate => Failed && Result?.ResultCode != ResultCodes.LocalContextUnavailable &&
        Job?.Type != TestingJob.InvestigateFailure;
    public bool CanStartScenario => Failed && Job?.Type is not (TestingJob.AddTest or TestingJob.AnalyseTests);
    public bool BelongsToRunView => Job?.Type is TestingJob.AnalyseTests or TestingJob.AddTest;
    public bool CanCheckAnother => !BelongsToRunView && Result?.Outcome == PublicOutcomes.Passed;

    public string ScenarioUrl =>
        $"/?project_id={Uri.EscapeDataString(Session.Session.ProjectId)}&target={Uri.EscapeDataString(Job?.Target ?? "")}" +
        $"&behaviour={Uri.EscapeDataString(Job?.ExpectedBehaviour ?? Job?.Focus ?? "")}";

    public string BackToRunUrl => Job?.RunId is { } runId ? $"/sessions/{Uri.EscapeDataString(runId)}" : ScenarioUrl;

    public string Version => SessionEvents.Version([Session]);

    private PublicCompletion ForDisplay(PublicCompletion completion) =>
        completion.Outcome != PublicOutcomes.NeedsAttention || Job?.Type == TestingJob.AnalyseTests ? completion
            : completion with { Outcome = PublicOutcomes.CouldNotRun, ResultCode = ResultCodes.ChecksIncomplete };

    private static PublicCompletion Synthetic(string outcome, string resultCode) =>
        new(outcome, false, 0, 0, 0, 0, resultCode, null, [], false);
}
