using System.Text.Json;
using System.Text.Json.Serialization;

namespace Quodery.Protocol;

public static class ProtocolConstants
{
    public const string Version = "16";
    public const int MaximumQuestionLength = 240;
    public const int MaximumQuestionChoices = 6;
    public const int MaximumQuestionChoiceLength = 80;
    public const int MaximumAnswerLength = 2000;
    public const int MaximumSafeSummaryLength = 280;
    public const int MaximumTestDescriptionLength = 160;
    public const int MaximumSafeFindings = 100;
    public const int MaximumCatalogueTests = 100;
    public const int MaximumCatalogueTimeoutSeconds = 3600;
    public const int MaximumBranchNameLength = 100;
    public const int MaximumReportedBranches = 200;
    public const int MaximumCommitMessageLength = 200;
}

public static class RuntimeEventTypes
{
    public const string RuntimeConnected = "runtime.connected";
    public const string RuntimeHeartbeat = "runtime.heartbeat";
    public const string RuntimeGitStatus = "runtime.git_status";
    public const string SessionStarted = "session.started";
    public const string SessionProgress = "session.progress";
    public const string SessionQuestion = "session.question";
    public const string SessionCompleted = "session.completed";
    public const string SessionDiscovery = "session.discovery";
    public const string SessionTestResult = "session.test_result";
    public const string SessionTestState = "session.test_state";
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        RuntimeConnected, RuntimeHeartbeat, RuntimeGitStatus, SessionStarted,
        SessionProgress, SessionQuestion, SessionCompleted, SessionDiscovery, SessionTestResult, SessionTestState
    };
}

public static class ProgressStages
{
    public const string Preparing = "preparing";
    public const string UnderstandingRequest = "understanding_request";
    public const string FindingChecks = "finding_checks";
    public const string RunningChecks = "running_checks";
    public const string ReviewingResult = "reviewing_result";
    public const string FindingTestArea = "finding_test_area";
    public const string CreatingTest = "creating_test";
    public const string ReviewingFailure = "reviewing_failure";
    public const string ReproducingIssue = "reproducing_issue";
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Preparing, UnderstandingRequest, FindingChecks, RunningChecks, ReviewingResult,
        FindingTestArea, CreatingTest, ReviewingFailure, ReproducingIssue
    };
}

public static class PublicOutcomes
{
    public const string Passed = "passed";
    public const string Failed = "failed";
    public const string NeedsAttention = "needs_attention";
    public const string CouldNotRun = "could_not_run";
    public const string OutOfScope = "out_of_scope";
    public const string LocalContextUnavailable = "local_context_unavailable";
    public const string Stopped = "stopped";
    public const string Proposed = "proposed";
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    { Passed, Failed, NeedsAttention, CouldNotRun, OutOfScope, LocalContextUnavailable, Stopped, Proposed };
}

public static class ResultCodes
{
    public const string ChecksPassed = "checks_passed";
    public const string ChecksFailed = "checks_failed";
    public const string ChecksIncomplete = "checks_incomplete";
    public const string TestingEnvironmentLimited = "testing_environment_limited";
    public const string TestProposedPassing = "test_proposed_passing";
    public const string TestProposedFailing = "test_proposed_failing";
    public const string PotentialGapsFoundLocally = "potential_gaps_found_locally";
    public const string NoGapsFound = "no_gaps_found";
    public const string EnvironmentDisconnected = "environment_disconnected";
    public const string RequestCouldNotBeResolved = "request_could_not_be_resolved";
    public const string LocalContextUnavailable = "local_context_unavailable";
    public const string TestCreationNotConfigured = "test_creation_not_configured";
    public const string TestCreationNotSafe = "test_creation_not_safe";
    public const string TimedOut = "timed_out";
    public const string Stopped = "stopped";
    public const string OutOfScope = "out_of_scope";
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        ChecksPassed, ChecksFailed, ChecksIncomplete, TestingEnvironmentLimited,
        TestProposedPassing, TestProposedFailing, PotentialGapsFoundLocally, NoGapsFound, EnvironmentDisconnected,
        RequestCouldNotBeResolved, LocalContextUnavailable, TestCreationNotConfigured,
        TestCreationNotSafe, TimedOut, Stopped, OutOfScope
    };
}

