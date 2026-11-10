using Microsoft.AspNetCore.Mvc;
using Quodery.Protocol;

namespace Quodery.Web.Pages;

public sealed class WorkspacesModel(WorkspaceContext workspace, ProjectService projects, ServingMode serving) : AppPageModel
{
    [BindProperty(SupportsGet = true, Name = "project_id")] public string? ProjectId { get; set; }

    public IReadOnlyList<ProjectWithRuntime> Projects => workspace.Projects;
    public ProjectWithRuntime? Current => workspace.Current;
    public bool IsAdmin => Access.Role == "admin";

    public ProjectWithRuntime? RefreshedProject { get; private set; }
    public RuntimeKey? RefreshedKey { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (serving.ServesRuntime) return RedirectToPage("/Index");
        await workspace.LoadAsync(ProjectId, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync(string id, CancellationToken ct)
    {
        if (await projects.DeleteAsync(Access, id, ct) is { } refusal) return Refused(refusal, Url.Page("/Workspaces")!);
        workspace.Forget(id);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRefreshAsync(string id, CancellationToken ct)
    {
        var refreshed = await projects.RefreshConnectionAsync(Access, id, ct);
        if (refreshed.Refusal is { } refusal) return Refused(refusal, Url.Page("/Workspaces")!);
        PreventCaching();
        await workspace.LoadAsync(id, ct);
        RefreshedProject = workspace.Current;
        RefreshedKey = refreshed.Value;
        return Page();
    }
}
