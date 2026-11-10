using Quodery.Protocol;

namespace Quodery.Web;

public sealed record WorkspaceStatus(ProjectWithRuntime? Project, RuntimeVersionInfo? Version, GitWorkspaceState? Git = null)
{
    public string? BranchText => Git?.Status is { } status
        ? status.Branch ?? status.Commit?[..Math.Min(12, status.Commit.Length)] ?? "No commits" : null;

    public int ChangedFiles => Git?.Status.ChangedFiles ?? 0;

    public string State => Project is null ? "no-projects" : !Project.Runtime.IsConfigured ? "needs-setup" :
        Project.Runtime.Status == "connected" ? "ready" : "offline";

    public string StateLabel => State switch
    {
        "no-projects" => "No workspace selected",
        "needs-setup" => "Runtime needs setup",
        "ready" => "Runtime connected",
        _ => "Runtime offline"
    };

    public string VersionStatus => Version?.Status ?? "current";

    public string VersionText => (VersionStatus, Version?.CurrentVersion) switch
    {
        ("incompatible", var current) => $"{current ?? "Runtime"} · can't connect",
        ("outdated", var current) => $"{current} · update to {Version!.ServerVersion}",
        (_, { } current) => current,
        _ => "N/A"
    };

    public string VersionLabel => (VersionStatus, Version?.CurrentVersion) switch
    {
        ("incompatible", var current) => $"{(current is null ? "The runtime" : $"Runtime version {current}")} uses a different protocol than this server and can't connect. Install quodery {Version!.ServerVersion} on the runtime machine.",
        ("outdated", var current) => $"Runtime version {current} is older than this server ({Version!.ServerVersion}). Install quodery {Version.ServerVersion} on the runtime machine.",
        (_, { } current) => $"Runtime version {current}",
        _ => "Runtime version unavailable"
    };

    public static async Task<WorkspaceStatus> LoadAsync(ProjectService projects, ProjectWithRuntime? project,
        CancellationToken ct)
    {
        if (project is null) return new(null, null);
        var version = await projects.GetRuntimeVersionAsync(project.Id, ct);
        return new(project, version.Value, projects.GetGitState(project));
    }
}
