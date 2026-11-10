using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Quodery.Web.Pages;

[AllowAnonymous]
public sealed class LoginModel(AccountService accounts) : PageModel
{
    [BindProperty] public string Username { get; set; } = string.Empty;
    [BindProperty] public string Password { get; set; } = string.Empty;
    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-cache, no-store";
        if (User.Identity?.IsAuthenticated == true) return LocalRedirect(SafeReturnUrl());
        return await accounts.IsRegistrationOpenAsync(ct) ? RedirectToPage("/Register", new { returnUrl = ReturnUrl }) : Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-cache, no-store";
        var result = await accounts.SignInAsync(Username, Password, ct);
        Password = string.Empty;
        ModelState.Remove(nameof(Password));
        if (result.Refusal is { } refusal)
        {
            ModelState.AddModelError(string.Empty, refusal.Message);
            return Page();
        }
        await AuthenticationSession.SignInAsync(HttpContext, result.Value!);
        return LocalRedirect(SafeReturnUrl());
    }

    private string SafeReturnUrl() => Url.IsLocalUrl(ReturnUrl) ? ReturnUrl! : Url.Page("/Workspaces")!;
}