public static class CloudCommandTypes
{
    public const string SessionStart = "session.start";
    public const string SessionCancel = "session.cancel";
    public const string SessionAnswerQuestion = "session.answer_question";
    public const string RuntimePing = "runtime.ping";
    public const string GitSwitchBranch = "git.switch_branch";
    public const string GitPull = "git.pull";
    public const string GitCommit = "git.commit";
    public const string GitDiscard = "git.discard";
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    { SessionStart, SessionCancel, SessionAnswerQuestion, RuntimePing, GitSwitchBranch, GitPull, GitCommit, GitDiscard };
}

public static class TestStates
{
    public const string Committed = "committed";
    public const string Discarded = "discarded";
}

public static class GitResultCodes
{
    public const string Ok = "ok";
    public const string Busy = "busy";
    public const string UncommittedChanges = "uncommitted_changes";
    public const string BranchNotFound = "branch_not_found";
    public const string NotFastForward = "not_fast_forward";
    public const string NoUpstream = "no_upstream";
    public const string NothingToCommit = "nothing_to_commit";
    public const string RemoteUnavailable = "remote_unavailable";
    public const string TimedOut = "timed_out";
    public const string Failed = "failed";
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Ok, Busy, UncommittedChanges, BranchNotFound, NotFastForward, NoUpstream,
        NothingToCommit, RemoteUnavailable, TimedOut, Failed
    };
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProtocolEnvelope
{
    [JsonPropertyName("protocol_version")] public required string ProtocolVersion { get; init; }
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("session_id")] public string? SessionId { get; init; }
    [JsonPropertyName("runtime_id")] public required string RuntimeId { get; init; }
    [JsonPropertyName("timestamp")] public required DateTimeOffset Timestamp { get; init; }
    [JsonPropertyName("type")] public required string Type { get; init; }
    [JsonPropertyName("payload")] public required JsonElement Payload { get; init; }
}

public static class ProtocolEnvelopeFactory
{
    public static ProtocolEnvelope CreateCommand(string runtimeId, string? sessionId, string type, object payload) =>
        Create("cmd_", runtimeId, sessionId, type, payload);
    public static ProtocolEnvelope CreateRuntimeEvent(string runtimeId, string? sessionId, string type, object payload) =>
        Create("evt_", runtimeId, sessionId, type, payload);
    private static ProtocolEnvelope Create(string prefix, string runtimeId, string? sessionId, string type, object payload) => new()
    {
        ProtocolVersion = ProtocolConstants.Version,
        Id = $"{prefix}{Guid.NewGuid():N}",
        SessionId = sessionId,
        RuntimeId = runtimeId,
        Timestamp = DateTimeOffset.UtcNow,
        Type = type,
        Payload = JsonSerializer.SerializeToElement(payload, ProtocolJson.Options)
    };
}

public static class ProtocolValidator
{
    public static bool TryValidateCloudCommand(ProtocolEnvelope envelope, string expectedRuntimeId, out string error)
    {
        error = "unknown or malformed cloud command";
        if (!ValidEnvelope(envelope, expectedRuntimeId, CloudCommandTypes.All, "cmd_")) return false;
        return envelope.Type switch
        {
            CloudCommandTypes.SessionStart => TryReadSessionStart(envelope, out _, out error),
            CloudCommandTypes.SessionCancel => ValidateEmptySessionCommand(envelope, out error),
            CloudCommandTypes.SessionAnswerQuestion => TryReadSessionAnswerQuestion(envelope, out _, out error),
            CloudCommandTypes.RuntimePing => ValidateEmptyPayload(envelope.Payload, out error),
            CloudCommandTypes.GitSwitchBranch => TryReadGitSwitchBranch(envelope, out _, out error),
            CloudCommandTypes.GitCommit => TryReadGitCommit(envelope, out _, out error),
            CloudCommandTypes.GitPull or CloudCommandTypes.GitDiscard =>
                envelope.SessionId is null && ValidateEmptyPayload(envelope.Payload, out error),
            _ => false
        };
    }

