using System.Text.Json.Serialization;

namespace Quodery.Protocol;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TestingJob(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("starting_situation")] string? StartingSituation = null,
    [property: JsonPropertyName("action")] string? Action = null,
    [property: JsonPropertyName("expected_behaviour")] string? ExpectedBehaviour = null,
    [property: JsonPropertyName("focus")] string? Focus = null,
    [property: JsonPropertyName("source_session_id")] string? SourceSessionId = null,
    [property: JsonPropertyName("test_id")] string? TestId = null,
    [property: JsonPropertyName("test_ids")] IReadOnlyList<string>? TestIds = null,
    [property: JsonPropertyName("run_id")] string? RunId = null,
    [property: JsonPropertyName("gap_index")] int? GapIndex = null,
    [property: JsonPropertyName("gap_batch_id")] string? GapBatchId = null)
{
    public const string DiscoverTests = "discover_tests";
    public const string AnalyseTests = "analyse_tests";
    public const string RunTests = "run_tests";
    public const string AddTest = "add_test";
    public const string InvestigateFailure = "investigate_failure";

    public bool TryValidate(out string error)
    {
        error = "Choose a supported testing task and describe what should be tested.";
        if (Type is not (DiscoverTests or AnalyseTests or RunTests or AddTest or InvestigateFailure) || !Text(Target, 200)) return false;
        if (!OptionalText(StartingSituation, 1000) || !OptionalText(Action, 1000) ||
            !OptionalText(ExpectedBehaviour, 2000) || !OptionalText(Focus, 2000))
        { error = "Keep each description concise and use plain product language."; return false; }
        if (Type == AddTest)
        {
            if (!Text(ExpectedBehaviour, 2000)) { error = "Describe the behaviour to test."; return false; }
        }
        else if (Type != DiscoverTests && (ExpectedBehaviour is not null || StartingSituation is not null || Action is not null))
        { error = "Scenario details are supported only when adding a test."; return false; }
        if (Type is not (DiscoverTests or AnalyseTests or RunTests) && Focus is not null)
        { error = "Focus is supported only when running or reviewing existing tests."; return false; }
        if (Type == RunTests ? TestId is not null && !IsTestId(TestId) : TestId is not null)
        { error = "Choose a valid test from the testing environment."; return false; }
        if (TestIds is not null && (Type != RunTests || TestId is not null || TestIds.Count is < 1 or > ProtocolConstants.MaximumCatalogueTests ||
            TestIds.Any(id => !IsTestId(id)) || TestIds.Distinct(StringComparer.Ordinal).Count() != TestIds.Count))
        { error = "Select between 1 and 100 distinct tests from a discovery list."; return false; }
        if (Type == InvestigateFailure || TestIds is not null)
        {
            if (!IsSessionId(SourceSessionId)) { error = "A valid source run is required."; return false; }
        }
        else if (SourceSessionId is not null && (!IsSessionId(SourceSessionId) ||
            Type is not (DiscoverTests or AnalyseTests or AddTest or RunTests) ||
            Type is (AnalyseTests or AddTest or RunTests) && RunId is null))
        { error = "A valid source run is required."; return false; }
        if (RunId is not null && (!IsSessionId(RunId) || SourceSessionId is null ||
            Type is not (DiscoverTests or RunTests or AnalyseTests or AddTest or InvestigateFailure)))
        { error = "A valid parent run is required."; return false; }
        if ((GapIndex is not null && (Type != AddTest || RunId is null || SourceSessionId is null ||
            GapIndex < 0 || GapIndex >= ProtocolConstants.MaximumSafeFindings ||
            !Guid.TryParse(GapBatchId, out _))) || (GapIndex is null && GapBatchId is not null))
        { error = "Choose a valid gap from this run."; return false; }
        error = string.Empty;
        return true;
    }

    public string Description => Target;

    public static bool IsSessionId(string? value) => value is { Length: > 4 and <= 128 } &&
        value.StartsWith("ses_", StringComparison.Ordinal) && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
    public static bool IsTestId(string? value) => value is { Length: >= 20 and <= 80 } &&
        value.StartsWith("tst_", StringComparison.Ordinal) && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
    private static bool OptionalText(string? value, int maximum) => value is null || Text(value, maximum);
    private static bool Text(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum &&
        !value.Any(c => char.IsControl(c) && c is not '\n' and not '\t');
}
