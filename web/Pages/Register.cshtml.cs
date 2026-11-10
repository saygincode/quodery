using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Quodery.Web.Pages;

[AllowAnonymous]
public sealed class RegisterModel(AccountService accounts) : NewPasswordPageModel
{
    [BindProperty] public string Username { get; set; } = string.Empty;
    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }
    [BindProperty(SupportsGet = true, Name = "setup")] public string? SetupToken { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-cache, no-store";
        if (User.Identity?.IsAuthenticated == true) return LocalRedirect(SafeReturnUrl());
        return await accounts.IsRegistrationOpenAsync(ct) ? Page() : RedirectToPage("/Login", new { returnUrl = ReturnUrl });
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-cache, no-store";
        if (!PasswordsMatch()) return Page();
        return await SignInOrShowErrorAsync(
            await accounts.RegisterAsync(Username, Password, SetupToken, ct), SafeReturnUrl());
    }

    private string SafeReturnUrl() => Url.IsLocalUrl(ReturnUrl) ? ReturnUrl! : Url.Page("/Workspaces")!;
}
