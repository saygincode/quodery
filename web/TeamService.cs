using Quodery.Web.Storage;

namespace Quodery.Web;

public sealed record TeamMembers(ApplicationUser[] Users, UserInvitation[] Invitations);
public sealed record CreatedInvitation(UserInvitation Invitation, string Token);

public sealed class TeamService(ISessionStore store)
{
    public async Task<Result<TeamMembers>> GetMembersAsync(BrowserAccess access, CancellationToken ct)
    {
        if (access.Role != "admin") return Refusal.Forbidden();
        var users = await store.ListUsersAsync(ct);
        var invitations = await store.ListInvitationsAsync(ct);
        return new TeamMembers([.. users], [.. invitations]);
    }

    public async Task<Refusal?> DeleteMemberAsync(BrowserAccess access, string userId, CancellationToken ct)
    {
        if (access.Role != "admin") return Refusal.Forbidden();
        if (userId == access.Actor)
            return Refusal.Rejected("An administrator account can't be deleted.");
        return await store.DeleteUserAsync(userId, ct)
            ? null : Refusal.NotFound("The team member was not found or cannot be deleted.");
    }

    public async Task<Refusal?> RevokeInvitationAsync(BrowserAccess access, string invitationId, CancellationToken ct)
    {
        if (access.Role != "admin") return Refusal.Forbidden();
        return await store.RevokeInvitationAsync(invitationId, DateTimeOffset.UtcNow, ct)
            ? null : Refusal.NotFound("The pending invitation was not found. It may already have been accepted or cancelled.");
    }

    public async Task<Result<CreatedInvitation>> InviteAsync(BrowserAccess access, string? username, CancellationToken ct)
    {
        if (access.Role != "admin") return Refusal.Forbidden();
        if (!UserAuthentication.TryNormaliseUsername(username, out var normalised))
            return Refusal.Rejected(UserAuthentication.UsernameProblem);
        var now = DateTimeOffset.UtcNow;
        var invitationId = $"inv_{Guid.NewGuid():N}";
        var token = UserAuthentication.CreateLinkToken(invitationId);
        UserAuthentication.TryHashLinkToken(token, "inv_", out _, out var tokenHash);
        var invitation = new UserInvitation(invitationId, normalised, "user", tokenHash, access.Actor,
            now, now.AddDays(7), null, null);
        if (!await store.CreateInvitationAsync(invitation, ct))
            return Refusal.Rejected("This username already has an account.");
        return new CreatedInvitation(invitation, token);
    }
}
