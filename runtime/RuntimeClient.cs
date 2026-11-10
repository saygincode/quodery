using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Quodery.Runtime.Agent;
using Quodery.Runtime.Security;
using Quodery.Protocol;

namespace Quodery.Runtime;

internal sealed class RuntimeClient(RuntimeConfiguration configuration, IAgentAdapter adapter,
    LocalSessionReportStore reports, RuntimeLog log)
{
    private readonly SecretRedactor redactor = new();
    private readonly GitWorkspace git = new(configuration.Agent.WorkspaceRoot, log);
    private readonly SemaphoreSlim workspaceReport = new(1, 1);
    private string? lastGitStatus;
    private readonly ConcurrentDictionary<string, byte> handledCommands = new();
    private readonly ConcurrentDictionary<string, ProtocolEnvelope> pendingEvents = new();
    private readonly Channel<byte> eventReady = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.DropWrite });
    private string? runtimeId;
    private string? projectId;
    private static readonly TimeSpan StableConnection = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan WorkspaceReportInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MismatchRetry = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(45);
    private const int MaximumEventBatchBytes = 48 * 1024;
    private const int MaximumEventBatchCount = 50;
    private const string ConnectionHeader = "X-Quodery-Connection";
    private HttpClient? http;

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeat = HeartbeatLoopAsync(lifetime.Token);
        var workspaceReports = WorkspaceReportLoopAsync(lifetime.Token);
        try
        {
            var delay = TimeSpan.FromSeconds(1);
            while (!cancellationToken.IsCancellationRequested)
            {
                var started = DateTimeOffset.UtcNow;
                try
                {
                    log.Information("connection.attempting", new { runtime_id = runtimeId });
                    await RunConnectionAsync(cancellationToken);
                    if (DateTimeOffset.UtcNow - started >= StableConnection)
                        delay = TimeSpan.FromSeconds(1);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
                catch (ProtocolMismatchException error)
                {
                    Console.Error.WriteLine($"cannot connect: {error.Message}");
                    log.Error("connection.protocol_mismatch", new
                    {
                        runtime_id = runtimeId,
                        runtime_version = RuntimeVersion.Current,
                        runtime_protocol = ProtocolConstants.Version,
                        server_version = error.ServerVersion,
                        server_protocol = error.ServerProtocol,
                        retry_seconds = MismatchRetry.TotalSeconds
                    });
                    await Task.Delay(MismatchRetry, cancellationToken);
                    delay = TimeSpan.FromSeconds(1);
                    continue;
                }
                catch (Exception error)
                {
                    var message = redactor.Redact(error.Message);
                    Console.Error.WriteLine($"cloud connection failed: {message}");
                    log.Error("connection.failed", new
                    {
                        runtime_id = runtimeId,
                        error_type = error.GetType().Name,
                        message,
                        retry_seconds = delay.TotalSeconds
                    });
                }
                await Task.Delay(delay, cancellationToken);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
            }
        }
        finally
        {
            lifetime.Cancel();
            try { await heartbeat; } catch (OperationCanceledException) { }
            try { await workspaceReports; } catch (OperationCanceledException) { }
        }
    }

    private async Task RunConnectionAsync(CancellationToken cancellationToken)
    {
        var connectionId = await StartSessionAsync(cancellationToken);
        log.Information("connection.connected", new
        {
            runtime_id = RuntimeId,
            project_id = ProjectId
        });
        Emit(null, RuntimeEventTypes.RuntimeConnected, new RuntimeInfo(RuntimeVersion.Current));
        lastGitStatus = null;
        await ReportWorkspaceAsync(cancellationToken);
        Console.Error.WriteLine($"connected runtime {RuntimeId} to control plane");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var sender = SendPendingEventsAsync(connectionId, linked.Token);
        var poller = PollCommandsAsync(connectionId, cancellationToken, linked.Token);
        try { await await Task.WhenAny(sender, poller); }
        catch (ConnectionClosedException)
        {
            log.Information("connection.closed", new { runtime_id = RuntimeId });
        }
        finally
        {
            linked.Cancel();
            try { await Task.WhenAll(sender, poller); } catch (Exception) { }
            log.Information("connection.ended", new
            {
                runtime_id = RuntimeId,
                pending_event_count = pendingEvents.Count
            });
        }
    }

    private async Task<string> StartSessionAsync(CancellationToken cancellationToken)
    {
        string? serverProtocol, serverVersion;
        using (var response = await PostAsync("challenge", null, new { }, RequestTimeout, cancellationToken))
        {
            if (response.StatusCode == HttpStatusCode.NotFound) throw new ProtocolMismatchException(null, null);
            using var document = await ReadSuccessAsync(response, "challenge", cancellationToken);
            serverProtocol = OptionalString(document, "protocol_version");
            serverVersion = OptionalString(document, "server_version");
        }
        using var session = await PostAsync("session", null, new
        {
            protocol_version = ProtocolConstants.Version,
            runtime_version = RuntimeVersion.Current
        }, RequestTimeout, cancellationToken);
        if (serverProtocol is not null && serverProtocol != ProtocolConstants.Version)
            throw new ProtocolMismatchException(serverVersion, serverProtocol);
        if (session.StatusCode == HttpStatusCode.Unauthorized)
            throw new InvalidOperationException(
                "the server rejected the runtime key; it may have been replaced or its workspace deleted. " +
                "Set QUODERY_RUNTIME_KEY to the key shown when the workspace's connection was last created or refreshed");
        using var started = await ReadSuccessAsync(session, "session", cancellationToken);
        var connectionId = OptionalString(started, "connection_id");
        var sessionRuntimeId = OptionalString(started, "runtime_id");
        var sessionProjectId = OptionalString(started, "project_id");
        if (string.IsNullOrWhiteSpace(connectionId) || string.IsNullOrWhiteSpace(sessionRuntimeId) ||
            string.IsNullOrWhiteSpace(sessionProjectId))
            throw new InvalidOperationException("the server returned an incomplete session");
        if (runtimeId is not null && (runtimeId != sessionRuntimeId || projectId != sessionProjectId))
            throw new InvalidOperationException("the runtime key's workspace changed while the process was running");
        runtimeId = sessionRuntimeId;
        projectId = sessionProjectId;
        return connectionId;
    }

    private async Task PollCommandsAsync(string connectionId, CancellationToken runtimeToken, CancellationToken connectionToken)
    {
        var acknowledged = new List<string>();
        while (true)
        {
            using var response = await PostAsync("commands", connectionId, new { acknowledged }, PollTimeout, connectionToken);
            using var document = await ReadSuccessAsync(response, "commands", connectionToken);
            acknowledged.Clear();
            foreach (var item in document.RootElement.GetProperty("commands").EnumerateArray())
                if (await HandleCommandAsync(item.GetRawText(), runtimeToken) is { } commandId)
                    acknowledged.Add(commandId);
        }
    }

    private async Task<string?> HandleCommandAsync(string raw, CancellationToken cancellationToken)
    {
        ProtocolEnvelope? command;
        try { command = JsonSerializer.Deserialize<ProtocolEnvelope>(raw, ProtocolJson.Options); }
        catch (JsonException error)
        {
            Console.Error.WriteLine($"ignored malformed cloud command: {error.Message}");
            log.Warning("command.rejected", new { reason = "malformed_json", message_bytes = Encoding.UTF8.GetByteCount(raw) });
            return null;
        }
        if (command is null)
        {
            Console.Error.WriteLine("ignored empty cloud command");
            log.Warning("command.rejected", new { reason = "empty_command" });
            return null;
        }
        if (!ProtocolValidator.TryValidateCloudCommand(command, RuntimeId, out var reason))
        {
            Console.Error.WriteLine($"ignored invalid cloud command: {reason}");
            log.Warning("command.rejected", CommandData(command, new { reason }));
            return command.Id;
        }

        log.Information("command.received", CommandData(command));
        var firstDelivery = handledCommands.TryAdd(command.Id, 0);
        log.Information(firstDelivery ? "command.accepted" : "command.duplicate", CommandData(command));
        if (!firstDelivery) return command.Id;
        if (handledCommands.Count > 4096)
        {
            var pruned = 0;
            foreach (var id in handledCommands.Keys.Take(1024))
                if (handledCommands.TryRemove(id, out _)) pruned++;
            log.Information("command.deduplication_cache_pruned", new { removed_count = pruned });
        }

        switch (command.Type)
        {
            case CloudCommandTypes.SessionStart when command.SessionId is { } sessionId:
                if (!ProtocolValidator.TryReadSessionStart(command, out var start, out _))
                {
                    EmitLocalFailure(sessionId, start?.Job, "invalid_session_configuration");
                    break;
                }
                try
                {
                    if (start!.ProjectId != ProjectId)
                        throw new AgentException("project_not_allowed", "Job is outside the runtime key's workspace");
                    var agentSession = new AgentSession(start.SessionId, start.Job);
                    var agentEvents = await adapter.StartSessionAsync(agentSession, cancellationToken);
                    log.Information("session.started", CommandData(command, new { job_type = start.Job.Type }));
                    _ = ForwardAgentEventsAsync(agentSession, agentEvents, cancellationToken);
                }
                catch (Exception error)
                {
                    var code = error is AgentException agentError ? agentError.Code : "invalid_session_configuration";
                    var message = redactor.Redact(error.Message);
                    Console.Error.WriteLine($"agent could not start session: {message}");
                    log.Error("session.start_failed", CommandData(command, new { error_code = code, message }));
                    EmitLocalFailure(sessionId, start?.Job, code);
                }
                break;
            case CloudCommandTypes.SessionCancel when command.SessionId is { } sessionId:
                try
                {
                    await adapter.CancelAsync(sessionId);
                    log.Information("session.cancelled", CommandData(command));
                }
                catch (AgentException error)
                {
                    log.Warning("session.cancel_ignored", CommandData(command, new { error_code = error.Code }));
                }
                break;
            case CloudCommandTypes.SessionAnswerQuestion when command.SessionId is { } sessionId:
                try
                {
                    if (!ProtocolValidator.TryReadSessionAnswerQuestion(command, out var answer, out _)) break;
                    await adapter.AnswerQuestionAsync(sessionId, answer!.QuestionId, answer.Answer);
                    log.Information("session.answer_accepted", CommandData(command, new { question_id = answer.QuestionId }));
                }
                catch (AgentException error)
                {
                    log.Warning("session.answer_rejected", CommandData(command, new { error_code = error.Code }));
                    log.Warning("session.answer_not_forwarded", CommandData(command, new { reason = "local_session_rejected_answer" }));
                }
                break;
            case CloudCommandTypes.GitSwitchBranch:
            case CloudCommandTypes.GitPull:
            case CloudCommandTypes.GitCommit:
            case CloudCommandTypes.GitDiscard:
                await HandleGitCommandAsync(command, cancellationToken);
                break;
        }
        return command.Id;
    }

    private async Task HandleGitCommandAsync(ProtocolEnvelope command, CancellationToken cancellationToken)
    {
        if (!configuration.AllowGit)
        {
            log.Warning("git.command_refused", CommandData(command, new { reason = "git_not_allowed" }));
            return;
        }
        var result = adapter.IsBusy ? GitResultCodes.Busy : command.Type switch
        {
            CloudCommandTypes.GitSwitchBranch when ProtocolValidator.TryReadGitSwitchBranch(command, out var branch, out _) =>
                await git.SwitchAsync(branch!, cancellationToken),
            CloudCommandTypes.GitCommit when ProtocolValidator.TryReadGitCommit(command, out var message, out _) =>
                await git.CommitAsync(message!, cancellationToken),
            CloudCommandTypes.GitPull => await git.PullAsync(cancellationToken),
            CloudCommandTypes.GitDiscard => await git.DiscardAsync(cancellationToken),
            _ => GitResultCodes.Failed
        };
        log.Information("git.command_finished", CommandData(command, new { result_code = result }));
        await ReportWorkspaceAsync(cancellationToken, command.Id, result);
    }

    private async Task ReportWorkspaceAsync(CancellationToken cancellationToken, string? commandId = null, string? resultCode = null)
    {
        if (projectId is null) return;
        await workspaceReport.WaitAsync(cancellationToken);
        try
        {
            var proposedTests = await ReportTestStatesAsync(cancellationToken);
            if (!configuration.AllowGit) return;
            var status = await git.ReadStatusAsync(cancellationToken) with
            { ProposedTests = proposedTests, CommandId = commandId, ResultCode = resultCode };
            var text = JsonSerializer.Serialize(status, ProtocolJson.Options);
            if (commandId is null && text == lastGitStatus) return;
            lastGitStatus = text;
            Emit(null, RuntimeEventTypes.RuntimeGitStatus, status);
        }
        finally { workspaceReport.Release(); }
    }

    private async Task<int> ReportTestStatesAsync(CancellationToken cancellationToken)
    {
        var proposals = await reports.ReadProposalsAsync(configuration.Agent.WorkspaceRoot, cancellationToken);
        if (proposals.Count == 0) return 0;
        if (await git.ReadUncommittedFilesAsync(cancellationToken) is not { } uncommitted) return proposals.Count;
        var stillProposed = 0;
        foreach (var proposal in proposals)
        {
            if (pendingEvents.Values.Any(envelope => envelope.Type == RuntimeEventTypes.SessionTestState &&
                    envelope.SessionId == proposal.SessionId)) continue;
            if (await git.ReadTestStateAsync(proposal, uncommitted, cancellationToken) is { } update)
                Emit(proposal.SessionId, RuntimeEventTypes.SessionTestState, new { state = update.State, commit = update.Commit });
            else stillProposed++;
        }
        return stillProposed;
    }

    private async Task WorkspaceReportLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(WorkspaceReportInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            try { await ReportWorkspaceAsync(cancellationToken); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                log.Warning("workspace_report.failed", new { error_type = error.GetType().Name });
            }
        }
    }

    private async Task ForwardAgentEventsAsync(AgentSession session, ChannelReader<AgentEvent> source, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var item in source.ReadAllAsync(cancellationToken))
                if (OutboundEventProjector.Project(RuntimeId, session, item) is { } projected)
                    QueueEvent(projected);
            log.Information("session.event_stream_completed", new { session_id = session.Id });
            await ReportWorkspaceAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (await timer.WaitForNextTickAsync(cancellationToken))
            if (!pendingEvents.Values.Any(x => x.Type == RuntimeEventTypes.RuntimeHeartbeat))
                Emit(null, RuntimeEventTypes.RuntimeHeartbeat, new { });
    }

    private async Task SendPendingEventsAsync(string connectionId, CancellationToken cancellationToken)
    {
        while (true)
        {
            while (eventReady.Reader.TryRead(out _)) { }
            var batch = NextEventBatch();
            if (batch.Count == 0)
            {
                await eventReady.Reader.WaitToReadAsync(cancellationToken);
                continue;
            }
            await SendEventsAsync(connectionId, batch, cancellationToken);
        }
    }

    private List<ProtocolEnvelope> NextEventBatch()
    {
        var batch = new List<ProtocolEnvelope>();
        var bytes = 0;
        foreach (var envelope in pendingEvents.Values.OrderBy(envelope => envelope.Timestamp).ThenBy(envelope => envelope.Id))
        {
            var size = JsonSerializer.SerializeToUtf8Bytes(envelope, ProtocolJson.Options).Length;
            if (batch.Count > 0 && (batch.Count == MaximumEventBatchCount || bytes + size > MaximumEventBatchBytes)) break;
            batch.Add(envelope);
            bytes += size;
        }
        return batch;
    }

    private async Task SendEventsAsync(string connectionId, List<ProtocolEnvelope> batch, CancellationToken cancellationToken)
    {
        using var response = await PostAsync("events", connectionId, new { events = batch }, RequestTimeout, cancellationToken);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retry = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5);
            log.Warning("event.rate_limited", new { event_count = batch.Count, retry_seconds = retry.TotalSeconds });
            await Task.Delay(retry, cancellationToken);
            return;
        }
        if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            using var refusal = await ReadJsonAsync(response, cancellationToken);
            var accepted = Acknowledge(refusal);
            if (accepted < batch.Count && pendingEvents.TryRemove(batch[accepted].Id, out var rejected))
            {
                log.Error("event.rejected_by_server", EventData(rejected, new { reason = ErrorCode(refusal) }));
                FinishTestState(rejected);
            }
            return;
        }
        using var document = await ReadSuccessAsync(response, "events", cancellationToken);
        Acknowledge(document);
    }

    private int Acknowledge(JsonDocument document)
    {
        if (!document.RootElement.TryGetProperty("acknowledged", out var acknowledged) ||
            acknowledged.ValueKind != JsonValueKind.Array) return 0;
        foreach (var id in acknowledged.EnumerateArray())
            if (id.GetString() is { } eventId && pendingEvents.TryRemove(eventId, out var envelope))
            {
                log.Information("event.acknowledged", EventData(envelope));
                FinishTestState(envelope);
            }
        return acknowledged.GetArrayLength();
    }

    private void FinishTestState(ProtocolEnvelope envelope)
    {
        if (envelope.Type != RuntimeEventTypes.SessionTestState || envelope.SessionId is not { } sessionId) return;
        try { reports.DeleteProposal(sessionId); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            log.Warning("local_proposal.not_deleted", new { session_id = sessionId, error_type = error.GetType().Name });
        }
    }

    private async Task<HttpResponseMessage> PostAsync(
        string request, string? connectionId, object body, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, configuration.EndpointUrl(request))
        {
            Content = JsonContent.Create(body, options: ProtocolJson.Options)
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", configuration.RuntimeKey);
        if (connectionId is not null) message.Headers.Add(ConnectionHeader, connectionId);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try { return await (http ??= CreateHttpClient()).SendAsync(message, deadline.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException($"the server did not answer the {request} request in time"); }
    }

    private HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler { AllowAutoRedirect = false };
        if (configuration.UnixSocketPath is { } path)
        {
            handler.ConnectCallback = async (_, connectToken) =>
            {
                var unixSocket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await unixSocket.ConnectAsync(new UnixDomainSocketEndPoint(path), connectToken);
                    return new NetworkStream(unixSocket, ownsSocket: true);
                }
                catch
                {
                    unixSocket.Dispose();
                    throw;
                }
            };
        }
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = 1024 * 1024 };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("quodery-runtime", RuntimeVersion.Current));
        return client;
    }

    private static async Task<JsonDocument> ReadSuccessAsync(
        HttpResponseMessage response, string request, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return await ReadJsonAsync(response, cancellationToken);
        using var document = response.Content.Headers.ContentType?.MediaType == "application/json"
            ? await ReadJsonAsync(response, cancellationToken) : null;
        if (response.StatusCode == HttpStatusCode.Conflict && ErrorCode(document) == "protocol_mismatch")
            throw new ProtocolMismatchException(
                OptionalString(document, "server_version"), OptionalString(document, "server_protocol"));
        if (response.StatusCode == HttpStatusCode.Unauthorized && request is "commands" or "events")
            throw new ConnectionClosedException();
        throw new HttpRequestException(
            $"the server answered the {request} request with {(int)response.StatusCode} {ErrorCode(document) ?? response.ReasonPhrase}",
            null, response.StatusCode);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try { return JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken)); }
        catch (JsonException)
        { throw new InvalidDataException($"the server answered with {(int)response.StatusCode} and no valid JSON"); }
    }

    private static string? ErrorCode(JsonDocument? document) => OptionalString(document, "error");

    private static string? OptionalString(JsonDocument? document, string property) =>
        document?.RootElement is { ValueKind: JsonValueKind.Object } root &&
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private void Emit(string? sessionId, string type, object payload)
    {
        if (projectId is null) return;
        var envelope = ProtocolEnvelopeFactory.CreateRuntimeEvent(RuntimeId, sessionId, type, payload);
        if (ProtocolValidator.TryValidateRuntimeEvent(envelope, RuntimeId, out _)) QueueEvent(envelope);
        else log.Warning("event.rejected_before_queue", new { event_type = type });
    }

    private void EmitLocalFailure(string sessionId, TestingJob? job, string code)
    {
        if (job is null) return;
        var local = new AgentEvent("session.failed", new System.Text.Json.Nodes.JsonObject { ["error_code"] = code });
        if (OutboundEventProjector.Project(RuntimeId, new AgentSession(sessionId, job), local) is { } projected)
            QueueEvent(projected);
    }

    private void QueueEvent(ProtocolEnvelope envelope)
    {
        pendingEvents[envelope.Id] = envelope;
        eventReady.Writer.TryWrite(0);
        log.Information("event.queued", EventData(envelope));
    }

    private static object CommandData(ProtocolEnvelope command, object? detail = null) => new
    {
        command_id = command.Id,
        command_type = command.Type,
        runtime_id = command.RuntimeId,
        session_id = command.SessionId,
        detail
    };

    private static object EventData(ProtocolEnvelope envelope, object? detail = null) => new
    {
        event_id = envelope.Id,
        event_type = envelope.Type,
        runtime_id = envelope.RuntimeId,
        session_id = envelope.SessionId,
        detail
    };

    private string RuntimeId => runtimeId
        ?? throw new InvalidOperationException("runtime has not connected");
    private string ProjectId => projectId
        ?? throw new InvalidOperationException("runtime has not connected");
}

internal sealed class ProtocolMismatchException(string? serverVersion, string? serverProtocol) : Exception(
    serverVersion is null
        ? $"this runtime is version {RuntimeVersion.Current} (protocol {ProtocolConstants.Version}) and the server uses another protocol; install the quodery release that matches the server on this machine"
        : $"this runtime is version {RuntimeVersion.Current} (protocol {ProtocolConstants.Version}) but the server is version {serverVersion} (protocol {serverProtocol ?? "unknown"}); install quodery {serverVersion} on this machine")
{
    public string? ServerVersion { get; } = serverVersion;
    public string? ServerProtocol { get; } = serverProtocol;
}

internal sealed class ConnectionClosedException() : Exception("the server closed this connection");
