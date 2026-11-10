using Quodery.Web.Runtime;
using Quodery.Web.Storage;
using Quodery.Protocol;
using Quodery.Runtime;
using Quodery.Web;

namespace Quodery.Cli;

internal static class ServingRuntime
{
    private static readonly string QuoderyDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".quodery");
    private static readonly SemaphoreSlim WorkspaceLock = new(1, 1);

    public static async Task<int> RunAsync(string[] args)
    {
        var (runtimeArgs, serverArgs) = RuntimeCommand.SplitServingOptions([.. args.Where(arg => arg != "--serve")]);
        if (RuntimeCommand.ValidateServing(runtimeArgs) is { } runtimeError)
        {
            Console.Error.WriteLine(runtimeError);
            return 2;
        }
        var socketPath = PrepareSocket();
        return await QuoderyServer.RunAsync(serverArgs, socketPath,
            (services, stop) => RunRuntimeAsync(services, runtimeArgs, socketPath, stop));
    }

    private static string PrepareSocket()
    {
        Directory.CreateDirectory(QuoderyDirectory);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(QuoderyDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var path = Path.Combine(QuoderyDirectory, "runtime.sock");
        File.Delete(path);
        return path;
    }

    private static async Task<int> RunRuntimeAsync(IServiceProvider services, string[] args, string socketPath, CancellationToken stop)
    {
        var store = services.GetRequiredService<ISessionStore>();
        var hub = services.GetRequiredService<RuntimeHub>();
        var name = WorkspaceName(args);
        services.GetRequiredService<ServingMode>().SetupCompleted =
            async ct => await EnsureWorkspaceAsync(store, name, ct);
        try
        {
            while (true)
            {
                while (!await store.HasUsersAsync(stop))
                    await Task.Delay(TimeSpan.FromSeconds(2), stop);
                var (project, runtimeKey) = await PrepareWorkspaceAsync(store, hub, name, stop);
                var environment = await store.GetProjectConnectionAsync(project.Id, stop)
                    ?? throw new InvalidOperationException("The serving runtime's workspace has no runtime connection");

                using var restart = CancellationTokenSource.CreateLinkedTokenSource(stop);
                var runtime = RuntimeCommand.RunAsync([.. args, "--server-url", RuntimeCommand.ServingServerUrl],
                    runtimeKey, restart.Token, socketPath);
                while (await Task.WhenAny(runtime, Task.Delay(TimeSpan.FromSeconds(10), stop)) != runtime)
                {
                    var connection = await store.GetProjectConnectionAsync(project.Id, stop);
                    if (connection is null || connection.Id != environment.Id || connection.ResetAt != environment.ResetAt ||
                        connection.RevokedAt is not null)
                    {
                        restart.Cancel();
                        break;
                    }
                }
                var exitCode = await runtime;
                if (!restart.IsCancellationRequested) return exitCode;
                stop.ThrowIfCancellationRequested();
                Console.WriteLine("The serving runtime's workspace or key was replaced; connecting it again.");
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { return 0; }
    }

    private static async Task<(Project Project, string RuntimeKey)> PrepareWorkspaceAsync(
        ISessionStore store, RuntimeHub hub, string name, CancellationToken cancellationToken)
    {
        var project = await EnsureWorkspaceAsync(store, name, cancellationToken);
        var environment = await store.GetProjectConnectionAsync(project.Id, cancellationToken)
            ?? throw new InvalidOperationException("The serving runtime's workspace has no runtime connection");
        var runtimeKey = await hub.ReplaceRuntimeKeyAsync(environment, cancellationToken)
            ?? throw new InvalidOperationException("Could not create a runtime key for the serving runtime");
        return (project, runtimeKey);
    }

    private static async Task<Project> EnsureWorkspaceAsync(ISessionStore store, string name, CancellationToken cancellationToken)
    {
        await WorkspaceLock.WaitAsync(cancellationToken);
        try
        {
            if ((await store.ListProjectsAsync(cancellationToken)).FirstOrDefault() is { } existing)
                return existing;
            var now = DateTimeOffset.UtcNow;
            var runtimeId = $"run_{Guid.NewGuid():N}";
            var project = new Project($"prj_{Guid.NewGuid():N}", name, runtimeId);
            await store.CreateProjectAsync(project, new RuntimeEnvironment($"env_{Guid.NewGuid():N}", project.Id,
                runtimeId, project.Name, null, now, null, null, null), cancellationToken);
            return project;
        }
        finally { WorkspaceLock.Release(); }
    }

    private static string WorkspaceName(string[] args)
    {
        var root = args.SkipWhile(arg => arg != "--workspace-root").Skip(1).FirstOrDefault() ?? ".";
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)));
        return string.IsNullOrWhiteSpace(name) ? "Workspace" : name.Length > 100 ? name[..100] : name;
    }
}
