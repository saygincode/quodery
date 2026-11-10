using System.Text;
using System.Text.Json;
using Quodery.Protocol;

namespace Quodery.Runtime.Agent;

internal sealed class LocalSessionReportStore
{
    private const string ProposalSuffix = ".proposal.json";
    private readonly string runtimeDirectory;
    private readonly string directory;
    private readonly RuntimeLog log;

    public LocalSessionReportStore(RuntimeLog log)
    {
        runtimeDirectory = log.RuntimeDirectory;
        directory = Path.Combine(log.SessionDirectory, "session-reports");
        this.log = log;
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public async Task SaveAsync(string sessionId, string reportJson, CancellationToken cancellationToken)
    {
        if (!TestingJob.IsSessionId(sessionId)) throw new ArgumentException("Invalid session id", nameof(sessionId));
        await WriteAsync(sessionId + ".json", reportJson, cancellationToken);
        log.Information("local_report.saved", new { session_id = sessionId });
    }

    public async Task SaveProposalAsync(LocalTestProposal proposal, CancellationToken cancellationToken)
    {
        if (!TestingJob.IsSessionId(proposal.SessionId)) throw new ArgumentException("Invalid session id", nameof(proposal));
        await WriteAsync(proposal.SessionId + ProposalSuffix, JsonSerializer.Serialize(proposal, ProtocolJson.Options), cancellationToken);
        log.Information("local_proposal.saved", new { session_id = proposal.SessionId, file_count = proposal.Files.Count });
    }

    public async Task<IReadOnlyList<LocalTestProposal>> ReadProposalsAsync(string workspaceRoot, CancellationToken cancellationToken)
    {
        var proposals = new List<LocalTestProposal>();
        foreach (var path in ProposalFiles("*" + ProposalSuffix))
        {
            try
            {
                var proposal = JsonSerializer.Deserialize<LocalTestProposal>(
                    await File.ReadAllTextAsync(path, cancellationToken), ProtocolJson.Options);
                if (proposal is not null && proposal.WorkspaceRoot == workspaceRoot) proposals.Add(proposal);
            }
            catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
            {
                log.Warning("local_proposal.unreadable", new { error_type = error.GetType().Name });
            }
        }
        return proposals;
    }

    public void DeleteProposal(string sessionId)
    {
        if (!TestingJob.IsSessionId(sessionId)) return;
        foreach (var path in ProposalFiles(sessionId + ProposalSuffix)) File.Delete(path);
    }

    private IEnumerable<string> ProposalFiles(string pattern) => Directory.EnumerateDirectories(runtimeDirectory)
        .Select(run => Path.Combine(run, "session-reports"))
        .Where(Directory.Exists)
        .SelectMany(reports => Directory.EnumerateFiles(reports, pattern));

    private async Task WriteAsync(string fileName, string content, CancellationToken cancellationToken)
    {
        var destination = Path.Combine(directory, fileName);
        var temporary = destination + ".tmp";
        await File.WriteAllTextAsync(temporary, content, new UTF8Encoding(false), cancellationToken);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temporary, destination, true);
    }

    public async Task<string?> ReadAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (!TestingJob.IsSessionId(sessionId)) return null;
        foreach (var sessionDirectory in Directory.EnumerateDirectories(runtimeDirectory)
                     .OrderByDescending(path => path, StringComparer.Ordinal))
        {
            var path = Path.Combine(sessionDirectory, "session-reports", sessionId + ".json");
            if (!File.Exists(path)) continue;
            return await File.ReadAllTextAsync(path, cancellationToken);
        }
        return null;
    }
}
