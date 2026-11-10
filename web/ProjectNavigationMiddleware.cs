using Quodery.Web.Storage;

namespace Quodery.Web;

public sealed class ProjectNavigationMiddleware(RequestDelegate next, ServingMode serving, ISessionStore store)
{
    private static readonly HashSet<string> AuthenticationPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/Login",
        "/Register",
        "/AcceptInvite"
    };

    private static readonly HashSet<string> ProjectRequiredPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/",
        "/Workspaces",
        "/History",
        "/Sessions"
    };

    public async Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        var user = context.User;
        if (!HttpMethods.IsGet(request.Method) || user.Identity?.IsAuthenticated != true)
        {
            await next(context);
            return;
        }

        var path = NormalisePath(request.Path);
        var home = serving.ServesRuntime ? "/" : "/Workspaces";
        if (AuthenticationPaths.Contains(path))
        {
            TemporaryRedirect(context, home);
            return;
        }

        if (!RequiresProject(path) || context.FindAccess() is null ||
            (await store.ListProjectsAsync(context.RequestAborted)).Count > 0)
        {
            await next(context);
            return;
        }

        if (serving.ServesRuntime)
        {
            if (path != "/") TemporaryRedirect(context, "/");
            else await next(context);
            return;
        }

        if (user.IsInRole("admin"))
        {
            TemporaryRedirect(context, "/Setup");
            return;
        }

        if (!path.Equals("/Workspaces", StringComparison.OrdinalIgnoreCase))
        {
            TemporaryRedirect(context, "/Workspaces");
            return;
        }

        await next(context);
    }

    private static bool RequiresProject(string path) =>
        ProjectRequiredPaths.Contains(path) ||
        path.StartsWith("/Sessions/", StringComparison.OrdinalIgnoreCase);

    private static string NormalisePath(PathString path)
    {
        var value = path.Value ?? "/";
        return value.Length > 1 ? value.TrimEnd('/') : value;
    }

    private static void TemporaryRedirect(HttpContext context, string destination) =>
        context.Response.Redirect(destination, permanent: false);
}
