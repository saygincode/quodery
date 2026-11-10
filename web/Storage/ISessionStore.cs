using Quodery.Protocol;

namespace Quodery.Web.Storage;

public interface ISessionStore : IAsyncDisposable
{
    Task InitialiseAsync(CancellationToken cancellationToken = default);
    Task<bool> HasUsersAsync(CancellationToken cancellationToken = default);
    Task<bool> CreateFirstUserAsync(ApplicationUser user, string passwordHash, CancellationToken cancellationToken = default);
    Task<ApplicationUser?> GetUserByIdAsync(string id, CancellationToken cancellationToken = default);
    Task<bool> DeleteUserAsync(string id, CancellationToken cancellationToken = default);
    Task<SignInRecord?> GetSignInAsync(string username, CancellationToken cancellationToken = default);
    Task<string?> GetPasswordHashAsync(string userId, CancellationToken cancellationToken = default);
    Task SetPasswordAsync(string userId, string passwordHash, string sessionVersion, CancellationToken cancellationToken = default);
    Task<bool> RotateSessionVersionAsync(string userId, string version, CancellationToken cancellationToken = default);
    Task MarkUserLoginAsync(string id, DateTimeOffset loginAt, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ApplicationUser>> ListUsersAsync(CancellationToken cancellationToken = default);
    Task<bool> CreateInvitationAsync(UserInvitation invitation, CancellationToken cancellationToken = default);
    Task<UserInvitation?> GetInvitationAsync(string id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<UserInvitation>> ListInvitationsAsync(CancellationToken cancellationToken = default);
    Task<bool> RevokeInvitationAsync(string id, DateTimeOffset revokedAt, CancellationToken cancellationToken = default);
    Task<bool> AcceptInvitationAsync(string invitationId, byte[] tokenHash, ApplicationUser user, string passwordHash,
        DateTimeOffset acceptedAt, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Project>> ListProjectsAsync(CancellationToken cancellationToken = default);
    Task<Project?> GetProjectAsync(string id, CancellationToken cancellationToken = default);
    Task<bool> DeleteProjectAsync(string id, CancellationToken cancellationToken = default);
    Task CreateProjectAsync(Project project, RuntimeEnvironment environment, CancellationToken cancellationToken = default);
    Task<RuntimeEnvironment?> GetProjectConnectionAsync(string projectId, CancellationToken cancellationToken = default);
    Task<RuntimeEnvironment?> GetEnvironmentByRuntimeIdAsync(string runtimeId, CancellationToken cancellationToken = default);
    Task<RuntimeEnvironment?> GetEnvironmentByKeyHashAsync(byte[] keyHash, CancellationToken cancellationToken = default);
    Task<bool> ReplaceRuntimeKeyAsync(string projectId, string environmentId, byte[] keyHash,
        DateTimeOffset replacedAt, CancellationToken cancellationToken = default);
    Task MarkEnvironmentActivatedAsync(string environmentId, DateTimeOffset activatedAt,
        CancellationToken cancellationToken = default);
    Task<bool> CreateSessionAsync(InvestigationSession session, CancellationToken cancellationToken = default);
    Task<InvestigationSession?> GetSessionAsync(string id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<InvestigationSession>> ListSessionsAsync(string? projectId = null, int limit = 100, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<InvestigationSession>> ListRelatedSessionsAsync(string projectId, string runId, CancellationToken cancellationToken = default);
    Task<bool> TryUpdateSessionStatusAsync(string id, string expectedStatus, string status, CancellationToken cancellationToken = default);
    Task UpdateSessionStatusAsync(string id, string status, CancellationToken cancellationToken = default);
    Task EnqueueCommandAsync(ProtocolEnvelope command, CancellationToken cancellationToken = default);
    Task AcknowledgeCommandAsync(string runtimeId, string commandId, CancellationToken cancellationToken = default);
    Task DiscardCommandAsync(string commandId, CancellationToken cancellationToken = default);
    Task DiscardCommandsForSessionAsync(string sessionId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProtocolEnvelope>> ListPendingCommandsAsync(string runtimeId, CancellationToken cancellationToken = default);
    Task<bool> AppendEventAsync(ProtocolEnvelope envelope, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProtocolEnvelope>> ListEventsAsync(string sessionId, CancellationToken cancellationToken = default);
    Task AppendAuditAsync(AuditRecord audit, CancellationToken cancellationToken = default);
}

public sealed record ApplicationUser(
    string Id,
    string Username,
    string Role,
    string SessionVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastLoginAt);

public sealed record SignInRecord(ApplicationUser User, string PasswordHash);

public sealed record UserInvitation(
    string Id,
    string Username,
    string Role,
    byte[] TokenHash,
    string InvitedByUserId,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? AcceptedAt,
    DateTimeOffset? RevokedAt);

public sealed record RuntimeEnvironment(
    string Id,
    string ProjectId,
    string RuntimeId,
    string Name,
    byte[]? KeyHash,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ActivatedAt,
    DateTimeOffset? ResetAt,
    DateTimeOffset? RevokedAt);

public sealed record AuditRecord(string Id, string ProjectId, string? SessionId, string Actor, string Action,
    string Outcome, DateTimeOffset Timestamp);
