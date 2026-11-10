using Quodery.Protocol;

namespace Quodery.Web;

public sealed class WorkspaceContext(IHttpContextAccessor accessor, ProjectService projects)
{
    private const string CookieName = "quodery.workspace";
    private ProjectWithRuntime[]? _projects;

    public IReadOnlyList<ProjectWithRuntime> Projects => _projects ?? [];
    public ProjectWithRuntime? Current { get; private set; }
    public bool IsLoaded => _projects is not null;

    private HttpContext Context => accessor.HttpContext ?? throw new InvalidOperationException("No current request");

    public async Task<ProjectWithRuntime?> LoadAsync(string? requestedId, CancellationToken ct)
    {
        var all = await ListAsync(ct);
        var remembered = Context.Request.Cookies[CookieName];
        Current = all.FirstOrDefault(project => project.Id == requestedId) ??
            all.FirstOrDefault(project => project.Id == remembered) ?? all.FirstOrDefault();
        Remember(Current);
        return Current;
    }

    public async Task SelectAsync(string? projectId, CancellationToken ct)
    {
        Current = (await ListAsync(ct)).FirstOrDefault(project => project.Id == projectId);
        Remember(Current);
    }

    public async Task EnsureLoadedAsync(CancellationToken ct)
    {
        if (IsLoaded) return;
        var all = await ListAsync(ct);
        var remembered = Context.Request.Cookies[CookieName];
        Current = all.FirstOrDefault(project => project.Id == remembered) ?? all.FirstOrDefault();
    }

    public void Forget(string projectId)
    {
        if (Context.Request.Cookies[CookieName] == projectId) Context.Response.Cookies.Delete(CookieName);
    }

    private async Task<ProjectWithRuntime[]> ListAsync(CancellationToken ct) =>
        _projects ??= Context.FindAccess() is not null ? await projects.ListAsync(ct) : Array.Empty<ProjectWithRuntime>();

    private void Remember(ProjectWithRuntime? project)
    {
        if (project is null || Context.Request.Cookies[CookieName] == project.Id) return;
        Context.Response.Cookies.Append(CookieName, project.Id, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = Context.Request.IsHttps,
            IsEssential = true,
            MaxAge = TimeSpan.FromDays(365)
        });
    }
}
