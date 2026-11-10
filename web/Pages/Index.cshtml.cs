using Microsoft.AspNetCore.Mvc;
using Quodery.Protocol;

namespace Quodery.Web.Pages;

public sealed record Readiness(ProjectWithRuntime? Project, ProjectConnection? Connection, bool IsAdmin,
    bool ServesRuntime = false)
{
    public bool IsReady => Connection is { Connected: true, IsActivated: true };
    public bool NeedsSetup => Project is not null && Connection is not { IsActivated: true };
}

public sealed class IndexModel(WorkspaceContext workspace, ProjectService projects, SessionService sessions,
    ServingMode serving) : AppPageModel
{
    [BindProperty(SupportsGet = true, Name = "project_id")] public string? ProjectId { get; set; }
    [BindProperty(SupportsGet = true)] public string? Target { get; set; }
    [BindProperty(SupportsGet = true)] public string? Behaviour { get; set; }

    public Readiness Readiness { get; private set; } = new(null, null, false);

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        Readiness = await LoadReadinessAsync(ct);
        if (Readiness.IsAdmin && !Readiness.ServesRuntime && (Readiness.Project is null || Readiness.NeedsSetup))
            return RedirectToPage("/Setup", new { project_id = Readiness.Project?.Id });
        return Page();
    }

    public async Task<IActionResult> OnGetReadinessAsync(CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        return Partial("_Readiness", await LoadReadinessAsync(ct));
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var focus = Behaviour?.Trim().ReplaceLineEndings("\n");
        var job = new TestingJob(TestingJob.DiscoverTests, Target?.Trim() ?? string.Empty,
            Focus: string.IsNullOrEmpty(focus) ? null : focus);
        var started = await sessions.StartAsync(Access, ProjectId, job, ct);
        if (started.Refusal is { } refusal)
        {
            if (refusal.Kind == RefusalKind.Forbidden) return Forbid();
            Readiness = await LoadReadinessAsync(ct);
            Error = refusal.Message;
            return Page();
        }
        return Redirect($"/sessions/{Uri.EscapeDataString(started.Value!.Id)}");
    }

    private async Task<Readiness> LoadReadinessAsync(CancellationToken ct)
    {
        var project = await workspace.LoadAsync(ProjectId, ct);
        ProjectId = project?.Id;
        var connection = project is null ? null : (await projects.GetConnectionAsync(project.Id, ct)).Value;
        return new Readiness(project, connection, Access.Role == "admin", serving.ServesRuntime);
    }
}
