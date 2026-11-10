using Microsoft.AspNetCore.Mvc;
using Quodery.Protocol;

namespace Quodery.Web.Pages;

public sealed record HistoryRow(SessionHistoryItem Item, string Outcome, string OutcomeLabel);

public sealed class HistoryModel(WorkspaceContext workspace, SessionService sessions, ServingMode serving) : AppPageModel
{
    public static readonly IReadOnlyList<(string Value, string Label)> Filters =
    [
        ("all", "All outcomes"), ("discovered", "Tests found"), ("partial", "Partial discovery"), ("passed", "Passed"),
        ("failed", "Failed"), ("proposed", "Proposed tests"), ("committed", "Committed tests"),
        ("discarded", "Discarded tests"), ("needs_attention", "Needs attention"),
        ("could_not_run", "Could not run"), ("running", "In progress")
    ];

    [BindProperty(SupportsGet = true, Name = "project_id")] public string? ProjectId { get; set; }
    [BindProperty(SupportsGet = true)] public string Outcome { get; set; } = "all";

    public ProjectWithRuntime? Project { get; private set; }
    public IReadOnlyList<HistoryRow> Rows { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        Project = await workspace.LoadAsync(ProjectId, ct);
        if (Project is null) return Page();
        if (Access.Role == "admin" && !Project.Runtime.IsConfigured && !serving.ServesRuntime)
            return RedirectToPage("/Setup", new { project_id = Project.Id });
        var history = await sessions.HistoryAsync(Project.Id, ct);
        if (history.Refusal is { } refusal) return Refused(refusal, "/");
        Rows = history.Value!.Select(item => new HistoryRow(item, OutcomeOf(item), LabelOf(item)))
            .Where(row => Outcome == "all" || row.Outcome == Outcome)
            .ToArray();
        return Page();
    }

    private static bool IsActive(string status) =>
        status is SessionStatuses.Queued or SessionStatuses.Running or SessionStatuses.AwaitingInput;

    private static string OutcomeOf(SessionHistoryItem item)
    {
        if (item.Discovery is { } discovery && !IsActive(item.Status))
            return item.Status == SessionStatuses.Cancelled || discovery.Status == "partial" ||
                discovery.TestCount > 0 && discovery.Status != "ready" ? "partial"
                : discovery.Status == "ready" ? "discovered" : "could_not_run";
        if (item.Result?.Outcome == PublicOutcomes.NeedsAttention && item.LatestTaskType != TestingJob.AnalyseTests)
            return item.LatestIsAddedTest && item.Result.Passed > 0 && item.Result.Failed == 0 ? "passed" : "could_not_run";
        if (item.Result?.Outcome == PublicOutcomes.Proposed && item.TestState is { } state) return state.State;
        return item.Result?.Outcome ?? (IsActive(item.Status) ? "running"
            : item.Status == SessionStatuses.Cancelled ? "stopped" : "could_not_run");
    }

    private static string LabelOf(SessionHistoryItem item)
    {
        var testCount = item.Discovery?.TestCount ?? 0;
        var label = OutcomeOf(item) switch
        {
            "discovered" => $"{testCount} tests found",
            "partial" => $"Partial discovery · {testCount} tests found",
            "passed" => "Passed",
            "failed" => "Failed",
            "proposed" or TestStates.Committed or TestStates.Discarded => Display.ProposedTestLabel(item.TestState),
            "needs_attention" => "Needs attention",
            "stopped" => "Stopped",
            "running" => item.Status == SessionStatuses.AwaitingInput ? "Needs your input"
                : item.Status == SessionStatuses.Queued ? "Waiting" : "Running",
            _ => "Couldn't Run"
        };
        var counts = item.Result is { Tested: true } result
            ? $" · {result.Passed} passed{(result.Failed > 0 ? $", {result.Failed} failed" : "")}" : "";
        return label + counts;
    }
}
