using System.Globalization;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Html;
using Quodery.Protocol;

namespace Quodery.Web;

public static class Display
{
    public static readonly IReadOnlyDictionary<string, string> TaskLabels = new Dictionary<string, string>
    {
        [TestingJob.DiscoverTests] = "Discover relevant tests",
        [TestingJob.RunTests] = "Run existing tests",
        [TestingJob.AddTest] = "Request a new test",
        [TestingJob.AnalyseTests] = "Find gaps",
        [TestingJob.InvestigateFailure] = "Investigate failure"
    };

    public static readonly IReadOnlyDictionary<string, string> StatusLabels = new Dictionary<string, string>
    {
        [SessionStatuses.Queued] = "Waiting",
        [SessionStatuses.Running] = "Running",
        [SessionStatuses.AwaitingInput] = "Needs your input",
        [SessionStatuses.Completed] = "Completed",
        [SessionStatuses.Cancelled] = "Stopped"
    };

    public static readonly IReadOnlyDictionary<string, string> StageLabels = new Dictionary<string, string>
    {
        [ProgressStages.Preparing] = "Preparing",
        [ProgressStages.UnderstandingRequest] = "Understanding your request",
        [ProgressStages.FindingChecks] = "Finding relevant checks",
        [ProgressStages.RunningChecks] = "Running checks",
        [ProgressStages.ReviewingResult] = "Reviewing the result",
        [ProgressStages.FindingTestArea] = "Finding the right test area",
        [ProgressStages.CreatingTest] = "Writing the new test",
        [ProgressStages.ReviewingFailure] = "Reviewing the failed check",
        [ProgressStages.ReproducingIssue] = "Reproducing the issue"
    };

    public static readonly IReadOnlyDictionary<string, (string Title, string Summary)> ResultCopy =
        new Dictionary<string, (string, string)>
        {
            [ResultCodes.ChecksPassed] = ("Passed", "All checks passed."),
            [ResultCodes.ChecksFailed] = ("Failed", "One or more checks failed."),
            [ResultCodes.ChecksIncomplete] = ("Couldn't Run", "Some checks could not complete, so the result is inconclusive."),
            [ResultCodes.TestingEnvironmentLimited] = ("Couldn't Run", "The workspace runtime could not complete the required checks."),
            [ResultCodes.TestProposedPassing] = (ProposedLabel, "The new test passed when it ran. It is in the workspace, uncommitted, waiting for review."),
            [ResultCodes.TestProposedFailing] = (ProposedLabel, "The new test failed when it ran, which may point to a bug. It is in the workspace, uncommitted, waiting for review."),
            [ResultCodes.PotentialGapsFoundLocally] = ("Needs attention", "A potential testing gap was found. Open the connected runtime's local session report for the detailed finding."),
            [ResultCodes.NoGapsFound] = ("Passed", "No important testing gaps were identified in this review."),
            [ResultCodes.EnvironmentDisconnected] = ("Couldn't Run", "The workspace runtime disconnected before the check could complete."),
            [ResultCodes.RequestCouldNotBeResolved] = ("Couldn't Run", "The check could not be completed safely in the workspace runtime."),
            [ResultCodes.LocalContextUnavailable] = ("Couldn't Run", "The detailed local information for this test run is no longer available. Run the check again to investigate it."),
            [ResultCodes.TestCreationNotConfigured] = ("Couldn't Run", "New tests can't be proposed from this workspace yet. Ask the operator to check its code review setup."),
            [ResultCodes.TestCreationNotSafe] = ("Couldn't Run", "The automated test could not be safely created in the connected workspace."),
            [ResultCodes.TimedOut] = ("Couldn't Run", "The check took too long and could not be completed."),
            [ResultCodes.Stopped] = ("Stopped", "The test run was stopped."),
            [ResultCodes.OutOfScope] = ("Couldn't Run", "This request is outside the testing tasks Quodery can perform.")
        };

    public static (string Title, string Summary) Result(string? resultCode) =>
        resultCode is not null && ResultCopy.TryGetValue(resultCode, out var copy)
            ? copy : ("Could not run", "The check could not be completed in the workspace runtime.");

