using Quodery.Protocol;
using Quodery.Runtime.Agent;

namespace Quodery.Runtime;

public static class RuntimeCommand
{
    public static string Usage => RuntimeConfiguration.Usage;
    public static string Version => RuntimeVersion.Current;

    public const string ServingServerUrl = "http://localhost/runtime";

    public static (string[] Runtime, string[] Server) SplitServingOptions(string[] args)
    {
        List<string> runtime = [], server = [];
        for (var i = 0; i < args.Length; i++)
        {
            if (RuntimeConfiguration.FlagOptions.Contains(args[i]) || args[i] == "--server-url")
                runtime.Add(args[i]);
            else if (RuntimeConfiguration.ValueOptions.Contains(args[i]))
            {
                runtime.Add(args[i]);
                if (i + 1 < args.Length) runtime.Add(args[++i]);
            }
            else server.Add(args[i]);
        }
        return ([.. runtime], [.. server]);
    }

    public static string? ValidateServing(string[] args)
    {
        if (args.Contains("--server-url"))
            return "--serve and --server-url can't be combined: a serving runtime connects to its own server";
        try { RuntimeConfiguration.Load([.. args, "--server-url", ServingServerUrl], "unused"); }
        catch (ConfigurationException error) { return $"invalid runtime option: {error.Message}"; }
        return null;
    }

    public static async Task<int> RunAsync(string[] args)
    {
        if (args is ["--help"] or ["-h"])
        {
            Console.WriteLine(RuntimeConfiguration.Usage);
            return 0;
        }
        using var stop = new CancellationTokenSource();
        using var terminate = System.Runtime.InteropServices.PosixSignalRegistration.Create(
            System.Runtime.InteropServices.PosixSignal.SIGTERM, context => { context.Cancel = true; stop.Cancel(); });
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        return await RunAsync(args, Environment.GetEnvironmentVariable("QUODERY_RUNTIME_KEY"), stop.Token);
    }

    public static async Task<int> RunAsync(string[] args, string? runtimeKey, CancellationToken stopToken,
        string? unixSocketPath = null)
    {
        try
        {
            var config = RuntimeConfiguration.Load(args, runtimeKey) with { UnixSocketPath = unixSocketPath };
            if (string.IsNullOrWhiteSpace(config.RuntimeKey))
                throw new ConfigurationException("QUODERY_RUNTIME_KEY must be set");
            if (!RuntimeKeyFormat.IsWellFormed(config.RuntimeKey))
                throw new ConfigurationException("QUODERY_RUNTIME_KEY must be the runtime key from the workspace's setup page or refresh connection");
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(stopToken);
            using var log = RuntimeLog.Create();
            Console.Error.WriteLine($"runtime log: {log.FilePath}");
            if (config.UnsafeLogAgentIo)
                Console.Error.WriteLine("warning: agent I/O logging records redacted agent stdout and stderr, which may contain workspace data");
            log.Information("runtime.started", new
            {
                started_at = log.StartedAt,
                version = RuntimeVersion.Current,
                server_url = config.ServerUrl.ToString(),
                agent_provider = config.AgentProvider.ToString().ToLowerInvariant(),
                agent_io_logging = config.UnsafeLogAgentIo
            });
            try
            {
                var localReports = new LocalSessionReportStore(log);
                await using IAgentAdapter adapter = config.AgentProvider switch
                {
                    AgentProvider.Codex => new CodexAgentAdapter(config.Agent, localReports, log, config.UnsafeLogAgentIo),
                    _ => new ClaudeAgentAdapter(config.Agent, localReports, log, config.UnsafeLogAgentIo)
                };
                try { await new RuntimeClient(config, adapter, localReports, log).RunAsync(stop.Token); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            }
            finally
            {
                stop.Cancel();
                log.Information("runtime.stopped");
            }
            return 0;
        }
        catch (ConfigurationException error) { Console.Error.WriteLine($"invalid runtime configuration: {error.Message}"); return 2; }
        catch (OperationCanceledException) { return 0; }
        catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
    }
}
