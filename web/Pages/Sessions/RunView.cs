using System.Text.RegularExpressions;
using Quodery.Protocol;

namespace Quodery.Web.Pages.Sessions;

public sealed record TestRunItem(string Key, SessionDetail Step, string SourceId, string? TestId, bool IsAdded,
    string Description, string Title, PublicCompletion Completion, string Outcome)
{
    private bool IsProposed => IsAdded && Outcome == "proposed";
    public string OutcomeLabel => IsProposed ? Display.ProposedTestLabel(Step.TestState())
        : Display.OutcomeLabels.GetValueOrDefault(Outcome, "Couldn't Run");
    public string? TestStateSummary => IsProposed ? Display.ProposedTestSummary(Step.TestState()) : null;
}

public sealed partial class RunView
{
    private static readonly IReadOnlyDictionary<string, string> RunningBadges = new Dictionary<string, string>
    {
        [TestingJob.DiscoverTests] = "Discovering tests",
        [TestingJob.RunTests] = "Running selected tests",
        [TestingJob.AnalyseTests] = "Finding gaps",
        [TestingJob.AddTest] = "Creating a test"
    };

    public RunView(SessionDetail root, GapTestBatch? batch)
    {
        Root = root;
        Steps = [root, .. root.Related];
        Batch = batch;
        Running = Steps.FirstOrDefault(step => step.IsActive);
        ActiveDiscovery = ActiveOf(TestingJob.DiscoverTests);
        ActiveGaps = ActiveOf(TestingJob.AnalyseTests);
        ActiveTestCreation = ActiveOf(TestingJob.AddTest);
        ActiveTestRun = ActiveOf(TestingJob.RunTests);
        LatestRound = Steps.LastOrDefault(step => step.Job?.Type == TestingJob.DiscoverTests);
        LatestDiscovery = LatestRound?.Discovery();
        LatestGap = Steps.LastOrDefault(step => step.Job?.Type == TestingJob.AnalyseTests);
        var gapCompletion = LatestGap?.Completion();
        GapResultCode = LatestGap is { IsActive: false } ? gapCompletion?.ResultCode : null;
        Gaps = gapCompletion?.ResultCode == ResultCodes.PotentialGapsFoundLocally ? gapCompletion.SafeFindings : [];
        TestRuns = BuildTestRuns();
    }

    public SessionDetail Root { get; }
    public IReadOnlyList<SessionDetail> Steps { get; }
    public GapTestBatch? Batch { get; }
    public SessionDetail? Running { get; }
    public SessionDetail? ActiveDiscovery { get; }
    public SessionDetail? ActiveGaps { get; }
    public SessionDetail? ActiveTestCreation { get; }
    public SessionDetail? ActiveTestRun { get; }
    public SessionDetail? LatestRound { get; }
    public DiscoveryResult? LatestDiscovery { get; }
    public SessionDetail? LatestGap { get; }
    public string? GapResultCode { get; }
    public IReadOnlyList<string> Gaps { get; }
    public IReadOnlyList<TestRunItem> TestRuns { get; }

    public string Goal => Root.Session.Goal;
    public string? Behaviour => string.IsNullOrWhiteSpace(Root.Job?.Focus) ? null : Root.Job.Focus;

    public bool BatchActive => Batch is { Error: null };
    public bool Busy => Running is not null;
    public bool Locked => Busy || BatchActive;

    public SessionDetail? BannerStep => ActiveDiscovery ?? ActiveGaps ?? ActiveTestCreation ?? ActiveTestRun;
    public bool ShowBanner => BannerStep is not null || BatchActive;

    public string BadgeText => Running is not null
        ? RunningBadges.GetValueOrDefault(Running.Job?.Type ?? "", "Working")
        : BatchActive ? "Creating a test" : "Ready for next step";

    public string BannerTitle => BannerStep?.Job?.Type switch
    {
        TestingJob.DiscoverTests => "Finding tests",
        TestingJob.AnalyseTests => "Finding gaps",
        TestingJob.RunTests => "Running tests",
        _ => "Creating a test"
    };

    public string StopLabel => BannerStep?.Job?.Type switch
    {
        TestingJob.DiscoverTests => "Stop discovery",
        TestingJob.AnalyseTests => "Stop gap review",
        TestingJob.AddTest => "Stop test creation",
        TestingJob.RunTests => "Stop test run",
        _ => "Stop adding gap tests"
    };

    public SessionDetail? QuestionStep => (ActiveGaps ?? ActiveTestCreation ?? ActiveTestRun) is { Status: SessionStatuses.AwaitingInput } step &&
        step.Question() is not null ? step : null;

    public string BannerMessage
    {
        get
        {
            if (QuestionStep is { } asking)
                return asking.Job?.Type switch
                {
                    TestingJob.AnalyseTests => "Gap review needs your input before it can continue.",
                    TestingJob.AddTest => "Test creation needs your input before it can continue.",
                    _ => "Test run needs your input before it can continue."
                };
            if (ActiveDiscovery is { } discovery)
            {
                var count = discovery.Discovery()?.Tests.Count ?? 0;
                return $"Discovery is running. {count} test{(count == 1 ? "" : "s")} found so far. Stop discovery to use the session actions.";
            }
            if (ActiveGaps is not null) return "Gap review is running. Stop it to use the session actions.";
            if (ActiveTestCreation is { } creation)
                return Batch?.CurrentSessionId == creation.Id
                    ? $"Adding and running gap test {Batch.Started} of {Batch.GapIndexes.Count}. Stop it to cancel the remaining selection."
                    : "Test creation is running. Stop it to use the session actions.";
            if (ActiveTestRun is not null) return "The selected test is running. Stop it to cancel this execution.";
            return Batch is { } batch ? $"Preparing gap test {batch.Started + 1} of {batch.GapIndexes.Count}." : "";
        }
    }

