using System.Diagnostics;
using System.Text.Json;

namespace Quodery.Runtime.Agent;

internal sealed class CodexAgentAdapter(
    AgentSettings settings,
    LocalSessionReportStore localReports,
    RuntimeLog? runtimeLog = null,
    bool unsafeLogAgentIo = false) : CliAgentAdapter(settings, localReports, runtimeLog, unsafeLogAgentIo)
{
    protected override string ProviderName => "codex";

    protected override ProcessStartInfo BuildStartInfo(string? threadId, string schemaFile)
    {
        var info = CreateStartInfo();
        var args = new List<string> { "exec", "--cd", Settings.WorkspaceRoot };
        if (threadId is not null) args.Add("resume");
        args.AddRange(["--json", "--skip-git-repo-check", "--output-schema",
            Path.Combine(AppContext.BaseDirectory, "Agent", schemaFile)]);
        if (threadId is not null) args.Add(threadId);
        args.Add("-");
        foreach (var arg in args) info.ArgumentList.Add(arg);
        return info;
    }

    protected override CliAgentOutput ReadOutput(JsonElement root)
    {
        var type = String(root, "type");
        if (type == "thread.started") return new(ThreadId: String(root, "thread_id"));
        if (type is "turn.failed" or "error") return new(Failed: true);
        if (type == "item.completed" && root.TryGetProperty("item", out var item))
        {
            if (String(item, "type") == "agent_message")
            {
                var text = String(item, "text");
                return new(Message: text, Final: text);
            }
            if (String(item, "type") is "command_execution" or "mcp_tool_call")
                return new(ToolActivity: true);
        }
        return new();
    }
}
