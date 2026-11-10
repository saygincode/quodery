using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Quodery.Protocol;
using Quodery.Runtime.Agent;
using Quodery.Runtime.Security;

namespace Quodery.Runtime;

internal sealed record GitCodeVersion(string Commit, bool Modified);

internal sealed class GitWorkspace(string root, RuntimeLog? log)
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromMinutes(2);
    private const int MaximumBranchListBytes = 32 * 1024;
    private const int MaximumChangedFiles = 100000;
    private const int MaximumProposalFiles = 200;
    private const string DeletedFile = "deleted";
    private const string LocalBranches = "refs/heads/";
    private const string RemoteBranches = "refs/remotes/origin/";
    private readonly SecretRedactor redactor = new();

    public static bool IsRepository(string root)
    {
        try
        {
            using var process = Process.Start(StartInfo(root, ["rev-parse", "--git-dir"]));
            if (process is null) return false;
            process.StandardInput.Close();
            _ = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            return process.WaitForExit(OperationTimeout) && process.ExitCode == 0;
        }
        catch (Win32Exception) { return false; }
    }

    public async Task<GitCodeVersion?> ReadCodeVersionAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var deadline = Deadline(cancellationToken);
            var commit = await RunAsync(deadline.Token, "rev-parse", "--verify", "--quiet", "HEAD");
            if (commit.ExitCode != 0) return null;
            return new(commit.Output.Trim(), await IsModifiedAsync(deadline.Token));
        }
        catch (Exception error) when (IsExpected(error, cancellationToken)) { return null; }
    }

    public async Task<GitStatus> ReadStatusAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var deadline = Deadline(cancellationToken);
            var token = deadline.Token;
            var branch = await RunAsync(token, "symbolic-ref", "--quiet", "--short", "HEAD");
            var name = branch.ExitCode == 0 ? branch.Output.Trim() : null;
            var commit = await RunAsync(token, "rev-parse", "--verify", "--quiet", "HEAD");
            var hash = commit.ExitCode == 0 ? commit.Output.Trim() : null;
            return new(ProtocolValidator.IsBranchName(name) ? name : null,
                ProtocolValidator.IsCodeVersion(hash) ? hash : null,
                await CountChangedFilesAsync(token) ?? 0, ReportableBranches(await ReadRefsAsync(token)));
        }
        catch (Exception error) when (IsExpected(error, cancellationToken))
        {
            log?.Warning("git.status_failed", new { error_type = error.GetType().Name });
            return new(null, null, 0, []);
        }
    }

    public async Task<IReadOnlyDictionary<string, string>?> ReadUncommittedFilesAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var deadline = Deadline(cancellationToken);
            var token = deadline.Token;
            var top = await RunAsync(token, "rev-parse", "--show-toplevel");
            var status = await RunAsync(token, "status", "--porcelain", "-z", "--untracked-files=all", "--ignore-submodules=all");
            if (top.ExitCode != 0 || status.ExitCode != 0) return null;
            var entries = status.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
            var files = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var index = 0; index < entries.Length; index++)
            {
                var path = entries[index][3..];
                files[path] = await HashFileAsync(Path.Combine(top.Output.Trim(), path), token);
                if (entries[index][..2].IndexOfAny(['R', 'C']) >= 0) index++;
            }
            return files;
        }
        catch (Exception error) when (error is UnauthorizedAccessException || IsExpected(error, cancellationToken))
        {
            log?.Warning("git.uncommitted_files_failed", new { error_type = error.GetType().Name });
            return null;
        }
    }

    public static string[] NewChanges(IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> after) =>
        after.Where(file => !before.TryGetValue(file.Key, out var hash) || hash != file.Value)
            .Select(file => file.Key).Order(StringComparer.Ordinal).Take(MaximumProposalFiles).ToArray();

    public async Task<TestStateUpdate?> ReadTestStateAsync(LocalTestProposal proposal,
        IReadOnlyDictionary<string, string> uncommitted, CancellationToken cancellationToken)
    {
        if (proposal.Files.Any(uncommitted.ContainsKey)) return null;
        try
        {
            using var deadline = Deadline(cancellationToken);
            var arguments = new List<string> { "log", "-1", "--format=%H", "--exclude=refs/stash", "--all" };
            if (proposal.BaseCommit is { } baseCommit) arguments.Add($"^{baseCommit}");
            arguments.Add("--");
            arguments.AddRange(proposal.Files.Select(file => $":(top,literal){file}"));
            var commits = await RunAsync(deadline.Token, arguments.ToArray());
            if (commits.ExitCode != 0) return null;
            var commit = commits.Output.Trim();
            return commit.Length == 0 ? new TestStateUpdate(TestStates.Discarded, null)
                : new TestStateUpdate(TestStates.Committed, ProtocolValidator.IsCodeVersion(commit) ? commit : null);
        }
        catch (Exception error) when (IsExpected(error, cancellationToken))
        {
            log?.Warning("git.test_state_failed", new { error_type = error.GetType().Name });
            return null;
        }
    }

    public Task<string> SwitchAsync(string branch, CancellationToken cancellationToken) =>
        GuardedAsync("switch", async token =>
        {
            if (await IsModifiedAsync(token)) return GitResultCodes.UncommittedChanges;
            var refs = await ReadRefsAsync(token);
            if (refs.Contains(LocalBranches + branch))
                return Finish("switch", await RunAsync(token, "switch", branch));
            if (branch == "HEAD" || !refs.Contains(RemoteBranches + branch)) return GitResultCodes.BranchNotFound;
            return Finish("switch", await RunAsync(token, "switch", "--create", branch, "--track", $"origin/{branch}"));
        }, cancellationToken);

    public Task<string> PullAsync(CancellationToken cancellationToken) =>
        GuardedAsync("pull", async token =>
        {
            var fetch = await RunAsync(token, "fetch", "--prune", "origin");
            if (fetch.ExitCode != 0) return Finish("fetch", fetch, GitResultCodes.RemoteUnavailable);
            if (await IsModifiedAsync(token)) return GitResultCodes.UncommittedChanges;
            if ((await RunAsync(token, "rev-parse", "--verify", "--quiet", "@{upstream}")).ExitCode != 0)
                return GitResultCodes.NoUpstream;
            if ((await RunAsync(token, "merge-base", "--is-ancestor", "HEAD", "@{upstream}")).ExitCode != 0)
                return GitResultCodes.NotFastForward;
            return Finish("merge", await RunAsync(token, "merge", "--ff-only", "@{upstream}"));
        }, cancellationToken);

    public Task<string> CommitAsync(string message, CancellationToken cancellationToken) =>
        GuardedAsync("commit", async token =>
        {
            if (!await IsModifiedAsync(token)) return GitResultCodes.NothingToCommit;
            var add = await RunAsync(token, "add", "--all", "--", ":/");
            if (add.ExitCode != 0) return Finish("add", add);
            return Finish("commit", await RunAsync(token, "commit", "--message", message));
        }, cancellationToken);

    public Task<string> DiscardAsync(CancellationToken cancellationToken) =>
        GuardedAsync("discard", async token =>
        {
            var reset = await RunAsync(token, "reset", "--hard", "HEAD");
            if (reset.ExitCode != 0) return Finish("reset", reset);
            return Finish("clean", await RunAsync(token, "clean", "-fd", "--", ":/"));
        }, cancellationToken);

    private async Task<bool> IsModifiedAsync(CancellationToken cancellationToken) =>
        await CountChangedFilesAsync(cancellationToken) is not 0;

    private async Task<int?> CountChangedFilesAsync(CancellationToken cancellationToken)
    {
        var status = await RunAsync(cancellationToken, "status", "--porcelain", "--untracked-files=all", "--ignore-submodules=all");
        if (status.ExitCode != 0) return null;
        return Math.Min(status.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length, MaximumChangedFiles);
    }

    private async Task<string[]> ReadRefsAsync(CancellationToken cancellationToken)
    {
        var refs = await RunAsync(cancellationToken, "for-each-ref", "--sort=-committerdate", "--format=%(refname)",
            LocalBranches, RemoteBranches);
        return refs.ExitCode == 0 ? refs.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>();
    }

    private static List<string> ReportableBranches(string[] refs)
    {
        var branches = new List<string>();
        var bytes = 0;
        foreach (var name in refs.Select(ShortName).Where(ProtocolValidator.IsBranchName).Distinct(StringComparer.Ordinal))
        {
            if (name == "HEAD") continue;
            bytes += JsonSerializer.SerializeToUtf8Bytes(name, ProtocolJson.Options).Length + 1;
            if (branches.Count == ProtocolConstants.MaximumReportedBranches || bytes > MaximumBranchListBytes) break;
            branches.Add(name);
        }
        return branches;
    }

    private static string ShortName(string name) => name.StartsWith(LocalBranches, StringComparison.Ordinal)
        ? name[LocalBranches.Length..] : name[RemoteBranches.Length..];

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return DeletedFile;
        await using var file = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken));
    }

    private async Task<string> GuardedAsync(string operation, Func<CancellationToken, Task<string>> work,
        CancellationToken cancellationToken)
    {
        using var deadline = Deadline(cancellationToken);
        try { return await work(deadline.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            log?.Warning("git.timed_out", new { operation });
            return GitResultCodes.TimedOut;
        }
        catch (Exception error) when (IsExpected(error, cancellationToken))
        {
            log?.Warning("git.failed", new { operation, error_type = error.GetType().Name });
            return GitResultCodes.Failed;
        }
    }

    private string Finish(string operation, GitResult result, string failure = GitResultCodes.Failed)
    {
        if (result.ExitCode == 0) return GitResultCodes.Ok;
        log?.Warning("git.failed", new { operation, exit_code = result.ExitCode, message = redactor.Redact(result.Error) });
        return failure;
    }

    private async Task<GitResult> RunAsync(CancellationToken cancellationToken, params string[] arguments)
    {
        using var process = Process.Start(StartInfo(root, arguments))
            ?? throw new InvalidOperationException("git did not start");
        using var registration = cancellationToken.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        });
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var error = process.StandardError.ReadToEndAsync(CancellationToken.None);
        await process.WaitForExitAsync(cancellationToken);
        return new(process.ExitCode, await output, await error);
    }

    private static ProcessStartInfo StartInfo(string root, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo("git")
        {
            UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "-C", root, "-c", "core.hooksPath=/dev/null" }.Concat(arguments))
            info.ArgumentList.Add(argument);
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        info.Environment.Remove("QUODERY_RUNTIME_KEY");
        return info;
    }

    private static CancellationTokenSource Deadline(CancellationToken cancellationToken)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(OperationTimeout);
        return deadline;
    }

    private static bool IsExpected(Exception error, CancellationToken cancellationToken) =>
        error is Win32Exception or IOException or InvalidOperationException ||
        (error is OperationCanceledException && !cancellationToken.IsCancellationRequested);

    private sealed record GitResult(int ExitCode, string Output, string Error);
}