    public string? BatchStatus => BatchActive
        ? $"Adding and running selected gap tests: {Batch!.Finished} of {Batch.GapIndexes.Count} finished." : null;

    public string FinishedTab => Steps.MaxBy(step => step.Session.CreatedAt)?.Job?.Type switch
    {
        TestingJob.AnalyseTests => "test-gaps",
        TestingJob.AddTest or TestingJob.RunTests => "test-runs",
        _ => "discovered-tests"
    };

    public IReadOnlyList<PublicTestDescriptor> DiscoveredTests => LatestDiscovery?.Tests ?? [];

    public string DiscoverySummary
    {
        get
        {
            var count = DiscoveredTests.Count;
            if (LatestRound is null) return "Test discovery hasn't completed yet.";
            if (LatestRound.IsActive) return $"Discovering relevant tests… {count} found so far.";
            if (LatestRound.Status == SessionStatuses.Cancelled || LatestDiscovery?.Status == "partial")
                return count > 0 ? $"Discovery is partial. {count} tests are available to review." : "Test discovery hasn't completed yet.";
            if (LatestDiscovery?.Status == "ready")
                return count > 0 ? $"{count} relevant (up to 100) tests found. Select the tests you want to run." : "No relevant tests found.";
            return "Test discovery hasn't completed yet.";
        }
    }

    public string EmptyDiscoveryMessage => LatestRound is { IsActive: true } ? "Finding tests…"
        : LatestRound?.Status == SessionStatuses.Cancelled || LatestDiscovery?.Status == "partial"
            ? "Discovery was interrupted before finding any tests. You can try again."
        : LatestDiscovery?.Status == "ready"
            ? "No relevant existing tests were found. You can find gaps or add a test in the other tabs."
        : "Discovery did not produce a list. You can try again.";

    public bool CanRetry => LatestRound is { IsActive: false } && LatestDiscovery?.Status != "ready";

    public bool GapsFound => GapResultCode == ResultCodes.PotentialGapsFoundLocally && Gaps.Count > 0;
    public bool NoGapsFound => GapResultCode == ResultCodes.NoGapsFound;

    public string GapExplanation => GapsFound
        ? $"{Gaps.Count} potential test gap{(Gaps.Count == 1 ? "" : "s")} found. Select the ones to add and run."
        : "Find gaps reviews the original area and behaviour. Up to 100 gaps can be returned.";

    public string GapReviewSummary => LatestGap is null ? "Find gaps to see potential tests here."
        : LatestGap.IsActive ? "Finding gaps…"
        : LatestGap.Status == SessionStatuses.Cancelled ? "Gap review was stopped. You can try finding gaps again."
        : "Gap review did not produce a list. You can try finding gaps again.";

    public bool CanFindGaps => LatestGap is not { IsActive: true };

    public IReadOnlyList<SessionDetail> Investigations =>
        Steps.Where(step => step.Job?.Type == TestingJob.InvestigateFailure).ToArray();

    public string Version => SessionEvents.Version(Steps,
        Batch is { } batch ? $"{batch.BatchId}:{batch.Started}:{batch.Finished}:{batch.Error}" : "");

    private SessionDetail? ActiveOf(string type) => Steps.FirstOrDefault(step => step.IsActive && step.Job?.Type == type);

    private IReadOnlyList<TestRunItem> BuildTestRuns()
    {
        var runs = new List<TestRunItem>();
        foreach (var step in Steps)
        {
            if (step.Completion() is not { } completion) continue;
            if (step.Job?.Type == TestingJob.AddTest)
            {
                runs.Add(new TestRunItem(step.Id, step, step.Id, null, true, step.Job.ExpectedBehaviour ?? "Added test",
                    completion.TestDescription ?? CompactDescription(step.Job.ExpectedBehaviour), completion,
                    completion.Outcome == PublicOutcomes.Proposed ? "proposed" : "could_not_run"));
            }
            else if (step.Job?.Type == TestingJob.RunTests &&
                Steps.FirstOrDefault(item => item.Id == step.Job.SourceSessionId) is { Job.Type: TestingJob.DiscoverTests } source)
            {
                var tests = source.Discovery()?.Tests ?? [];
                var results = step.TestResults().ToArray();
                foreach (var testId in step.Job.TestIds ?? [])
                {
                    var description = tests.FirstOrDefault(test => test.Id == testId)?.Description ?? "Discovered test";
                    var outcome = results.FirstOrDefault(result => result.TestId == testId)?.Outcome ?? "could_not_run";
                    runs.Add(new TestRunItem($"{step.Id}:{testId}", step, source.Id, testId, false, description, description,
                        completion, outcome));
                }
            }
        }
        runs.Reverse();
        return runs;
    }

    private static string CompactDescription(string? value)
    {
        var text = Whitespace().Replace(value ?? "", " ").Trim();
        if (text.Length == 0) return "Added test";
        var sentence = FirstSentence().Match(text) is { Success: true } match ? match.Value : text;
        if (sentence.Length <= 160) return sentence;
        var words = TrailingWord().Replace(sentence[..159], "").TrimEnd();
        return $"{(words.Length > 0 ? words : sentence[..159])}…";
    }

    [GeneratedRegex(@"\s+")] private static partial Regex Whitespace();
    [GeneratedRegex(@"^.*?[.!?](?=\s|$)")] private static partial Regex FirstSentence();
    [GeneratedRegex(@"\s+\S*$")] private static partial Regex TrailingWord();
}
