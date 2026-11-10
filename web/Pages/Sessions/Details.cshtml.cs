using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Quodery.Protocol;

namespace Quodery.Web.Pages.Sessions;

public sealed class DetailsModel(SessionService sessions, AccountService accounts, GapTestBatches batches,
    WorkspaceContext workspace) : AppPageModel
{
    public RunView? Run { get; private set; }
    public SessionView? Single { get; private set; }

    public async Task<IActionResult> OnGetAsync(string id, CancellationToken ct)
    {
        var found = await sessions.GetAsync(id, ct);
        if (found.Refusal is { } refusal) return Refused(refusal, "/");
        var detail = found.Value!;
        if (detail.Job?.Type is TestingJob.DiscoverTests or TestingJob.AnalyseTests && detail.Job.RunId is { } runId)
            return Redirect(SessionUrl(runId));
        await workspace.SelectAsync(detail.Session.ProjectId, ct);
        Load(detail);
        return Page();
    }

    public async Task<IActionResult> OnGetLiveAsync(string id, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-cache, no-store";
        var found = await sessions.GetAsync(id, ct);
        if (found.Refusal is { } refusal) return refusal.Kind == RefusalKind.Forbidden ? Forbid() : NotFound();
        Load(found.Value!);
        return Run is not null ? Partial("_RunLive", this) : Partial("_SessionLive", this);
    }

    public async Task<IActionResult> OnGetEventsAsync(string id, CancellationToken ct)
    {
        var subscribed = await sessions.SubscribeAsync(id, ct);
        if (subscribed.Refusal is { } refusal)
            return refusal.Kind switch
            {
                RefusalKind.NotFound => NotFound(),
                RefusalKind.Forbidden => Forbid(),
                _ => StatusCode(StatusCodes.Status429TooManyRequests)
            };
        await using var subscription = subscribed.Value!;
        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache, no-transform";
        await Response.StartAsync(ct);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var available = subscription.Reader.WaitToReadAsync(ct).AsTask();
                if (await Task.WhenAny(available, Task.Delay(TimeSpan.FromSeconds(15), ct)) != available)
                {
                    if (await accounts.AuthoriseAsync(User.FindFirstValue(ClaimTypes.NameIdentifier),
                            User.FindFirstValue("quodery:session_version"), ct) is null) break;
                    await Response.WriteAsync(": heartbeat\n\n", ct);
                    await Response.Body.FlushAsync(ct);
                    continue;
                }
                if (!await available) break;
                while (subscription.Reader.TryRead(out var envelope))
                    await Response.WriteAsync($"data: {envelope.Type}\n\n", ct);
                await Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        return new EmptyResult();
    }

    public async Task<IActionResult> OnPostStopAsync(string id, string? stepId, CancellationToken ct)
    {
        batches.Dismiss(id);
        if (batches.Get(id) is { } batch && batch.CurrentSessionId == stepId) batches.Cancel(id);
        return Done(await sessions.CancelAsync(Access, stepId ?? id, ct), id);
    }

    public async Task<IActionResult> OnPostAnswerAsync(string id, string? stepId, string? questionId, string? answer,
        string? choice, CancellationToken ct)
    {
        batches.Dismiss(id);
        var reply = string.IsNullOrWhiteSpace(answer) ? choice : answer;
        return Done(await sessions.AnswerAsync(Access, stepId ?? id, questionId, reply, ct), id);
    }

    public async Task<IActionResult> OnPostRetryDiscoveryAsync(string id, CancellationToken ct)
    {
        batches.Dismiss(id);
        return Done((await sessions.RetryDiscoveryAsync(Access, id, ct)).Refusal, id);
    }

    public async Task<IActionResult> OnPostFindGapsAsync(string id, CancellationToken ct)
    {
        batches.Dismiss(id);
        return Done((await sessions.FindGapsAsync(Access, id, ct)).Refusal, id);
    }

    public async Task<IActionResult> OnPostAddTestAsync(string id, string? description, CancellationToken ct)
    {
        batches.Dismiss(id);
        return Done((await sessions.AddTestAsync(Access, id, description, ct)).Refusal, id);
    }

    public async Task<IActionResult> OnPostRunTestsAsync(string id, string roundId, string[]? testIds, CancellationToken ct)
    {
        batches.Dismiss(id);
        return Done((await sessions.RunSelectedAsync(Access, roundId, testIds, ct)).Refusal, id);
    }

    public async Task<IActionResult> OnPostRerunAsync(string id, string sourceId, string? testId, CancellationToken ct)
    {
        batches.Dismiss(id);
        return Done((await sessions.RerunSelectedAsync(Access, sourceId, testId, ct)).Refusal, id);
    }

    public async Task<IActionResult> OnPostAddGapTestsAsync(string id, string? gapSessionId, int[]? gapIndexes,
        CancellationToken ct)
    {
        batches.Dismiss(id);
        return Done(await batches.StartAsync(Access, id, gapSessionId, gapIndexes ?? [], ct), id);
    }

    public async Task<IActionResult> OnPostInvestigateAsync(string id, CancellationToken ct)
    {
        var found = await sessions.GetAsync(id, ct);
        if (found.Refusal is { } refusal) return Refused(refusal, SessionUrl(id));
        var session = found.Value!;
        var started = session.Job?.RunId is not null || session.Job?.Type == TestingJob.DiscoverTests
            ? await sessions.InvestigateAsync(Access, session.Job!.RunId ?? id, id, ct)
            : await sessions.StartAsync(Access, session.Session.ProjectId,
                new TestingJob(TestingJob.InvestigateFailure, session.Job?.Target ?? "", SourceSessionId: id), ct);
        return Started(started, id, "The investigation could not be started. Please try again.");
    }

    public async Task<IActionResult> OnPostRunAgainAsync(string id, CancellationToken ct)
    {
        var found = await sessions.GetAsync(id, ct);
        if (found.Refusal is { } refusal) return Refused(refusal, SessionUrl(id));
        var session = found.Value!;
        if (session.Job?.RunId is { } runId) return Redirect(SessionUrl(runId));
        if (session.Job is not { } original) return RedirectWithError("The next test run could not be started. Please try again.", SessionUrl(id));
        var job = original with { SourceSessionId = null };
        if (job.Type is TestingJob.InvestigateFailure or TestingJob.RunTests) job = job with { Type = TestingJob.DiscoverTests, TestId = null };
        return Started(await sessions.StartAsync(Access, session.Session.ProjectId, job, ct), id,
            "The next test run could not be started. Please try again.");
    }

    public static string SessionUrl(string id) => $"/sessions/{Uri.EscapeDataString(id)}";

    private void Load(SessionDetail detail)
    {
        if (detail.Job?.Type == TestingJob.DiscoverTests && detail.Job.RunId is null)
            Run = new RunView(detail, batches.Get(detail.Id));
        else
            Single = new SessionView(detail);
    }

    private IActionResult Done(Refusal? refusal, string id) =>
        refusal is null ? Redirect(SessionUrl(id)) : Refused(refusal, SessionUrl(id));

    private IActionResult Started(Result<InvestigationSession> started, string id, string failure)
    {
        if (started.Refusal is { Kind: RefusalKind.Forbidden }) return Forbid();
        return started.Refusal is null ? Redirect(SessionUrl(started.Value!.Id)) : RedirectWithError(failure, SessionUrl(id));
    }
}
