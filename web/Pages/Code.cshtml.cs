using Microsoft.AspNetCore.Mvc;

namespace Quodery.Web.Pages;

public sealed record CodePanel(string ProjectId, GitWorkspaceState State, bool Connected)
{
    public string Version => string.Join('|', State.Status.Branch, State.Status.Commit, State.Status.ChangedFiles, State.Status.ProposedTests,
        State.LastResult, State.Working, Connected, string.Join(',', State.Status.Branches).GetHashCode());
}

public sealed class CodeModel(WorkspaceContext workspace, ProjectService projects, GitService git) : AppPageModel
{
    [BindProperty(SupportsGet = true, Name = "project_id")] public string? ProjectId { get; set; }

    public CodePanel? Panel { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        Panel = await LoadPanelAsync(ct);
        return Panel is null ? RedirectToPage("/Index") : Page();
    }

    public async Task<IActionResult> OnGetPanelAsync(CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        return await LoadPanelAsync(ct) is { } panel ? Partial("_CodePanel", panel) : Content(string.Empty);
    }

    public async Task<IActionResult> OnPostSwitchAsync(string? branch, CancellationToken ct) =>
        Finished(await git.SwitchBranchAsync(Access, ProjectId, branch, ct));

    public async Task<IActionResult> OnPostPullAsync(CancellationToken ct) =>
        Finished(await git.PullAsync(Access, ProjectId, ct));

    public async Task<IActionResult> OnPostCommitAsync(string? message, CancellationToken ct) =>
        Finished(await git.CommitAsync(Access, ProjectId, message, ct));

    public async Task<IActionResult> OnPostDiscardAsync(string? confirmation, CancellationToken ct) =>
        Finished(await git.DiscardAsync(Access, ProjectId, confirmation, ct));

    private IActionResult Finished(Refusal? refusal)
    {
        var page = Url.Page("/Code", new { project_id = ProjectId })!;
        return refusal is null ? LocalRedirect(page) : Refused(refusal, page);
    }

    private async Task<CodePanel?> LoadPanelAsync(CancellationToken ct)
    {
        var project = await workspace.LoadAsync(ProjectId, ct);
        ProjectId = project?.Id;
        return project is not null && projects.GetGitState(project) is { } state
            ? new CodePanel(project.Id, state, project.Runtime.Status == "connected") : null;
    }
}
