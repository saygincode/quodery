using Quodery.Protocol;
using Quodery.Web.Runtime;
using Quodery.Web.Storage;

namespace Quodery.Web;

public sealed record GitWorkspaceState(GitStatus Status, string? LastResult, bool Working);

public sealed class GitService(ISessionStore store, RuntimeHub hub)
{
    public const string DiscardConfirmation = "DISCARD";

    public async Task<Refusal?> SwitchBranchAsync(BrowserAccess access, string? projectId, string? branch, CancellationToken ct)
    {
        var found = await FindAsync(projectId, ct);
        if (found.Refusal is { } refusal) return refusal;
        var (project, state) = found.Value!;
        if (branch is null || !state.Status.Branches.Contains(branch, StringComparer.Ordinal))
            return Refusal.Rejected("Choose a branch from the list.");
        return await SendAsync(access, project, CloudCommandTypes.GitSwitchBranch, new { branch }, "git.branch_switched", ct);
    }

    public async Task<Refusal?> PullAsync(BrowserAccess access, string? projectId, CancellationToken ct)
    {
        var found = await FindAsync(projectId, ct);
        if (found.Refusal is { } refusal) return refusal;
        return await SendAsync(access, found.Value!.Project, CloudCommandTypes.GitPull, new { }, "git.pulled", ct);
    }

    public async Task<Refusal?> CommitAsync(BrowserAccess access, string? projectId, string? message, CancellationToken ct)
    {
        message = message?.Trim();
        if (!ProtocolValidator.IsCommitMessage(message))
            return Refusal.Rejected($"Enter a one-line commit message of up to {ProtocolConstants.MaximumCommitMessageLength} characters.");
        var found = await FindAsync(projectId, ct);
        if (found.Refusal is { } refusal) return refusal;
        return await SendAsync(access, found.Value!.Project, CloudCommandTypes.GitCommit, new { message }, "git.committed", ct);
    }

    public async Task<Refusal?> DiscardAsync(BrowserAccess access, string? projectId, string? confirmation, CancellationToken ct)
    {
        if (confirmation?.Trim() != DiscardConfirmation) return Refusal.Rejected($"Type {DiscardConfirmation} to confirm.");
        var found = await FindAsync(projectId, ct);
        if (found.Refusal is { } refusal) return refusal;
        return await SendAsync(access, found.Value!.Project, CloudCommandTypes.GitDiscard, new { }, "git.discarded", ct);
    }

    private async Task<Result<GitTarget>> FindAsync(string? projectId, CancellationToken ct)
    {
        var project = string.IsNullOrWhiteSpace(projectId) ? null : await store.GetProjectAsync(projectId, ct);
        if (project is null) return Refusal.NotFound("The workspace was not found.");
        if (hub.GitState(project.RuntimeId, project.Id) is not { } state)
            return Refusal.Rejected("This workspace's runtime doesn't allow git actions.");
        return new GitTarget(project, state);
    }

    private async Task<Refusal?> SendAsync(BrowserAccess access, Project project, string type, object payload, string action,
        CancellationToken ct)
    {
        if (!await hub.SendGitCommandAsync(project.RuntimeId, project.Id, type, payload, ct))
            return Refusal.Rejected("The runtime is offline or still working. Try again in a moment.");
        await Audit.RecordAsync(store, project.Id, null, access.Actor, action, ct);
        return null;
    }

    private sealed record GitTarget(Project Project, GitWorkspaceState State);
}
