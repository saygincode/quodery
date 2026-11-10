using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.DataProtection;
using Quodery.Web.Storage;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.FileProviders;

namespace Quodery.Web;

public static class QuoderyServer
{
    public const string ListeningUsage = ServerListener.Usage;

    public static async Task<int> RunAsync(string[] args, string? runtimeSocketPath = null,
        Func<IServiceProvider, CancellationToken, Task<int>>? runtime = null)
    {
        if (ServerListener.TryParse(args, out var listener) is { } error)
        {
            Console.Error.WriteLine(error);
            return 2;
        }
        if (HostingStartupVariable() is { } variable)
        {
            Console.Error.WriteLine($"{variable} is set: the server doesn't load hosting startup assemblies; unset it");
            return 2;
        }
        var app = await BuildAsync(listener, runtimeSocketPath);
        var setup = app.Services.GetRequiredService<SetupLink>();
        await app.StartAsync();
        if (!await app.Services.GetRequiredService<ISessionStore>().HasUsersAsync())
            Console.WriteLine($"""
                This server isn't set up yet. Open this link to create its administrator account.
                It works once; use the address you reach this server by if it differs:

                  {listener.PublicBaseUrl()}/Register?setup={setup.Token}
                """);
        if (runtime is null)
        {
            await app.WaitForShutdownAsync();
            return 0;
        }
        var running = runtime(app.Services, app.Lifetime.ApplicationStopping);
        _ = running.ContinueWith(_ => app.Lifetime.StopApplication(), TaskScheduler.Default);
        await app.WaitForShutdownAsync();
        return await running;
    }

    private static WebApplicationBuilder CreateBuilder()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [],
            EnvironmentName = Environments.Production,
            ContentRootPath = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory
        });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Logging:LogLevel:Default"] = "Information",
            ["Logging:LogLevel:Microsoft.AspNetCore"] = "Warning",
            ["AllowedHosts"] = "*"
        });
        builder.Environment.WebRootFileProvider = new ManifestEmbeddedFileProvider(typeof(QuoderyServer).Assembly, "wwwroot");
        return builder;
    }

    private static async Task<WebApplication> BuildAsync(ServerListener listener, string? runtimeSocketPath)
    {
        var builder = CreateBuilder();
        var plainHttp = listener.Tls is null;
        if (plainHttp && !listener.IsLoopbackOnly)
            Console.Error.WriteLine($"warning: serving plain HTTP on port {listener.Port}, so passwords and session cookies cross the network unencrypted. Use --tls-certificate and --tls-key, or --address 127.0.0.1.");
        builder.WebHost.ConfigureKestrel(options =>
        {
            void Listen(int port, Action<ListenOptions> configure)
            {
                if (listener.Address is null) options.ListenAnyIP(port, configure);
                else options.Listen(listener.Address, port, configure);
            }
            Listen(listener.Port, listen =>
            {
                if (listener.Tls is { } tls) listen.UseHttps(tls.Certificate);
            });
            if (listener.HttpPort is { } httpPort) Listen(httpPort, _ => { });
            if (runtimeSocketPath is not null) options.ListenUnixSocket(runtimeSocketPath);
        });
        if (listener.Tls is not null)
        {
            builder.Services.AddSingleton(listener.Tls);
            builder.Services.AddHostedService<CertificateExpiryService>();
        }
        builder.Services.AddRazorPages();
        ServerApplication.AddServices(builder, servesRuntime: runtimeSocketPath is not null);
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<WorkspaceContext>();
        builder.Services.AddDataProtection().SetApplicationName("Quodery.Web");
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                var path = context.Request.Path.Value;
                if (!HttpMethods.IsPost(context.Request.Method))
                    return RateLimitPartition.GetNoLimiter("other");
                var signIn = string.Equals(path, "/Login", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(path, "/Register", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(path, "/AcceptInvite", StringComparison.OrdinalIgnoreCase);
                var accountAction = string.Equals(path, "/Administration", StringComparison.OrdinalIgnoreCase);
                if (!signIn && !accountAction)
                    return RateLimitPartition.GetNoLimiter("other");
                var key = $"{path?.ToLowerInvariant()}:{context.Connection.RemoteIpAddress}";
                return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
                { PermitLimit = signIn ? 10 : 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0,
                  AutoReplenishment = true });
            });
        });
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.Cookie.Name = "quodery.session";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.Cookie.SecurePolicy = plainHttp ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
                options.LoginPath = "/Login";
                options.AccessDeniedPath = "/AccessDenied";
                options.ExpireTimeSpan = TimeSpan.FromHours(8);
                options.SlidingExpiration = true;
                options.Events.OnValidatePrincipal = async context =>
                {
                    if (!long.TryParse(context.Principal?.FindFirstValue("quodery:auth_started"), out var started) ||
                        DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(started) > TimeSpan.FromHours(24))
                    {
                        context.RejectPrincipal();
                        return;
                    }
                    var access = await context.HttpContext.RequestServices.GetRequiredService<AccountService>().AuthoriseAsync(
                        context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier),
                        context.Principal?.FindFirstValue("quodery:session_version"), context.HttpContext.RequestAborted);
                    if (access is null)
                    {
                        context.RejectPrincipal();
                        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                        return;
                    }
                    CurrentAccess.Set(context.HttpContext, access);
                };
            });
        builder.Services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build());

        var app = builder.Build();
        await ServerApplication.InitialiseAsync(app.Services);
        if (runtimeSocketPath is not null)
            app.Use(async (context, next) =>
            {
                if (IsRuntimeSocket(context) != context.Request.Path.StartsWithSegments("/runtime"))
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }
                await next();
            });
        if (listener.HttpPort is { } redirectPort)
            app.Use(async (context, next) =>
            {
                if (context.Connection.LocalPort != redirectPort || IsRuntimeSocket(context))
                {
                    await next();
                    return;
                }
                if (!context.Request.Host.HasValue)
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }
                var port = listener.Port == 443 ? "" : $":{listener.Port}";
                context.Response.Redirect(
                    $"https://{context.Request.Host.Host}{port}{context.Request.PathBase}{context.Request.Path}{context.Request.QueryString}",
                    permanent: false, preserveMethod: true);
            });
        app.UseExceptionHandler("/Error");
        if (!plainHttp)
            app.UseHsts();

        app.Use(async (context, next) =>
        {
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
            await next();
        });
        app.UseStaticFiles();
        app.UseRouting();
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseMiddleware<ProjectNavigationMiddleware>();
        app.UseAuthorization();
        ServerApplication.MapRuntime(app);
        app.MapRazorPages();
        app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
        return app;
    }

    private static string? HostingStartupVariable() =>
        Environment.GetEnvironmentVariables().Keys.Cast<string>().FirstOrDefault(name =>
            name.Equals("ASPNETCORE_HOSTINGSTARTUPASSEMBLIES", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("DOTNET_HOSTINGSTARTUPASSEMBLIES", StringComparison.OrdinalIgnoreCase));

    private static bool IsRuntimeSocket(HttpContext context) => context.Connection.LocalIpAddress is null;
}
