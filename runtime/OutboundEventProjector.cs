using System.Text.Json;
using System.Text.Json.Nodes;
using Quodery.Protocol;
using Quodery.Runtime.Agent;

namespace Quodery.Runtime;

internal static class OutboundEventProjector
{
    public static ProtocolEnvelope? Project(string runtimeId, AgentSession session, AgentEvent localEvent)
    {
        object payload;
        string publicType;
        switch (localEvent.Type)
        {
            case "session.started":
                publicType = RuntimeEventTypes.SessionStarted;
                payload = new { stage = InitialStage(session.Job.Type) };
                break;
            case "agent.progress":
                var stage = String(localEvent.Payload, "stage");
                if (stage is null || !ProgressStages.All.Contains(stage)) return null;
                publicType = RuntimeEventTypes.SessionProgress;
                payload = new { stage };
                break;
            case "agent.question":
                var questionId = String(localEvent.Payload, "question_id");
                if (questionId is null || !ProtocolValidator.IsQuestionId(questionId)) return null;
                publicType = RuntimeEventTypes.SessionQuestion;
                payload = Question(questionId, localEvent.Payload);
                break;
            case "agent.discovery":
                publicType = RuntimeEventTypes.SessionDiscovery;
                payload = localEvent.Payload;
                break;
            case "agent.test_result":
                publicType = RuntimeEventTypes.SessionTestResult;
                payload = localEvent.Payload;
                break;
            case "session.completed":
                publicType = RuntimeEventTypes.SessionCompleted;
                payload = Completion(session.Job, localEvent.Payload);
                break;
            case "session.failed":
                publicType = RuntimeEventTypes.SessionCompleted;
                payload = Failure(String(localEvent.Payload, "error_code"));
                break;
            default:
                return null;
        }
        var envelope = ProtocolEnvelopeFactory.CreateRuntimeEvent(runtimeId, session.Id, publicType, payload);
        if (!ProtocolValidator.TryValidateRuntimeEvent(envelope, runtimeId, out _)) return null;
        return envelope;
    }

    private static object Completion(TestingJob job, JsonObject payload)
    {
        var reportStatus = String(payload, "status") ?? "completed";
        var tested = Boolean(payload, "tested");
        var passed = Integer(payload, "passed");
        var failed = Integer(payload, "failed");
        var errors = Integer(payload, "errors");
        var limitations = Strings(payload, "limitations")
            .Where(TestingLimitations.All.Contains)
            .ToHashSet(StringComparer.Ordinal);
        var total = tested ? passed + failed + errors : 0;
        var evidence = Boolean(payload, "local_evidence_available");
        var hadPriorAttempts = Boolean(payload, "had_prior_attempts");
        var gapCount = job.Type == TestingJob.AnalyseTests && payload["findings"] is JsonArray gapItems
            ? gapItems.Count : 0;
        string outcome;
        string code;
        if (reportStatus == "out_of_scope")
        {
            outcome = PublicOutcomes.OutOfScope;
            code = ResultCodes.OutOfScope;
        }
        else if (job.Type == TestingJob.AnalyseTests)
        {
            if (limitations.Count > 0)
            {
                outcome = PublicOutcomes.CouldNotRun;
                code = ResultCodes.TestingEnvironmentLimited;
            }
            else
            {
                outcome = gapCount > 0 ? PublicOutcomes.NeedsAttention : PublicOutcomes.Passed;
                code = gapCount > 0 ? ResultCodes.PotentialGapsFoundLocally : ResultCodes.NoGapsFound;
            }
        }
        else if (job.Type == TestingJob.AddTest)
        {
            if (Boolean(payload, "proposed"))
            {
                outcome = PublicOutcomes.Proposed;
                code = failed > 0 ? ResultCodes.TestProposedFailing : ResultCodes.TestProposedPassing;
            }
            else
            {
                outcome = PublicOutcomes.CouldNotRun;
                code = limitations.Count > 0 ? ResultCodes.TestingEnvironmentLimited : ResultCodes.RequestCouldNotBeResolved;
            }
        }
        else if (failed > 0)
        {
            outcome = PublicOutcomes.Failed;
            code = ResultCodes.ChecksFailed;
        }
        else if (errors > 0)
        {
            outcome = PublicOutcomes.CouldNotRun;
            code = passed > 0 ? ResultCodes.ChecksIncomplete : ResultCodes.TestingEnvironmentLimited;
        }
        else if (!tested)
        {
            outcome = PublicOutcomes.CouldNotRun;
            code = limitations.Count > 0
                ? ResultCodes.TestingEnvironmentLimited
                : ResultCodes.RequestCouldNotBeResolved;
        }
        else
        {
            outcome = PublicOutcomes.Passed;
            code = ResultCodes.ChecksPassed;
        }
        var safeFindings = job.Type == TestingJob.AnalyseTests && code == ResultCodes.PotentialGapsFoundLocally
            ? SafeGapFindings(payload) : Array.Empty<string>();
        var testDescription = job.Type == TestingJob.AddTest
            ? PublicBusinessSummary(String(payload, "public_test_description"), ProtocolConstants.MaximumTestDescriptionLength) : null;
        var safeSummary = code == ResultCodes.PotentialGapsFoundLocally
            ? $"{gapCount} potential testing gap{(gapCount == 1 ? " was" : "s were")} found."
            : outcome == PublicOutcomes.Proposed
                ? $"The new test {(failed > 0 ? "failed" : "passed")} when it ran and is waiting for review."
            : hadPriorAttempts && tested && errors == 0 && passed + failed == 1
                ? failed > 0 ? "The final test execution failed after a retry." :
                    "The final test execution passed after a retry."
                : SafeSummary(tested, passed, failed, errors, limitations);
        var codeVersion = String(payload, "code_version") is { } commit && ProtocolValidator.IsCodeVersion(commit) ? commit : null;
        return PublicPayload(outcome, tested, total, tested ? passed : 0, tested ? failed : 0,
            tested ? errors : 0, code, evidence, safeSummary, safeFindings, testDescription,
            codeVersion, codeVersion is not null && Boolean(payload, "code_modified"));
    }