    public static readonly IReadOnlyDictionary<string, string> OutcomeLabels = new Dictionary<string, string>
    {
        ["passed"] = "Passed",
        ["failed"] = "Failed",
        ["could_not_run"] = "Couldn't Run",
        ["proposed"] = ProposedLabel
    };

    public static string TaskName(string? type) => type is not null && TaskLabels.TryGetValue(type, out var label) ? label : "Test run";

    public static string Status(string status) => StatusLabels.TryGetValue(status, out var label) ? label : "Waiting";

    public static IReadOnlyList<string> StagesFor(string? type) => type switch
    {
        TestingJob.AddTest => [ProgressStages.UnderstandingRequest, ProgressStages.FindingTestArea, ProgressStages.CreatingTest,
            ProgressStages.RunningChecks, ProgressStages.ReviewingResult],
        TestingJob.InvestigateFailure => [ProgressStages.ReviewingFailure, ProgressStages.ReproducingIssue, ProgressStages.ReviewingResult],
        TestingJob.DiscoverTests or TestingJob.AnalyseTests => [ProgressStages.Preparing, ProgressStages.UnderstandingRequest,
            ProgressStages.FindingChecks, ProgressStages.ReviewingResult],
        _ => [ProgressStages.Preparing, ProgressStages.UnderstandingRequest, ProgressStages.FindingChecks,
            ProgressStages.RunningChecks, ProgressStages.ReviewingResult]
    };

    public const string ProposedLabel = "Proposed, waiting for review";

    public static string ProposedTestLabel(TestStateUpdate? state) => state switch
    {
        { State: TestStates.Committed, Commit: { } commit } => $"Committed · {commit[..Math.Min(12, commit.Length)]}",
        { State: TestStates.Committed } => "Committed",
        { State: TestStates.Discarded } => "Discarded",
        _ => ProposedLabel
    };

    public static string? ProposedTestSummary(TestStateUpdate? state) => state?.State switch
    {
        TestStates.Committed => "The new test was committed to the workspace's repository.",
        TestStates.Discarded => "The new test was removed from the workspace without being committed.",
        _ => null
    };

    public static string UncommittedFiles(int count) =>
        $"{count:N0} uncommitted file{(count == 1 ? "" : "s")}";

    public static string GitResult(string code) => code switch
    {
        GitResultCodes.Ok => "The workspace was updated.",
        GitResultCodes.Busy => "A test run is using the workspace. Try again when it finishes.",
        GitResultCodes.UncommittedChanges => "The workspace has uncommitted changes. Commit or discard them first.",
        GitResultCodes.BranchNotFound => "That branch no longer exists. Get the latest changes to refresh the list.",
        GitResultCodes.NotFastForward => "This branch has commits that aren't on the remote, so it can't be updated here. Ask the operator to resolve it on the runtime machine.",
        GitResultCodes.NoUpstream => "This branch isn't linked to a remote branch, so there is nothing to get.",
        GitResultCodes.NothingToCommit => "There was nothing to commit.",
        GitResultCodes.RemoteUnavailable => "The remote repository couldn't be reached. Ask the operator to check the runtime's access to it.",
        GitResultCodes.TimedOut => "This took too long and was stopped.",
        _ => "This couldn't be completed. The details are in the runtime's local log."
    };

    public static string CodeVersion(PublicCompletion completion) => completion.CodeVersion is { } version
        ? $"{version[..Math.Min(12, version.Length)]}{(completion.CodeModified ? " with uncommitted changes" : "")}"
        : "Unknown";

    public static string Counts(PublicCompletion completion) =>
        $"{completion.Passed} passed · {completion.Failed} failed{(completion.Errors > 0 ? $" · {completion.Errors} could not complete" : "")}";

    public static IHtmlContent Time(DateTimeOffset value)
    {
        var iso = value.UtcDateTime.ToString("o", CultureInfo.InvariantCulture);
        var text = value.UtcDateTime.ToString("d MMM yyyy, HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
        return new HtmlString($"<time datetime=\"{HtmlEncoder.Default.Encode(iso)}\" data-local-time>{HtmlEncoder.Default.Encode(text)}</time>");
    }
}
