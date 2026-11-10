using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Quodery.Web.Pages;

public sealed class WorkspaceStatusModel(WorkspaceContext workspace, ProjectService projects) : PageModel
{
    public async Task<IActionResult> OnGetAsync([FromQuery(Name = "project_id")] string? projectId, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        await workspace.SelectAsync(projectId, ct);
        return Partial("_WorkspaceStatus", await WorkspaceStatus.LoadAsync(projects, workspace.Current, ct));
    }
}
