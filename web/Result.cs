using Quodery.Web.Storage;

namespace Quodery.Web;

public enum RefusalKind { NotFound, Forbidden, Rejected }

public sealed record Refusal(RefusalKind Kind, string Message)
{
    public static Refusal NotFound(string message) => new(RefusalKind.NotFound, message);
    public static Refusal Forbidden() => new(RefusalKind.Forbidden, "You don't have access to this.");
    public static Refusal Rejected(string message) => new(RefusalKind.Rejected, message);
}

public readonly struct Result<T>
{
    private Result(T? value, Refusal? refusal) { Value = value; Refusal = refusal; }

    public T? Value { get; }
    public Refusal? Refusal { get; }

    public static implicit operator Result<T>(T value) => new(value, null);
    public static implicit operator Result<T>(Refusal refusal) => new(default, refusal);
}

internal static class Audit
{
    public static Task RecordAsync(ISessionStore store, string projectId, string? sessionId, string actor, string action,
        CancellationToken cancellationToken) => store.AppendAuditAsync(new AuditRecord($"aud_{Guid.NewGuid():N}", projectId,
            sessionId, actor, action, "allowed", DateTimeOffset.UtcNow), cancellationToken);
}
