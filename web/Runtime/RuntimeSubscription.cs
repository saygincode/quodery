using System.Threading.Channels;
using Quodery.Protocol;

namespace Quodery.Web.Runtime;

public sealed class RuntimeSubscription : IAsyncDisposable
{
    private readonly RuntimeHub _hub;

    internal RuntimeSubscription(
        RuntimeHub hub,
        string sessionId,
        Guid id,
        ChannelReader<ProtocolEnvelope> reader)
    {
        _hub = hub;
        SessionId = sessionId;
        Id = id;
        Reader = reader;
    }

    internal string SessionId { get; }
    internal Guid Id { get; }
    public ChannelReader<ProtocolEnvelope> Reader { get; }

    public ValueTask DisposeAsync()
    {
        _hub.Unsubscribe(this);
        return ValueTask.CompletedTask;
    }
}