    private static object Question(string questionId, JsonObject payload)
    {
        var prompt = String(payload, "prompt")?.Trim();
        var choices = Strings(payload, "choices").Select(choice => choice.Trim()).ToArray();
        if (!ProtocolValidator.IsPublicText(prompt, ProtocolConstants.MaximumQuestionLength) ||
            choices.Length > ProtocolConstants.MaximumQuestionChoices ||
            choices.Any(choice => !ProtocolValidator.IsPublicText(choice, ProtocolConstants.MaximumQuestionChoiceLength)) ||
            choices.Distinct(StringComparer.Ordinal).Count() != choices.Length)
            return new
            {
                question_id = questionId,
                prompt = "What additional product behaviour should this check take into account?",
                choices = Array.Empty<string>()
            };
        return new { question_id = questionId, prompt, choices };
    }

    private static object Failure(string? localCode)
    {
        var (outcome, code) = localCode switch
        {
            "agent_stopped" => (PublicOutcomes.Stopped, ResultCodes.Stopped),
            "agent_timed_out" => (PublicOutcomes.CouldNotRun, ResultCodes.TimedOut),
            "local_context_unavailable" => (PublicOutcomes.LocalContextUnavailable, ResultCodes.LocalContextUnavailable),
            "test_creation_not_configured" => (PublicOutcomes.CouldNotRun, ResultCodes.TestCreationNotConfigured),
            "test_creation_not_safe" => (PublicOutcomes.CouldNotRun, ResultCodes.TestCreationNotSafe),
            _ => (PublicOutcomes.CouldNotRun, ResultCodes.RequestCouldNotBeResolved)
        };
        return PublicPayload(outcome, false, 0, 0, 0, 0, code, false, null);
    }

    private static object PublicPayload(string outcome, bool tested, int total, int passed, int failed, int errors,
        string resultCode, bool localEvidenceAvailable, string? safeSummary, IReadOnlyList<string>? safeFindings = null,
        string? testDescription = null, string? codeVersion = null, bool codeModified = false) => new
    {
        outcome, tested, total, passed, failed, errors,
        result_code = resultCode,
        safe_summary = safeSummary,
        test_description = testDescription,
        safe_findings = safeFindings ?? Array.Empty<string>(),
        local_evidence_available = localEvidenceAvailable,
        code_version = codeVersion,
        code_modified = codeModified
    };

