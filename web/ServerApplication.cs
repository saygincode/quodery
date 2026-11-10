using Quodery.Web.Runtime;
using Quodery.Web.Storage;

namespace Quodery.Web;

public static class ServerApplication
{
    public static void AddServices(WebApplicationBuilder builder, bool servesRuntime = false)
    {
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = 64 * 1024;
        });

        var databasePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".quodery", "quodery.db");
        builder.Services.AddSingleton<ISessionStore>(_ => new SqliteSessionStore(databasePath));
        builder.Services.AddSingleton<RuntimeHub>();
        builder.Services.AddSingleton<SetupLink>();
        builder.Services.AddSingleton(new ServingMode(servesRuntime));
        builder.Services.AddSingleton<AccountService>();
        builder.Services.AddSingleton<TeamService>();
        builder.Services.AddSingleton<ProjectService>();
        builder.Services.AddSingleton<SessionService>();
        builder.Services.AddSingleton<GitService>();
        builder.Services.AddSingleton<GapTestBatches>();
        builder.Services.AddHostedService(services => services.GetRequiredService<GapTestBatches>());
    }

    public static Task InitialiseAsync(IServiceProvider services) =>
        services.GetRequiredService<ISessionStore>().InitialiseAsync();

    public static void MapRuntime(IEndpointRouteBuilder app)
    {
        var hub = app.ServiceProvider.GetRequiredService<RuntimeHub>();
        var runtime = app.MapGroup("/runtime").AllowAnonymous().DisableAntiforgery();
        runtime.MapPost("/challenge", hub.ChallengeAsync);
        runtime.MapPost("/session", hub.SessionAsync);
        runtime.MapPost("/commands", hub.CommandsAsync);
        runtime.MapPost("/events", hub.EventsAsync);
    }
}
