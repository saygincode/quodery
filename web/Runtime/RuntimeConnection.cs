namespace Quodery.Web.Runtime;

internal sealed class RuntimeConnection(
    string runtimeId, string environmentId, string projectId, byte[] keyHash, string connectionId)
{
    private readonly CancellationTokenSource _closed = new();
    private readonly Lock _rateLock = new();
    private DateTimeOffset _windowStarted = DateTimeOffset.UtcNow;
    private int _eventsInWindow;

    public string RuntimeId { get; } = runtimeId;
    public string EnvironmentId { get; } = environmentId;
    public string ProjectId { get; } = projectId;
    public byte[] KeyHash { get; } = keyHash;
    public string ConnectionId { get; } = connectionId;
    public CancellationToken Closed => _closed.Token;
    public bool IsClosed => _closed.IsCancellationRequested;

    public bool TryCountEvents(int count, int maximumPerMinute)
    {
        lock (_rateLock)
        {
            if (DateTimeOffset.UtcNow - _windowStarted >= TimeSpan.FromMinutes(1))
            { _windowStarted = DateTimeOffset.UtcNow; _eventsInWindow = 0; }
            _eventsInWindow += count;
            return _eventsInWindow <= maximumPerMinute;
        }
    }

    public void Close() => _closed.Cancel();
}
