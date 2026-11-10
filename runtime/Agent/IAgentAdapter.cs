using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Quodery.Protocol;

namespace Quodery.Runtime.Agent;

internal sealed record AgentSession(string Id, TestingJob Job, string? SourceResult = null, IReadOnlyList<SelectedTest>? SelectedTests = null);
internal sealed record SelectedTest([property: JsonPropertyName("test_id")] string TestId, string Selector);
internal sealed record LocalDiscoveredTest(PublicTestDescriptor Public, string Selector);
internal sealed record LocalDiscoveryReport(IReadOnlyList<LocalDiscoveredTest> Tests);
internal sealed record AgentEvent(string Type, JsonObject Payload);
internal sealed record LocalTestProposal(
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("workspace_root")] string WorkspaceRoot,
    [property: JsonPropertyName("base_commit")] string? BaseCommit,
    [property: JsonPropertyName("files")] IReadOnlyList<string> Files);

internal interface IAgentAdapter : IAsyncDisposable
{
    string Capability { get; }
    bool IsBusy { get; }
    Task<ChannelReader<AgentEvent>> StartSessionAsync(AgentSession session, CancellationToken cancellationToken = default);
    Task AnswerQuestionAsync(string sessionId, string questionId, string answer);
    Task CancelAsync(string sessionId);
}

internal sealed class AgentException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
    public static AgentException SessionNotFound() => new("agent_session_not_found", "agent session was not found");
    public static AgentException SessionBusy() => new("agent_session_busy", "agent session is already running");
}
