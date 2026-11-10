using Quodery.Protocol;

namespace Quodery.Web.Pages.Sessions;

public static class SessionEvents
{
    public static ProtocolEnvelope? Latest(this SessionDetail session, string type) =>
        session.Events.LastOrDefault(envelope => envelope.Type == type);

    public static PublicCompletion? Completion(this SessionDetail session) =>
        session.Latest(RuntimeEventTypes.SessionCompleted) is { } envelope &&
        ProtocolValidator.TryReadCompletion(envelope.Payload, out var completion, out _) ? completion : null;

    public static TestStateUpdate? TestState(this SessionDetail session) =>
        session.Latest(RuntimeEventTypes.SessionTestState) is { } envelope &&
        ProtocolValidator.TryReadTestState(envelope.Payload, out var update, out _) ? update : null;

    public static DateTimeOffset? CompletedAt(this SessionDetail session) =>
        session.Latest(RuntimeEventTypes.SessionCompleted)?.Timestamp;

    public static DiscoveryResult? Discovery(this SessionDetail session) =>
        session.Latest(RuntimeEventTypes.SessionDiscovery) is { } envelope &&
        ProtocolValidator.TryReadDiscovery(envelope.Payload, out var discovery, out _) ? discovery : null;

    public static AgentQuestion? Question(this SessionDetail session) =>
        session.Latest(RuntimeEventTypes.SessionQuestion) is { } envelope &&
        ProtocolValidator.TryReadSessionQuestion(envelope.Payload, out var question, out _) ? question : null;

    public static IEnumerable<SelectedTestResult> TestResults(this SessionDetail session) =>
        session.Events.Where(envelope => envelope.Type == RuntimeEventTypes.SessionTestResult)
            .Select(envelope => ProtocolValidator.TryReadTestResult(envelope.Payload, out var result, out _) ? result : null)
            .OfType<SelectedTestResult>();

    public static IEnumerable<string> Stages(this SessionDetail session) =>
        session.Events.Where(envelope => envelope.Type is RuntimeEventTypes.SessionStarted or RuntimeEventTypes.SessionProgress)
            .Select(envelope => ProtocolValidator.TryReadProgress(envelope.Payload, out var progress, out _) ? progress!.Stage : null)
            .OfType<string>();

    public static string Version(IEnumerable<SessionDetail> sessions, string extra = "")
    {
        var text = string.Join("|", sessions.Select(session =>
            $"{session.Id}:{session.Status}:{string.Join(",", session.Events.Select(envelope => envelope.Id))}")) + "|" + extra;
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)))[..16];
    }
}
