using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Quodery.Protocol;

namespace Quodery.Runtime.Agent;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record TestCatalogueReport(List<DiscoveredTest> Tests, bool Partial)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true };

    public static IReadOnlyList<(PublicTestDescriptor Public, string Selector)> Parse(string json, byte[] opaqueIdKey)
    {
        var report = JsonSerializer.Deserialize<TestCatalogueReport>(json, Options);
        if (report?.Tests is null || report.Tests.Count > ProtocolConstants.MaximumCatalogueTests)
            throw Invalid();
        var result = new List<(PublicTestDescriptor, string)>();
        foreach (var test in report.Tests)
        {
            if (!Text(test.Selector, 500) || !PublicText(test.Description, ProtocolConstants.MaximumSafeSummaryLength)) throw Invalid();
            var digest = Convert.ToHexString(HMACSHA256.HashData(opaqueIdKey, Encoding.UTF8.GetBytes(test.Selector)))[..32].ToLowerInvariant();
            result.Add((new($"tst_{digest}", test.Description.Trim()), test.Selector));
        }
        if (result.Select(item => item.Item1.Id).Distinct(StringComparer.Ordinal).Count() != result.Count)
            throw Invalid();
        return result;
    }

    private static bool Text(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximum && !value.Any(char.IsControl);
    private static bool PublicText(string? value, int maximum) => Text(value, maximum);
    private static AgentException Invalid() => new("agent_invalid_response", "Agent returned an invalid test catalogue");
}

internal sealed record DiscoveredTest(string Selector, string Description);