    public static bool TryReadSessionStart(ProtocolEnvelope envelope, out SessionStartCommand? command, out string error)
    {
        command = null; error = "malformed testing job";
        if (envelope.Type != CloudCommandTypes.SessionStart || !TestingJob.IsSessionId(envelope.SessionId) ||
            !HasOnly(envelope.Payload, "project_id", "job") ||
            !envelope.Payload.TryGetProperty("project_id", out var project) || project.ValueKind != JsonValueKind.String ||
            !IsOpaqueId(project.GetString(), "prj_", 128) || !envelope.Payload.TryGetProperty("job", out var jobValue)) return false;
        TestingJob? job;
        try { job = jobValue.Deserialize<TestingJob>(ProtocolJson.Options); }
        catch (JsonException) { return false; }
        if (job is null || !job.TryValidate(out error)) return false;
        command = new SessionStartCommand(envelope.SessionId!, project.GetString()!, job);
        error = string.Empty; return true;
    }

    public static bool TryReadSessionAnswerQuestion(ProtocolEnvelope envelope, out SessionAnswerQuestionCommand? command, out string error)
    {
        command = null; error = "invalid clarification answer command";
        if (envelope.Type != CloudCommandTypes.SessionAnswerQuestion || !TestingJob.IsSessionId(envelope.SessionId) ||
            !HasOnly(envelope.Payload, "question_id", "answer") ||
            !envelope.Payload.TryGetProperty("question_id", out var id) || id.ValueKind != JsonValueKind.String ||
            !envelope.Payload.TryGetProperty("answer", out var answer) || answer.ValueKind != JsonValueKind.String ||
            !IsQuestionId(id.GetString() ?? "") || !IsValidAnswer(answer.GetString())) return false;
        command = new(envelope.SessionId!, id.GetString()!, answer.GetString()!);
        error = string.Empty; return true;
    }

    public static bool TryReadGitSwitchBranch(ProtocolEnvelope envelope, out string? branch, out string error)
    {
        branch = null; error = "invalid branch command";
        if (envelope.Type != CloudCommandTypes.GitSwitchBranch || envelope.SessionId is not null ||
            !HasOnly(envelope.Payload, "branch") || !StringProperty(envelope.Payload, "branch", out var value) ||
            !IsBranchName(value)) return false;
        branch = value; error = string.Empty; return true;
    }

    public static bool TryReadGitCommit(ProtocolEnvelope envelope, out string? message, out string error)
    {
        message = null; error = "invalid commit command";
        if (envelope.Type != CloudCommandTypes.GitCommit || envelope.SessionId is not null ||
            !HasOnly(envelope.Payload, "message") || !StringProperty(envelope.Payload, "message", out var value) ||
            !IsCommitMessage(value)) return false;
        message = value; error = string.Empty; return true;
    }

