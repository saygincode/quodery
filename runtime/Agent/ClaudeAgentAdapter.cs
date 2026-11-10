using System.Diagnostics;
using System.Text.Json;

namespace Quodery.Runtime.Agent;

internal sealed class ClaudeAgentAdapter(
    AgentSettings settings,
    LocalSessionReportStore localReports,
    RuntimeLog? runtimeLog = null,
    bool unsafeLogAgentIo = false) : CliAgentAdapter(settings, localReports, runtimeLog, unsafeLogAgentIo)
{
    protected override string ProviderName => "claude";

    protected override ProcessStartInfo BuildStartInfo(string? threadId, string schemaFile)
    {
        var info = CreateStartInfo();
        foreach (var arg in new[] { "--print", "--output-format", "stream-json", "--verbose", "--json-schema",
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Agent", schemaFile)),
            "--disallowedTools", "AskUserQuestion" })
            info.ArgumentList.Add(arg);
        if (threadId is not null)
        {
            info.ArgumentList.Add("--resume");
            info.ArgumentList.Add(threadId);
        }
        return info;
    }

    protected override CliAgentOutput ReadOutput(JsonElement root)
    {
        var type = String(root, "type");
        if (type == "system" && String(root, "subtype") == "init")
            return new(ThreadId: String(root, "session_id"));
        if (type == "result")
        {
            if (String(root, "subtype") != "success" ||
                !root.TryGetProperty("is_error", out var isError) || isError.ValueKind != JsonValueKind.False)
                return new(Failed: true);
            if (!root.TryGetProperty("structured_output", out var report) || report.ValueKind != JsonValueKind.Object)
                throw new AgentException("agent_invalid_response", "Agent returned no structured testing report");
            return new(ThreadId: String(root, "session_id"), Final: report.GetRawText());
        }
        if (type == "assistant" &&
            (!root.TryGetProperty("parent_tool_use_id", out var parent) || parent.ValueKind == JsonValueKind.Null))
        {
            if (String(root, "error") is not null) return new(Failed: true);
            if (root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object &&
                message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                var texts = new List<string>();
                var toolActivity = false;
                foreach (var block in content.EnumerateArray())
                {
                    if (String(block, "type") == "text" && String(block, "text") is { } text) texts.Add(text);
                    if (String(block, "type") == "tool_use") toolActivity = true;
                }
                return new(Message: texts.Count > 0 ? string.Join("\n", texts) : null, ToolActivity: toolActivity);
            }
        }
        return new();
    }
}
