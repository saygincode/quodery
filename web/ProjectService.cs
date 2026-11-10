using Quodery.Protocol;
using Quodery.Web.Runtime;
using Quodery.Web.Storage;

namespace Quodery.Web;

public sealed record ProjectConnection(bool Connected, bool IsActivated, DateTimeOffset? ActivatedAt, DateTimeOffset? LastSeenAt);
public sealed record RuntimeVersionInfo(string? CurrentVersion, string ServerVersion, string Status, bool Connected);
public sealed record RuntimeKey(string Token);

public sealed class ProjectService(ISessionStore store, RuntimeHub hub, ServingMode serving)
{
    private static readonly Refusal ServingRuntimeWorkspace =
        Refusal.Rejected("This server's own runtime manages its workspace and connection.");

    public async Task<ProjectWithRuntime[]> ListAsync(CancellationToken ct)
    {
        var results = new List<ProjectWithRuntime>();
        foreach (var project in await store.ListProjectsAsync(ct))
        {
            var environment = await store.GetProjectConnectionAsync(project.Id, ct);
            results.Add(new(project.Id, project.Name, project.RuntimeId,
                new RuntimeSummary(project.RuntimeId,
                    hub.IsConnected(project.RuntimeId, project.Id) ? "connected" : "disconnected",
                    hub.LastSeen(project.RuntimeId, project.Id), environment?.ActivatedAt is not null)));
        }
        return [.. results];
    }

    public async Task<Result<ProjectWithRuntime>> CreateAsync(BrowserAccess access, string? name, CancellationToken ct)
    {
        if (access.Role != "admin") return Refusal.Forbidden();
        if (serving.ServesRuntime) return ServingRuntimeWorkspace;
        name = name?.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100 || name.Any(char.IsControl))
            return Refusal.Rejected("Enter a workspace name of up to 100 characters.");
        var now = DateTimeOffset.UtcNow;
        var projectId = $"prj_{Guid.NewGuid():N}";
        var runtimeId = $"run_{Guid.NewGuid():N}";
        var project = new Project(projectId, name, runtimeId);
        var environment = new RuntimeEnvironment($"env_{Guid.NewGuid():N}", projectId, runtimeId, name,
            null, now, null, null, null);
        await store.CreateProjectAsync(project, environment, ct);
        await Audit.RecordAsync(store, project.Id, null, access.Actor, "project.created", ct);
        return new ProjectWithRuntime(project.Id, project.Name, project.RuntimeId,
            new RuntimeSummary(project.RuntimeId, "disconnected", null, false));
    }

    public async Task<Refusal?> DeleteAsync(BrowserAccess access, string projectId, CancellationToken ct)
    {
        if (access.Role != "admin") return Refusal.Forbidden();
        if (serving.ServesRuntime) return ServingRuntimeWorkspace;
        var project = await store.GetProjectAsync(projectId, ct);
        if (project is null || !await hub.DeleteProjectAsync(project.RuntimeId, projectId, ct))
            return Refusal.NotFound("The workspace was not found.");
        return null;
    }

    public async Task<Result<ProjectConnection>> GetConnectionAsync(string projectId, CancellationToken ct)
    {
        var project = await store.GetProjectAsync(projectId, ct);
        if (project is null) return Refusal.NotFound("The workspace was not found.");
        var connection = await store.GetProjectConnectionAsync(projectId, ct);
        if (connection is null) return Refusal.NotFound("The workspace connection was not found.");
        return new ProjectConnection(hub.IsConnected(connection.RuntimeId, project.Id),
            connection.ActivatedAt is not null, connection.ActivatedAt,
            hub.LastSeen(connection.RuntimeId, project.Id));
    }

    public async Task<Result<RuntimeVersionInfo>> GetRuntimeVersionAsync(string projectId, CancellationToken ct)
    {
        var project = await store.GetProjectAsync(projectId, ct);
        if (project is null) return Refusal.NotFound("The workspace was not found.");
        var currentVersion = hub.RuntimeVersion(project.RuntimeId, projectId);
        var status = hub.IsIncompatible(project.RuntimeId, projectId) ? "incompatible"
            : Version.TryParse(currentVersion, out var runtimeVersion) &&
              Version.TryParse(RuntimeHub.ServerVersion, out var serverVersion) && runtimeVersion < serverVersion
                ? "outdated" : "current";
        return new RuntimeVersionInfo(currentVersion, RuntimeHub.ServerVersion, status,
            hub.IsConnected(project.RuntimeId, projectId));
    }

    public GitWorkspaceState? GetGitState(ProjectWithRuntime project) => hub.GitState(project.RuntimeId, project.Id);

    public async Task<Result<RuntimeKey>> RefreshConnectionAsync(BrowserAccess access, string projectId, CancellationToken ct)
    {
        if (access.Role != "admin") return Refusal.Forbidden();
        if (serving.ServesRuntime) return ServingRuntimeWorkspace;
        var project = await store.GetProjectAsync(projectId, ct);
        if (project is null)
            return Refusal.NotFound("The workspace was not found.");
        var connection = await store.GetProjectConnectionAsync(projectId, ct);
        if (connection is null) return Refusal.NotFound("The workspace connection was not found.");
        var key = await hub.ReplaceRuntimeKeyAsync(connection, ct);
        if (key is null) return Refusal.NotFound("The workspace has no active connection to refresh.");
        await Audit.RecordAsync(store, projectId, null, access.Actor, "environment.refreshed", ct);
        return new RuntimeKey(key);
    }

    public async Task<Result<RuntimeKey>> CreateRuntimeKeyAsync(BrowserAccess access, string projectId, CancellationToken ct)
    {
        if (access.Role != "admin") return Refusal.Forbidden();
        if (serving.ServesRuntime) return ServingRuntimeWorkspace;
        var project = await store.GetProjectAsync(projectId, ct);
        if (project is null) return Refusal.NotFound("The workspace was not found.");
        var environment = await store.GetProjectConnectionAsync(project.Id, ct);
        if (environment is null) return Refusal.NotFound("The workspace connection was not found.");
        if (environment.ActivatedAt is not null)
            return Refusal.Rejected("The workspace is already connected. Refresh its connection to replace its runtime key.");
        var key = await hub.ReplaceRuntimeKeyAsync(environment, ct);
        if (key is null) return Refusal.NotFound("The workspace connection was not found.");
        await Audit.RecordAsync(store, project.Id, null, access.Actor, "environment.key_created", ct);
        return new RuntimeKey(key);
    }

    public async Task<bool> HasPendingRuntimeKeyAsync(BrowserAccess access, string projectId, CancellationToken ct)
    {
        if (access.Role != "admin") return false;
        var environment = await store.GetProjectConnectionAsync(projectId, ct);
        return environment is { KeyHash: not null, ActivatedAt: null };
    }
}