    public static bool TryValidateRuntimeEvent(ProtocolEnvelope envelope, string expectedRuntimeId, out string error)
    {
        error = "unknown or malformed runtime event";
        if (!ValidEnvelope(envelope, expectedRuntimeId, RuntimeEventTypes.All, "evt_")) return false;
        var needsSession = envelope.Type is RuntimeEventTypes.SessionStarted or RuntimeEventTypes.SessionProgress or
            RuntimeEventTypes.SessionQuestion or RuntimeEventTypes.SessionCompleted or
            RuntimeEventTypes.SessionDiscovery or RuntimeEventTypes.SessionTestResult or RuntimeEventTypes.SessionTestState;
        if (needsSession != TestingJob.IsSessionId(envelope.SessionId)) return false;
        var valid = envelope.Type switch
        {
            RuntimeEventTypes.RuntimeConnected => RuntimeInfo.TryRead(envelope.Payload, out _),
            RuntimeEventTypes.RuntimeHeartbeat => ValidateEmptyPayload(envelope.Payload, out error),
            RuntimeEventTypes.RuntimeGitStatus => TryReadGitStatus(envelope.Payload, out _, out error),
            RuntimeEventTypes.SessionStarted or RuntimeEventTypes.SessionProgress => TryReadProgress(envelope.Payload, out _, out error),
            RuntimeEventTypes.SessionQuestion => TryReadSessionQuestion(envelope.Payload, out _, out error),
            RuntimeEventTypes.SessionCompleted => TryReadCompletion(envelope.Payload, out _, out error),
            RuntimeEventTypes.SessionDiscovery => TryReadDiscovery(envelope.Payload, out _, out error),
            RuntimeEventTypes.SessionTestResult => TryReadTestResult(envelope.Payload, out _, out error),
            RuntimeEventTypes.SessionTestState => TryReadTestState(envelope.Payload, out _, out error),
            _ => false
        };
        if (valid) error = string.Empty;
        return valid;
    }

