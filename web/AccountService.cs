using System.Security.Cryptography;
using Quodery.Web.Storage;

namespace Quodery.Web;

public sealed class AccountService(ISessionStore store, SetupLink setup, ServingMode serving)
{
    private static readonly string UnknownAccountHash = UserAuthentication.HashPassword(Convert.ToHexString(RandomNumberGenerator.GetBytes(16)));

    private static readonly Refusal InvalidCredentials = Refusal.Rejected("The username or password is incorrect.");
    private static readonly Refusal RegistrationClosed = Refusal.Rejected("This server is already set up. Ask its administrator for an invitation.");
    private static readonly Refusal InvalidLink = Refusal.Rejected("This link is invalid, expired or already used. Ask your administrator for a new one.");

    public async Task<BrowserAccess?> AuthoriseAsync(string? userId, string? sessionVersion, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(sessionVersion)) return null;
        var user = await store.GetUserByIdAsync(userId, ct);
        if (user is null || !string.Equals(sessionVersion, user.SessionVersion, StringComparison.Ordinal)) return null;
        return new BrowserAccess(user.Id, user.Role);
    }

    public async Task<Result<ApplicationUser>> SignInAsync(string? username, string? password, CancellationToken ct)
    {
        var account = UserAuthentication.TryNormaliseUsername(username, out var normalised)
            ? await store.GetSignInAsync(normalised, ct) : null;
        if (!UserAuthentication.VerifyPassword(account?.PasswordHash ?? UnknownAccountHash, password) || account is null)
            return InvalidCredentials;
        await store.MarkUserLoginAsync(account.User.Id, DateTimeOffset.UtcNow, ct);
        return account.User;
    }

    public async Task<bool> IsRegistrationOpenAsync(CancellationToken ct) => !await store.HasUsersAsync(ct);

    public async Task<Result<ApplicationUser>> RegisterAsync(string? username, string? password, string? setupToken,
        CancellationToken ct)
    {
        if (await store.HasUsersAsync(ct)) return RegistrationClosed;
        if (!setup.Verify(setupToken))
            return Refusal.Rejected("Open the setup link that the server printed when it started.");
        if (!UserAuthentication.TryNormaliseUsername(username, out var normalised))
            return Refusal.Rejected(UserAuthentication.UsernameProblem);
        if (UserAuthentication.PasswordProblem(password) is { } problem) return Refusal.Rejected(problem);
        var now = DateTimeOffset.UtcNow;
        var user = new ApplicationUser($"usr_{Guid.NewGuid():N}", normalised, "admin", NewSessionVersion(), now, now);
        if (!await store.CreateFirstUserAsync(user, UserAuthentication.HashPassword(password!), ct))
            return RegistrationClosed;
        setup.Consume();
        if (serving.SetupCompleted is { } completed) await completed(ct);
        return user;
    }

    public async Task<string?> GetInvitedUsernameAsync(string? token, CancellationToken ct) =>
        (await FindInvitationAsync(token, ct))?.Invitation.Username;

    public async Task<Result<ApplicationUser>> AcceptInvitationAsync(string? token, string? password, CancellationToken ct)
    {
        if (await FindInvitationAsync(token, ct) is not { } found) return InvalidLink;
        if (UserAuthentication.PasswordProblem(password) is { } problem) return Refusal.Rejected(problem);
        var now = DateTimeOffset.UtcNow;
        var user = new ApplicationUser($"usr_{Guid.NewGuid():N}", found.Invitation.Username, found.Invitation.Role,
            NewSessionVersion(), now, now);
        if (!await store.AcceptInvitationAsync(found.Invitation.Id, found.TokenHash, user,
                UserAuthentication.HashPassword(password!), now, ct))
            return Refusal.Rejected("This invitation can no longer be accepted.");
        return user;
    }

    private async Task<(UserInvitation Invitation, byte[] TokenHash)?> FindInvitationAsync(string? token, CancellationToken ct)
    {
        if (!UserAuthentication.TryHashLinkToken(token, "inv_", out var invitationId, out var tokenHash)) return null;
        var invitation = await store.GetInvitationAsync(invitationId, ct);
        if (invitation is not { Role: "user", AcceptedAt: null, RevokedAt: null } ||
            invitation.ExpiresAt <= DateTimeOffset.UtcNow ||
            !CryptographicOperations.FixedTimeEquals(invitation.TokenHash, tokenHash))
            return null;
        return (invitation, tokenHash);
    }

    public async Task<Refusal?> ChangePasswordAsync(BrowserAccess access, string? currentPassword, string? newPassword,
        CancellationToken ct)
    {
        if (!UserAuthentication.VerifyPassword(await store.GetPasswordHashAsync(access.Actor, ct), currentPassword))
            return Refusal.Rejected("Your current password is incorrect.");
        if (UserAuthentication.PasswordProblem(newPassword) is { } problem) return Refusal.Rejected(problem);
        await store.SetPasswordAsync(access.Actor, UserAuthentication.HashPassword(newPassword!), NewSessionVersion(), ct);
        return null;
    }

    public async Task<Refusal?> SignOutEverywhereAsync(BrowserAccess access, CancellationToken ct) =>
        await store.RotateSessionVersionAsync(access.Actor, NewSessionVersion(), ct)
            ? null : Refusal.Rejected("Could not sign out all devices. Please try again.");

    private static string NewSessionVersion() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
}
