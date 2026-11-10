using System.Text.Json;
using System.Text.Json.Serialization;
using Quodery.Protocol;

namespace Quodery.Runtime.Agent;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record TestReport(string Status, string Summary, bool Tested,
    List<string> Observations, List<TestExecution> Executions, List<string> Limitations,
    List<TestFinding> Findings, TestQuestion? Question,
    [property: JsonPropertyName("public_test_description")] string? PublicTestDescription = null)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true };

    public static TestReport Parse(string json)
    {
        var value = JsonSerializer.Deserialize<TestReport>(json, Options);
        if (value is null || value.Status is not ("completed" or "needs_input" or "out_of_scope") ||
            !Text(value.Summary, 8192) ||
            (value.PublicTestDescription is not null && !PublicText(value.PublicTestDescription, 160)) ||
            value.Observations is null || value.Observations.Count > 20 ||
            value.Observations.Any(x => !Text(x, 2048)) || value.Executions is null || value.Executions.Count > ProtocolConstants.MaximumCatalogueTests ||
            value.Executions.Any(x => x is null || !Text(x.Test, 200) || x.Outcome is not ("passed" or "failed" or "error") ||
                x.TestId is not null && !TestingJob.IsTestId(x.TestId) || x.Artefacts is null || x.Artefacts.Count > 10 || x.Artefacts.Any(p => !ArtefactPath(p))) ||
            value.Limitations is null || value.Limitations.Count > 5 ||
            value.Limitations.Any(x => !TestingLimitations.All.Contains(x)) ||
            value.Limitations.Distinct(StringComparer.Ordinal).Count() != value.Limitations.Count ||
            (value.Status != "completed" && value.Limitations.Count != 0) ||
            (value.Tested && value.Executions.All(x => x.Outcome != "error") && value.Limitations.Count != 0) ||
            (value.Tested && value.Executions.Count == 0) ||
            (!value.Tested && value.Executions.Any(x => x.Outcome is "passed" or "failed")) ||
            (value.Status == "needs_input") != (value.Question is not null) ||
            value.Findings is null || value.Findings.Count > ProtocolConstants.MaximumSafeFindings ||
            (value.Status != "completed" && (value.Tested || value.Executions.Count > 0 || value.Findings.Count > 0)))
            throw Invalid();
        if (value.Question is { } question &&
            (!Text(question.Prompt, ProtocolConstants.MaximumQuestionLength) || question.Choices is null ||
             question.Choices.Count > ProtocolConstants.MaximumQuestionChoices ||
             question.Choices.Any(x => !Text(x, ProtocolConstants.MaximumQuestionChoiceLength)) ||
             question.Choices.Distinct(StringComparer.Ordinal).Count() != question.Choices.Count))
            throw Invalid();
        if (value.Findings.Any(finding => finding is null || !Text(finding.Summary, 8192) ||
            (finding.Title is not null && !Text(finding.Title, 200)) ||
            !Text(finding.PublicSummary, ProtocolConstants.MaximumSafeSummaryLength) ||
            !GapCategories.All.Contains(finding.Category) ||
            finding.Confidence is not ("low" or "medium" or "high") ||
            finding.Severity is not ("info" or "low" or "medium" or "high" or "critical") ||
            (value.Tested ? finding.Reproducibility is not ("consistent" or "intermittent") : finding.Reproducibility != "not_executed")))
            throw Invalid();
        if (value.Findings.Select(finding => finding.PublicSummary.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != value.Findings.Count)
            throw Invalid();
        return value;
    }

    private static bool Text(string? text, int max) => !string.IsNullOrWhiteSpace(text) && text.Length <= max &&
        !text.Any(c => char.IsControl(c) && c is not '\n' and not '\t');
    private static bool PublicText(string? text, int max) => Text(text, max) && !text!.Contains('\n');
    private static bool ArtefactPath(string? path) => Text(path, 512) && !Path.IsPathRooted(path!) &&
        !path!.Contains(':') && !path.Replace('\\', '/').Split('/').Any(p => p is ".." or "");
    private static AgentException Invalid() => new("agent_invalid_response", "Agent returned an invalid testing report");
}

internal sealed record TestExecution(string Test, string Outcome, [property: JsonPropertyName("exit_code")] int? ExitCode, List<string> Artefacts, [property: JsonPropertyName("test_id")] string? TestId = null);
internal sealed record TestFinding(string Summary,
    [property: JsonPropertyName("public_summary")] string PublicSummary, string Category,
    string Confidence, string Reproducibility, string Severity,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Title = null);
internal sealed record TestQuestion(string Prompt, List<string> Choices);

internal static class GapCategories
{
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        "missing_coverage", "edge_cases", "error_handling", "integration", "accessibility",
        "security", "performance", "other"
    };
}

internal static class TestingLimitations
{
    public const string RequiredToolUnavailable = "required_tool_unavailable";
    public const string ServiceUnavailable = "service_unavailable";
    public const string OperationNotPermitted = "operation_not_permitted";
    public const string WorkspaceNotWritable = "workspace_not_writable";
    public const string ConfigurationInvalid = "configuration_invalid";
    public const string TimedOut = "timed_out";
    public const string OtherEnvironmentLimitation = "other_environment_limitation";
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        RequiredToolUnavailable, ServiceUnavailable, OperationNotPermitted, WorkspaceNotWritable,
        ConfigurationInvalid, TimedOut, OtherEnvironmentLimitation
    };
}