    public static bool TryReadGitStatus(JsonElement payload, out GitStatus? status, out string error)
    {
        status = null; error = "invalid git status";
        if (!HasOnly(payload, "branch", "commit", "changed_files", "branches", "command_id", "result_code", "proposed_tests") ||
            !OptionalStringProperty(payload, "branch", out var branch) || (branch is not null && !IsBranchName(branch)) ||
            !OptionalStringProperty(payload, "commit", out var commit) || (commit is not null && !IsCodeVersion(commit)) ||
            !NonNegativeInteger(payload, "changed_files", out var changedFiles) ||
            !NonNegativeInteger(payload, "proposed_tests", out var proposedTests) ||
            !OptionalStringProperty(payload, "command_id", out var commandId) ||
            (commandId is not null && !IsOpaqueId(commandId, "cmd_", 80)) ||
            !OptionalStringProperty(payload, "result_code", out var resultCode) ||
            (resultCode is not null && !GitResultCodes.All.Contains(resultCode)) ||
            (commandId is null) != (resultCode is null) ||
            !payload.TryGetProperty("branches", out var branchesValue) || branchesValue.ValueKind != JsonValueKind.Array ||
            branchesValue.GetArrayLength() > ProtocolConstants.MaximumReportedBranches) return false;
        var branches = new List<string>();
        foreach (var value in branchesValue.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String || !IsBranchName(value.GetString())) return false;
            branches.Add(value.GetString()!);
        }
        status = new(branch, commit, changedFiles, branches, commandId, resultCode, proposedTests); error = string.Empty; return true;
    }

    public static bool TryReadProgress(JsonElement payload, out SessionProgress? progress, out string error)
    {
        progress = null; error = "invalid progress stage";
        if (!HasOnly(payload, "stage") || !payload.TryGetProperty("stage", out var stage) ||
            stage.ValueKind != JsonValueKind.String || !ProgressStages.All.Contains(stage.GetString() ?? "")) return false;
        progress = new(stage.GetString()!); error = string.Empty; return true;
    }

    public static bool TryReadSessionQuestion(JsonElement payload, out AgentQuestion? question, out string error)
    {
        question = null; error = "invalid clarification question";
        if (!HasOnly(payload, "question_id", "prompt", "choices") ||
            !payload.TryGetProperty("question_id", out var id) || id.ValueKind != JsonValueKind.String || !IsQuestionId(id.GetString() ?? "") ||
            !payload.TryGetProperty("prompt", out var prompt) || prompt.ValueKind != JsonValueKind.String ||
            !IsValidPublicText(prompt.GetString(), ProtocolConstants.MaximumQuestionLength) ||
            !payload.TryGetProperty("choices", out var choicesValue) || choicesValue.ValueKind != JsonValueKind.Array) return false;
        var choices = new List<string>();
        foreach (var value in choicesValue.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String || !IsValidPublicText(value.GetString(), ProtocolConstants.MaximumQuestionChoiceLength) ||
                choices.Count >= ProtocolConstants.MaximumQuestionChoices) return false;
            choices.Add(value.GetString()!);
        }
        if (choices.Distinct(StringComparer.Ordinal).Count() != choices.Count) return false;
        question = new(id.GetString()!, prompt.GetString()!, choices); error = string.Empty; return true;
    }

    public static bool TryReadCompletion(JsonElement payload, out PublicCompletion? completion, out string error)
    {
        completion = null; error = "invalid public completion";
        if (!HasFields(payload, ["outcome", "tested", "total", "passed", "failed", "errors", "result_code",
                "safe_summary", "safe_findings", "local_evidence_available"],
                ["test_description", "code_version", "code_modified"]) ||
            !StringProperty(payload, "outcome", out var outcome) || !PublicOutcomes.All.Contains(outcome) ||
            !BooleanProperty(payload, "tested", out var tested) ||
            !NonNegativeInteger(payload, "total", out var total) || !NonNegativeInteger(payload, "passed", out var passed) ||
            !NonNegativeInteger(payload, "failed", out var failed) || !NonNegativeInteger(payload, "errors", out var errors) ||
            total != passed + failed + errors || (!tested && total != 0) ||
            !StringProperty(payload, "result_code", out var resultCode) || !ResultCodes.All.Contains(resultCode) ||
            !BooleanProperty(payload, "local_evidence_available", out var localEvidence) ||
            !payload.TryGetProperty("safe_summary", out var summaryValue) ||
            (summaryValue.ValueKind != JsonValueKind.Null && (summaryValue.ValueKind != JsonValueKind.String ||
                !IsValidPublicText(summaryValue.GetString(), ProtocolConstants.MaximumSafeSummaryLength))) ||
            (payload.TryGetProperty("test_description", out var testDescriptionValue) &&
                testDescriptionValue.ValueKind != JsonValueKind.Null &&
                (testDescriptionValue.ValueKind != JsonValueKind.String ||
                    !IsValidPublicText(testDescriptionValue.GetString(), ProtocolConstants.MaximumTestDescriptionLength))) ||
            !payload.TryGetProperty("safe_findings", out var findingsValue) || findingsValue.ValueKind != JsonValueKind.Array ||
            (payload.TryGetProperty("code_version", out var versionValue) && versionValue.ValueKind != JsonValueKind.Null &&
                (versionValue.ValueKind != JsonValueKind.String || !IsCodeVersion(versionValue.GetString()))) ||
            (payload.TryGetProperty("code_modified", out var modifiedValue) &&
                modifiedValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))) return false;
        var findings = new List<string>();
        foreach (var value in findingsValue.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String || !IsValidPublicText(value.GetString(), ProtocolConstants.MaximumSafeSummaryLength) ||
                findings.Count >= ProtocolConstants.MaximumSafeFindings) return false;
            findings.Add(value.GetString()!);
        }
        completion = new(outcome, tested, total, passed, failed, errors, resultCode,
            summaryValue.ValueKind == JsonValueKind.String ? summaryValue.GetString() : null, findings, localEvidence,
            payload.TryGetProperty("test_description", out var descriptionValue) && descriptionValue.ValueKind == JsonValueKind.String
                ? descriptionValue.GetString() : null,
            versionValue.ValueKind == JsonValueKind.String ? versionValue.GetString() : null,
            modifiedValue.ValueKind == JsonValueKind.True);
        error = string.Empty; return true;
    }

    public static bool TryReadDiscovery(JsonElement payload, out DiscoveryResult? result, out string error)
    {
        result = null; error = "invalid discovery result";
        if (!HasOnly(payload, "status", "tests") ||
            !StringProperty(payload, "status", out var status) ||
            status is not ("discovering" or "ready" or "partial" or "cancelled" or "timed_out" or "unavailable") ||
            !payload.TryGetProperty("tests", out var testsValue) || testsValue.ValueKind != JsonValueKind.Array) return false;
        var tests = new List<PublicTestDescriptor>();
        foreach (var value in testsValue.EnumerateArray())
        {
            if (tests.Count >= ProtocolConstants.MaximumCatalogueTests ||
                !HasOnly(value, "id", "description") ||
                !StringProperty(value, "id", out var id) || !TestingJob.IsTestId(id) ||
                !StringProperty(value, "description", out var description) || !IsValidPublicText(description, ProtocolConstants.MaximumSafeSummaryLength)) return false;
            tests.Add(new(id, description));
        }
        if (tests.Select(test => test.Id).Distinct(StringComparer.Ordinal).Count() != tests.Count) return false;
        result = new(status, tests); error = string.Empty; return true;
    }

    public static bool TryReadTestResult(JsonElement payload, out SelectedTestResult? result, out string error)
    {
        result = null; error = "invalid selected test result";
        if (!HasOnly(payload, "test_id", "outcome") || !StringProperty(payload, "test_id", out var id) ||
            !TestingJob.IsTestId(id) || !StringProperty(payload, "outcome", out var outcome) ||
            outcome is not ("passed" or "failed" or "could_not_run")) return false;
        result = new(id, outcome); error = string.Empty; return true;
    }

    public static bool TryReadTestState(JsonElement payload, out TestStateUpdate? update, out string error)
    {
        update = null; error = "invalid test state";
        if (!HasOnly(payload, "state", "commit") || !StringProperty(payload, "state", out var state) ||
            state is not (TestStates.Committed or TestStates.Discarded) ||
            !OptionalStringProperty(payload, "commit", out var commit) ||
            (commit is not null && (state == TestStates.Discarded || !IsCodeVersion(commit)))) return false;
        update = new(state, commit); error = string.Empty; return true;
    }

    public static bool IsQuestionId(string value) => IsOpaqueId(value, "qst_", 36);
    public static bool IsCodeVersion(string? value) => value is { Length: >= 7 and <= 40 } &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    public static bool IsBranchName(string? value) =>
        value is { Length: > 0 and <= ProtocolConstants.MaximumBranchNameLength } && value[0] != '-' &&
        !value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c));
    public static bool IsCommitMessage(string? value) =>
        IsValidPublicText(value, ProtocolConstants.MaximumCommitMessageLength);
    public static bool IsPublicText(string? value, int maximum) => IsValidPublicText(value, maximum);
    public static bool IsValidAnswer(string? value) => IsValidUserText(value, ProtocolConstants.MaximumAnswerLength);
    private static bool ValidEnvelope(ProtocolEnvelope envelope, string runtimeId, IReadOnlySet<string> types, string idPrefix) =>
        envelope.ProtocolVersion == ProtocolConstants.Version && envelope.RuntimeId == runtimeId &&
        IsOpaqueId(envelope.Id, idPrefix, 80) && types.Contains(envelope.Type);
    private static bool ValidateEmptySessionCommand(ProtocolEnvelope envelope, out string error)
    {
        if (!TestingJob.IsSessionId(envelope.SessionId))
        {
            error = "invalid session command";
            return false;
        }
        return ValidateEmptyPayload(envelope.Payload, out error);
    }
    private static bool ValidateEmptyPayload(JsonElement payload, out string error)
    { error = "payload must be empty"; if (payload.ValueKind != JsonValueKind.Object || payload.EnumerateObject().Any()) return false; error = ""; return true; }
    private static bool HasFields(JsonElement value, string[] required, string[] optional) =>
        value.ValueKind == JsonValueKind.Object && required.All(name => value.TryGetProperty(name, out _)) &&
        value.EnumerateObject().All(p => required.Contains(p.Name, StringComparer.Ordinal) || optional.Contains(p.Name, StringComparer.Ordinal));
    private static bool HasOnly(JsonElement value, params string[] names) => value.ValueKind == JsonValueKind.Object &&
        value.EnumerateObject().Count() == names.Length && value.EnumerateObject().All(p => names.Contains(p.Name, StringComparer.Ordinal));
    private static bool StringProperty(JsonElement payload, string name, out string value)
    { value = ""; if (!payload.TryGetProperty(name, out var item) || item.ValueKind != JsonValueKind.String) return false; value = item.GetString() ?? ""; return true; }
    private static bool OptionalStringProperty(JsonElement payload, string name, out string? value)
    {
        value = null;
        if (!payload.TryGetProperty(name, out var item)) return false;
        if (item.ValueKind == JsonValueKind.Null) return true;
        if (item.ValueKind != JsonValueKind.String) return false;
        value = item.GetString(); return true;
    }
    private static bool BooleanProperty(JsonElement payload, string name, out bool value)
    { value = false; if (!payload.TryGetProperty(name, out var item) || item.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false; value = item.GetBoolean(); return true; }
    private static bool NonNegativeInteger(JsonElement payload, string name, out int value)
    { value = 0; return payload.TryGetProperty(name, out var item) && item.TryGetInt32(out value) && value is >= 0 and <= 100000; }
    private static bool IsOpaqueId(string? value, string prefix, int maximum) => value is { Length: > 4 } && value.Length <= maximum &&
        value.StartsWith(prefix, StringComparison.Ordinal) && value.Skip(prefix.Length).All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
    private static bool IsValidPublicText(string? value, int max) => IsValidUserText(value, max) &&
        !value!.Contains('\n') && !value.Contains('\r');
    private static bool IsValidUserText(string? value, int max) => value is { Length: > 0 } && value.Length <= max &&
        !string.IsNullOrWhiteSpace(value) && !value.Any(c => char.IsControl(c) && c is not '\n' and not '\t');
}

