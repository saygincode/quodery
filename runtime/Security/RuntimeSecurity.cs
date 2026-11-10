using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Quodery.Runtime.Security;

internal sealed class SecretRedactor
{
    private static readonly Regex CredentialPattern = new(
        "(?i)(bearer\\s+|(?:api[_-]?key|token|password|secret)\\s*[:=]\\s*)[^\\s,;\\\"']+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));
    private readonly string[] secrets;

    public SecretRedactor(IEnumerable<string>? explicitSecrets = null)
    {
        secrets = (explicitSecrets ?? DiscoverEnvironmentSecrets())
            .Where(value => !string.IsNullOrWhiteSpace(value) && value.Length >= 4)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(value => value.Length)
            .ToArray();
    }

    public string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value ?? string.Empty;
        var result = value;
        foreach (var secret in secrets) result = result.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        return CredentialPattern.Replace(result, match => $"{match.Groups[1].Value}[REDACTED]");
    }

    public JsonObject Redact(JsonObject payload) => (JsonObject)RedactNode(payload)!;

    private JsonNode? RedactNode(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var text)) return JsonValue.Create(Redact(text));
        if (node is JsonObject objectNode)
        {
            var copy = new JsonObject();
            foreach (var item in objectNode) copy[item.Key] = RedactNode(item.Value);
            return copy;
        }
        if (node is JsonArray arrayNode) return new JsonArray(arrayNode.Select(RedactNode).ToArray());
        return node?.DeepClone();
    }

    private static IEnumerable<string> DiscoverEnvironmentSecrets()
    {
        foreach (System.Collections.DictionaryEntry item in Environment.GetEnvironmentVariables())
        {
            var name = item.Key?.ToString() ?? string.Empty;
            if (name.Contains("TOKEN", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("SECRET", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("API_KEY", StringComparison.OrdinalIgnoreCase))
                yield return item.Value?.ToString() ?? string.Empty;
        }
    }
}
