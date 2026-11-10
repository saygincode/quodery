namespace Quodery.Runtime;

internal enum AgentProvider { Codex, Claude }

internal sealed record AgentSettings(
    string Binary, string WorkspaceRoot, TimeSpan Timeout, TimeSpan? CatalogueTimeout = null);

internal sealed record RuntimeConfiguration(
    Uri ServerUrl, string RuntimeKey, AgentProvider AgentProvider, AgentSettings Agent,
    bool UnsafeLogAgentIo)
{
    public const string Usage = """
        Usage: quodery runtime --server-url <url> [options]
               quodery runtime --serve [listening options] [options]

        Options:
          --server-url <url>          A standalone runtime's server: its runtime endpoint, such as
                                      https://quodery.example.com/runtime (http:// on loopback only).
          --serve                     Instead of connecting to a server, also serve the web UI and
                                      accounts for this workspace, as a serving runtime.
          --workspace-root <path>     Workspace in which the agent runs. Default: current directory.
          --agent-provider <provider> Agent provider: codex or claude. Default: codex.
          --agent-binary <path>       Agent executable. Default: selected provider.
          --timeout-seconds <seconds> Maximum testing job duration. Default: 600.
          --catalogue-timeout-seconds <seconds> Exploration duration, 1–3600. Default: 600.
          --allow-git                 Let web users switch branch, get the latest changes, and
                                      commit or discard uncommitted changes in this workspace.
          --unsafe-log-agent-io       Log redacted agent output; may expose workspace data.
          -h, --help                  Show this help.

        A standalone runtime needs QUODERY_RUNTIME_KEY set to its workspace's runtime key from
        the server's setup page. A serving runtime gets its key from its own server.
        """;

    public static readonly IReadOnlySet<string> ValueOptions = new HashSet<string>(StringComparer.Ordinal)
    { "--workspace-root", "--agent-provider", "--agent-binary", "--timeout-seconds", "--catalogue-timeout-seconds" };
    public static readonly IReadOnlySet<string> FlagOptions = new HashSet<string>(StringComparer.Ordinal)
    { "--allow-git", "--unsafe-log-agent-io" };

    public string? UnixSocketPath { get; init; }
    public bool AllowGit { get; init; }

    public static RuntimeConfiguration Load(string[] args, string? runtimeKey)
    {
        var workspaceRoot = ".";
        string? serverUrl = null;
        var providerName = "codex";
        string? binary = null;
        var timeoutSeconds = 600;
        var catalogueTimeoutSeconds = 600;
        var allowGit = false;
        var unsafeLogAgentIo = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        string Value(string option, ref int index)
        {
            if (!seen.Add(option)) throw new ConfigurationException($"{option} may only be specified once");
            if (++index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
                throw new ConfigurationException($"{option} requires a value");
            return Required(args[index], option);
        }

        static string Required(string? value, string name) => !string.IsNullOrWhiteSpace(value)
            ? value.Trim() : throw new ConfigurationException($"{name} must be set");

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--workspace-root": workspaceRoot = Value(args[i], ref i); break;
                case "--server-url": serverUrl = Value(args[i], ref i); break;
                case "--agent-provider": providerName = Value(args[i], ref i); break;
                case "--agent-binary": binary = Value(args[i], ref i); break;
                case "--timeout-seconds":
                    var timeout = Value(args[i], ref i);
                    if (!int.TryParse(timeout, out timeoutSeconds) || timeoutSeconds <= 0)
                        throw new ConfigurationException("--timeout-seconds must be a positive integer");
                    break;
                case "--catalogue-timeout-seconds":
                    var catalogueTimeout = Value(args[i], ref i);
                    if (!int.TryParse(catalogueTimeout, out catalogueTimeoutSeconds) || catalogueTimeoutSeconds <= 0 ||
                        catalogueTimeoutSeconds > Quodery.Protocol.ProtocolConstants.MaximumCatalogueTimeoutSeconds)
                        throw new ConfigurationException("--catalogue-timeout-seconds must be between 1 and 3600");
                    break;
                case "--allow-git":
                    if (!seen.Add(args[i])) throw new ConfigurationException("--allow-git may only be specified once");
                    allowGit = true;
                    break;
                case "--unsafe-log-agent-io":
                    if (!seen.Add(args[i])) throw new ConfigurationException("--unsafe-log-agent-io may only be specified once");
                    unsafeLogAgentIo = true;
                    break;
                default: throw new ConfigurationException($"unknown argument: {args[i]}");
            }
        }

        var provider = providerName switch
        {
            "codex" => AgentProvider.Codex, "claude" => AgentProvider.Claude,
            _ => throw new ConfigurationException("--agent-provider must be codex or claude")
        };
        string workspace;
        try { workspace = Path.GetFullPath(workspaceRoot); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException)
        { throw new ConfigurationException($"invalid --workspace-root: {error.Message}"); }
        if (!Directory.Exists(workspace))
            throw new ConfigurationException("--workspace-root must be an existing directory");
        if (allowGit && !GitWorkspace.IsRepository(workspace))
            throw new ConfigurationException("--allow-git requires --workspace-root to be in a Git repository");
        if (serverUrl is null)
            throw new ConfigurationException("--server-url is required; copy the runtime command from the server's setup page");
        binary ??= provider == AgentProvider.Claude ? "claude" : "codex";
        return new(ServerUri(serverUrl), runtimeKey?.Trim() ?? "", provider,
            new(binary, workspace, TimeSpan.FromSeconds(timeoutSeconds), TimeSpan.FromSeconds(catalogueTimeoutSeconds)),
            unsafeLogAgentIo) { AllowGit = allowGit };
    }

    private static Uri ServerUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http" or "wss" or "ws") ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ConfigurationException("--server-url must be an https:// URL without credentials, query, or fragment");
        var builder = new UriBuilder(uri) { Scheme = uri.Scheme is "https" or "wss" ? "https" : "http" };
        if (builder.Scheme == "http" && !uri.IsLoopback)
            throw new ConfigurationException("--server-url may use http:// only for loopback hosts; use https:// otherwise");
        if (uri.IsDefaultPort) builder.Port = -1;
        return builder.Uri;
    }

    public Uri EndpointUrl(string request)
    {
        var builder = new UriBuilder(ServerUrl);
        builder.Path = builder.Path.TrimEnd('/') + "/" + request;
        return builder.Uri;
    }
}

internal sealed class ConfigurationException(string message) : Exception(message);