    private static string? SafeSummary(bool tested, int passed, int failed, int errors, IReadOnlySet<string> limitations)
    {
        string? result = null;
        if (!tested && errors > 0)
            result = $"{Checks(errors, "could not start")}.";
        else if (failed > 0 && errors > 0)
            result = $"{Checks(failed, "failed")} and {Checks(errors, "could not complete")}.";
        else if (errors > 0 && passed > 0)
            result = $"{Checks(passed, "passed")}, but {Checks(errors, "could not complete")}, so the result is inconclusive.";
        else if (errors > 0)
            result = $"{Checks(errors, "could not complete")}.";
        else if (!tested && limitations.Count > 0)
            result = "The check could not start.";

        var reasons = LimitationSentences
            .Where(item => limitations.Contains(item.Code))
            .Take(2)
            .Select(item => item.Sentence);
        var reasonText = string.Join(" ", reasons);
        return string.IsNullOrEmpty(reasonText) ? result : string.IsNullOrEmpty(result) ? reasonText : $"{result} {reasonText}";
    }

    private static string Checks(int count, string result) =>
        $"{count} check{(count == 1 ? "" : "s")} {result}";

    private static string[] SafeGapFindings(JsonObject payload) =>
        payload["findings"] is not JsonArray findings ? [] : findings
            .OfType<JsonObject>()
            .Select(item => PublicBusinessSummary(String(item, "public_summary")) ??
                (String(item, "category") is { } category && GapCategorySummaries.TryGetValue(category, out var summary)
                    ? summary : GapCategorySummaries["other"]))
            .Take(ProtocolConstants.MaximumSafeFindings)
            .ToArray();

    private static string? PublicBusinessSummary(string? value, int maximum = ProtocolConstants.MaximumSafeSummaryLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum ||
            value.Any(char.IsControl))
            return null;
        return value.Trim();
    }

    private static readonly (string Code, string Sentence)[] LimitationSentences =
    [
        (TestingLimitations.RequiredToolUnavailable, "Required testing tools were unavailable."),
        (TestingLimitations.ServiceUnavailable, "A required testing service was unavailable."),
        (TestingLimitations.OperationNotPermitted, "The testing environment did not permit a required operation."),
        (TestingLimitations.WorkspaceNotWritable, "The testing environment could not write required local files."),
        (TestingLimitations.ConfigurationInvalid, "The testing environment configuration was incomplete or invalid."),
        (TestingLimitations.TimedOut, "A testing operation took too long."),
        (TestingLimitations.OtherEnvironmentLimitation, "Another testing environment limitation prevented completion.")
    ];

    private static readonly IReadOnlyDictionary<string, string> GapCategorySummaries =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["missing_coverage"] = "An expected behaviour may have no automated test.",
            ["edge_cases"] = "Boundary or unusual cases may need more test coverage.",
            ["error_handling"] = "Failure and recovery behaviour may need more test coverage.",
            ["integration"] = "Interactions between components may need more test coverage.",
            ["accessibility"] = "Accessibility behaviour may need more test coverage.",
            ["security"] = "Security behaviour may need more test coverage.",
            ["performance"] = "Performance behaviour may need more test coverage.",
            ["other"] = "A potential gap needs review in the connected runtime."
        };

    private static string InitialStage(string jobType) => jobType switch
    {
        TestingJob.AddTest => ProgressStages.UnderstandingRequest,
        TestingJob.InvestigateFailure => ProgressStages.ReviewingFailure,
        _ => ProgressStages.Preparing
    };
    private static string? String(JsonObject payload, string name) =>
        payload[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    private static bool Boolean(JsonObject payload, string name) =>
        payload[name] is JsonValue value && value.TryGetValue<bool>(out var result) && result;
    private static int Integer(JsonObject payload, string name) =>
        payload[name] is JsonValue node && node.TryGetValue<int>(out var value) && value >= 0 ? value : 0;
    private static IEnumerable<string> Strings(JsonObject payload, string name) =>
        payload[name] is JsonArray values
            ? values.Select(value => value is JsonValue item && item.TryGetValue<string>(out var text) ? text : null)
                .OfType<string>()
            : [];
}
