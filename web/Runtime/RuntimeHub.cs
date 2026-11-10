using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Security.Cryptography;
using System.Threading.Channels;
using Quodery.Web.Storage;
using Quodery.Protocol;

namespace Quodery.Web.Runtime;

public sealed class RuntimeHub(
    ISessionStore store,
    IHostApplicationLifetime lifetime,
    ILogger<RuntimeHub> logger) : IAsyncDisposable
{
    private static readonly TimeSpan PollWait = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan ConnectedWindow = TimeSpan.FromSeconds(45);
    private const int MaximumEventsPerMinute = 200;
    private const int MaximumEventsPerRequest = 50;
    private const int MaximumCommandsPerResponse = 20;
    private const string ConnectionHeader = "X-Quodery-Connection";
    private static readonly TimeSpan GitCommandExpiry = TimeSpan.FromMinutes(3);

    private readonly ConcurrentDictionary<string, RuntimeConnection> _connections = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _commandSignals = new();
    private readonly ConcurrentDictionary<string, string> _runtimeVersions = new();
    private readonly ConcurrentDictionary<string, byte> _incompatibleRuntimes = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSeen = new();
    private readonly ConcurrentDictionary<string, GitStatus> _gitStatuses = new();
    private readonly ConcurrentDictionary<string, string> _gitResults = new();
    private readonly ConcurrentDictionary<string, PendingGitCommand> _pendingGitCommands = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _registrationLocks = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, Channel<ProtocolEnvelope>>> _subscribers = new();

    public bool IsConnected(string runtimeId, string projectId) =>
        _connections.TryGetValue(runtimeId, out var connection) &&
        connection.ProjectId == projectId && !connection.IsClosed &&
        LastSeen(runtimeId, projectId) is { } seen && DateTimeOffset.UtcNow - seen < ConnectedWindow;

    public DateTimeOffset? LastSeen(string runtimeId, string projectId) =>
        _lastSeen.TryGetValue(ConnectionKey(runtimeId, projectId), out var timestamp) ? timestamp : null;

    public string? RuntimeVersion(string runtimeId, string projectId) =>
        _runtimeVersions.GetValueOrDefault(ConnectionKey(runtimeId, projectId));

    public bool IsIncompatible(string runtimeId, string projectId) =>
        _incompatibleRuntimes.ContainsKey(ConnectionKey(runtimeId, projectId));

    public GitWorkspaceState? GitState(string runtimeId, string projectId)
    {
        var key = ConnectionKey(runtimeId, projectId);
        return _gitStatuses.TryGetValue(key, out var status)
            ? new(status, _gitResults.GetValueOrDefault(key), IsGitCommandPending(key)) : null;
    }

    public async Task<bool> SendGitCommandAsync(string runtimeId, string projectId, string type, object payload,
        CancellationToken cancellationToken)
    {
        var key = ConnectionKey(runtimeId, projectId);
        var command = ProtocolEnvelopeFactory.CreateCommand(runtimeId, null, type, payload);
        var pending = new PendingGitCommand(command.Id, DateTimeOffset.UtcNow);
        if (IsGitCommandPending(key) || !_pendingGitCommands.TryAdd(key, pending)) return false;
        _gitResults.TryRemove(key, out _);
        if (await SendAsync(command, cancellationToken, projectId: projectId)) return true;
        _pendingGitCommands.TryRemove(new KeyValuePair<string, PendingGitCommand>(key, pending));
        return false;
    }

    public static string ServerVersion => typeof(RuntimeHub).Assembly.GetName().Version!.ToString(3);

    public async Task ChallengeAsync(HttpContext context)
    {
        await WriteJsonAsync(context, StatusCodes.Status200OK, new
        {
            challenge = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            protocol_version = ProtocolConstants.Version,
            server_version = ServerVersion
        });
    }

    public async Task SessionAsync(HttpContext context)
    {
        using var request = await ReadJsonAsync(context);
        var key = BearerToken(context);
        var environment = RuntimeKeyFormat.IsWellFormed(key)
            ? await store.GetEnvironmentByKeyHashAsync(RuntimeKeys.Hash(key!), context.RequestAborted)
            : null;
        if (environment is null || environment.RevokedAt is not null)
        { await WriteErrorAsync(context, StatusCodes.Status401Unauthorized, "runtime_key_invalid"); return; }
        var runtimeId = environment.RuntimeId;
        var runtimeProtocol = OptionalString(request, "protocol_version");
        var reportedVersion = OptionalString(request, "runtime_version") is { Length: <= 32 } candidate &&
            System.Version.TryParse(candidate, out _) ? candidate : null;

        var registrationLock = _registrationLocks.GetOrAdd(runtimeId, _ => new SemaphoreSlim(1, 1));
        await registrationLock.WaitAsync(context.RequestAborted);
        RuntimeConnection connection;
        try
        {
            environment = await store.GetEnvironmentByRuntimeIdAsync(runtimeId, context.RequestAborted);
            if (environment is null || environment.RevokedAt is not null || !RuntimeKeys.Matches(environment.KeyHash, key!))
            {
                await WriteErrorAsync(context, StatusCodes.Status401Unauthorized, "runtime_key_invalid");
                return;
            }

            var connectionKey = ConnectionKey(runtimeId, environment.ProjectId);
            if (reportedVersion is not null)
                _runtimeVersions[connectionKey] = reportedVersion;
            if (runtimeProtocol != ProtocolConstants.Version)
            {
                _incompatibleRuntimes[connectionKey] = 0;
                _lastSeen[connectionKey] = DateTimeOffset.UtcNow;
                logger.LogWarning("Refused runtime {RuntimeId} version {RuntimeVersion}: it uses protocol {Received}, this server {Expected}",
                    runtimeId, reportedVersion, runtimeProtocol, ProtocolConstants.Version);
                await WriteProtocolMismatchAsync(context);
                return;
            }
            _incompatibleRuntimes.TryRemove(connectionKey, out _);

            if (environment.ActivatedAt is null)
                await store.MarkEnvironmentActivatedAsync(environment.Id, DateTimeOffset.UtcNow, context.RequestAborted);
            connection = new RuntimeConnection(runtimeId, environment.Id, environment.ProjectId, environment.KeyHash!,
                $"{runtimeId}.{Base64UrlEncode(RandomNumberGenerator.GetBytes(16))}");
            if (_connections.TryGetValue(runtimeId, out var previous)) previous.Close();
            _connections[runtimeId] = connection;
            _lastSeen[connectionKey] = DateTimeOffset.UtcNow;
            _gitStatuses.TryRemove(connectionKey, out _);
            _gitResults.TryRemove(connectionKey, out _);
            _pendingGitCommands.TryRemove(connectionKey, out _);
        }
        finally { registrationLock.Release(); }

        logger.LogInformation("Runtime {RuntimeId} connected", runtimeId);
        await WriteJsonAsync(context, StatusCodes.Status200OK, new
        {
            connection_id = connection.ConnectionId,
            runtime_id = connection.RuntimeId,
            project_id = connection.ProjectId,
            environment_id = connection.EnvironmentId
        });
    }

    public async Task CommandsAsync(HttpContext context)
    {
        if (Authorise(context) is not { } connection)
        { await WriteErrorAsync(context, StatusCodes.Status401Unauthorized, "connection_invalid"); return; }
        var cancellationToken = context.RequestAborted;
        using var request = await ReadJsonAsync(context);
        if (request?.RootElement is { ValueKind: JsonValueKind.Object } root &&
            root.TryGetProperty("acknowledged", out var acknowledged) && acknowledged.ValueKind == JsonValueKind.Array)
        {
            foreach (var id in acknowledged.EnumerateArray().Take(MaximumCommandsPerResponse))
                if (id.ValueKind == JsonValueKind.String && id.GetString() is { } commandId)
                    await store.AcknowledgeCommandAsync(connection.RuntimeId, commandId, cancellationToken);
        }
        Touch(connection);

        using var wait = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, connection.Closed, lifetime.ApplicationStopping);
        var elapsed = Stopwatch.StartNew();
        IReadOnlyList<ProtocolEnvelope> commands;
        while (true)
        {
            var signal = CommandSignal(connection.RuntimeId);
            commands = await PendingCommandsAsync(connection, cancellationToken);
            var remaining = PollWait - elapsed.Elapsed;
            if (commands.Count > 0 || remaining <= TimeSpan.Zero || wait.IsCancellationRequested) break;
            await Task.WhenAny(signal, Task.Delay(remaining, wait.Token));
        }
        if (cancellationToken.IsCancellationRequested) return;
        if (connection.IsClosed)
        { await WriteErrorAsync(context, StatusCodes.Status401Unauthorized, "connection_invalid"); return; }
        Touch(connection);
        await WriteJsonAsync(context, StatusCodes.Status200OK, new { commands });
    }

    public async Task EventsAsync(HttpContext context)
    {
        if (Authorise(context) is not { } connection)
        { await WriteErrorAsync(context, StatusCodes.Status401Unauthorized, "connection_invalid"); return; }
        var cancellationToken = context.RequestAborted;
        using var request = await ReadJsonAsync(context);
        if (request?.RootElement is not { ValueKind: JsonValueKind.Object } root ||
            !root.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array ||
            events.GetArrayLength() is 0 or > MaximumEventsPerRequest)
        { await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "invalid_request"); return; }
        if (!connection.TryCountEvents(events.GetArrayLength(), MaximumEventsPerMinute))
        {
            context.Response.Headers.RetryAfter = "60";
            await WriteErrorAsync(context, StatusCodes.Status429TooManyRequests, "rate_limited");
            return;
        }
        Touch(connection);

        var accepted = new List<string>();
        foreach (var item in events.EnumerateArray())
        {
            ProtocolEnvelope? envelope;
            try { envelope = item.Deserialize<ProtocolEnvelope>(ProtocolJson.Options); }
            catch (JsonException) { envelope = null; }

            if (envelope is not null && envelope.ProtocolVersion != ProtocolConstants.Version)
            {
                _incompatibleRuntimes[ConnectionKey(connection.RuntimeId, connection.ProjectId)] = 0;
                logger.LogWarning("Refused runtime {RuntimeId}: it uses protocol {Received}, this server {Expected}",
                    connection.RuntimeId, envelope.ProtocolVersion, ProtocolConstants.Version);
                connection.Close();
                await WriteProtocolMismatchAsync(context);
                return;
            }

            var error = envelope is null ? "Invalid JSON" : await AcceptEventAsync(connection, envelope, cancellationToken);
            if (error is not null)
            {
                logger.LogWarning("Rejected runtime event from {RuntimeId}: {Reason}; expected protocol {Expected}, received {Received}",
                    connection.RuntimeId, error, ProtocolConstants.Version, envelope?.ProtocolVersion);
                await WriteJsonAsync(context, StatusCodes.Status422UnprocessableEntity, new { error, acknowledged = accepted });
                return;
            }
            accepted.Add(envelope!.Id);
        }
        await WriteJsonAsync(context, StatusCodes.Status200OK, new { acknowledged = accepted });
    }

    public async Task<bool> SendAsync(
        ProtocolEnvelope command,
        CancellationToken cancellationToken,
        bool retainWhenDisconnected = false,
        string? projectId = null)
    {
        await store.EnqueueCommandAsync(command, cancellationToken);
        if (!_connections.TryGetValue(command.RuntimeId, out var connection) ||
            (projectId is not null && connection.ProjectId != projectId) ||
            !IsConnected(command.RuntimeId, connection.ProjectId))
        {
            if (!retainWhenDisconnected) await store.DiscardCommandAsync(command.Id, cancellationToken);
            return false;
        }

        if (command.SessionId is not null)
        {
            var session = await store.GetSessionAsync(command.SessionId, cancellationToken);
            if (session is null || session.ProjectId != connection.ProjectId || session.RuntimeId != command.RuntimeId)
            {
                await store.DiscardCommandAsync(command.Id, cancellationToken);
                return false;
            }
        }
        WakeCommandPoll(command.RuntimeId);
        return true;
    }

    public async Task<string?> ReplaceRuntimeKeyAsync(RuntimeEnvironment environment, CancellationToken cancellationToken)
    {
        var registrationLock = _registrationLocks.GetOrAdd(environment.RuntimeId, _ => new SemaphoreSlim(1, 1));
        await registrationLock.WaitAsync(cancellationToken);
        try
        {
            var (plaintext, hash) = RuntimeKeys.Generate();
            if (!await store.ReplaceRuntimeKeyAsync(environment.ProjectId, environment.Id, hash, DateTimeOffset.UtcNow,
                    cancellationToken)) return null;
            CloseConnections(connection => connection.ProjectId == environment.ProjectId);
            return plaintext;
        }
        finally { registrationLock.Release(); }
    }

    public async Task<bool> DeleteProjectAsync(string runtimeId, string projectId, CancellationToken cancellationToken)
    {
        var registrationLock = _registrationLocks.GetOrAdd(runtimeId, _ => new SemaphoreSlim(1, 1));
        await registrationLock.WaitAsync(cancellationToken);
        try
        {
            if (!await store.DeleteProjectAsync(projectId, cancellationToken)) return false;
            CloseConnections(connection => connection.ProjectId == projectId);
            return true;
        }
        finally { registrationLock.Release(); }
    }

    public RuntimeSubscription? Subscribe(string sessionId)
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<ProtocolEnvelope>(new BoundedChannelOptions(256)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        var subscribers = _subscribers.GetOrAdd(sessionId, _ => new());
        if (subscribers.Count >= 16) return null;
        subscribers[id] = channel;
        return new RuntimeSubscription(this, sessionId, id, channel.Reader);
    }

    internal void Unsubscribe(RuntimeSubscription subscription)
    {
        if (!_subscribers.TryGetValue(subscription.SessionId, out var subscribers))
        {
            return;
        }

        if (subscribers.TryRemove(subscription.Id, out var channel))
        {
            channel.Writer.TryComplete();
        }

        if (subscribers.IsEmpty)
        {
            _subscribers.TryRemove(subscription.SessionId, out _);
        }
    }

    private RuntimeConnection? Authorise(HttpContext context)
    {
        var key = BearerToken(context);
        var connectionId = context.Request.Headers[ConnectionHeader].ToString();
        var separator = connectionId.IndexOf('.');
        if (key is null || separator <= 0 ||
            !_connections.TryGetValue(connectionId[..separator], out var connection) || connection.IsClosed ||
            connectionId != connection.ConnectionId || !RuntimeKeys.Matches(connection.KeyHash, key))
            return null;
        return connection;
    }

    private static string? BearerToken(HttpContext context)
    {
        var authorisation = context.Request.Headers.Authorization.ToString();
        return authorisation.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authorisation["Bearer ".Length..].Trim()
            : null;
    }

    private void Touch(RuntimeConnection connection) =>
        _lastSeen[ConnectionKey(connection.RuntimeId, connection.ProjectId)] = DateTimeOffset.UtcNow;

    private void CloseConnections(Func<RuntimeConnection, bool> predicate)
    {
        foreach (var connection in _connections.Values.Where(predicate))
        {
            _connections.TryRemove(new KeyValuePair<string, RuntimeConnection>(connection.RuntimeId, connection));
            connection.Close();
            logger.LogInformation("Runtime {RuntimeId} disconnected", connection.RuntimeId);
        }
    }

    private Task CommandSignal(string runtimeId) =>
        _commandSignals.GetOrAdd(runtimeId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

    private void WakeCommandPoll(string runtimeId)
    {
        if (_commandSignals.TryRemove(runtimeId, out var signal)) signal.TrySetResult();
    }

    private async Task<string?> AcceptEventAsync(
        RuntimeConnection connection,
        ProtocolEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var runtimeId = connection.RuntimeId;
        if (!ProtocolValidator.TryValidateRuntimeEvent(envelope, runtimeId, out var error)) return error;

        if (envelope.SessionId is not null)
        {
            var session = await store.GetSessionAsync(envelope.SessionId, cancellationToken);
            if (session is null || session.ProjectId != connection.ProjectId || session.RuntimeId != runtimeId)
                return "Session is outside the runtime key's workspace";
        }
        if (envelope.Type == RuntimeEventTypes.RuntimeConnected && RuntimeInfo.TryRead(envelope.Payload, out var info))
            _runtimeVersions[ConnectionKey(runtimeId, connection.ProjectId)] = info!.Version;
        if (envelope.Type == RuntimeEventTypes.RuntimeGitStatus && ProtocolValidator.TryReadGitStatus(envelope.Payload, out var git, out _))
            RecordGitStatus(ConnectionKey(runtimeId, connection.ProjectId), git!);
        if (envelope.SessionId is null) return null;

        var owningSession = await store.GetSessionAsync(envelope.SessionId, cancellationToken);
        if (owningSession is null) return null;
        if ((envelope.Type == RuntimeEventTypes.SessionDiscovery && owningSession.Job?.Type != TestingJob.DiscoverTests) ||
            (envelope.Type == RuntimeEventTypes.SessionTestResult &&
                (owningSession.Job?.TestIds is not { } selectedIds ||
                 !selectedIds.Contains(envelope.Payload.GetProperty("test_id").GetString()!, StringComparer.Ordinal))))
            return "Event does not belong to this testing step";
        if (envelope.Type == RuntimeEventTypes.SessionTestState)
            return await AcceptTestStateAsync(owningSession, envelope, cancellationToken);
        if (owningSession.Status is SessionStatuses.Cancelled or SessionStatuses.Completed) return null;

        var inserted = await store.AppendEventAsync(envelope, cancellationToken);
        var status = envelope.Type switch
        {
            RuntimeEventTypes.SessionStarted => SessionStatuses.Running,
            RuntimeEventTypes.SessionQuestion => SessionStatuses.AwaitingInput,
            RuntimeEventTypes.SessionCompleted => SessionStatuses.Completed,
            RuntimeEventTypes.SessionDiscovery => envelope.Payload.GetProperty("status").GetString() == "discovering"
                ? SessionStatuses.Running : SessionStatuses.Completed,
            _ => null
        };
        if (inserted && status is not null && owningSession.Status is not (SessionStatuses.Cancelled or SessionStatuses.Completed))
        {
            await store.TryUpdateSessionStatusAsync(envelope.SessionId, owningSession.Status, status, cancellationToken);
        }

        if (inserted) Publish(envelope);
        return null;
    }

    private async Task<string?> AcceptTestStateAsync(InvestigationSession session, ProtocolEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var events = await store.ListEventsAsync(session.Id, cancellationToken);
        var completed = events.LastOrDefault(item => item.Type == RuntimeEventTypes.SessionCompleted);
        if (session.Job?.Type != TestingJob.AddTest || completed is null ||
            !ProtocolValidator.TryReadCompletion(completed.Payload, out var completion, out _) ||
            completion!.Outcome != PublicOutcomes.Proposed)
            return "Event does not belong to this testing step";
        if (events.Any(item => item.Type == RuntimeEventTypes.SessionTestState)) return null;
        if (await store.AppendEventAsync(envelope, cancellationToken)) Publish(envelope);
        return null;
    }

    private async Task<IReadOnlyList<ProtocolEnvelope>> PendingCommandsAsync(
        RuntimeConnection connection,
        CancellationToken cancellationToken)
    {
        var (runtimeId, projectId) = (connection.RuntimeId, connection.ProjectId);
        var commands = new List<ProtocolEnvelope>();
        foreach (var command in await store.ListPendingCommandsAsync(runtimeId, cancellationToken))
        {
            if (command.Payload.ValueKind == JsonValueKind.Object && command.Payload.TryGetProperty("project_id", out var commandProject) &&
                commandProject.ValueKind == JsonValueKind.String && commandProject.GetString() != projectId)
                continue;
            if (command.SessionId is { } sessionId)
            {
                var session = await store.GetSessionAsync(sessionId, cancellationToken);
                if (session is null || session.ProjectId != projectId) continue;
                if (command.Type == CloudCommandTypes.SessionStart && session.Status == SessionStatuses.Cancelled)
                { await store.DiscardCommandAsync(command.Id, cancellationToken); continue; }
            }
            if (!ProtocolValidator.TryValidateCloudCommand(command, runtimeId, out _))
            {
                await store.DiscardCommandAsync(command.Id, cancellationToken);
                if (command.SessionId is { } oldId && await store.GetSessionAsync(oldId, cancellationToken) is { } old && old.Status == SessionStatuses.Queued)
                    await store.UpdateSessionStatusAsync(oldId, SessionStatuses.Completed, cancellationToken);
                continue;
            }
            commands.Add(command);
            if (commands.Count == MaximumCommandsPerResponse) break;
        }
        return commands;
    }

    private void RecordGitStatus(string key, GitStatus status)
    {
        _gitStatuses[key] = status;
        if (status.ResultCode is null) return;
        _gitResults[key] = status.ResultCode;
        if (_pendingGitCommands.TryGetValue(key, out var pending) && pending.CommandId == status.CommandId)
            _pendingGitCommands.TryRemove(new KeyValuePair<string, PendingGitCommand>(key, pending));
    }

    private bool IsGitCommandPending(string key)
    {
        if (!_pendingGitCommands.TryGetValue(key, out var pending)) return false;
        if (DateTimeOffset.UtcNow - pending.SentAt < GitCommandExpiry) return true;
        _pendingGitCommands.TryRemove(new KeyValuePair<string, PendingGitCommand>(key, pending));
        return false;
    }

    private static async Task<JsonDocument?> ReadJsonAsync(HttpContext context)
    {
        try { return await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted); }
        catch (JsonException) { return null; }
    }

    private static string? OptionalString(JsonDocument? document, string property) =>
        document?.RootElement is { ValueKind: JsonValueKind.Object } root &&
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static Task WriteJsonAsync(HttpContext context, int statusCode, object value)
    {
        context.Response.StatusCode = statusCode;
        context.Response.Headers.CacheControl = "no-store";
        return context.Response.WriteAsJsonAsync(value, ProtocolJson.Options, context.RequestAborted);
    }

    private static Task WriteErrorAsync(HttpContext context, int statusCode, string error) =>
        WriteJsonAsync(context, statusCode, new { error });

    private static Task WriteProtocolMismatchAsync(HttpContext context) =>
        WriteJsonAsync(context, StatusCodes.Status409Conflict, new
        {
            error = "protocol_mismatch",
            server_version = ServerVersion,
            server_protocol = ProtocolConstants.Version
        });

    private static string Base64UrlEncode(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string ConnectionKey(string runtimeId, string projectId) => $"{projectId}\n{runtimeId}";

    private sealed record PendingGitCommand(string CommandId, DateTimeOffset SentAt);

    private void Publish(ProtocolEnvelope envelope)
    {
        if (envelope.SessionId is null || !_subscribers.TryGetValue(envelope.SessionId, out var subscribers))
        {
            return;
        }

        foreach (var channel in subscribers.Values)
        {
            if (!channel.Writer.TryWrite(envelope)) channel.Writer.TryComplete();
        }
    }

    public ValueTask DisposeAsync()
    {
        CloseConnections(_ => true);
        foreach (var subscribers in _subscribers.Values)
        {
            foreach (var channel in subscribers.Values)
            {
                channel.Writer.TryComplete();
            }
        }

        _subscribers.Clear();
        return ValueTask.CompletedTask;
    }
}
