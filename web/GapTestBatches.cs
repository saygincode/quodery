using System.Collections.Concurrent;
using Quodery.Protocol;
using Quodery.Web.Storage;

namespace Quodery.Web;

public sealed class GapTestBatch(string runId, string gapSessionId, int[] gapIndexes, string actor)
{
    public string RunId { get; } = runId;
    public string GapSessionId { get; } = gapSessionId;
    public string BatchId { get; } = Guid.NewGuid().ToString();
    public IReadOnlyList<int> GapIndexes { get; } = gapIndexes;
    public string Actor { get; } = actor;
    public int Started { get; internal set; }
    public int Finished { get; internal set; }
    public string? CurrentSessionId { get; internal set; }
    public string? Error { get; internal set; }
}

public sealed class GapTestBatches(ISessionStore store, SessionService sessions, ILogger<GapTestBatches> logger)
    : BackgroundService
{
    private static readonly HashSet<string> StoppingResults = new(StringComparer.Ordinal)
    {
        ResultCodes.TestCreationNotConfigured, ResultCodes.TestCreationNotSafe, ResultCodes.TestingEnvironmentLimited,
        ResultCodes.EnvironmentDisconnected, ResultCodes.LocalContextUnavailable, ResultCodes.TimedOut
    };

    private readonly ConcurrentDictionary<string, GapTestBatch> _batches = new();
    private readonly SemaphoreSlim _wake = new(0);

    public GapTestBatch? Get(string runId) => _batches.GetValueOrDefault(runId);

    public async Task<Refusal?> StartAsync(BrowserAccess access, string runId, string? gapSessionId,
        IReadOnlyList<int> gapIndexes, CancellationToken ct)
    {
        var root = await store.GetSessionAsync(runId, ct);
        if (root is null) return Refusal.NotFound("This run was not found.");
        var indexes = gapIndexes.Distinct().Order().ToArray();
        if (indexes.Length == 0) return Refusal.Rejected("Select the gaps to add tests for.");
        if (!TestingJob.IsSessionId(gapSessionId) ||
            indexes.Any(index => index is < 0 or >= ProtocolConstants.MaximumSafeFindings))
            return Refusal.Rejected("Choose a valid gap.");
        if (_batches.TryGetValue(runId, out var running) && running.Error is null)
            return Refusal.Rejected("Tests for selected gaps are already being added in this run.");
        _batches[runId] = new GapTestBatch(runId, gapSessionId!, indexes, access.Actor);
        _wake.Release();
        return null;
    }

    public void Cancel(string runId) => _batches.TryRemove(runId, out _);

    public void Dismiss(string runId)
    {
        if (_batches.TryGetValue(runId, out var batch) && batch.Error is not null)
            _batches.TryRemove(new KeyValuePair<string, GapTestBatch>(runId, batch));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var batch in _batches.Values.Where(batch => batch.Error is null))
            {
                try { await AdvanceAsync(batch, stoppingToken); }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    logger.LogError(error, "Gap test batch for run {RunId} failed", batch.RunId);
                    batch.Error = "Test creation could not continue. Remaining selected gaps were not started.";
                }
            }
            await _wake.WaitAsync(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }

    private async Task AdvanceAsync(GapTestBatch batch, CancellationToken ct)
    {
        if (batch.CurrentSessionId is { } currentId)
        {
            var current = await store.GetSessionAsync(currentId, ct);
            if (current is null || current.Status is SessionStatuses.Queued or SessionStatuses.Running or SessionStatuses.AwaitingInput)
                return;
            if (current.Status == SessionStatuses.Cancelled)
            {
                batch.Error = "The selected gap test was stopped. Remaining tests were not started.";
                return;
            }
            var completion = (await store.ListEventsAsync(current.Id, ct))
                .LastOrDefault(item => item.Type == RuntimeEventTypes.SessionCompleted);
            if (completion is null || !ProtocolValidator.TryReadCompletion(completion.Payload, out var result, out _) ||
                StoppingResults.Contains(result!.ResultCode))
            {
                batch.Error = "Test creation could not continue. Remaining selected gaps were not started.";
                return;
            }
            batch.CurrentSessionId = null;
            batch.Finished++;
        }
        if (batch.Started >= batch.GapIndexes.Count)
        {
            _batches.TryRemove(new KeyValuePair<string, GapTestBatch>(batch.RunId, batch));
            return;
        }
        var root = await store.GetSessionAsync(batch.RunId, ct);
        if (root is null) { Cancel(batch.RunId); return; }
        if ((await store.ListRelatedSessionsAsync(root.ProjectId, root.Id, ct)).Any(step =>
                step.Status is SessionStatuses.Queued or SessionStatuses.Running or SessionStatuses.AwaitingInput))
            return;
        if (Get(batch.RunId) != batch) return;
        var created = await sessions.AddGapTestAsync(root, batch.GapSessionId, batch.GapIndexes[batch.Started],
            batch.BatchId, batch.Actor, ct);
        if (created.Refusal is { } refusal) { batch.Error = refusal.Message; return; }
        batch.CurrentSessionId = created.Value!.Id;
        batch.Started++;
    }
}
