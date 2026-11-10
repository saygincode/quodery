using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Quodery.Web.Storage;

namespace Quodery.Web;

internal static class AuthenticationSession
{
    public static Task SignInAsync(HttpContext context, ApplicationUser user)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id),
            new Claim(ClaimTypes.Name, user.Username),
            new Claim(ClaimTypes.Role, user.Role),
            new Claim("quodery:session_version", user.SessionVersion),
            new Claim("quodery:auth_started", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString())
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        return context.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties
            {
                IsPersistent = false,
                AllowRefresh = true,
                IssuedUtc = DateTimeOffset.UtcNow,
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8)
            });
    }
}

internal static class CurrentAccess
{
    public static void Set(HttpContext context, BrowserAccess access) => context.Items[typeof(BrowserAccess)] = access;

    public static BrowserAccess? FindAccess(this HttpContext context) => context.Items[typeof(BrowserAccess)] as BrowserAccess;

    public static BrowserAccess Access(this HttpContext context) =>
        context.FindAccess() ?? throw new InvalidOperationException("The request has no signed-in user");
}