public sealed record SessionStartCommand(string SessionId, string ProjectId, TestingJob Job);
public sealed record SessionAnswerQuestionCommand(string SessionId, string QuestionId, string Answer);
public sealed record SessionProgress([property: JsonPropertyName("stage")] string Stage);
public sealed record AgentQuestion(string QuestionId, string Prompt, IReadOnlyList<string> Choices);
public sealed record PublicCompletion(string Outcome, bool Tested, int Total, int Passed, int Failed, int Errors,
    string ResultCode, string? SafeSummary, IReadOnlyList<string> SafeFindings, bool LocalEvidenceAvailable,
    string? TestDescription = null, string? CodeVersion = null, bool CodeModified = false);
public sealed record PublicTestDescriptor(string Id, string Description);
public sealed record DiscoveryResult(string Status, IReadOnlyList<PublicTestDescriptor> Tests);
public sealed record SelectedTestResult(string TestId, string Outcome);
public sealed record TestStateUpdate(string State, string? Commit);
public sealed record GitStatus(
    [property: JsonPropertyName("branch")] string? Branch,
    [property: JsonPropertyName("commit")] string? Commit,
    [property: JsonPropertyName("changed_files")] int ChangedFiles,
    [property: JsonPropertyName("branches")] IReadOnlyList<string> Branches,
    [property: JsonPropertyName("command_id")] string? CommandId = null,
    [property: JsonPropertyName("result_code")] string? ResultCode = null,
    [property: JsonPropertyName("proposed_tests")] int ProposedTests = 0);

public static class ProtocolJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
}

public sealed record Project(string Id, string Name, string RuntimeId);
public sealed record RuntimeSummary(string Id, string Status, DateTimeOffset? LastSeenAt, bool IsConfigured);
public sealed record ProjectWithRuntime(string Id, string Name, string RuntimeId, RuntimeSummary Runtime);
public sealed record InvestigationSession(string Id, string ProjectId, string RuntimeId, string Goal, string Status,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, TestingJob? Job = null);

public static class SessionStatuses
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string AwaitingInput = "awaiting_input";
    public const string Completed = "completed";
    public const string Cancelled = "cancelled";
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    { Queued, Running, AwaitingInput, Completed, Cancelled };
}
