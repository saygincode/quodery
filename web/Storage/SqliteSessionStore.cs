using System.Text.Json;
using System.Security.Cryptography;
using Quodery.Protocol;
using Microsoft.Data.Sqlite;

namespace Quodery.Web.Storage;

public sealed class SqliteSessionStore : ISessionStore
{
    private readonly string _connectionString;

    public SqliteSessionStore(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
            else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            DefaultTimeout = 30
        }.ToString();
    }

    public async Task InitialiseAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS users (
              id TEXT PRIMARY KEY,
              username TEXT NOT NULL UNIQUE,
              role TEXT NOT NULL,
              session_version TEXT NOT NULL,
              created_at INTEGER NOT NULL,
              last_login_at INTEGER NULL,
              password_hash TEXT NOT NULL
            ) STRICT;
            CREATE TABLE IF NOT EXISTS user_invitations (
              id TEXT PRIMARY KEY,
              username TEXT NOT NULL,
              role TEXT NOT NULL,
              token_hash BLOB NOT NULL,
              invited_by_user_id TEXT NOT NULL REFERENCES users(id),
              created_at INTEGER NOT NULL,
              expires_at INTEGER NOT NULL,
              accepted_at INTEGER NULL,
              revoked_at INTEGER NULL
            ) STRICT;
            CREATE TABLE IF NOT EXISTS projects (
              id TEXT PRIMARY KEY,
              name TEXT NOT NULL,
              runtime_id TEXT NOT NULL
            ) STRICT;
            CREATE TABLE IF NOT EXISTS runtime_environments (
              id TEXT PRIMARY KEY,
              project_id TEXT NOT NULL REFERENCES projects(id),
              runtime_id TEXT NOT NULL UNIQUE,
              name TEXT NOT NULL,
              key_hash BLOB NULL,
              created_at INTEGER NOT NULL,
              activated_at INTEGER NULL,
              reset_at INTEGER NULL,
              revoked_at INTEGER NULL
            ) STRICT;
            -- Runtimes are found by their key's hash.
            CREATE UNIQUE INDEX IF NOT EXISTS runtime_environments_key_hash_idx
              ON runtime_environments(key_hash) WHERE key_hash IS NOT NULL;
            CREATE TABLE IF NOT EXISTS sessions (
              id TEXT PRIMARY KEY,
              project_id TEXT NOT NULL REFERENCES projects(id),
              runtime_id TEXT NOT NULL,
              goal TEXT NOT NULL,
              status TEXT NOT NULL,
              created_at INTEGER NOT NULL,
              updated_at INTEGER NOT NULL,
              job TEXT NULL
            ) STRICT;
            CREATE INDEX IF NOT EXISTS sessions_run_idx ON sessions(job->>'run_id');
            CREATE INDEX IF NOT EXISTS sessions_project_status_idx ON sessions(project_id, status);
            CREATE INDEX IF NOT EXISTS sessions_project_created_idx ON sessions(project_id, created_at);
            CREATE INDEX IF NOT EXISTS sessions_runtime_status_idx ON sessions(runtime_id, status);
            CREATE TABLE IF NOT EXISTS session_events (
              id TEXT PRIMARY KEY,
              session_id TEXT REFERENCES sessions(id),
              runtime_id TEXT NOT NULL,
              timestamp INTEGER NOT NULL,
              type TEXT NOT NULL,
              envelope TEXT NOT NULL
            ) STRICT;
            CREATE INDEX IF NOT EXISTS session_events_session_idx ON session_events(session_id, timestamp, id);
            CREATE TABLE IF NOT EXISTS cloud_commands (
              id TEXT PRIMARY KEY,
              runtime_id TEXT NOT NULL,
              session_id TEXT NULL REFERENCES sessions(id),
              timestamp INTEGER NOT NULL,
              envelope TEXT NOT NULL,
              acknowledged_at INTEGER NULL
            ) STRICT;
            CREATE INDEX IF NOT EXISTS cloud_commands_pending_idx
              ON cloud_commands(runtime_id, timestamp) WHERE acknowledged_at IS NULL;
            CREATE INDEX IF NOT EXISTS cloud_commands_session_idx ON cloud_commands(session_id);
            CREATE TABLE IF NOT EXISTS audit_events (
              id TEXT PRIMARY KEY,
              project_id TEXT NOT NULL REFERENCES projects(id),
              session_id TEXT NULL REFERENCES sessions(id),
              actor TEXT NOT NULL,
              action TEXT NOT NULL,
              outcome TEXT NOT NULL,
              timestamp INTEGER NOT NULL
            ) STRICT;
            CREATE INDEX IF NOT EXISTS audit_events_project_idx ON audit_events(project_id, timestamp DESC);
            CREATE INDEX IF NOT EXISTS audit_events_session_idx ON audit_events(session_id);
            DELETE FROM cloud_commands
            WHERE envelope->>'protocol_version' IS NOT $1;
            """;
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Command(connection, sql);
        Add(command, ProtocolConstants.Version);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> HasUsersAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Command(connection, "SELECT 1 FROM users LIMIT 1");
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    public async Task<bool> CreateFirstUserAsync(ApplicationUser user, string passwordHash,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await using var command = Command(connection, "SELECT 1 FROM users LIMIT 1", transaction);
        if (await command.ExecuteScalarAsync(cancellationToken) is not null) return false;
        if (!await InsertUserAsync(connection, transaction, user, passwordHash, cancellationToken))
            return false;
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static async Task<bool> InsertUserAsync(SqliteConnection connection, SqliteTransaction transaction,
        ApplicationUser user, string passwordHash, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, """
            INSERT INTO users (id, username, role, session_version, created_at, last_login_at, password_hash)
            VALUES ($1,$2,$3,$4,$5,$6,$7)
            ON CONFLICT (username) DO NOTHING
            """, transaction);
        Add(command, user.Id);
        Add(command, user.Username);
        Add(command, user.Role);
        Add(command, user.SessionVersion);
        Add(command, user.CreatedAt);
        Add(command, user.LastLoginAt);
        Add(command, passwordHash);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<ApplicationUser?> GetUserByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Command(connection, """
            SELECT id, username, role, session_version, created_at, last_login_at
            FROM users WHERE id = $1
            """);
        Add(command, id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadUser(reader) : null;
    }

    public async Task<SignInRecord?> GetSignInAsync(string username, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Command(connection, """
            SELECT id, username, role, session_version, created_at, last_login_at, password_hash
            FROM users WHERE username = $1
            """);
        Add(command, username);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new SignInRecord(ReadUser(reader), reader.GetString(6));
    }

    public async Task<string?> GetPasswordHashAsync(string userId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Command(connection, "SELECT password_hash FROM users WHERE id = $1");
        Add(command, userId);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    public async Task SetPasswordAsync(string userId, string passwordHash, string sessionVersion,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Command(connection, """
            UPDATE users SET password_hash = $2, session_version = $3 WHERE id = $1
            """);
        Add(command, userId);
        Add(command, passwordHash);
        Add(command, sessionVersion);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> RotateSessionVersionAsync(string userId, string version,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Command(connection,
            "UPDATE users SET session_version = $2 WHERE id = $1");
        Add(command, userId);
        Add(command, version);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<bool> DeleteUserAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await using var command = Command(connection, "SELECT 1 FROM users WHERE id = $1 AND role = 'user'", transaction);
        Add(command, id);
        if (await command.ExecuteScalarAsync(cancellationToken) is null) return false;
        command.CommandText = "DELETE FROM user_invitations WHERE invited_by_user_id = $1";
        await command.ExecuteNonQueryAsync(cancellationToken);
        command.CommandText = "DELETE FROM users WHERE id = $1 AND role = 'user'";
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task MarkUserLoginAsync(string id, DateTimeOffset loginAt, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Command(connection,
            "UPDATE users SET last_login_at = $2 WHERE id = $1");
        Add(command, id);
        Add(command, loginAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ApplicationUser>> ListUsersAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Command(connection, """
            SELECT id, username, role, session_version, created_at, last_login_at
            FROM users ORDER BY username
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var users = new List<ApplicationUser>();
        while (await reader.ReadAsync(cancellationToken)) users.Add(ReadUser(reader));
        return users;
    }

    public async Task<bool> CreateInvitationAsync(
        UserInvitation invitation, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await using var command = Command(connection, "SELECT 1 FROM users WHERE username = $1", transaction);
        Add(command, invitation.Username);
        if (await command.ExecuteScalarAsync(cancellationToken) is not null) return false;
        command.Parameters.Clear();
        command.CommandText = """
            UPDATE user_invitations SET revoked_at = $2
            WHERE username = $1 AND accepted_at IS NULL AND revoked_at IS NULL
            """;
        Add(command, invitation.Username);
        Add(command, invitation.CreatedAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
        command.Parameters.Clear();
        command.CommandText = """
            INSERT INTO user_invitations
              (id, username, role, token_hash, invited_by_user_id, created_at, expires_at, accepted_at, revoked_at)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9)
            """;
        Add(command, invitation.Id);
        Add(command, invitation.Username);
        Add(command, invitation.Role);
        Add(command, invitation.TokenHash);
        Add(command, invitation.InvitedByUserId);
        Add(command, invitation.CreatedAt);
        Add(command, invitation.ExpiresAt);
        Add(command, invitation.AcceptedAt);
        Add(command, invitation.RevokedAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<UserInvitation?> GetInvitationAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Command(connection, """
            SELECT id, username, role, token_hash, invited_by_user_id,
                   created_at, expires_at, accepted_at, revoked_at
            FROM user_invitations WHERE id = $1
            """);
        Add(command, id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadInvitation(reader) : null;
    }

    public async Task<IReadOnlyList<UserInvitation>> ListInvitationsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Command(connection, """
            SELECT id, username, role, token_hash, invited_by_user_id,
                   created_at, expires_at, accepted_at, revoked_at
            FROM user_invitations
            WHERE accepted_at IS NULL AND revoked_at IS NULL AND expires_at > $1
            ORDER BY created_at DESC
            """);
        Add(command, DateTimeOffset.UtcNow);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var invitations = new List<UserInvitation>();
        while (await reader.ReadAsync(cancellationToken)) invitations.Add(ReadInvitation(reader));
        return invitations;
    }

    public async Task<bool> RevokeInvitationAsync(string id, DateTimeOffset revokedAt, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Command(connection, """
            UPDATE user_invitations SET revoked_at = $2
            WHERE id = $1 AND accepted_at IS NULL AND revoked_at IS NULL
            """);
        Add(command, id);
        Add(command, revokedAt);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<bool> AcceptInvitationAsync(string invitationId, byte[] tokenHash, ApplicationUser user,
        string passwordHash, DateTimeOffset acceptedAt, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        UserInvitation? invitation;
        await using (var select = Command(connection, """
            SELECT id, username, role, token_hash, invited_by_user_id,
                   created_at, expires_at, accepted_at, revoked_at
            FROM user_invitations WHERE id = $1
            """, transaction))
        {
            Add(select, invitationId);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            invitation = await reader.ReadAsync(cancellationToken) ? ReadInvitation(reader) : null;
        }
        if (invitation is null || invitation.AcceptedAt is not null || invitation.RevokedAt is not null ||
            invitation.ExpiresAt <= acceptedAt || invitation.Username != user.Username ||
            !CryptographicOperations.FixedTimeEquals(invitation.TokenHash, tokenHash))
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }
        if (!await InsertUserAsync(connection, transaction, user, passwordHash, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }
        await using var update = Command(connection, "UPDATE user_invitations SET accepted_at = $2 WHERE id = $1", transaction);
        Add(update, invitationId);
        Add(update, acceptedAt);
        await update.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<Project>> ListProjectsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Command(connection,
            """
            SELECT id, name, runtime_id FROM projects ORDER BY name, id
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var projects = new List<Project>();
        while (await reader.ReadAsync(cancellationToken))
        {
            projects.Add(new Project(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        return projects;
    }

    public async Task<Project?> GetProjectAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Command(connection,
            """
            SELECT id, name, runtime_id FROM projects WHERE id = $1
            """);
        Add(command, id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new Project(reader.GetString(0), reader.GetString(1), reader.GetString(2))
            : null;
    }

    public async Task<bool> DeleteProjectAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await using var command = Command(connection,
            "SELECT 1 FROM projects WHERE id = $1", transaction);
        Add(command, id);
        if (await command.ExecuteScalarAsync(cancellationToken) is null) return false;
        command.Parameters.Clear();
        Add(command, id);
        var deletes = new[]
        {
            "DELETE FROM audit_events WHERE project_id = $1",
            "DELETE FROM cloud_commands WHERE runtime_id IN (SELECT runtime_id FROM projects WHERE id = $1) OR session_id IN (SELECT id FROM sessions WHERE project_id = $1)",
            "DELETE FROM session_events WHERE runtime_id IN (SELECT runtime_id FROM projects WHERE id = $1) OR session_id IN (SELECT id FROM sessions WHERE project_id = $1)",
            "DELETE FROM sessions WHERE project_id = $1",
            "DELETE FROM runtime_environments WHERE project_id = $1",
            "DELETE FROM projects WHERE id = $1"
        };
        foreach (var sql in deletes)
        {
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task CreateProjectAsync(
        Project project, RuntimeEnvironment environment, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await using var command = Command(connection, """
            INSERT INTO projects (id, name, runtime_id)
            VALUES ($1,$2,$3)
            """, transaction);
        Add(command, project.Id);
        Add(command, project.Name);
        Add(command, project.RuntimeId);
        await command.ExecuteNonQueryAsync(cancellationToken);

        command.Parameters.Clear();
        command.CommandText = """
            INSERT INTO runtime_environments
              (id, project_id, runtime_id, name, key_hash, created_at, activated_at, reset_at, revoked_at)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9)
            """;
        Add(command, environment.Id);
        Add(command, environment.ProjectId);
        Add(command, environment.RuntimeId);
        Add(command, environment.Name);
        Add(command, environment.KeyHash);
        Add(command, environment.CreatedAt);
        Add(command, environment.ActivatedAt);
        Add(command, environment.ResetAt);
        Add(command, environment.RevokedAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<RuntimeEnvironment?> GetProjectConnectionAsync(
        string projectId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Command(connection, """
            SELECT connection.id, connection.project_id, connection.runtime_id, connection.name,
                   connection.key_hash, connection.created_at, connection.activated_at,
                   connection.reset_at, connection.revoked_at
            FROM runtime_environments connection
            JOIN projects project ON project.id = connection.project_id
            WHERE project.id = $1 AND connection.runtime_id = project.runtime_id
              AND connection.revoked_at IS NULL
            """);
        Add(command, projectId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadEnvironment(reader) : null;
    }

    public async Task<RuntimeEnvironment?> GetEnvironmentByRuntimeIdAsync(
        string runtimeId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Command(connection, """
            SELECT id, project_id, runtime_id, name, key_hash, created_at,
                   activated_at, reset_at, revoked_at
            FROM runtime_environments WHERE runtime_id = $1
            """);
        Add(command, runtimeId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadEnvironment(reader) : null;
    }

    public async Task<RuntimeEnvironment?> GetEnvironmentByKeyHashAsync(
        byte[] keyHash, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Command(connection, """
            SELECT id, project_id, runtime_id, name, key_hash, created_at,
                   activated_at, reset_at, revoked_at
            FROM runtime_environments WHERE key_hash = $1
            """);
        Add(command, keyHash);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadEnvironment(reader) : null;
    }

    public async Task<bool> ReplaceRuntimeKeyAsync(string projectId, string environmentId, byte[] keyHash,
        DateTimeOffset replacedAt, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Command(connection, """
            UPDATE runtime_environments
            SET key_hash = $3, activated_at = NULL, reset_at = $4
            WHERE project_id = $1 AND id = $2 AND revoked_at IS NULL
            """);
        Add(command, projectId);
        Add(command, environmentId);
        Add(command, keyHash);
        Add(command, replacedAt);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task MarkEnvironmentActivatedAsync(string environmentId, DateTimeOffset activatedAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Command(connection, """
            UPDATE runtime_environments SET activated_at = $2
            WHERE id = $1 AND activated_at IS NULL AND key_hash IS NOT NULL
            """);
        Add(command, environmentId);
        Add(command, activatedAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> CreateSessionAsync(
        InvestigationSession session,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Command(connection, """
            INSERT INTO sessions (id, project_id, runtime_id, goal, status, created_at, updated_at, job)
            SELECT $1, $2, $3, $4, $5, $6, $7, $8
            WHERE (SELECT COUNT(*) FROM sessions WHERE project_id = $2
                   AND status IN ('queued', 'running', 'awaiting_input')) < 100
              AND (SELECT COUNT(*) FROM sessions WHERE project_id = $2
                   AND created_at > $10) < 1000
              AND ($9 IS NULL OR NOT EXISTS (
                   SELECT 1 FROM sessions WHERE project_id = $2 AND (id = $9 OR job->>'run_id' = $9)
                   AND status IN ('queued', 'running', 'awaiting_input')))
            """);
        Add(command, session.Id);
        Add(command, session.ProjectId);
        Add(command, session.RuntimeId);
        Add(command, session.Goal);
        Add(command, session.Status);
        Add(command, session.CreatedAt);
        Add(command, session.UpdatedAt);
        Add(command, session.Job is null ? null : JsonSerializer.Serialize(session.Job, ProtocolJson.Options));
        Add(command, session.Job?.RunId);
        Add(command, DateTimeOffset.UtcNow.AddDays(-1));
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<InvestigationSession?> GetSessionAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Command(connection, """
            SELECT id, project_id, runtime_id, goal, status, created_at, updated_at, job
            FROM sessions WHERE id = $1
            """);
        Add(command, id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadSession(reader) : null;
    }

    public async Task<IReadOnlyList<InvestigationSession>> ListSessionsAsync(
        string? projectId = null, int limit = 100, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Command(connection, """
            SELECT listed.id, listed.project_id, listed.runtime_id, listed.goal, listed.status,
                   listed.created_at, listed.updated_at, listed.job
            FROM sessions AS listed
            WHERE ($1 IS NULL OR listed.project_id = $1) AND listed.job->>'run_id' IS NULL
              AND NOT (listed.job->>'type' = 'investigate_failure' AND EXISTS (
                  SELECT 1 FROM sessions AS source
                  JOIN sessions AS parent ON parent.id = COALESCE(source.job->>'run_id', source.id)
                  WHERE source.id = listed.job->>'source_session_id'
                    AND source.project_id = listed.project_id
                    AND parent.job->>'type' = 'discover_tests'
                    AND parent.project_id = listed.project_id
                    AND parent.runtime_id = listed.runtime_id
              ))
            ORDER BY listed.created_at DESC, listed.id DESC LIMIT $2
            """);
        Add(command, projectId);
        Add(command, Math.Clamp(limit, 1, 200));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var sessions = new List<InvestigationSession>();
        while (await reader.ReadAsync(cancellationToken)) sessions.Add(ReadSession(reader));
        return sessions;
    }

    public async Task<IReadOnlyList<InvestigationSession>> ListRelatedSessionsAsync(
        string projectId, string runId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Command(connection, """
            SELECT id, project_id, runtime_id, goal, status, created_at, updated_at, job
            FROM sessions AS related
            WHERE project_id = $2 AND (job->>'run_id' = $1 OR
              (job->>'type' = 'investigate_failure' AND job->>'run_id' IS NULL AND EXISTS (
                  SELECT 1 FROM sessions AS source
                  WHERE source.id = related.job->>'source_session_id'
                    AND COALESCE(source.job->>'run_id', source.id) = $1
                    AND source.project_id = related.project_id
                    AND source.runtime_id = related.runtime_id
              )))
            ORDER BY created_at, id
            """);
        Add(command, runId);
        Add(command, projectId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var sessions = new List<InvestigationSession>();
        while (await reader.ReadAsync(cancellationToken)) sessions.Add(ReadSession(reader));
        return sessions;
    }

    public async Task<bool> TryUpdateSessionStatusAsync(string id, string expectedStatus, string status, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Command(connection,
            "UPDATE sessions SET status = $3, updated_at = $4 WHERE id = $1 AND status = $2");
        Add(command, id);
        Add(command, expectedStatus);
        Add(command, status);
        Add(command, DateTimeOffset.UtcNow);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task UpdateSessionStatusAsync(
        string id,
        string status,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Command(connection,
            "UPDATE sessions SET status = $2, updated_at = $3 WHERE id = $1");
        Add(command, id);
        Add(command, status);
        Add(command, DateTimeOffset.UtcNow);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task EnqueueCommandAsync(ProtocolEnvelope envelope, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Command(connection, """
            INSERT INTO cloud_commands (id, runtime_id, session_id, timestamp, envelope)
            VALUES ($1,$2,$3,$4,$5) ON CONFLICT (id) DO NOTHING
            """);
        Add(command, envelope.Id); Add(command, envelope.RuntimeId);
        Add(command, envelope.SessionId); Add(command, envelope.Timestamp);
        Add(command, JsonSerializer.Serialize(envelope, ProtocolJson.Options));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task AcknowledgeCommandAsync(string runtimeId, string commandId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Command(connection, "DELETE FROM cloud_commands WHERE runtime_id=$1 AND id=$2");
        Add(command, runtimeId); Add(command, commandId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DiscardCommandAsync(string commandId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Command(connection, "DELETE FROM cloud_commands WHERE id=$1");
        Add(command, commandId); await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DiscardCommandsForSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Command(connection, "DELETE FROM cloud_commands WHERE session_id=$1");
        Add(command, sessionId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ProtocolEnvelope>> ListPendingCommandsAsync(string runtimeId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Command(connection, """
            SELECT envelope FROM cloud_commands
            WHERE runtime_id=$1 AND acknowledged_at IS NULL ORDER BY timestamp, id LIMIT 200
            """);
        Add(command, runtimeId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<ProtocolEnvelope>();
        while (await reader.ReadAsync(cancellationToken))
            if (JsonSerializer.Deserialize<ProtocolEnvelope>(reader.GetString(0), ProtocolJson.Options) is { } envelope)
                result.Add(envelope);
        return result;
    }

    public async Task<bool> AppendEventAsync(
        ProtocolEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Command(connection, """
            INSERT INTO session_events (id, session_id, runtime_id, timestamp, type, envelope)
            SELECT $1, $2, $3, $4, $5, $6
            WHERE (SELECT COUNT(*) FROM session_events WHERE session_id = $2) <
                  CASE WHEN $5 = 'session.completed' THEN 2000 ELSE 1999 END
              AND ($5 <> 'session.progress' OR
                   (SELECT COUNT(*) FROM session_events WHERE session_id = $2) < 1900)
            ON CONFLICT (id) DO NOTHING
            """);
        Add(command, envelope.Id);
        Add(command, envelope.SessionId);
        Add(command, envelope.RuntimeId);
        Add(command, envelope.Timestamp);
        Add(command, envelope.Type);
        Add(command, JsonSerializer.Serialize(envelope, ProtocolJson.Options));
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<IReadOnlyList<ProtocolEnvelope>> ListEventsAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Command(connection, """
            SELECT envelope FROM session_events
            WHERE session_id = $1 ORDER BY timestamp, id LIMIT 2000
            """);
        Add(command, sessionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var events = new List<ProtocolEnvelope>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var envelope = JsonSerializer.Deserialize<ProtocolEnvelope>(reader.GetString(0), ProtocolJson.Options);
            if (envelope is not null)
            {
                events.Add(envelope);
            }
        }

        return events;
    }

    public async Task AppendAuditAsync(AuditRecord audit, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Command(connection, """
            INSERT INTO audit_events (id, project_id, session_id, actor, action, outcome, timestamp)
            VALUES ($1,$2,$3,$4,$5,$6,$7) ON CONFLICT (id) DO NOTHING
            """);
        Add(command, audit.Id); Add(command, audit.ProjectId);
        Add(command, audit.SessionId); Add(command, audit.Actor);
        Add(command, audit.Action); Add(command, audit.Outcome); Add(command, audit.Timestamp);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var pragmas = Command(connection,
                "PRAGMA foreign_keys = ON; PRAGMA synchronous = NORMAL; PRAGMA busy_timeout = 5000;");
            await pragmas.ExecuteNonQueryAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static SqliteCommand Command(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        return command;
    }

    private static void Add(SqliteCommand command, object? value) =>
        command.Parameters.AddWithValue($"${command.Parameters.Count + 1}", value switch
        {
            null => DBNull.Value,
            DateTimeOffset timestamp => timestamp.UtcTicks,
            _ => value
        });

    private static DateTimeOffset Time(SqliteDataReader reader, int ordinal) =>
        new(reader.GetInt64(ordinal), TimeSpan.Zero);

    private static DateTimeOffset? OptionalTime(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Time(reader, ordinal);

    private static InvestigationSession ReadSession(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        Time(reader, 5),
        Time(reader, 6),
        reader.IsDBNull(7) ? null : JsonSerializer.Deserialize<TestingJob>(reader.GetString(7), ProtocolJson.Options));

    private static RuntimeEnvironment ReadEnvironment(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetFieldValue<byte[]>(4),
        Time(reader, 5), OptionalTime(reader, 6), OptionalTime(reader, 7), OptionalTime(reader, 8));

    private static ApplicationUser ReadUser(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
        Time(reader, 4), OptionalTime(reader, 5));

    private static UserInvitation ReadInvitation(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2),
        reader.GetFieldValue<byte[]>(3), reader.GetString(4),
        Time(reader, 5), Time(reader, 6), OptionalTime(reader, 7), OptionalTime(reader, 8));

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        return ValueTask.CompletedTask;
    }
}
